using CommunityToolkit.WinUI.Controls;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FeedPeek.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly System.Collections.ObjectModel.ObservableCollection<FeedSourceRow> _sources = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<AddedSourceEntry> _entries = [];
    private readonly FeedPeekPanelSession _session = new();
    private readonly PluginInstallationService _installations = new();
    private bool _disposed;

    public SourcesSettingsPage()
    {
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        AddedSourcesExpander.ItemsSource = _entries;
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
            await ReloadAddedSourcesAsync();
            await ReloadSourcesAsync();
            Trace($"reload done entries={_entries.Count} sources={_sources.Count}");
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    /// <summary>
    /// The combined list shows installed plugins first and then every added source, whether it came from
    /// RSS, Atom or a plugin.
    /// </summary>
    private async Task ReloadAddedSourcesAsync()
    {
        var plugins = await _session.GetPluginsAsync();
        var sources = await _session.GetSourcesAsync();
        _entries.Clear();
        foreach (var plugin in plugins.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture))
        {
            _entries.Add(AddedSourceEntry.ForPlugin(new PluginSettingsRow(plugin)));
        }

        foreach (var source in sources)
        {
            _entries.Add(AddedSourceEntry.ForSource(source));
        }
    }

    private void AddedSourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard { DataContext: AddedSourceEntry entry })
        {
            return;
        }

        switch (entry.Tag)
        {
            case PluginSettingsRow plugin:
                Frame.Navigate(typeof(PluginDetailPage), plugin);
                break;
            case FeedSourceRow source:
                Frame.Navigate(typeof(SourceDetailPage), source);
                break;
        }
    }

    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard { DataContext: FeedSourceRow source }) return;
        Frame.Navigate(typeof(SourceDetailPage), source);
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
            ShowStatus("请输入网站或 RSS 地址。", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            Trace("add start: " + address);
            var source = await _session.AddSourceFromAddressAsync(address);
            EndpointTextBox.Text = string.Empty;
            Trace($"add done provider={source.ProviderId} category={source.Category}");
            var kind = string.Equals(source.Category, "插件", StringComparison.Ordinal) ? "网站" : source.Category;
            ShowStatus($"{kind} 来源已添加。", InfoBarSeverity.Success);
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
        _sources.Clear();
        foreach (var source in sources)
        {
            _sources.Add(source);
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
        => EmptyPanel.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
        AddedSourcesExpander.ItemsSource = null!;
        _sources.Clear();
        _entries.Clear();
        _session.Dispose();
    }
}
