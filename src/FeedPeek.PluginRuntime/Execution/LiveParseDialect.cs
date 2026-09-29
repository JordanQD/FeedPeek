namespace FeedPeek.PluginRuntime.Execution;

/// <summary>
/// Host-side compatibility scripts for third-party plugins written against the LiveParse
/// (<c>globalThis.LiveParsePlugin</c>) contract. FeedPeek never patches plugin files: the host
/// installs a LiveParse-shaped <c>Host</c> facade and, after the plugin entry has run, wraps the
/// plugin's methods in the native <c>FeedPeekPlugin.handle(request)</c> contract.
/// </summary>
public static class LiveParseDialect
{
    public const string ConfigPlaceholder = "__FEEDPEEK_DIALECT_CONFIG__";

    public const string BootstrapScriptTemplate = """
        (() => {
          'use strict';

          // Browser environment shim, mirroring the AngelLive host bootstrap: bundled signing and
          // danmaku SDKs probe window/document/navigator and btoa/atob before they run.
          if (typeof globalThis.document === "undefined") globalThis.document = {};
          if (typeof globalThis.window === "undefined") globalThis.window = {};
          if (typeof globalThis.navigator === "undefined") globalThis.navigator = {};
          if (!globalThis.navigator.userAgent) {
            globalThis.navigator.userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
          }
          if (typeof globalThis.self === "undefined") globalThis.self = globalThis;

          var __lpBase64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
          if (typeof globalThis.btoa !== "function") {
            globalThis.btoa = function (input) {
              var source = String(input || "");
              var output = "";
              for (var i = 0; i < source.length; i += 3) {
                var byte1 = source.charCodeAt(i);
                var byte2 = i + 1 < source.length ? source.charCodeAt(i + 1) : NaN;
                var byte3 = i + 2 < source.length ? source.charCodeAt(i + 2) : NaN;
                if (byte1 > 0xff || (!isNaN(byte2) && byte2 > 0xff) || (!isNaN(byte3) && byte3 > 0xff)) {
                  throw new Error("InvalidCharacterError");
                }
                var chunk = (byte1 << 16) | ((isNaN(byte2) ? 0 : byte2) << 8) | (isNaN(byte3) ? 0 : byte3);
                output += __lpBase64Alphabet.charAt((chunk >> 18) & 63);
                output += __lpBase64Alphabet.charAt((chunk >> 12) & 63);
                output += isNaN(byte2) ? "=" : __lpBase64Alphabet.charAt((chunk >> 6) & 63);
                output += isNaN(byte3) ? "=" : __lpBase64Alphabet.charAt(chunk & 63);
              }
              return output;
            };
          }
          if (typeof globalThis.atob !== "function") {
            globalThis.atob = function (input) {
              var source = String(input || "").replace(/[\t\n\f\r ]+/g, "");
              if (source.length % 4 === 1) { throw new Error("InvalidCharacterError"); }
              var output = "";
              for (var i = 0; i < source.length; i += 4) {
                var enc1 = source.charAt(i);
                var enc2 = source.charAt(i + 1);
                var enc3 = source.charAt(i + 2);
                var enc4 = source.charAt(i + 3);
                var idx1 = __lpBase64Alphabet.indexOf(enc1);
                var idx2 = __lpBase64Alphabet.indexOf(enc2);
                var idx3 = enc3 === "=" ? 0 : __lpBase64Alphabet.indexOf(enc3);
                var idx4 = enc4 === "=" ? 0 : __lpBase64Alphabet.indexOf(enc4);
                if (idx1 < 0 || idx2 < 0 || (enc3 !== "=" && idx3 < 0) || (enc4 !== "=" && idx4 < 0)) {
                  throw new Error("InvalidCharacterError");
                }
                var chunk = (idx1 << 18) | (idx2 << 12) | (idx3 << 6) | idx4;
                output += String.fromCharCode((chunk >> 16) & 0xff);
                if (enc3 !== "=") output += String.fromCharCode((chunk >> 8) & 0xff);
                if (enc4 !== "=") output += String.fromCharCode(chunk & 0xff);
              }
              return output;
            };
          }

          var config = __FEEDPEEK_DIALECT_CONFIG__;
          var httpCall = globalThis.__feedpeekHttp;
          var cookieCall = typeof globalThis.__feedpeekCookieHeader === "function" ? globalThis.__feedpeekCookieHeader : function () { return ""; };
          var base64Call = typeof globalThis.__feedpeekBase64Decode === "function" ? globalThis.__feedpeekBase64Decode : function () { return ""; };
          var logCall = globalThis.__feedpeekLog;
          var md5Call = globalThis.__feedpeekMd5;
          delete globalThis.__feedpeekHttp;
          delete globalThis.__feedpeekCookieHeader;
          delete globalThis.__feedpeekBase64Decode;
          delete globalThis.__feedpeekLog;
          delete globalThis.__feedpeekMd5;
          delete globalThis.__feedpeekNow;

          // Error protocol: the structured code travels inside the message so that it survives the
          // JavaScript error boundary, exactly like the AngelLive host.
          var makeError = function (code, message, context) {
            var normalizedContext = {};
            if (context && typeof context === "object" && !Array.isArray(context)) {
              Object.keys(context).forEach(function (key) {
                var value = context[key];
                if (value === undefined || value === null) return;
                normalizedContext[String(key)] = String(value);
              });
            }
            return new Error("LP_PLUGIN_ERROR:" + JSON.stringify({
              code: String(code || "UNKNOWN"),
              message: String(message || ""),
              context: normalizedContext
            }));
          };
          var unwrap = async function (promise) {
            var reply = JSON.parse(await promise);
            if (!reply.ok) {
              var error = reply.error || {};
              throw makeError(error.code, error.message, error.context);
            }
            return reply.result;
          };
          var writeLog = function (level, message, context) {
            logCall(level, JSON.stringify({ message: String(message || ""), context: context || null }));
          };

          var hostOf = function (url) {
            var text = String(url || "");
            var scheme = text.indexOf("://");
            if (scheme < 0) return "";
            var rest = text.slice(scheme + 3);
            var stop = rest.length;
            var delimiters = ["/", "?", "#"];
            for (var d = 0; d < delimiters.length; d++) {
              var at = rest.indexOf(delimiters[d]);
              if (at >= 0 && at < stop) stop = at;
            }
            var authority = rest.slice(0, stop);
            var userinfo = authority.lastIndexOf("@");
            if (userinfo >= 0) authority = authority.slice(userinfo + 1);
            var colon = authority.indexOf(":");
            if (colon >= 0) authority = authority.slice(0, colon);
            return authority.toLowerCase();
          };
          var withinCredentialDomains = function (url) {
            var host = hostOf(url);
            if (!host) return false;
            var domains = config.cookieDomains || [];
            for (var i = 0; i < domains.length; i++) {
              var domain = String(domains[i] || "").toLowerCase();
              if (!domain) continue;
              if (host === domain) return true;
              if (host.length > domain.length && host.slice(host.length - domain.length - 1) === "." + domain) return true;
            }
            return false;
          };
          var cookieValues = function () {
            var values = {};
            var header = "";
            try { header = String(cookieCall("") || ""); } catch (error) { header = ""; }
            header.split(";").forEach(function (segment) {
              var trimmed = segment.trim();
              if (!trimmed) return;
              var index = trimmed.indexOf("=");
              if (index <= 0) return;
              values[trimmed.slice(0, index).trim().toLowerCase()] = trimmed.slice(index + 1).trim();
            });
            return values;
          };
          // The host only applies header targets; query and body rules are ignored there too.
          var applyCookieInject = function (options, headers) {
            var merged = {};
            Object.keys(headers || {}).forEach(function (key) { merged[key] = headers[key]; });
            var rules = options && options.cookieInject;
            if (!Array.isArray(rules) || rules.length === 0) return merged;
            var request = options.request && typeof options.request === "object" ? options.request : options;
            if (!withinCredentialDomains(request && request.url)) return merged;
            var values = cookieValues();
            for (var i = 0; i < rules.length; i++) {
              var rule = rules[i] && typeof rules[i] === "object" ? rules[i] : {};
              if (String(rule.target || "header").toLowerCase() !== "header") continue;
              var value = values[String(rule.cookieName || "").toLowerCase()];
              if (!value) continue;
              var headerName = String(rule.headerName || "");
              if (!headerName) continue;
              var injected = String(rule.prefix || "") + value;
              if (headerName.toLowerCase() === "cookie") {
                if (injected.indexOf("=") < 0) continue;
                var existing = merged.Cookie || merged.cookie || "";
                merged.Cookie = existing ? existing + "; " + injected : injected;
              } else {
                merged[headerName] = injected;
              }
            }
            return merged;
          };
          // Resolution order matches the host: request.timeoutMs, request.timeout,
          // options.timeoutMs, options.timeout, then 20 seconds.
          var resolveTimeoutMs = function (request, options) {
            var positive = function (value) {
              var number = Number(value);
              return isFinite(number) && number > 0 ? number : null;
            };
            var candidates = [request.timeoutMs, request.timeout, options && options.timeoutMs, options && options.timeout];
            for (var index = 0; index < candidates.length; index++) {
              var value = positive(candidates[index]);
              if (value === null) continue;
              var seconds = index === 0 || index === 2 ? value / 1000 : value;
              return Math.round(Math.min(Math.max(seconds, 0.1), 120) * 1000);
            }
            return 20000;
          };
          // A credential request must never carry plugin supplied Cookie or Authorization headers; the
          // host strips them instead of failing the call.
          var protectedHeadersRemoved = function (headers, protectsCredential) {
            var result = {};
            Object.keys(headers || {}).forEach(function (key) {
              var lowered = String(key).toLowerCase();
              if (protectsCredential && (lowered === "cookie" || lowered === "authorization")) return;
              result[key] = headers[key];
            });
            return result;
          };
          // Host-level failures surface as NETWORK, like the AngelLive host, but keep the message.
          var hostErrorAliases = {
            HTTP_URL_INVALID: "NETWORK",
            HTTP_HOST_FORBIDDEN: "NETWORK",
            HTTP_REDIRECT_FORBIDDEN: "NETWORK",
            HTTP_REDIRECT_HOST_FORBIDDEN: "NETWORK",
            HTTP_REDIRECT_LIMIT: "NETWORK",
            HTTP_HEADER_FORBIDDEN: "NETWORK",
            HOST_ERROR: "NETWORK"
          };
          var mapHostErrorCode = function (code) {
            var key = String(code || "");
            return hostErrorAliases[key] || key || "NETWORK";
          };

          globalThis.Host = globalThis.Host || {};
          Host.makeError = makeError;
          Host.raise = function (code, message, context) { throw makeError(code, message, context); };

          // Only capabilities FeedPeek really implements are advertised, so plugins keep their own
          // fallbacks for everything else.
          Host.capabilities = Host.capabilities || {};
          Host.capabilities.loginTransaction = false;
          Host.capabilities.loginChallengeProtocol = 0;
          Host.capabilities.loginChallengePush = false;
          Host.capabilities.loginChallengeBootstrap = false;
          Host.capabilities.credentialExposure = true;
          Host.capabilities.webSocketPlatformCookie = false;

          Host.http = Host.http || {};
          Host.http.request = function (options) {
            return new Promise(function (resolve, reject) {
              var call = options && typeof options === "object" ? options : {};
              var request = call.request && typeof call.request === "object" ? call.request : call;
              var authMode = String(call.authMode || "none").trim().toLowerCase();
              var cookieInject = call.cookieInject;
              var protectsCredential = authMode !== "none" || (Array.isArray(cookieInject) && cookieInject.length > 0);
              var payload = {
                url: String(request.url || ""),
                method: String(request.method || "GET").toUpperCase(),
                headers: protectedHeadersRemoved(applyCookieInject(call, request.headers), protectsCredential),
                body: null,
                bodyEncoding: null,
                timeoutMs: resolveTimeoutMs(request, call),
                maxResponseBytes: request.maxResponseBytes || null
              };
              if (typeof request.bodyBase64 === "string" && request.bodyBase64) {
                payload.body = request.bodyBase64;
                payload.bodyEncoding = "base64";
              } else if (typeof request.body === "string") {
                payload.body = request.body;
              } else if (request.body && typeof request.body === "object") {
                payload.body = JSON.stringify(request.body);
              }
              httpCall(JSON.stringify(payload)).then(function (replyText) {
                var reply = JSON.parse(replyText);
                if (!reply.ok) {
                  var error = reply.error || {};
                  reject(makeError(mapHostErrorCode(error.code), error.message, error.context));
                  return;
                }
                var raw = reply.result || {};
                var isText = raw.bodyEncoding === "utf8";
                var body = raw.body === undefined || raw.body === null ? "" : String(raw.body);
                var base64 = raw.bodyBase64 === undefined || raw.bodyBase64 === null ? "" : String(raw.bodyBase64);
                resolve({
                  status: raw.status,
                  headers: raw.headers || {},
                  setCookies: [],
                  url: raw.finalUrl,
                  bodyText: isText ? body : null,
                  bodyBase64: isText ? base64 : body
                });
              }, function (error) {
                reject(makeError("NETWORK", String(error && error.message ? error.message : error), {}));
              });
            });
          };

          Host.crypto = Host.crypto || {};
          Host.crypto.md5 = function (input) { return md5Call(String(input)); };
          Host.crypto.base64Decode = function (input) { return base64Call(String(input)); };

          Host.session = Host.session || {};
          Host.session.getCookieHeader = function (platformId) { return cookieCall(String(platformId || "")); };
          Host.session.getTransactionCookieHeader = function () {
            return Promise.reject(makeError(
              "UNSUPPORTED",
              "Login transaction cookie values are host-managed and unavailable to plugins",
              {}
            ));
          };
          Host.session.seedTransactionCookies = function () {
            return Promise.reject(makeError("UNSUPPORTED", "Login transactions are not supported by FeedPeek", {}));
          };

          Host.runtime = Host.runtime || {};
          Host.runtime.loadBuiltinScript = function () { return false; };

          Host.nativeStream = Host.nativeStream || {};
          Host.nativeStream.resolve = function () {
            return Promise.reject(makeError("UNSUPPORTED", "Native stream resolution is not available in FeedPeek", {}));
          };
          Host.stream = Host.stream || {};
          Host.stream.resolve = Host.nativeStream.resolve;
          Host.stream.getInfo = Host.nativeStream.resolve;

          Host.ws = Host.ws || {};
          Host.ws.open = function () {
            return Promise.reject(makeError("UNSUPPORTED", "WebSocket bridging is not available in FeedPeek", {}));
          };

          var consoleShim = globalThis.console && typeof globalThis.console === "object" ? globalThis.console : {};
          consoleShim.log = function (message) { writeLog("info", message, null); };
          consoleShim.error = function (message) { writeLog("error", message, null); };
          consoleShim.info = function (message) { writeLog("info", message, null); };
          consoleShim.warn = function (message) { writeLog("warn", message, null); };
          consoleShim.debug = function (message) { writeLog("debug", message, null); };
          globalThis.console = consoleShim;
        })();
        """;

    public static string BuildBootstrapScript(string configJson)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        return BootstrapScriptTemplate.Replace(ConfigPlaceholder, configJson, StringComparison.Ordinal);
    }

    public const string AdapterTemplate = """
        (() => {
          'use strict';
          const config = __FEEDPEEK_DIALECT_CONFIG__;
          const plugin = globalThis.LiveParsePlugin;
          if (!plugin || typeof plugin !== 'object') {
            throw Host.makeError('PLUGIN_EXPORT_MISSING', 'The plugin must define globalThis.LiveParsePlugin.');
          }
          if (Number(plugin.apiVersion || 0) !== Number(config.apiVersion)) {
            throw Host.makeError(
              'INVALID_RESPONSE',
              'Plugin exported apiVersion ' + String(plugin.apiVersion) + ' but the manifest declares ' + String(config.apiVersion) + '.',
              {});
          }
          // The host prefers the v2 name and retries the v1 name only for a missing function.
          const roomDetailMethod = typeof plugin.getRoomDetail === 'function'
            ? 'getRoomDetail'
            : (typeof plugin.getLiveLastestInfo === 'function' ? 'getLiveLastestInfo' : null);
          if (roomDetailMethod === null) {
            throw Host.makeError('INVALID_RESPONSE', 'Missing function: getRoomDetail', {});
          }

          const text = value => (value === undefined || value === null ? '' : String(value));
          const image = value => {
            const raw = text(value).trim();
            if (!raw) return null;
            if (raw.indexOf('//') === 0) return 'https:' + raw;
            if (raw.indexOf('http://') === 0) return 'https://' + raw.slice(7);
            return raw.indexOf('https://') === 0 ? raw : null;
          };
          const viewers = value => {
            const raw = text(value).trim();
            if (!raw) return null;
            const match = /^([0-9]+(?:.[0-9]+)?)s*([万亿]?)/.exec(raw);
            if (!match) return null;
            const amount = Number(match[1]);
            if (!Number.isFinite(amount)) return null;
            const scale = match[2] === '万' ? 10000 : match[2] === '亿' ? 100000000 : 1;
            return Math.round(amount * scale);
          };

          const template = text(config.runtimeOptions && config.runtimeOptions.roomUrlTemplate);
          const roomUrl = roomId => {
            if (!template) {
              throw Host.makeError('RUNTIME_OPTIONS_MISSING', 'runtimeOptions.roomUrlTemplate is required to build room links.');
            }
            return template.split('{roomId}').join(encodeURIComponent(roomId));
          };
          const toLiveItem = (room, fallbackRoomId) => {
            const source = room && typeof room === 'object' ? room : {};
            const roomId = text(source.roomId).trim() || text(fallbackRoomId).trim();
            if (!roomId) return null;
            const state = text(source.liveState).trim();
            return {
              creatorKey: roomId,
              displayName: text(source.userName).trim() || '房间 ' + roomId,
              url: roomUrl(roomId),
              title: text(source.roomTitle).trim() || null,
              avatarUrl: image(source.userHeadImg),
              coverUrl: image(source.roomCover),
              viewerCount: viewers(source.liveWatchedCount),
              startedAt: null,
              metadata: state ? { liveState: state } : null
            };
          };
          // A source pins concrete rooms. A full link such as https://www.douyu.com/74751 is accepted
          // as well as a bare id, and duplicates are folded.
          const parseRoomIds = value => {
            const ids = [];
            const seen = {};
            for (const token of text(value).split(/[,\s;，、]+/)) {
              const trimmed = token.trim();
              if (!trimmed) continue;
              const tail = trimmed.split('/').pop().split('?')[0].split('#')[0];
              const digits = /([0-9]+)/.exec(tail);
              if (!digits) continue;
              const id = digits[1];
              if (seen[id] === true) continue;
              seen[id] = true;
              ids.push(id);
              if (ids.length >= 20) break;
            }
            return ids;
          };

          const credentialState = async () => {
            const validate = plugin.validateCredential;
            const status = plugin.getCredentialStatus;
            const probe = typeof validate === 'function' ? validate : status;
            if (typeof probe !== 'function') return { state: 'unknown', profile: null, message: null };
            // The host tells the plugin that a credential is present rather than handing it the bytes.
            const argument = typeof validate === 'function' ? { credentialAvailable: true } : {};
            let result = null;
            try {
              result = await probe(argument);
            } catch (error) {
              result = null;
            }
            const state = text(result && result.state).toLowerCase();
            const authenticated = state === 'valid' || state === 'authenticated' || state === 'ok';
            return {
              state: authenticated ? 'authenticated' : (state || 'unknown'),
              profile: null,
              message: text(result && result.message) || null
            };
          };

          const handlers = {
            'plugin.handshake': () => ({
              providerId: config.pluginId,
              apiVersion: config.apiVersion,
              capabilities: config.capabilities || []
            }),

            'plugin.getStatus': () => ({ state: 'ready', message: null, details: null }),

            // A LiveParse plugin has no remote source object: a source is only the pinned room list the
            // host keeps, so deleting one is a local operation that always succeeds.
            'source.delete': () => ({ deleted: true }),

            // The host's following list comes from the plugin, but a pinned-room source has no feed to
            // derive it from, so an empty list is the correct answer rather than an unsupported method.
            'creator.getFollowing': () => ({ creators: [] }),

            'source.create': async payload => {
              const context = payload.context || {};
              const options = Object.assign({}, payload.options || {});
              const roomIds = parseRoomIds(options.roomIds);
              let displayName = text(payload.displayName).trim() || config.displayName;
              // A pinned room is a source even while the room is offline. Resolve its identity
              // here so the settings card does not depend on the live-only feed.getLive result.
              if (roomIds.length === 1) {
                try {
                  const room = await plugin[roomDetailMethod]({ roomId: roomIds[0] });
                  const name = text(room && room.userName).trim()
                    || text(room && room.roomTitle).trim();
                  if (name) displayName = name;
                  const avatar = image(room && room.userHeadImg);
                  if (avatar) options['source.icon'] = avatar;
                } catch (error) {
                  // A transient detail failure must not prevent the room from being pinned.
                }
              }
              return {
                providerId: context.providerId,
                sourceId: context.sourceId,
                displayName,
                credentialProfileId: context.credentialProfileId || null,
                feeds: [{
                  feedId: 'live',
                  displayName: '正在直播',
                  kind: 'live',
                  supportsPaging: false,
                  supportsNotifications: false
                }],
                options: payload.options ? options : null
              };
            },

            'feed.getLive': async payload => {
              const options = (payload.context && payload.context.options) || {};
              const roomIds = parseRoomIds(options.roomIds);
              if (roomIds.length === 0) {
                throw Host.makeError('SOURCE_OPTION_MISSING', 'The source is missing the roomIds option.', {});
              }
              const detail = plugin[roomDetailMethod];
              const results = await Promise.all(roomIds.map(async roomId => {
                try {
                  return { roomId, room: await detail({ roomId }) };
                } catch (error) {
                  return { roomId, error };
                }
              }));
              // Only rooms the platform reports as actually broadcasting survive. The converter
              // declares the accepted states in runtimeOptions.liveStates; a re-broadcast ("2") or an
              // offline room ("0"/"3") must not appear in the live strip.
              const liveStates = text(config.runtimeOptions && config.runtimeOptions.liveStates)
                .split(/[,\s]+/)
                .filter(Boolean);
              const items = [];
              let firstError = null;
              for (const result of results) {
                if (result.error) {
                  if (firstError === null) firstError = result.error;
                  continue;
                }
                if (liveStates.length > 0) {
                  const state = text(result.room && result.room.liveState).trim();
                  if (liveStates.indexOf(state) < 0) continue;
                }
                const item = toLiveItem(result.room, result.roomId);
                if (item) items.push(item);
              }
              // A single broken room must not empty the strip, but a systematic failure should surface.
              if (items.length === 0 && firstError !== null) throw firstError;
              return { items };
            },

            'auth.begin': () => ({ state: 'webviewRequired', data: null }),

            'auth.complete': async () => {
              const sync = plugin.setCredential || plugin.setCookie;
              if (typeof sync === 'function') await sync({});
              return await credentialState();
            },

            'auth.validate': () => credentialState(),

            'auth.clear': async () => {
              const clear = plugin.clearCredential || plugin.clearCookie;
              if (typeof clear === 'function') await clear();
              return { state: 'missing', profile: null, message: null };
            }
          };

          globalThis.FeedPeekPlugin = Object.freeze({
            apiVersion: config.apiVersion,
            async handle(request) {
              const method = text(request && request.method);
              if (!Object.prototype.hasOwnProperty.call(handlers, method)) {
                throw Host.makeError('UNSUPPORTED', 'Unsupported method', { method });
              }
              return await handlers[method](request.payload || {});
            }
          });
        })();
        """;

    public static string BuildAdapterScript(string configJson)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        return AdapterTemplate.Replace(ConfigPlaceholder, configJson, StringComparison.Ordinal);
    }
}
