using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginAccountService(string pluginId, PluginClient client)
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public Task<PluginAccountProfile> GetProfileAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
        }

        return _client.InvokeAsync<PluginAccountPayload, PluginAccountProfile>(
            ProviderId,
            PluginMethods.AccountGetProfile,
            new PluginAccountPayload(CreateContext(subscription)),
            cancellationToken);
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
