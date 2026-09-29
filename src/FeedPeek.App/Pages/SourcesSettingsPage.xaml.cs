using CommunityToolkit.WinUI.Controls;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FeedPeek.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly ObservableCollection<SourceSettingsRow> _sources = [];
    private readonly ObservableCollection<PluginSettingsRow> _plugins = [];
    private readonly FeedPeekPanelSession _session = new();
    private readonly PluginInstallationService _installations = new();
    private bool _disposed;

    public SourcesSettingsPage()
    {
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        PluginsListView.ItemsSource = _plugins;
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
            Trace("reload start");
            await _session.InitializeAsync();
            await ReloadSourcesAsync();
            Trace($"reload done sources={_sources.Count} plugins={_plugins.Count}");
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard { DataContext: SourceSettingsRow source }) return;
        if (source.OpensPluginDetails && source.Plugin is not null)
        {
            Frame.Navigate(typeof(PluginDetailPage), source.Plugin);
        }
        else
        {
            Frame.Navigate(typeof(SourceDetailPage), source.Source);
        }
    }

    private void PluginCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard { DataContext: PluginSettingsRow plugin })
        {
            Frame.Navigate(typeof(PluginDetailPage), plugin);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddSourceFromAddressAsync();

    private async void EndpointTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddSourceFromAddressAsync();
    }

    private async Task AddSourceFromAddressAsync()
    {
        var address = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            ShowStatus("请输入 RSS 地址或直播间链接。", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            Trace("add start: " + address);
            var source = await _session.AddSourceFromAddressAsync(address);
            EndpointTextBox.Text = string.Empty;
            Trace($"add done provider={source.ProviderId} category={source.Category}");
            ShowStatus("订阅已添加。", InfoBarSeverity.Success);
        });

        // Rebuild the lists after this handler has unwound, and never touch them once the page is gone:
        // replacing bound items while a control inside them is still dispatching its event, or after the
        // page was disposed, throws inside WinRT.
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed) return;
            try { await ReloadAsync(); }
            catch (Exception ex) { if (!_disposed) ShowStatus(ex.Message, InfoBarSeverity.Error); }
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
            Trace("install start: " + folder.Path);
            var installed = await _installations.InstallAsync(folder.Path);
            await ReloadAsync();
            ShowStatus($"已安装 {installed.DisplayName}。", InfoBarSeverity.Success);
        });
    }

    private async Task ReloadSourcesAsync()
    {
        var sources = await _session.GetSourcesAsync();
        var pluginRows = (await _session.GetPluginsAsync())
            .Select(plugin => new PluginSettingsRow(plugin))
            .OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCulture)
            .ToArray();
        var plugins = pluginRows.ToDictionary(plugin => plugin.PluginId, StringComparer.Ordinal);
        if (_disposed) return;
        _sources.Clear();
        foreach (var row in sources
            .Select(source => new SourceSettingsRow(source,
                plugins.TryGetValue(source.ProviderId, out var plugin) ? plugin : null))
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCulture))
        {
            _sources.Add(row);
        }

        _plugins.Clear();
        foreach (var plugin in pluginRows)
        {
            _plugins.Add(plugin);
        }

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
        => InfoBarAutoDismiss.Show(StatusInfoBar, message, severity);

    private void SetBusy(bool isBusy)
    {
        AddButton.IsEnabled = !isBusy;
        EndpointTextBox.IsEnabled = !isBusy;
        InstallPluginButton.IsEnabled = !isBusy;
    }

    private void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginsEmptyPanel.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Step markers so a crash report can name the last step that ran (Debug output window).</summary>
    private static void Trace(string message)
        => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Sources] {message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= SourcesSettingsPage_Loaded;
        Unloaded -= SourcesSettingsPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        SourcesListView.ItemsSource = null;
        PluginsListView.ItemsSource = null;
        _sources.Clear();
        _plugins.Clear();
        _session.Dispose();
    }
}
