using FeedPeek.Core.Models;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Pages;

public sealed partial class NotificationDetailPage : Page, IDisposable
{
    private NotificationRuleRow? _identity;
    private NotificationRuleRow? _current;
    private CreatorNotificationPolicy? _creatorPolicy;
    private CancellationTokenSource? _cancellation;
    private bool _loading = true;
    private bool _saving;
    private bool _scopeChanging;
    private bool _canOpenSource;

    public NotificationDetailPage()
    {
        InitializeComponent();
        InfoBarAutoDismiss.CollapseWhenClosed(StatusInfoBar);
        Loaded += async (_, _) =>
        {
            Dispose();
            _cancellation = new();
            await ReloadAsync(_cancellation.Token);
        };
        Unloaded += (_, _) => Dispose();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _identity = e.Parameter as NotificationRuleRow;
        if (_identity is not null)
            ShowHeader(_identity);
    }

    private bool Matches(NotificationRuleRow row)
    {
        if (_identity is null || row.Key != _identity.Key)
            return false;
        if (_identity.Creator is not null)
            return row.Creator?.IdentityKey == _identity.Creator.IdentityKey;
        // Existing sources stay visible even without a persisted notification entry.
        // Match source identity across virtual rows and their first explicit save.
        return row.Creator is null;
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        _loading = true;
        try
        {
            await using var session = new FeedPeekPanelSession();
            var data = await NotificationSettingsData.LoadAsync(session, token);
            if (!IsLoaded || token.IsCancellationRequested)
                return;
            _current = data.Rules.FirstOrDefault(Matches);
            var currentKey = _current?.Key;
            _creatorPolicy = _current?.Creator is null
                ? _current?.Policy
                : currentKey is { } key
                    ? data.Rules.FirstOrDefault(item => item.Creator is null
                        && item.Policy?.Key == key)?.Policy
                    : null;
            Render();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && !token.IsCancellationRequested)
            {
                _current = null;
                _creatorPolicy = null;
                Render();
                ShowError(exception);
            }
        }
        finally { _loading = false; }
    }

    private void ShowHeader(NotificationRuleRow row)
    {
        SourceName.Text = row.DisplayName;
        SourceKind.Text = row.Creator is not null || row.Policy is not null ? row.Detail : row.KindText;
        SourcePicture.ProfilePicture = row.IconSource;
        SourcePicture.Initials = row.Initials;
    }

    private void Render()
    {
        if (_current is not null)
            ShowHeader(_current);
        var policy = _current?.Creator is null ? _current?.Policy : null;
        var showUpdates = _current?.SupportsUpdates == true
            && (policy is null || policy.UpdatesScope == CreatorNotificationScope.AllFollowing);
        var showLive = _current?.SupportsLive == true
            && (policy is null || policy.LiveScope == CreatorNotificationScope.AllFollowing);
        UpdatesCard.Visibility = showUpdates ? Visibility.Visible : Visibility.Collapsed;
        LiveCard.Visibility = showLive ? Visibility.Visible : Visibility.Collapsed;
        LiveCard.Margin = new Thickness(0, showUpdates ? 4 : 0, 0, 0);
        NotificationTypesHeader.Visibility = showUpdates || showLive ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSwitch.IsOn = _current?.UpdatesEnabled == true;
        LiveSwitch.IsOn = _current?.LiveEnabled == true;
        UnavailableText.Visibility = _current?.IsAvailable == true ? Visibility.Collapsed : Visibility.Visible;
        UnavailableText.Text = L(_current is null ? "NotificationRuleMissing" : "NotificationRuleUnavailable",
            _current is null ? "此通知项已移除，请返回通知页面。" : "此来源暂不可用或不支持通知。启用插件或订阅后可恢复，已保存的开关保持不变。");
        CreatorPolicyPanel.Visibility = policy is null ? Visibility.Collapsed : Visibility.Visible;
        NotificationTypesHeader.Margin = new Thickness(0, policy is null ? 0 : 32, 0, 4);
        CreatorPersonalInfoText.Text = _current?.Creator is null
            ? string.Empty
            : GetCreatorPersonalInfo(_creatorPolicy);
        CreatorPersonalInfoText.Visibility = string.IsNullOrEmpty(CreatorPersonalInfoText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        if (policy is not null)
        {
            SelectScopeItem(policy);
            AllFollowingCreatorScopeItem.IsEnabled = HasCreatorFollowingCapability(_current);
            AddCreatorCard.Visibility = policy.ShouldShowSelectedCreators(policy.Key, _current?.Capabilities)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AddCreatorCard.Visibility = Visibility.Collapsed;
            AllFollowingCreatorScopeItem.IsEnabled = true;
        }
        var source = _current?.Source;
        // Disabled plugins are valid management targets; unavailable source editors are not.
        _canOpenSource = source is not null && (source.Plugin is null
            ? source.Source.ProviderId == FeedPeek.Rss.RssProviderInfo.ProviderId
            : source.Plugin is { IsEnabled: true, Manifest: not null, ErrorMessage: null });
        OpenSourceCard.IsEnabled = !_saving && _canOpenSource;
        ViewPluginCard.Visibility = source?.Plugin is not null ? Visibility.Visible : Visibility.Collapsed;
        NotificationManagementPanel.Visibility = _current?.Creator is not null ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = _current?.Creator is not null;
    }

    private static bool HasCreatorFollowingCapability(NotificationRuleRow? row)
        => row?.Source?.Plugin?.Manifest?.Capabilities.Contains("creator.following", StringComparer.Ordinal) == true;

    private static string GetCreatorPersonalInfo(CreatorNotificationPolicy? policy)
    {
        var global = AppSettings.NotificationTargetMode switch
        {
            NotificationTargetMode.None => L("NotificationCreatorPersonalNone",
                "当前全局模式为“不通知”，此作者设置暂不生效。"),
            NotificationTargetMode.AllFollowing => L("NotificationCreatorPersonalAllSources",
                "当前全局模式为“所有来源更新”，个人作者设置暂不参与筛选。"),
            _ => string.Empty,
        };
        if (policy is null || AppSettings.NotificationTargetMode != NotificationTargetMode.CustomCreators)
            return global;

        var scope = policy.UpdatesScope == policy.LiveScope
            ? policy.UpdatesScope == CreatorNotificationScope.AllFollowing
                ? L("NotificationCreatorPersonalAllScope",
                    "该来源使用全部已关注作者范围，个人作者开关会被来源范围覆盖。")
                : string.Empty
            : L("NotificationCreatorPersonalMixedScope",
                "该来源按更新和开播分别设置作者范围，个人开关仅在对应类型为自选时生效。");
        return string.Join(" ", new[] { global, scope }.Where(text => !string.IsNullOrEmpty(text)));
    }

    private void SelectScopeItem(CreatorNotificationPolicy policy)
    {
        MixedCreatorScopeItem.Visibility = policy.UpdatesScope != policy.LiveScope ? Visibility.Visible : Visibility.Collapsed;
        var item = policy.UpdatesScope == policy.LiveScope
            ? policy.UpdatesScope == CreatorNotificationScope.AllFollowing
                ? AllFollowingCreatorScopeItem
                : SelectedCreatorScopeItem
            : MixedCreatorScopeItem;
        var wasChanging = _scopeChanging;
        _scopeChanging = true;
        try { CreatorScopeComboBox.SelectedItem = item; }
        finally { _scopeChanging = wasChanging; }
    }

    private async void CreatorScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _saving || _scopeChanging || _cancellation is null
            || _current?.Creator is not null || _current?.Policy is not { } policy
            || CreatorScopeComboBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string scope)
            return;

        if (scope == "mixed")
        {
            SelectScopeItem(policy);
            return;
        }

        if (scope == "all" && !HasCreatorFollowingCapability(_current))
        {
            SelectScopeItem(policy);
            ShowError(new InvalidOperationException(L("NotificationCreatorAllFollowingUnavailable",
                "当前插件不支持完整关注列表，无法选择全部已关注作者。")));
            return;
        }

        if (scope is not ("all" or "selected"))
            return;

        var selected = scope == "all"
            ? CreatorNotificationScope.AllFollowing
            : CreatorNotificationScope.SelectedCreators;
        var token = _cancellation.Token;
        _saving = true;
        _scopeChanging = true;
        SetActionsEnabled(false);
        try
        {
            await using var session = new FeedPeekPanelSession();
            var fresh = await NotificationSettingsData.LoadAsync(session, token);
            var freshPolicy = fresh.Rules.FirstOrDefault(Matches)?.Policy
                ?? throw new InvalidOperationException(L("NotificationRuleMissing", "此通知项已移除，请返回通知页面。"));
            var edited = freshPolicy.WithScope(selected);
            await session.SaveCreatorNotificationPolicyAsync(edited, token);
            if (IsLoaded && !token.IsCancellationRequested)
                await ReloadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && !token.IsCancellationRequested)
            {
                ShowError(exception);
                await ReloadAsync(token);
            }
        }
        finally
        {
            _scopeChanging = false;
            _saving = false;
            if (IsLoaded && !token.IsCancellationRequested)
                SetActionsEnabled(true);
        }
    }

    private async void AddCreatorCard_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _saving || _current?.Creator is not null || _current?.Policy is null)
            return;
        if (_cancellation is null) return;
        var token = _cancellation.Token;
        _saving = true;
        SetActionsEnabled(false);
        try
        {
            await using var notificationSession = new FeedPeekNotificationSession(new CookieStore());
            var dialog = new NotificationAddDialog(this,
                (DataTemplate)Resources["NotificationCandidateTemplate"], notificationSession);
            await dialog.ShowAsync(token,
                new NotificationAddRequest(_current.ProviderId, _current.SourceId, AuthorsOnly: true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (IsLoaded && !token.IsCancellationRequested) ShowError(exception); }
        finally
        {
            if (IsLoaded && !token.IsCancellationRequested)
            {
                await ReloadAsync(token);
                SetActionsEnabled(true);
            }
            _saving = false;
        }
    }

    private async void NotificationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _saving || _current is null || _cancellation is null)
            return;
        var update = ReferenceEquals(sender, UpdatesSwitch);
        var enabled = update ? UpdatesSwitch.IsOn : LiveSwitch.IsOn;
        var token = _cancellation.Token;
        _saving = true;
        SetActionsEnabled(false);
        try
        {
            await using var session = new FeedPeekPanelSession();
            var data = await NotificationSettingsData.LoadAsync(session, token);
            var row = data.Rules.FirstOrDefault(Matches);
            if (row is null || (update ? !row.SupportsUpdates : !row.SupportsLive))
                throw new InvalidOperationException(L("NotificationUnavailable", "暂不可用"));
            if (row.Creator is { } creator)
            {
                var creators = AppSettings.CustomNotificationCreators.ToList();
                var current = creators.FirstOrDefault(item => item.IdentityKey == creator.IdentityKey)
                    ?? throw new InvalidOperationException(L("NotificationRuleMissing", "此通知项已移除，请返回通知页面。"));
                if (update) current.VideoNotificationsEnabled = enabled;
                else current.LiveNotificationsEnabled = enabled;
                AppSettings.CustomNotificationCreators = creators;
            }
            else if (row.Policy is { } policy)
            {
                var edited = policy with
                {
                    UpdatesEnabled = update ? enabled : policy.UpdatesEnabled,
                    LiveEnabled = update ? policy.LiveEnabled : enabled,
                };
                await session.SaveCreatorNotificationPolicyAsync(edited, token);
            }
            else if (row.Rule is { } rule)
            {
                // Refresh Feed IDs while preserving the other toggle and automatic/manual origin.
                var edited = rule with
                {
                    UpdateFeedIds = row.Capabilities!.UpdateFeedIds,
                    LiveFeedIds = row.Capabilities.LiveFeedIds,
                    UpdateNotificationsEnabled = update ? enabled : rule.UpdateNotificationsEnabled,
                    LiveNotificationsEnabled = update ? rule.LiveNotificationsEnabled : enabled,
                };
                await session.SaveSourceNotificationRuleAsync(edited, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (IsLoaded && !token.IsCancellationRequested) ShowError(exception); }
        finally
        {
            if (IsLoaded && !token.IsCancellationRequested)
            {
                await ReloadAsync(token);
                SetActionsEnabled(true);
            }
            _saving = false;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        UpdatesSwitch.IsEnabled = enabled;
        LiveSwitch.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled && _current?.Creator is not null;
        OpenSourceCard.IsEnabled = enabled && _canOpenSource;
        ViewPluginCard.IsEnabled = enabled;
        var isPolicy = _current?.Creator is null && _current?.Policy is not null;
        CreatorScopeCard.IsEnabled = enabled && isPolicy;
        AddCreatorCard.IsEnabled = enabled && AddCreatorCard.Visibility == Visibility.Visible;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _saving || _current?.Creator is null || _cancellation is null)
            return;
        var token = _cancellation.Token;
        _saving = true;
        SetActionsEnabled(false);
        try
        {
            if (_current.Creator is { } creator)
            {
                AppSettings.CustomNotificationCreators = AppSettings.CustomNotificationCreators
                    .Where(item => item.IdentityKey != creator.IdentityKey).ToList();
            }
            if (IsLoaded && !token.IsCancellationRequested)
            {
                if (Frame.CanGoBack) Frame.GoBack();
                else Frame.Navigate(typeof(NotificationSettingsPage));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && !token.IsCancellationRequested)
            {
                ShowError(exception);
                await ReloadAsync(token);
                SetActionsEnabled(true);
            }
        }
        finally { _saving = false; }
    }

    private async void OpenSource_Click(object sender, RoutedEventArgs e) => await NavigateToSettingsAsync(false);
    private async void ViewPlugin_Click(object sender, RoutedEventArgs e) => await NavigateToSettingsAsync(true);

    private async Task NavigateToSettingsAsync(bool plugin)
    {
        if (_loading || _saving || _cancellation is null)
            return;
        var token = _cancellation.Token;
        await ReloadAsync(token);
        if (!IsLoaded || token.IsCancellationRequested || _current?.Source is not { } source)
            return;
        if (plugin && source.Plugin is not null)
            Frame.Navigate(typeof(PluginDetailPage), source.Plugin);
        else if (!plugin && OpenSourceCard.IsEnabled)
        {
            Frame.Navigate(typeof(SourceDetailPage), source.Source);
        }
    }

    private void ShowError(Exception exception) => InfoBarAutoDismiss.Show(StatusInfoBar,
        L("NotificationSaveFailed", "操作失败，请重试。") + " " + exception.Message, InfoBarSeverity.Error);
    private static string L(string key, string fallback) => LocalizationHelper.GetString(key, fallback);

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
