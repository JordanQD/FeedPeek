using CommunityToolkit.WinUI.Controls;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Rss;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
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
    private readonly ObservableCollection<SourceSettingsGroup> _groups = [];
    private readonly Dictionary<string, bool> _expandedPlugins = new(StringComparer.Ordinal);
    private readonly FeedPeekPanelSession _session = new();
    private readonly PluginInstallationService _installations = new();
    private bool _disposed;
    private int _reloadVersion;

    public SourcesSettingsPage()
    {
        InitializeComponent();
        SourceGroupsListView.ItemsSource = _groups;
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
            Trace($"reload done groups={_groups.Count}");
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard { DataContext: SourceSettingsRow source }) return;
        Frame.Navigate(typeof(SourceDetailPage), source.Source);
    }

    private void PluginCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginSettingsRow plugin })
        {
            Frame.Navigate(typeof(PluginDetailPage), plugin);
        }
    }

    private void PluginExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsExpander { DataContext: SourceSettingsGroup group } expander)
            expander.IsExpanded = _expandedPlugins.GetValueOrDefault(group.ProviderId);
    }

    private void PluginExpander_StateChanged(object sender, object e)
    {
        if (sender is SettingsExpander { DataContext: SourceSettingsGroup group } expander && expander.IsLoaded)
            _expandedPlugins[group.ProviderId] = expander.IsExpanded;
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
        var version = ++_reloadVersion;
        var sources = await _session.GetSourcesAsync();
        var pluginRows = (await _session.GetPluginsAsync())
            .Select(plugin => new PluginSettingsRow(plugin))
            .OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCulture)
            .ToArray();
        var plugins = pluginRows.ToDictionary(plugin => plugin.PluginId, StringComparer.Ordinal);
        if (_disposed || version != _reloadVersion) return;
        var rows = sources
            .Select(source => new SourceSettingsRow(source,
                plugins.TryGetValue(source.ProviderId, out var plugin) ? plugin : null))
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCulture).ToArray();
        var pluginSources = rows.Where(row => row.Source.ProviderId != RssProviderInfo.ProviderId)
            .GroupBy(row => row.Source.ProviderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        // Start with installed plugins too: empty/disabled/error entries still need a settings route.
        var groups = pluginRows.Select(plugin => plugin.PluginId).Concat(pluginSources.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(providerId => new SourceSettingsGroup(providerId,
                plugins.GetValueOrDefault(providerId), null,
                pluginSources.GetValueOrDefault(providerId) ?? []))
            .OrderBy(group => group.DisplayName, StringComparer.CurrentCulture).ToList();
        groups.AddRange(rows.Where(row => row.Source.ProviderId == RssProviderInfo.ProviderId)
            .Select(row => new SourceSettingsGroup(row.Source.ProviderId, null, row, [])));
        foreach (var stale in _expandedPlugins.Keys.Where(providerId =>
            !groups.Any(group => group.Source is null && group.ProviderId == providerId)).ToArray())
            _expandedPlugins.Remove(stale);
        _groups.Clear();
        foreach (var group in groups) _groups.Add(group);

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
        if (!_disposed) InfoBarAutoDismiss.Show(StatusInfoBar, message, severity);
    }

    private void SetBusy(bool isBusy)
    {
        if (_disposed) return;
        AddButton.IsEnabled = !isBusy;
        EndpointTextBox.IsEnabled = !isBusy;
        InstallPluginButton.IsEnabled = !isBusy;
    }

    private void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceGroupsContainer.MinHeight = _groups.Count == 0 ? 180 : 0;
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
        SourceGroupsListView.ItemsSource = null;
        _groups.Clear();
        _expandedPlugins.Clear();
        _session.Dispose();
    }
}
