using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginCreatorService(string pluginId, PluginClient client)
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public async Task<IReadOnlyList<CreatorDescriptor>> GetFollowingAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => (await GetFollowingPageAsync(subscription, cancellationToken).ConfigureAwait(false)).Creators;

    public async Task<FollowingCreatorsPage> GetFollowingPageAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscription(subscription);
        var result = await _client.InvokeAsync<PluginAccountPayload, PluginCreatorListResult>(
            ProviderId,
            PluginMethods.CreatorGetFollowing,
            new PluginAccountPayload(CreateContext(subscription)),
            cancellationToken).ConfigureAwait(false);
        if (result.Creators is null)
            throw InvalidResult("Plugin creator list is required.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var creators = result.Creators.Select(creator =>
        {
            if (creator is null || !keys.Add(creator.CreatorKey))
                throw InvalidResult("Plugin creator keys must be non-empty and unique.");
            return MapCreator(subscription, creator);
        }).ToArray();
        return new(creators, result.IsComplete);
    }

    public async Task<CreatorResolutionResult> ResolveAsync(
        FeedSubscription subscription,
        string input,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscription(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.Length > 2048) throw new ArgumentException("Author input is too long.", nameof(input));
        var result = await _client.InvokeAsync<PluginCreatorResolvePayload, PluginCreatorResolveResult>(
            ProviderId, PluginMethods.CreatorResolve,
            new(CreateContext(subscription), input.Trim()), cancellationToken).ConfigureAwait(false);
        var state = result.FollowState switch
        {
            "followed" => CreatorFollowState.Followed,
            "notFollowed" => CreatorFollowState.NotFollowed,
            "unknown" => CreatorFollowState.Unknown,
            _ => throw InvalidResult("Plugin author follow state is invalid."),
        };
        return new(MapCreator(subscription, result.Creator), state);
    }

    private static CreatorDescriptor MapCreator(FeedSubscription subscription, PluginCreatorDescriptor? creator)
    {
        if (creator is null || string.IsNullOrWhiteSpace(creator.CreatorKey) || creator.CreatorKey.Length > 256)
            throw InvalidResult("Plugin creator key is required.");
        if (string.IsNullOrWhiteSpace(creator.DisplayName) || creator.DisplayName.Length > 256)
            throw InvalidResult("Plugin creator name is required.");
        ValidateOptionalWebUrl(creator.ProfileUrl, "creator profile URL");
        ValidateOptionalWebUrl(creator.AvatarUrl, "creator avatar URL");
        var kinds = creator.SupportedNotificationKinds ?? [];
        if (kinds.Any(kind => kind is not ("video" or "updates" or "live"))
            || kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)
            throw InvalidResult("Plugin creator notification kinds are invalid.");
        return new(subscription.ProviderId, subscription.SourceId, creator.CreatorKey, creator.DisplayName,
            creator.ProfileUrl, creator.AvatarUrl, kinds);
    }

    public async Task<PluginLivePage> GetLivePageAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscription(subscription);
        var result = await _client.InvokeAsync<PluginAccountPayload, PluginLivePage>(
            ProviderId,
            PluginMethods.FeedGetLive,
            new PluginAccountPayload(CreateContext(subscription)),
            cancellationToken).ConfigureAwait(false);
        if (result is null || result.Items is null)
            throw InvalidResult("Plugin live items are required.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in result.Items)
        {
            if (string.IsNullOrWhiteSpace(item.CreatorKey)
                || string.IsNullOrWhiteSpace(item.DisplayName)
                || !keys.Add(item.CreatorKey))
            {
                throw InvalidResult("Plugin live creators must have unique keys and names.");
            }
            RequireWebUrl(item.Url, "live URL");
            ValidateOptionalWebUrl(item.AvatarUrl, "live avatar URL");
            ValidateOptionalWebUrl(item.CoverUrl, "live cover URL");
            if (item.ViewerCount < 0)
                throw InvalidResult("Plugin live viewer count cannot be negative.");
        }

        return result;
    }

    public async Task<IReadOnlyList<PluginLiveItem>> GetLiveAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => (await GetLivePageAsync(subscription, cancellationToken).ConfigureAwait(false)).Items;

    public Task<ActionDescriptor[]> GetActionsAsync(
        FeedSubscription subscription,
        string creatorKey,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscription(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorKey);
        return _client.InvokeAsync<PluginCreatorReference, ActionDescriptor[]>(
            ProviderId,
            PluginMethods.CreatorGetActions,
            new PluginCreatorReference(CreateContext(subscription), creatorKey, metadata),
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> ExecuteAsync(
        FeedSubscription subscription,
        string creatorKey,
        ActionDescriptor action,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscription(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorKey);
        ArgumentNullException.ThrowIfNull(action);
        return _client.InvokeAsync<ExecuteCreatorActionPayload, IReadOnlyDictionary<string, string>>(
            ProviderId,
            PluginMethods.CreatorExecuteAction,
            new ExecuteCreatorActionPayload(
                CreateContext(subscription),
                creatorKey,
                action.ActionId,
                action.Payload),
            cancellationToken);
    }

    private void ValidateSubscription(FeedSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
    }

    private static PluginInvocationContext CreateContext(FeedSubscription subscription)
        => new(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options);

    private static void ValidateOptionalWebUrl(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value)) RequireWebUrl(value, field);
    }

    private static void RequireWebUrl(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw InvalidResult($"The plugin returned an invalid {field}.");
        }
    }

    private static PluginInvocationException InvalidResult(string message)
        => new("PLUGIN_RESULT_INVALID", message);
}
