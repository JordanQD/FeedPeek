using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginAuthService(string pluginId, PluginClient client)
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public Task<PluginAuthBeginResult> BeginAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginAuthBeginPayload, PluginAuthBeginResult>(
            PluginMethods.AuthBegin,
            subscription,
            new PluginAuthBeginPayload(CreateContext(subscription), subscription.Options),
            cancellationToken);

    public Task<PluginAuthState> CompleteAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginAuthCompletePayload, PluginAuthState>(
            PluginMethods.AuthComplete,
            subscription,
            new PluginAuthCompletePayload(CreateContext(subscription), null),
            cancellationToken);

    public Task<PluginAuthState> ValidateAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginAuthBeginPayload, PluginAuthState>(
            PluginMethods.AuthValidate,
            subscription,
            new PluginAuthBeginPayload(CreateContext(subscription), null),
            cancellationToken);

    public Task<PluginAuthState> ClearAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => InvokeAsync<PluginAuthBeginPayload, PluginAuthState>(
            PluginMethods.AuthClear,
            subscription,
            new PluginAuthBeginPayload(CreateContext(subscription), null),
            cancellationToken);

    private Task<TResult> InvokeAsync<TPayload, TResult>(
        string method,
        FeedSubscription subscription,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
        }

        return _client.InvokeAsync<TPayload, TResult>(
            ProviderId,
            method,
            payload,
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
