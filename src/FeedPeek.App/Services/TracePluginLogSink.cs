using FeedPeek.PluginRuntime.Host;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FeedPeek.Services;

/// <summary>
/// Routes plugin <c>console</c>/<c>Host.log</c> output into the application trace. The transport already
/// redacted cookies, tokens and signed query parameters before calling this.
/// </summary>
internal sealed class TracePluginLogSink : IPluginLogSink
{
    public static TracePluginLogSink Instance { get; } = new();

    private TracePluginLogSink()
    {
    }

    public void Write(string pluginId, string level, string message, IReadOnlyDictionary<string, string>? context)
    {
        var suffix = context is { Count: > 0 }
            ? " {" + string.Join(", ", context.Select(pair => pair.Key + "=" + pair.Value)) + "}"
            : string.Empty;
        NotificationService.TraceNotification($"[plugin:{pluginId}] {level}: {message}{suffix}");
    }
}
