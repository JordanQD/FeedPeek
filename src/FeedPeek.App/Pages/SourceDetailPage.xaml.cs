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
        await LoadAsync();
    }

    private void SourceDetailPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

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

            if (string.Equals(_source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal))
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
                PluginNameText.Text = plugin?.DisplayName ?? _source.ProviderId;
                PluginVersionText.Text = plugin is null ? string.Empty : $"版本 {plugin.Version}";
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
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
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                && !string.Equals(uri.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new BitmapImage(uri);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= SourceDetailPage_Loaded;
        Unloaded -= SourceDetailPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        _session.Dispose();
    }
}
