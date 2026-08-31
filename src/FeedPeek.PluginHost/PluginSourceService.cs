using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginSourceService(string pluginId, PluginClient client)
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public Task<PluginSourceDefinition> CreateAsync(
        FeedSubscription contextSubscription,
        string? displayName,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginSourceCreatePayload, PluginSourceDefinition>(
            PluginMethods.SourceCreate,
            contextSubscription,
            new PluginSourceCreatePayload(CreateContext(contextSubscription), displayName, options),
            cancellationToken);

    public Task<PluginSourceDefinition> GetAsync(
        FeedSubscription contextSubscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginSourcePayload, PluginSourceDefinition>(
            PluginMethods.SourceGet,
            contextSubscription,
            new PluginSourcePayload(CreateContext(contextSubscription)),
            cancellationToken);

    public Task<PluginSourceListResult> ListFeedsAsync(
        FeedSubscription contextSubscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginSourcePayload, PluginSourceListResult>(
            PluginMethods.SourceListFeeds,
            contextSubscription,
            new PluginSourcePayload(CreateContext(contextSubscription)),
            cancellationToken);

    public Task<PluginSourceDeleteResult> DeleteAsync(
        FeedSubscription contextSubscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginSourcePayload, PluginSourceDeleteResult>(
            PluginMethods.SourceDelete,
            contextSubscription,
            new PluginSourcePayload(CreateContext(contextSubscription)),
            cancellationToken);

    private Task<TResult> InvokeAsync<TPayload, TResult>(
        string method,
        FeedSubscription subscription,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
        return _client.InvokeAsync<TPayload, TResult>(ProviderId, method, payload, cancellationToken);
    }

    private static PluginInvocationContext CreateContext(FeedSubscription subscription)
        => new(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options);
}
