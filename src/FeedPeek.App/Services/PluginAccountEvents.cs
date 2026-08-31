using System;

namespace FeedPeek.Services;

internal static class PluginAccountEvents
{
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
