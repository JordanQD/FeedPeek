using System;
using Windows.Storage;

namespace BiliRadar.Services;

public sealed class CookieStore
{
    private const string ContainerName = "BiliRadar";
    private const string CookieKey = "BiliCookie";

    public bool HasCookie => !string.IsNullOrWhiteSpace(GetCookieString());

    public string GetCookieString()
    {
        var container = GetContainer();
        return container.Values.TryGetValue(CookieKey, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }

    public void SaveCookieString(string cookie)
    {
        var normalized = CookieNormalizer.Normalize(cookie);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            Clear();
            return;
        }

        GetContainer().Values[CookieKey] = normalized;
    }

    public void Clear()
    {
        GetContainer().Values.Remove(CookieKey);
    }

    private static ApplicationDataContainer GetContainer()
        => ApplicationData.Current.LocalSettings.CreateContainer(ContainerName, ApplicationDataCreateDisposition.Always);

}
