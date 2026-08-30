using System;
using System.Collections.Generic;

namespace BiliRadar.Services;

internal sealed class LegacyItemIdTracker
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public bool Add(string id) => _ids.Add(id);

    public bool Contains(string id) => _ids.Contains(id);

    public bool Remove(string id) => _ids.Remove(id);

    public void Clear() => _ids.Clear();
}
