using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginItemActionService(string pluginId, PluginClient client)
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public Task<ActionDescriptor[]> GetActionsAsync(
        FeedSubscription subscription,
        FeedItem item,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(subscription, item);
        return _client.InvokeAsync<PluginItemReference, ActionDescriptor[]>(
            ProviderId,
            PluginMethods.ItemGetActions,
            new PluginItemReference(CreateContext(subscription), item.ItemId, item.Metadata),
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> ExecuteAsync(
        FeedSubscription subscription,
        ActionDescriptor action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
        }

        return _client.InvokeAsync<ExecuteActionPayload, IReadOnlyDictionary<string, string>>(
            ProviderId,
            PluginMethods.ItemExecuteAction,
            new ExecuteActionPayload(CreateContext(subscription), action.ActionId, action.Payload),
            cancellationToken);
    }

    private void ValidateTarget(FeedSubscription subscription, FeedItem item)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(item);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal)
            || !string.Equals(item.ProviderId, subscription.ProviderId, StringComparison.Ordinal)
            || !string.Equals(item.SourceId, subscription.SourceId, StringComparison.Ordinal)
            || !string.Equals(item.FeedId, subscription.FeedId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The item does not belong to the plugin subscription.");
        }
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
