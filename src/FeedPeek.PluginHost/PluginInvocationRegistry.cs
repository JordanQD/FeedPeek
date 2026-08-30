using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Host;
using System.Collections.Concurrent;

namespace FeedPeek.PluginHost;

public sealed class PluginInvocationRegistry : IPluginInvocationAuthorizer
{
    private readonly ConcurrentDictionary<(string ProviderId, string SubscriptionId), PluginInvocationContext> _contexts = new();

    public void Register(PluginInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateContext(context);
        _contexts[(context.ProviderId, context.SubscriptionId)] = context;
    }

    public bool Remove(string providerId, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        return _contexts.TryRemove((providerId, subscriptionId), out _);
    }

    public Task<PluginHostContext> AuthorizeAsync(
        string pluginId,
        string requestId,
        string method,
        PluginInvocationContext? requestedContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedContext is null)
        {
            if (method is not (PluginMethods.Handshake or PluginMethods.GetStatus))
                throw Forbidden("The plugin method requires an authorized source context.");
            return Task.FromResult(new PluginHostContext(pluginId, requestId, method, null));
        }

        if (!_contexts.TryGetValue((pluginId, requestedContext.SubscriptionId), out var authorized)
            || !ContextMatches(pluginId, requestedContext, authorized))
        {
            throw Forbidden("The requested source context is not authorized for this plugin.");
        }

        return Task.FromResult(new PluginHostContext(
            pluginId,
            requestId,
            method,
            authorized.CredentialProfileId));
    }

    private static bool ContextMatches(
        string pluginId,
        PluginInvocationContext requested,
        PluginInvocationContext authorized)
        => string.Equals(pluginId, authorized.ProviderId, StringComparison.Ordinal)
            && string.Equals(requested.ProviderId, authorized.ProviderId, StringComparison.Ordinal)
            && string.Equals(requested.SourceId, authorized.SourceId, StringComparison.Ordinal)
            && string.Equals(requested.FeedId, authorized.FeedId, StringComparison.Ordinal)
            && string.Equals(requested.CredentialProfileId, authorized.CredentialProfileId, StringComparison.Ordinal)
            && OptionsMatch(requested.Options, authorized.Options);

    private static bool OptionsMatch(
        IReadOnlyDictionary<string, string>? requested,
        IReadOnlyDictionary<string, string>? authorized)
    {
        if (ReferenceEquals(requested, authorized)) return true;
        if (requested is null || authorized is null || requested.Count != authorized.Count) return false;
        return requested.All(item => authorized.TryGetValue(item.Key, out var value)
            && string.Equals(item.Value, value, StringComparison.Ordinal));
    }

    private static void ValidateContext(PluginInvocationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.FeedId);
    }

    private static PluginHostBridgeException Forbidden(string message)
        => new("INVOCATION_FORBIDDEN", message);
}
