using CommunityToolkit.WinUI.Controls;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FeedPeek.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly ObservableCollection<FeedSourceRow> _sources = [];
    private readonly ObservableCollection<PluginSettingsRow> _plugins = [];
    private readonly FeedPeekPanelSession _session = new();
    private readonly PluginInstallationService _installations = new();
    private bool _disposed;

    public SourcesSettingsPage()
    {
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        PluginsExpander.ItemsSource = _plugins;
        Loaded += SourcesSettingsPage_Loaded;
        Unloaded += SourcesSettingsPage_Unloaded;
    }

    private async void SourcesSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        await ReloadAsync();
    }

    private void SourcesSettingsPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async Task ReloadAsync()
    {
        try
        {
            await _session.InitializeAsync();
            await ReloadPluginsAsync();
            await ReloadSourcesAsync();
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task ReloadPluginsAsync()
    {
        var entries = await _session.GetPluginsAsync();
        _plugins.Clear();
        foreach (var entry in entries.OrderBy(item => item.DisplayName))
            _plugins.Add(new PluginSettingsRow(entry));
    }

    private void PluginCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard { DataContext: PluginSettingsRow plugin }) return;
        Frame.Navigate(typeof(PluginDetailPage), plugin.PluginId);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddRssSourceAsync();

    private async void EndpointTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddRssSourceAsync();
    }

    private async Task AddRssSourceAsync()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowStatus("请输入网站或 RSS 地址。", InfoBarSeverity.Warning);
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _session.AddRssSubscriptionAsync(endpoint);
            EndpointTextBox.Text = string.Empty;
            await ReloadSourcesAsync();
            ShowStatus("RSS 来源已添加。", InfoBarSeverity.Success);
        });
    }

    private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunBusyAsync(async () =>
        {
            var installed = await _installations.InstallAsync(folder.Path);
            await ReloadPluginsAsync();
            ShowStatus($"已安装 {installed.DisplayName}。", InfoBarSeverity.Success);
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedSourceRow source }) return;
        await RunBusyAsync(async () =>
        {
            await _session.DeleteSourceAsync(source);
            _sources.Remove(source);
            UpdateEmptyState();
            ShowStatus("来源已删除。", InfoBarSeverity.Success);
        });
    }

    private async Task ReloadSourcesAsync()
    {
        var sources = await _session.GetSourcesAsync();
        _sources.Clear();
        foreach (var source in sources) _sources.Add(source);
        UpdateEmptyState();
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        SetBusy(true);
        try { await operation(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
        => ShowStatus(status.Message, status.Severity);

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void SetBusy(bool isBusy)
    {
        AddButton.IsEnabled = !isBusy;
        EndpointTextBox.IsEnabled = !isBusy;
        InstallPluginButton.IsEnabled = !isBusy;
    }

    private void UpdateEmptyState()
        => EmptyPanel.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= SourcesSettingsPage_Loaded;
        Unloaded -= SourcesSettingsPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        SourcesListView.ItemsSource = null;
        PluginsExpander.ItemsSource = null!;
        _sources.Clear();
        _plugins.Clear();
        _session.Dispose();
    }
}
