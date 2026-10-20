using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using Jint;
using Jint.Constraints;
using Jint.Native;
using Jint.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeedPeek.PluginRuntime.Execution;

public sealed class JintPluginTransport : IPluginTransport
{
    private const string HostBootstrapScript = """
        (() => {
          'use strict';
          const httpCall = globalThis.__feedpeekHttp;
          const credentialCall = globalThis.__feedpeekCredential;
          const logCall = globalThis.__feedpeekLog;
          const md5Call = globalThis.__feedpeekMd5;
          const nowCall = globalThis.__feedpeekNow;
          delete globalThis.__feedpeekHttp;
          delete globalThis.__feedpeekCredential;
          delete globalThis.__feedpeekLog;
          delete globalThis.__feedpeekMd5;
          delete globalThis.__feedpeekNow;

          const makeError = (code, message, options = {}) => {
            const error = new Error(String(message ?? code ?? 'Plugin error'));
            error.code = String(code ?? 'PLUGIN_ERROR');
            error.retryable = options.retryable === true;
            error.context = options.context ?? null;
            return error;
          };
          const unwrap = async promise => {
            const reply = JSON.parse(await promise);
            if (!reply.ok) {
              throw makeError(reply.error?.code, reply.error?.message, {
                retryable: reply.error?.retryable,
                context: reply.error?.context
              });
            }
            return reply.result;
          };
          const credential = (operation, request) =>
            unwrap(credentialCall(operation, JSON.stringify(request ?? {})));
          const writeLog = (level, message, context) => {
            logCall(level, JSON.stringify({ message: String(message ?? ''), context: context ?? null }));
          };

          const host = Object.freeze({
            http: Object.freeze({ request: options => unwrap(httpCall(JSON.stringify(options ?? {}))) }),
            credentials: Object.freeze({
              getStatus: request => credential('getStatus', request),
              getOpaqueSecret: request => credential('getOpaqueSecret', request),
              setOpaqueSecret: request => credential('setOpaqueSecret', request),
              deleteOpaqueSecret: request => credential('deleteOpaqueSecret', request)
            }),
            crypto: Object.freeze({ md5: value => md5Call(String(value ?? '')) }),
            log: Object.freeze({
              debug: (message, context) => writeLog('debug', message, context),
              info: (message, context) => writeLog('info', message, context),
              warn: (message, context) => writeLog('warn', message, context),
              error: (message, context) => writeLog('error', message, context)
            }),
            clock: Object.freeze({ now: () => nowCall() }),
            makeError,
            raise: (code, message, options) => { throw makeError(code, message, options); }
          });
          Object.defineProperty(globalThis, 'Host', {
            value: host,
            writable: false,
            configurable: false,
            enumerable: true
          });
        })();
        """;

    private readonly PluginPackage _package;
    private readonly PluginHostServices _host;
    private readonly PluginRuntimeOptions _options;
    private readonly bool _isLiveParseDialect;
    private readonly Engine _engine;
    private readonly SemaphoreSlim _invocationGate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationConstraint? _cancellationConstraint;
    private PluginInvocationScope? _activeScope;
    private string _cookieHeader = string.Empty;
    private int _disposeState;
    private int _faulted;

    private JintPluginTransport(PluginPackage package, PluginHostServices host, PluginRuntimeOptions options)
    {
        _package = package;
        _host = host;
        _options = options;
        _isLiveParseDialect = string.Equals(
            PluginManifestRules.ResolveRuntime(package.Manifest),
            PluginManifestRules.LiveParseRuntime,
            StringComparison.Ordinal);
        if (_isLiveParseDialect)
        {
            // The LiveParse host applies no execution budget at all, and a pinned-room source fans out
            // one HTTP call per room in parallel, so this dialect gets more headroom than the native
            // contract allows for.
            _options = options with
            {
                InvocationTimeout = TimeSpan.FromSeconds(Math.Max(options.InvocationTimeout.TotalSeconds, 60)),
                PromiseTimeout = TimeSpan.FromSeconds(Math.Max(options.PromiseTimeout.TotalSeconds, 60)),
                // Douyin resolves an anonymous room by parsing a ~1 MB HTML page, which costs orders
                // of magnitude more statements than the native contract's 250k. The invocation timeout
                // stays the real bound on runaway scripts.
                MaxStatements = Math.Max(options.MaxStatements, 500_000_000),
                // Parsing Douyin's ~1 MB room page also holds the raw HTML, regex intermediates and the
                // decoded JSON, which repeatedly tripped the native 64 MB budget.
                MemoryLimitBytes = Math.Max(options.MemoryLimitBytes, 256L * 1024 * 1024),
            };
        }
        // The engine must be configured from the effective options, not the constructor argument:
        // the dialect overrides above (longer timeouts, larger statement budget) only exist on _options.
        var effective = _options;
        _invocationGate = new SemaphoreSlim(effective.MaxConcurrentInvocations, effective.MaxConcurrentInvocations);
        _engine = new Engine(engineOptions =>
        {
            engineOptions.Strict();
            engineOptions.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            engineOptions.LimitMemory(effective.MemoryLimitBytes);
            engineOptions.TimeoutInterval(effective.InvocationTimeout);
            engineOptions.MaxStatements(effective.MaxStatements);
            engineOptions.CancellationToken(_lifetime.Token);
            engineOptions.Constraints.MaxRecursionDepth = effective.MaxRecursionDepth;
            engineOptions.Constraints.StackOverflowGuard = true;
            engineOptions.Constraints.PromiseTimeout = effective.PromiseTimeout;
        });
        _cancellationConstraint = _engine.Constraints.Find<CancellationConstraint>();
    }

    public string PluginId => _package.Manifest.PluginId;

    public static async Task<JintPluginTransport> CreateAsync(
        PluginPackage package,
        PluginHostServices host,
        PluginRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);
        options ??= new PluginRuntimeOptions();
        options.Validate();

        var runtime = new JintPluginTransport(package, host, options);
        try
        {
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> InvokeAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (Encoding.UTF8.GetByteCount(requestJson) > _options.MaxRequestJsonBytes)
            return SerializeFailure(string.Empty, "REQUEST_TOO_LARGE", "Plugin request exceeds the configured size limit.");

        PluginRequestEnvelope<JsonElement>? request;
        try
        {
            request = JsonSerializer.Deserialize<PluginRequestEnvelope<JsonElement>>(requestJson, PluginJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return SerializeFailure(string.Empty, "REQUEST_INVALID_JSON", ex.Message);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
            return SerializeFailure(string.Empty, "REQUEST_INVALID", "Plugin requestId is required.");
        if (Volatile.Read(ref _faulted) != 0)
            return SerializeFailure(request.RequestId, "PLUGIN_RUNTIME_UNAVAILABLE", "The plugin runtime was quarantined after a host task ignored cancellation.");
        if (request.ApiVersion != PluginProtocolVersion.Current)
            return SerializeFailure(request.RequestId, "API_VERSION_UNSUPPORTED", "The request API version is not supported.");
        if (!string.Equals(request.PluginId, PluginId, StringComparison.Ordinal))
            return SerializeFailure(request.RequestId, "PLUGIN_ID_MISMATCH", "The request targets a different plugin.");
        if (!PluginMethods.All.Contains(request.Method))
            return SerializeFailure(request.RequestId, "METHOD_UNSUPPORTED", "The requested plugin method is not supported by this protocol version.");
        if (request.Payload.ValueKind == JsonValueKind.Undefined)
            return SerializeFailure(request.RequestId, "REQUEST_INVALID", "Plugin payload is required.");

        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // Runtime work and cancellation cleanup must finish even while the caller's UI is closing.
        await _invocationGate.WaitAsync(gateCancellation.Token).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(_options.InvocationTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, timeout.Token);
            PluginInvocationContext? requestedContext;
            try
            {
                requestedContext = ExtractInvocationContext(request.Payload);
            }
            catch (JsonException)
            {
                return SerializeFailure(request.RequestId, "REQUEST_CONTEXT_INVALID", "The plugin invocation context is invalid.");
            }
            PluginHostContext authorizedContext;
            try
            {
                authorizedContext = await _host.InvocationAuthorizer.AuthorizeAsync(
                    request.PluginId,
                    request.RequestId,
                    request.Method,
                    requestedContext,
                    linked.Token).ConfigureAwait(false);
            }
            catch (PluginHostBridgeException ex)
            {
                return SerializeFailure(request.RequestId, ex.Code, ex.Message, ex.Retryable);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return SerializeFailure(request.RequestId, "HOST_AUTHORIZATION_ERROR", "The host could not authorize this plugin invocation.");
            }
            if (authorizedContext.PluginId != request.PluginId
                || authorizedContext.RequestId != request.RequestId
                || authorizedContext.Method != request.Method)
            {
                return SerializeFailure(request.RequestId, "HOST_CONTEXT_INVALID", "The host returned an invalid authoritative invocation context.");
            }
            if (_isLiveParseDialect)
            {
                // Host.session.getCookieHeader is synchronous in the LiveParse dialect, so the
                // credential for this invocation is resolved before the script starts.
                _cookieHeader = await LoadCookieHeaderAsync(
                    request.PluginId,
                    authorizedContext.CredentialProfileId,
                    linked.Token).ConfigureAwait(false);
            }

            var invocationScope = new PluginInvocationScope(authorizedContext, linked.Token);
            _activeScope = invocationScope;
            _cancellationConstraint?.Reset(linked.Token);

            try
            {
                var script = BuildInvocationScript(requestJson);
                var value = await _engine.EvaluateAsync(script, "feedpeek-invoke.js", linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!value.IsString())
                    return SerializeFailure(request.RequestId, "PROTOCOL_RESPONSE_INVALID", "Plugin response was not JSON text.");

                var responseJson = value.AsString();
                if (Encoding.UTF8.GetByteCount(responseJson) > _options.MaxResponseJsonBytes)
                    return SerializeFailure(request.RequestId, "RESPONSE_TOO_LARGE", "Plugin response exceeds the configured size limit.");
                return ValidateResponse(request.RequestId, request.Method, responseJson);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_TIMEOUT", "Plugin execution exceeded its time budget.", true);
            }
            catch (ExecutionCanceledException) when (timeout.IsCancellationRequested)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_TIMEOUT", "Plugin execution exceeded its time budget.", true);
            }
            catch (TimeoutException)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_TIMEOUT", "Plugin execution exceeded its time budget.", true);
            }
            catch (StatementsCountOverflowException)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_BUDGET_EXCEEDED", "Plugin execution exceeded its statement budget.");
            }
            catch (MemoryLimitExceededException)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_MEMORY_LIMIT", "Plugin execution exceeded its memory budget.");
            }
            catch (RecursionDepthOverflowException)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_RECURSION_LIMIT", "Plugin execution exceeded its recursion limit.");
            }
            catch (Exception ex)
            {
                return SerializeFailure(request.RequestId, "PLUGIN_RUNTIME_ERROR", ex.Message);
            }
            finally
            {
                _activeScope = null;
                if (!await invocationScope.StopAsync(_options.HostTaskDrainTimeout).ConfigureAwait(false))
                    Interlocked.Exchange(ref _faulted, 1);
            }
        }
        finally
        {
            _invocationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _lifetime.Cancel();
        await _invocationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _engine.Dispose();
            _lifetime.Dispose();
        }
        finally
        {
            _invocationGate.Release();
            _invocationGate.Dispose();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.InitializationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _lifetime.Token);
        _cancellationConstraint?.Reset(linked.Token);

        InstallHostDelegates();
        _engine.Execute(
            _isLiveParseDialect
                ? LiveParseDialect.BuildBootstrapScript(BuildDialectConfigJson())
                : HostBootstrapScript,
            "feedpeek-host.js");

        long totalBytes = 0;
        foreach (var relativePath in (_package.Manifest.PreloadScripts ?? []).Append(_package.Manifest.Entry))
        {
            if (!_package.Scripts.TryGetValue(relativePath, out var script))
                throw new PluginManifestException("PLUGIN_SCRIPT_SNAPSHOT_MISSING", "A validated plugin script snapshot is missing.");
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(script));
            if (totalBytes > _options.MaxScriptBytes)
                throw new PluginManifestException("PLUGIN_SCRIPT_TOO_LARGE", "Plugin scripts exceed the configured size limit.");
            _engine.Execute(script, relativePath);
        }

        if (_isLiveParseDialect)
        {
            _engine.Execute(
                LiveParseDialect.BuildAdapterScript(BuildDialectConfigJson()),
                "feedpeek-liveparse-adapter.js");
        }

        var exportType = _engine.Evaluate("typeof globalThis.FeedPeekPlugin").AsString();
        if (exportType != "object")
            throw new PluginManifestException("PLUGIN_EXPORT_MISSING", "Plugin must export globalThis.FeedPeekPlugin.");
        var apiVersion = _engine.Evaluate("globalThis.FeedPeekPlugin.apiVersion").AsNumber();
        if (apiVersion != _package.Manifest.ApiVersion)
            throw new PluginManifestException("PLUGIN_EXPORT_VERSION_MISMATCH", "Plugin export apiVersion does not match its manifest.");
        if (!_engine.Evaluate("typeof globalThis.FeedPeekPlugin.handle === 'function'").AsBoolean())
            throw new PluginManifestException("PLUGIN_HANDLE_MISSING", "Plugin must export a handle(request) function.");
    }

    private async Task<string> LoadCookieHeaderAsync(
        string pluginId,
        string? credentialProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialProfileId))
            return string.Empty;
        try
        {
            return await _host.CookieProvider.GetCookieHeaderAsync(
                pluginId,
                credentialProfileId,
                CookieScopeUri(),
                cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A missing or unreadable credential store must not fail the plugin call itself.
            return string.Empty;
        }
    }

    private Uri CookieScopeUri()
    {
        var domain = _package.Manifest.Auth?.CookieDomains.FirstOrDefault(
            value => !string.IsNullOrWhiteSpace(value));
        return domain is null ? new Uri("https://localhost/") : new Uri("https://" + domain + "/");
    }

    private string ReadCookieHeader(string platformId)
    {
        _ = RequireActiveScope();
        if (string.IsNullOrEmpty(_cookieHeader))
            return string.Empty;
        return IsOwnPlatformId(platformId) ? _cookieHeader : string.Empty;
    }

    /// <summary>
    /// Mirrors the host rule that a plugin can only read its own credential: the requested platform id
    /// may be empty, the plugin id itself, or its trailing segment (<c>liveparse.douyin</c> to
    /// <c>douyin</c>, which is how the bundled plugins name themselves).
    /// </summary>
    private bool IsOwnPlatformId(string? platformId)
    {
        var requested = platformId?.Trim() ?? string.Empty;
        if (requested.Length == 0)
            return true;
        if (string.Equals(requested, PluginId, StringComparison.OrdinalIgnoreCase))
            return true;
        var separator = PluginId.LastIndexOf('.');
        return separator > 0
            && string.Equals(requested, PluginId[(separator + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeBase64Decode(string input)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(input ?? string.Empty);
            var bytes = Convert.FromBase64String(decoded);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException
            or UriFormatException
            or ArgumentException)
        {
            return string.Empty;
        }
    }

    private string BuildDialectConfigJson()
    {
        var manifest = _package.Manifest;
        var options = manifest.RuntimeOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(manifest.RuntimeOptions, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(
            new
            {
                pluginId = manifest.PluginId,
                displayName = manifest.DisplayName,
                apiVersion = manifest.ApiVersion,
                capabilities = manifest.Capabilities,
                runtimeOptions = options,
                cookieDomains = manifest.Auth?.CookieDomains ?? [],
            },
            PluginJson.SerializerOptions);
        // The default JavaScriptEncoder escapes every non-ASCII character (including the
        // U+2028/U+2029 line separators), so the JSON text is always a valid JS literal.
        return json;
    }

    private void InstallHostDelegates()
    {
        _engine.SetValue("__feedpeekHttp", new Func<string, Task<string>>(BeginHttpCall));
        _engine.SetValue("__feedpeekCookieHeader", new Func<string, string>(ReadCookieHeader));
        _engine.SetValue("__feedpeekBase64Decode", new Func<string, string>(ComputeBase64Decode));
        _engine.SetValue("__feedpeekCredential", new Func<string, string, Task<string>>(BeginCredentialCall));
        _engine.SetValue("__feedpeekLog", new Action<string, string>(WriteLog));
        _engine.SetValue("__feedpeekMd5", new Func<string, string>(ComputeMd5));
        _engine.SetValue("__feedpeekNow", new Func<string>(() => DateTimeOffset.UtcNow.ToString("O")));
    }

    private Task<string> BeginHttpCall(string requestJson)
    {
        var scope = RequireActiveScope();
        var task = InvokeHttpAsync(scope, requestJson);
        scope.Track(task);
        return task;
    }

    private async Task<string> InvokeHttpAsync(PluginInvocationScope scope, string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<HostHttpRequest>(requestJson, PluginJson.SerializerOptions)
                ?? throw new PluginHostBridgeException("HTTP_REQUEST_INVALID", "Host.http.request requires an options object.");
            var result = await _host.Http.SendAsync(_package.Manifest, scope.Context, request, scope.Token).ConfigureAwait(false);
            var isText = string.Equals(result.BodyEncoding, "utf8", StringComparison.OrdinalIgnoreCase);
            // The host always exposes the raw bytes as base64 in addition to bodyText.
            return SerializeBridgeSuccess(new
            {
                status = result.Status,
                finalUrl = result.FinalUrl,
                headers = result.Headers,
                body = result.Body,
                bodyEncoding = result.BodyEncoding,
                bodyBase64 = isText
                    ? Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Body ?? string.Empty))
                    : result.Body,
            });
        }
        catch (Exception ex)
        {
            return SerializeBridgeFailure(ex);
        }
    }

    private Task<string> BeginCredentialCall(string operation, string requestJson)
    {
        var scope = RequireActiveScope();
        var task = InvokeCredentialAsync(scope, operation, requestJson);
        scope.Track(task);
        return task;
    }

    private async Task<string> InvokeCredentialAsync(PluginInvocationScope scope, string operation, string requestJson)
    {
        var context = scope.Context;
        try
        {
            var request = JsonSerializer.Deserialize<CredentialRequest>(requestJson, PluginJson.SerializerOptions)
                ?? throw new PluginHostBridgeException("CREDENTIAL_REQUEST_INVALID", "Credential request is invalid.");
            EnsureCredentialScope(context, request.CredentialProfileId);
            object? result = operation switch
            {
                "getStatus" => new CredentialStatus(await _host.Credentials.ExistsAsync(
                    context.PluginId, request.CredentialProfileId, scope.Token).ConfigureAwait(false)),
                "getOpaqueSecret" => new Dictionary<string, string?>
                {
                    ["value"] = await _host.Credentials.GetOpaqueSecretAsync(
                        context.PluginId, request.CredentialProfileId, RequireSecretName(request.Name), scope.Token).ConfigureAwait(false),
                },
                "setOpaqueSecret" => await SetSecretAsync(context, request, scope.Token).ConfigureAwait(false),
                "deleteOpaqueSecret" => await DeleteSecretAsync(context, request, scope.Token).ConfigureAwait(false),
                _ => throw new PluginHostBridgeException("CREDENTIAL_OPERATION_UNSUPPORTED", "Credential operation is not supported."),
            };
            return SerializeBridgeSuccess(result);
        }
        catch (Exception ex)
        {
            return SerializeBridgeFailure(ex);
        }
    }

    private async Task<object> SetSecretAsync(PluginHostContext context, CredentialRequest request, CancellationToken cancellationToken)
    {
        if (request.Value is null)
            throw new PluginHostBridgeException("CREDENTIAL_VALUE_REQUIRED", "Credential value is required.");
        await _host.Credentials.SetOpaqueSecretAsync(
            context.PluginId, request.CredentialProfileId, RequireSecretName(request.Name), request.Value, cancellationToken).ConfigureAwait(false);
        return new { updated = true };
    }

    private async Task<object> DeleteSecretAsync(PluginHostContext context, CredentialRequest request, CancellationToken cancellationToken)
    {
        await _host.Credentials.DeleteOpaqueSecretAsync(
            context.PluginId, request.CredentialProfileId, RequireSecretName(request.Name), cancellationToken).ConfigureAwait(false);
        return new { deleted = true };
    }

    private void WriteLog(string level, string payloadJson)
    {
        var context = RequireActiveScope().Context;
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : string.Empty;
        IReadOnlyDictionary<string, string>? values = null;
        if (root.TryGetProperty("context", out var contextElement) && contextElement.ValueKind == JsonValueKind.Object)
        {
            values = contextElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ToString(),
                StringComparer.Ordinal);
        }
        _host.Log.Write(context.PluginId, NormalizeLogLevel(level), PluginLogRedactor.Redact(message), PluginLogRedactor.Redact(values));
    }

    private PluginInvocationScope RequireActiveScope()
        => _activeScope ?? throw new PluginHostBridgeException("HOST_CONTEXT_MISSING", "Host API is only available during a plugin invocation.");

    private static PluginInvocationContext? ExtractInvocationContext(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("context", out var context))
            return null;
        if (context.ValueKind != JsonValueKind.Object)
            throw new JsonException("Plugin invocation context must be an object.");
        return context.Deserialize<PluginInvocationContext>(PluginJson.SerializerOptions);
    }

    private static void EnsureCredentialScope(PluginHostContext context, string requestedProfileId)
    {
        if (string.IsNullOrWhiteSpace(context.CredentialProfileId)
            || !string.Equals(context.CredentialProfileId, requestedProfileId, StringComparison.Ordinal))
        {
            throw new PluginHostBridgeException("CREDENTIAL_SCOPE_FORBIDDEN", "Plugin credential access is outside the active source profile.");
        }
    }

    private static string RequireSecretName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new PluginHostBridgeException("CREDENTIAL_NAME_INVALID", "Opaque secret name is required and must not exceed 100 characters.");
        return name;
    }

    private static string NormalizeLogLevel(string level)
        => level is "debug" or "info" or "warn" or "error" ? level : "info";

    private static string ComputeMd5(string value)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string BuildInvocationScript(string requestJson)
    {
        var requestLiteral = JsonSerializer.Serialize(requestJson, PluginJson.SerializerOptions);
        return string.Concat(
            "(async()=>{const request=JSON.parse(", requestLiteral, ");",
            "const validate=(value,seen)=>{if(value===null)return;const type=typeof value;",
            "if(type==='string'||type==='number'||type==='boolean')return;",
            "if(type!=='object')throw Host.makeError('PROTOCOL_RESULT_INVALID','Plugin returned a non-JSON value.');",
            "if(seen.has(value))throw Host.makeError('PROTOCOL_RESULT_INVALID','Plugin returned a circular value.');",
            "seen.add(value);if(Array.isArray(value)){for(const item of value)validate(item,seen);}",
            "else{const prototype=Object.getPrototypeOf(value);if(prototype!==Object.prototype&&prototype!==null)",
            "throw Host.makeError('PROTOCOL_RESULT_INVALID','Plugin returned a host or non-plain object.');",
            "for(const key of Object.keys(value))validate(value[key],seen);}seen.delete(value);};",
            "try{const result=await globalThis.FeedPeekPlugin.handle(request);validate(result,new WeakSet());",
            "return JSON.stringify({requestId:request.requestId,success:true,result});}",
            "catch(error){const raw=error&&typeof error==='object'?error:{};",
            "let code=raw.code,message=raw.message,retryable=raw.retryable===true,rawContext=raw.context;",
            "const marker='LP_PLUGIN_ERROR:';const text=String(raw.message||'');const at=text.indexOf(marker);",
            "if(at>=0){try{const parsed=JSON.parse(text.slice(at+marker.length));",
            "code=parsed.code;message=parsed.message;retryable=parsed.retryable===true;rawContext=parsed.context;}catch(ignored){}}",
            "const context=rawContext&&typeof rawContext==='object'?Object.fromEntries(Object.entries(rawContext).map(([k,v])=>[k,String(v)])):null;",
            "return JSON.stringify({requestId:request.requestId,success:false,error:{code:String(code||'PLUGIN_ERROR'),",
            "message:String(message||error||'Plugin error'),retryable,context}});}})()"
        );
    }

    private static string ValidateResponse(string requestId, string method, string responseJson)
    {
        try
        {
            var response = JsonSerializer.Deserialize<PluginResponseEnvelope<JsonElement>>(responseJson, PluginJson.SerializerOptions);
            if (response is null
                || response.RequestId != requestId
                || response.Success == (response.Error is not null)
                || (response.Success && response.Result.ValueKind == JsonValueKind.Undefined))
                return SerializeFailure(requestId, "PROTOCOL_RESPONSE_INVALID", "Plugin returned an invalid response envelope.");
            if (response.Success && method is PluginMethods.ItemGetActions or PluginMethods.CreatorGetActions)
            {
                var actions = response.Result.Deserialize<ActionDescriptor[]>(PluginJson.SerializerOptions)
                    ?? throw new InvalidDataException("Plugin action response must be an array.");
                PluginActionValidator.Validate(actions);
            }
            return responseJson;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return SerializeFailure(requestId, "PROTOCOL_RESPONSE_INVALID", ex.Message);
        }
    }

    private static string SerializeFailure(string requestId, string code, string message, bool retryable = false)
        => JsonSerializer.Serialize(
            new
            {
                requestId,
                success = false,
                error = new PluginError(code, message, retryable, null),
            },
            PluginJson.SerializerOptions);

    private static string SerializeBridgeSuccess<T>(T result)
        => JsonSerializer.Serialize(new BridgeReply<T>(true, result, null), PluginJson.SerializerOptions);

    private static string SerializeBridgeFailure(Exception exception)
    {
        var error = exception is PluginHostBridgeException bridge
            ? new PluginError(bridge.Code, bridge.Message, bridge.Retryable, bridge.Context)
            : new PluginError("HOST_ERROR", exception.Message, false, null);
        return JsonSerializer.Serialize(new BridgeReply<object>(false, null, error), PluginJson.SerializerOptions);
    }

    private sealed record BridgeReply<T>(bool Ok, T? Result, PluginError? Error);

    private sealed class PluginInvocationScope : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<Task> _hostTasks = [];
        private readonly CancellationTokenSource _cancellation;
        private bool _stopping;

        public PluginInvocationScope(PluginHostContext context, CancellationToken cancellationToken)
        {
            Context = context;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public PluginHostContext Context { get; }
        public CancellationToken Token => _cancellation.Token;

        public void Track(Task task)
        {
            lock (_sync)
                _hostTasks.Add(task);
        }

        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            lock (_sync)
                _stopping = true;
            _cancellation.Cancel();
            using var timeoutSource = new CancellationTokenSource(timeout);

            try
            {
                var observedCount = -1;
                while (true)
                {
                    Task[] tasks;
                    lock (_sync)
                    {
                        if (_hostTasks.Count == observedCount)
                            break;
                        observedCount = _hostTasks.Count;
                        tasks = _hostTasks.ToArray();
                    }
                    try
                    {
                        await Task.WhenAll(tasks).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                    }
                    catch when (!timeoutSource.IsCancellationRequested)
                    {
                        // The main invocation response already carries the relevant host error.
                    }
                }
                return true;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (!_stopping)
                    _cancellation.Cancel();
                _stopping = true;
            }
            _cancellation.Dispose();
        }
    }
}
