using FeedPeek.Services;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FeedPeek.Models;

internal sealed class IncrementalFeedItemCollection(
    Func<CancellationToken, Task<FeedItemLoadResult>> loadMore) :
    ObservableCollection<FeedItemRow>,
    ISupportIncrementalLoading
{
    private readonly Func<CancellationToken, Task<FeedItemLoadResult>> _loadMore = loadMore
        ?? throw new ArgumentNullException(nameof(loadMore));
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public bool HasMoreItems { get; private set; }

    public void Replace(IReadOnlyList<FeedItemRow> items, bool hasMore)
    {
        HasMoreItems = hasMore;
        Clear();
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadMoreCoreAsync().AsAsyncOperation();

    public void StopLoading() => HasMoreItems = false;

    private async Task<LoadMoreItemsResult> LoadMoreCoreAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            if (!HasMoreItems)
            {
                return new LoadMoreItemsResult { Count = 0 };
            }

            var existingKeys = this.Select(item => item.Key).ToHashSet();
            var result = await _loadMore(CancellationToken.None);
            Replace(result.Items, result.HasMore);
            var addedCount = result.Items.Count(item => !existingKeys.Contains(item.Key));
            return new LoadMoreItemsResult { Count = checked((uint)addedCount) };
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
