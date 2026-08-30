using System;
using System.Collections.Generic;

namespace BiliRadar.Services;

internal sealed class LegacyVideoPageBuffer<T>
{
    private readonly int _pageSize;
    private readonly Queue<T> _pendingItems = new();

    public LegacyVideoPageBuffer(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        _pageSize = pageSize;
    }

    public int Count => _pendingItems.Count;

    public void Clear() => _pendingItems.Clear();

    public IReadOnlyList<T> TakePage(IReadOnlyList<T> items)
    {
        if (items.Count <= _pageSize)
        {
            return items;
        }

        var page = new List<T>(_pageSize);
        for (var index = 0; index < items.Count; index++)
        {
            if (index < _pageSize)
            {
                page.Add(items[index]);
            }
            else
            {
                _pendingItems.Enqueue(items[index]);
            }
        }

        return page;
    }

    public IReadOnlyList<T> TakePendingPage()
    {
        var count = Math.Min(_pageSize, _pendingItems.Count);
        var page = new List<T>(count);
        for (var index = 0; index < count; index++)
        {
            page.Add(_pendingItems.Dequeue());
        }

        return page;
    }
}
