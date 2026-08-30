using BiliRadar.Models;
using BiliRadar.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.System;

namespace BiliRadar.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly ObservableCollection<FeedSourceRow> _sources = [];
    private readonly FeedPeekPanelSession _session = new();
    private bool _disposed;

    public SourcesSettingsPage()
    {
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        Loaded += SourcesSettingsPage_Loaded;
        Unloaded += SourcesSettingsPage_Unloaded;
    }

    private async void SourcesSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        await ReloadSourcesAsync();
    }

    private void SourcesSettingsPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddSourceAsync();

    private async void EndpointTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddSourceAsync();
    }

    private async Task AddSourceAsync()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowStatus("请输入网站或 RSS 地址。", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            await _session.AddRssSubscriptionAsync(endpoint);
            EndpointTextBox.Text = string.Empty;
            await ReloadSourcesAsync();
            ShowStatus("RSS 来源已添加。", InfoBarSeverity.Success);
        }
        catch
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedSourceRow source }) return;
        SetBusy(true);
        try
        {
            await _session.DeleteSourceAsync(source);
            _sources.Remove(source);
            UpdateEmptyState();
            ShowStatus("来源已删除。", InfoBarSeverity.Success);
        }
        catch
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadSourcesAsync()
    {
        try
        {
            await _session.InitializeAsync();
            var sources = await _session.GetSourcesAsync();
            _sources.Clear();
            foreach (var source in sources) _sources.Add(source);
            UpdateEmptyState();
        }
        catch
        {
        }
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
        _sources.Clear();
        _session.Dispose();
    }
}
