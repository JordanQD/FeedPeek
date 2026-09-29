using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Rss;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FeedPeek.Pages;

public sealed partial class SourceDetailPage : Page, IDisposable
{
    private readonly FeedPeekPanelSession _session = new();
    private FeedSourceRow? _source;
    private PluginSettingsRow? _plugin;
    private bool _disposed;

    public SourceDetailPage()
    {
        InitializeComponent();
        Loaded += SourceDetailPage_Loaded;
        Unloaded += SourceDetailPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _source = e.Parameter as FeedSourceRow;
    }

    private async void SourceDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        AppSettings.WebsiteSettingsChanged += OnWebsiteSettingsChanged;
        await LoadAsync();
    }

    private void OnWebsiteSettingsChanged(object? sender, EventArgs e)
    {
        if (_source is null || _disposed)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || _source is null)
            {
                return;
            }

            try
            {
                var sources = await _session.GetSourcesAsync();
                var row = sources.FirstOrDefault(item =>
                    string.Equals(item.ProviderId, _source.ProviderId, StringComparison.Ordinal)
                    && string.Equals(item.SourceId, _source.SourceId, StringComparison.Ordinal));
                if (row is null || _disposed)
                {
                    return;
                }

                _source = row;
                WebsiteBox.Text = GetSourceWebsite(row)
                    ?? (_plugin is null ? null : AppSettings.GetPluginWebsite(_plugin.PluginId)
                        ?? _plugin.Manifest?.Website)
                    ?? string.Empty;
            }
            catch
            {
                // Keep the current text if a refresh fails.
            }
        });
    }

    private void SourceDetailPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async void SaveWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _disposed)
        {
            return;
        }

        try
        {
            await _session.UpdateSourceWebsiteAsync(
                _source.ProviderId,
                _source.SourceId,
                WebsiteBox.Text);
            var trimmed = WebsiteBox.Text?.Trim();
            WebsiteBox.Text = trimmed ?? string.Empty;
            ShowStatus("已保存打开的网页。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LoadAsync()
    {
        if (_source is null)
        {
            ShowStatus("缺少来源信息。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await _session.InitializeAsync();

            HeroName.Text = _source.DisplayName;
            HeroCategory.Text = _source.Category;
            IconPicture.ProfilePicture = CreateImageSource(_source.IconUrl);
            IconPicture.Initials = GetInitials(_source.DisplayName);
            var isRss = string.Equals(_source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal);
            if (isRss)
            {
                RssCard.Visibility = Visibility.Visible;
                PluginCard.Visibility = Visibility.Collapsed;
                EndpointTextBox.Text = _source.Endpoint;
            }
            else
            {
                RssCard.Visibility = Visibility.Collapsed;
                PluginCard.Visibility = Visibility.Visible;
                var plugins = await _session.GetPluginsAsync();
                var plugin = plugins.FirstOrDefault(item =>
                    string.Equals(item.PluginId, _source.ProviderId, StringComparison.Ordinal));
                _plugin = plugin is null ? null : new PluginSettingsRow(plugin);
                PluginCard.IsClickEnabled = _plugin is not null;
                HeroCategory.Text = new SourceSettingsRow(_source, _plugin).Detail;
                PluginNameText.Text = _plugin?.DisplayName ?? _source.ProviderId;
                PluginVersionText.Text = plugin is null ? string.Empty : $"版本 {plugin.Version}";
            }

            var sourceWebsite = GetSourceWebsite(_source);
            var hasContentFeed = _source.Feeds.Any(feed => !string.Equals(feed.Kind, "live", StringComparison.Ordinal));
            WebsiteCard.Visibility = isRss || (hasContentFeed
                && (!string.IsNullOrWhiteSpace(sourceWebsite)
                    || !string.IsNullOrWhiteSpace(_plugin?.Manifest?.Website)))
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            WebsiteCard.Margin = isRss ? new Thickness(0, 4, 0, 0) : new Thickness(0);
            PluginCard.Margin = !isRss && WebsiteCard.Visibility == Visibility.Visible
                ? new Thickness(0, 4, 0, 0)
                : new Thickness(0);
            WebsiteBox.Text = sourceWebsite
                ?? (isRss || _plugin is null ? null : AppSettings.GetPluginWebsite(_plugin.PluginId)
                    ?? _plugin.Manifest?.Website)
                ?? string.Empty;
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void PluginCard_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is not null) Frame.Navigate(typeof(PluginDetailPage), _plugin);
    }

    private async void PickIconFile_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        var picker = new FileOpenPicker();
        picker.ViewMode = PickerViewMode.Thumbnail;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".gif", ".webp" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        var hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await RunBusyAsync(async () =>
        {
            var iconUri = await StoreIconFileAsync(file);
            await _session.UpdateSourceIconAsync(_source.ProviderId, _source.SourceId, iconUri);
            IconPicture.ProfilePicture = CreateImageSource(iconUri);
            ShowStatus("图标已更新。", InfoBarSeverity.Success);
        });
    }

    private async void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        await RunBusyAsync(async () =>
        {
            await _session.UpdateSourceIconAsync(_source.ProviderId, _source.SourceId, null);
            IconPicture.ProfilePicture = null;
            ShowStatus("已恢复默认图标。", InfoBarSeverity.Success);
        });
    }

    private async Task<string> StoreIconFileAsync(StorageFile file)
    {
        var folder = await ApplicationData.Current.LocalFolder
            .CreateFolderAsync("source-icons", CreationCollisionOption.OpenIfExists);
        var ext = Path.GetExtension(file.Name);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".png";
        }

        // 用唯一文件名，避免覆盖被 BitmapImage 占用的旧文件导致“文件被占用”错误。
        var fileName = $"{SanitizeFileName(_source!.SourceId)}_{Guid.NewGuid():N}{ext}";
        await file.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
        return $"ms-appdata:///local/source-icons/{fileName}";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private void IconGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        => EditOverlay.Visibility = Visibility.Visible;

    private void IconGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        => EditOverlay.Visibility = Visibility.Collapsed;

    private static string GetInitials(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return new string(trimmed
            .Take(2)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string? GetSourceWebsite(FeedSourceRow source)
    {
        if (source.Source.Options is not { } options)
        {
            return null;
        }

        foreach (var key in new[] { "source.website", "website", "homepage", "home" })
        {
            if (options.TryGetValue(key, out var website)
                && Uri.TryCreate(website, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    private async void SaveEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowStatus("请输入有效的源链接。", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _session.UpdateRssEndpointAsync(_source.ProviderId, _source.SourceId, endpoint);
            ShowStatus("源链接已保存。", InfoBarSeverity.Success);
        });
    }

    private async void DeleteSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除来源",
            Content = $"删除 {_source.DisplayName}？该来源的本地缓存也会删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunBusyAsync(async () =>
        {
            await _session.DeleteSourceAsync(_source);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Frame.CanGoBack) Frame.GoBack();
            });
        });
    }

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
        => ShowStatus(status.Message, status.Severity);

    private void ShowStatus(string message, InfoBarSeverity severity)
        => InfoBarAutoDismiss.Show(StatusInfoBar, message, severity);

    private async Task RunBusyAsync(Func<Task> operation)
    {
        SetBusy(true);
        try { await operation(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool isBusy)
    {
        EndpointTextBox.IsEnabled = !isBusy;
        SaveEndpointButton.IsEnabled = !isBusy;
        IconButton.IsEnabled = !isBusy;
        DeleteSourceButton.IsEnabled = !isBusy;
    }

    private static ImageSource? CreateImageSource(string? imageUrl)
        => ImageSourceCache.GetSettingsAvatar(imageUrl);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= SourceDetailPage_Loaded;
        Unloaded -= SourceDetailPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        AppSettings.WebsiteSettingsChanged -= OnWebsiteSettingsChanged;
        _session.Dispose();
    }
}
