namespace FeedPeek.Core.Feeds;

public interface IFeedProviderResolver
{
    IFeedProvider Resolve(string providerId);
}

public sealed class FeedProviderRegistry : IFeedProviderResolver, IDisposable
{
    private readonly Dictionary<string, IFeedProvider> _providers;

    public FeedProviderRegistry(IEnumerable<IFeedProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.ProviderId, StringComparer.Ordinal);
    }

    public void Register(IFeedProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.ProviderId] = provider;
    }

    public bool Unregister(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.Remove(providerId);
    }

    public IFeedProvider Resolve(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException($"Feed provider '{providerId}' is not registered.");
    }

    public void Dispose()
    {
        foreach (var provider in _providers.Values.OfType<IDisposable>())
        {
            provider.Dispose();
        }
    }
}
