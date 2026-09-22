using FeedPeek.Core.Models;
using FeedPeek.Services;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FeedPeek.Models;

/// <summary>
/// Virtualized list source with true incremental append. First page is set via
/// <see cref="Reset"/>; scroll-near-end loads call <see cref="LoadMoreItemsAsync"/> and only
/// append unseen keys so existing rows and their bitmaps are not rebuilt.
/// </summary>
internal sealed class IncrementalFeedItemCollection(
    Func<int, CancellationToken, Task<FeedItemLoadResult>> loadMore) :
    ObservableCollection<FeedItemRow>,
    ISupportIncrementalLoading
{
    private readonly Func<int, CancellationToken, Task<FeedItemLoadResult>> _loadMore = loadMore
        ?? throw new ArgumentNullException(nameof(loadMore));
    private readonly HashSet<(string ProviderId, string SourceId, string ItemId)> _keys = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public bool HasMoreItems { get; private set; }

    public void Reset(IReadOnlyList<FeedItemRow> items, bool hasMore)
    {
        HasMoreItems = hasMore;
        _keys.Clear();
        Clear();
        AppendCore(items);
    }

    public void Append(IReadOnlyList<FeedItemRow> items, bool hasMore)
    {
        HasMoreItems = hasMore;
        AppendCore(items);
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadMoreCoreAsync().AsAsyncOperation();

    public void StopLoading() => HasMoreItems = false;

    private void AppendCore(IReadOnlyList<FeedItemRow> items)
    {
        foreach (var item in items)
        {
            // Logical key: the same video can appear under updates / history / view-later feed ids.
            if (_keys.Add((item.ProviderId, item.SourceId, item.ItemId)))
            {
                Add(item);
            }
        }
    }

    private async Task<LoadMoreItemsResult> LoadMoreCoreAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            if (!HasMoreItems)
            {
                return new LoadMoreItemsResult { Count = 0 };
            }

            var before = Count;
            try
            {
                var result = await _loadMore(Count, CancellationToken.None);

                // ObservableCollection must be mutated on the UI thread.
                var tcs = new TaskCompletionSource<LoadMoreItemsResult>();
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                void Finish()
                {
                    try
                    {
                        Append(result.Items, result.HasMore);
                        var added = (uint)Math.Max(0, Count - before);
                        if (added == 0)
                        {
                            HasMoreItems = false;
                        }
                        tcs.SetResult(new LoadMoreItemsResult { Count = added });
                    }
                    catch (Exception ex)
                    {
                        HasMoreItems = false;
                        tcs.SetException(ex);
                    }
                }

                if (dispatcher is null)
                {
                    Finish();
                }
                else
                {
                    dispatcher.TryEnqueue(Finish);
                }

                return await tcs.Task;
            }
            catch (Exception)
            {
                HasMoreItems = false;
                return new LoadMoreItemsResult { Count = 0 };
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
