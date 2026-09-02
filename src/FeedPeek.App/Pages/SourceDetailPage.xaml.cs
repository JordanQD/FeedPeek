using FeedPeek.Models;
using FeedPeek.Rss;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;

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
            SourceCard.Header = _source.DisplayName;
            SourceCard.Description = _source.Category;

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

            SourceBreadcrumb.ItemsSource = new[]
            {
                new BreadcrumbCrumb("来源", 30, double.NaN),
                new BreadcrumbCrumb(_source.DisplayName, 30, double.NaN),
            };
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SourceBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index >= 1) return; // 最后一项是当前来源，不需要导航
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(SourcesSettingsPage));
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
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

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
        DeleteSourceButton.IsEnabled = !isBusy;
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
