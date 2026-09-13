using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginRuntime.Execution;
using Jint;

namespace FeedPeek.Core.Tests;

/// <summary>
/// Stage B of the M0 probe: the douyin package ships an obfuscated signing SDK that the converter
/// drops by default. This probe measures whether Jint can load those bundles at all, which decides
/// whether the a_bogus fallback inside the plugin is reachable. Opt-in via
/// FEEDPEEK_LIVEPARSE_DOUYIN_SIGNER=1; the report goes to %TEMP%/feedpeek-liveparse-signer.txt.
/// </summary>
public sealed class LiveParseSignerProbeTests
{
    [Fact]
    public async Task DouyinSignerBundles_LoadInJint()
    {
        var root = Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_REFERENCES");
        if (Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_DOUYIN_SIGNER") != "1"
            || string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var directory = Path.Combine(root, "douyin-2.0.5");
        var report = new List<string>();
        if (!Directory.Exists(directory))
        {
            report.Add("douyin-2.0.5: MISSING DIRECTORY");
        }
        else
        {
            report.Add(await ProbeSignerAsync(directory, browserGlobals: false));
            report.Add(await ProbeSignerAsync(directory, browserGlobals: true));
        }

        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "feedpeek-liveparse-signer.txt"),
            string.Join(Environment.NewLine, report));
        Assert.Equal(2, report.Count);
    }

    private static Task<string> ProbeSignerAsync(string directory, bool browserGlobals)
    {
        var scripts = new[]
        {
            "abogus_bdms_env.js",
            "abogus_bdms_sdk.js",
            "abogus_bdms_bind.js",
            "webmssdk.js",
            "index.js",
        };
        var notes = new List<string>();
        var engine = new Engine(options =>
        {
            options.Strict();
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.LimitMemory(64L * 1024 * 1024);
            options.TimeoutInterval(TimeSpan.FromSeconds(30));
            options.MaxStatements(20_000_000);
        });
        engine.SetValue("__feedpeekHttp", new Func<string, Task<string>>(
            _ => Task.FromResult("{\"ok\":false,\"error\":{\"code\":\"PROBE\",\"message\":\"no network\"}}")));
        engine.SetValue("__feedpeekCredential", new Func<string, string, Task<string>>(
            (_, _) => Task.FromResult("{\"ok\":false,\"error\":{\"code\":\"PROBE\",\"message\":\"no network\"}}")));
        engine.SetValue("__feedpeekLog", new Action<string, string>((_, _) => { }));
        engine.SetValue("__feedpeekMd5", new Func<string, string>(value =>
            Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant()));
        engine.SetValue("__feedpeekNow", new Func<string>(() => DateTimeOffset.UtcNow.ToString("O")));

        try
        {
            engine.Execute(
                LiveParseDialect.BuildBootstrapScript("{\"pluginId\":\"douyin\",\"cookieDomains\":[\"douyin.com\"]}"),
                "feedpeek-host.js");
        }
        catch (Exception ex)
        {
            return Task.FromResult("douyin signer: BOOTSTRAP FAILED " + ex.GetType().Name + ": " + ex.Message);
        }

        if (browserGlobals)
        {
            engine.Execute(
                "globalThis.self = globalThis; globalThis.window = globalThis;",
                "probe-browser-globals.js");
        }

        notes.Add("globals=" + string.Join(",", new[]
        {
            "TextEncoder", "TextDecoder", "URL", "URLSearchParams", "performance", "crypto",
            "atob", "btoa", "queueMicrotask", "requestAnimationFrame", "localStorage",
            "XMLHttpRequest", "fetch", "WebAssembly", "structuredClone", "Intl", "Proxy", "Reflect",
            "BigInt", "WeakRef", "FinalizationRegistry", "setTimeout", "setInterval",
        }.Select(name =>
        {
            try
            {
                return name + ":" + engine.Evaluate("typeof globalThis." + name).AsString();
            }
            catch
            {
                return name + ":?";
            }
        })));

        foreach (var script in scripts)
        {
            var path = Path.Combine(directory, script);
            if (!File.Exists(path))
            {
                notes.Add(script + "=missing");
                continue;
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                engine.Execute(File.ReadAllText(path), script);
                notes.Add(script + "=ok(" + started.ElapsedMilliseconds + "ms)");
            }
            catch (Exception ex)
            {
                var detail = ex is Jint.Runtime.JavaScriptException js
                    ? " @ line " + js.Location.Start.Line + " col " + js.Location.Start.Column
                        + " stack=" + FirstLines(js.StackTrace, 2)
                    : string.Empty;
                notes.Add(script + "=FAILED " + ex.GetType().Name + ": " + ex.Message + detail);
            }
        }

        foreach (var expression in new[]
        {
            "typeof globalThis.__lp_douyin_abogus_sign",
            "typeof globalThis.sign_datail",
            "typeof globalThis.LiveParsePlugin",
        })
        {
            try
            {
                notes.Add(expression + " -> " + engine.Evaluate(expression).AsString());
            }
            catch (Exception ex)
            {
                notes.Add(expression + " -> ERROR " + ex.GetType().Name);
            }
        }

        Call(notes, engine, "sign_datail", "sign_datail('room_id=123&ver=1&web_location=444.43', 'Mozilla/5.0')");
        Call(
            notes,
            engine,
            "__lp_douyin_abogus_sign",
            "globalThis.__lp_douyin_abogus_sign('room_id=123&ver=1&web_location=444.43', 'Mozilla/5.0')");

        engine.Dispose();
        var label = browserGlobals ? "douyin signer [self+window]:" : "douyin signer [bare]:";
        return Task.FromResult(label + " " + string.Join(" | ", notes));
    }

    private static string FirstLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(none)";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.Take(count)).Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    private static void Call(List<string> notes, Engine engine, string name, string expression)
    {
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = engine.Evaluate(
                "typeof " + name + " === 'function' ? String(" + expression + ") : 'MISSING'").AsString();
            notes.Add(name + "() -> len=" + result.Length + " (" + started.ElapsedMilliseconds + "ms)");
        }
        catch (Exception ex)
        {
            notes.Add(name + "() -> FAILED " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
