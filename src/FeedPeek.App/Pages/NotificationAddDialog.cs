using FeedPeek.Core.Models;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.PluginHost;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace FeedPeek.Pages;

internal sealed class NotificationAddDialog
{
    private readonly Page _owner;
    private readonly DataTemplate _template;
    private readonly FeedPeekNotificationSession NotificationSession;
    private bool IsLoaded => _owner.IsLoaded;
    private XamlRoot XamlRoot => _owner.XamlRoot;
    private Frame Frame => _owner.Frame;

    public NotificationAddDialog(Page owner, DataTemplate template, FeedPeekNotificationSession session)
    {
        _owner = owner;
        _template = template;
        NotificationSession = session;
    }
    private static string N(string key, string fallback) => LocalizationHelper.GetString(key, fallback);

    public async Task ShowAsync(CancellationToken pageToken, NotificationAddRequest? request = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        var token = cancellation.Token;
        await using var panel = new FeedPeekPanelSession();
        var data = await NotificationSettingsData.LoadAsync(panel, token);
        var sources = await panel.GetSourcesAsync(token);
        var plugins = (await panel.GetPluginsAsync(token)).Select(entry => new PluginSettingsRow(entry)).ToArray();
        if (!IsLoaded || token.IsCancellationRequested) return;
        var existing = data.Rules.Where(row => row.Creator is null).ToDictionary(row => row.Key);
        var available = data.AvailableSources.ToDictionary(row => row.Key);
        var local = sources.Select(source =>
        {
            var key = new SourceNotificationKey(source.ProviderId, source.SourceId);
            var row = existing.GetValueOrDefault(key) ?? available.GetValueOrDefault(key)
                ?? new NotificationRuleRow(new SourceSettingsRow(source,
                    plugins.FirstOrDefault(plugin => plugin.PluginId == key.ProviderId)), null, null, null);
            return new NotificationCandidateRow { Source = row, IsExisting = existing.ContainsKey(key) };
        }).ToList();
        var authors = new List<NotificationCandidateRow>();
        var visible = new ObservableCollection<NotificationCandidateRow>();
        var search = new AutoSuggestBox { QueryIcon = new SymbolIcon(Symbol.Find),
            PlaceholderText = request?.AuthorsOnly == true
                ? N("NotificationSearchPlaceholder", "搜索已关注作者，或输入作者 ID、链接查询")
                : N("NotificationSourceSearchPlaceholder", "搜索已添加的来源"),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, N("NotificationSearchName", "搜索通知对象"));
        var sourcePicker = new ComboBox { Header = N("CreatorQuerySource", "作者所属来源"),
            PlaceholderText = N("CreatorChooseSource", "选择作者所属来源"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(sourcePicker, N("CreatorQuerySource", "作者所属来源"));
        var list = new ListView { ItemsSource = visible, SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 260, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = _template };
        list.ItemContainerStyle = new Style(typeof(ListViewItem));
        list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        AutomationProperties.SetName(list, N("NotificationCandidates", "通知对象"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var info = new InfoBar { IsClosable = true, Visibility = Visibility.Collapsed };
        InfoBarAutoDismiss.CollapseWhenClosed(info);
        var types = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        var updates = new ToggleSwitch();
        var live = new ToggleSwitch();
        AutomationProperties.SetName(updates, N("NotificationUpdates", "更新通知"));
        AutomationProperties.SetName(live, N("NotificationLive", "开播通知"));
        var updatesCard = new SettingsCard { Header = N("NotificationUpdates", "更新通知"), Content = updates };
        var liveCard = new SettingsCard { Header = N("NotificationLive", "开播通知"), Content = live };
        types.Children.Add(updatesCard); types.Children.Add(liveCard);
        var selectionNote = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        types.Children.Add(selectionNote);
        var body = new StackPanel { Spacing = 12, MaxWidth = 520, MinWidth = 0 };
        var queryFields = new Grid();
        queryFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queryFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queryFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(sourcePicker, 1); Grid.SetRow(list, 2);
        list.Margin = new Thickness(0, 12, 0, 0);
        queryFields.Children.Add(search); queryFields.Children.Add(sourcePicker); queryFields.Children.Add(list);
        body.Children.Add(queryFields);
        // Conditional status elements share one child so closed InfoBars do not add blank rows.
        var messages = new Grid(); messages.Children.Add(status); messages.Children.Add(info);
        var selectionContent = new Grid();
        selectionContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        selectionContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(types, 1); types.Margin = new Thickness(0, 12, 0, 0);
        selectionContent.Children.Add(messages); selectionContent.Children.Add(types);
        body.Children.Add(selectionContent);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(160, (XamlRoot?.Size.Height ?? 800) - 220) };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = request?.AuthorsOnly == true
                ? N("NotificationAuthorSearchTitle", "添加作者通知") : N("NotificationAddTitle", "添加通知"),
            Content = scroll, PrimaryButtonText = N("SaveButton", "保存"), CloseButtonText = N("CancelButton", "取消"),
            DefaultButton = ContentDialogButton.None, IsPrimaryButtonEnabled = false };
        NotificationCandidateRow? selected = null;
        IReadOnlyList<NotificationCreatorTarget> targets = [];
        var closed = false;
        var saving = false;
        var routing = false;
        var updatingCandidates = false;
        var version = 0;
        var membershipVersion = 0;
        CancellationTokenSource? queryCancellation = null;
        Action? navigation = null;
        NotificationCreatorTarget? SelectedTarget() => (sourcePicker.SelectedItem as ComboBoxItem)?.Tag as NotificationCreatorTarget;
        bool Current(int expected) => !closed && !token.IsCancellationRequested && IsLoaded && version == expected;
        void ShowError(Exception exception)
        {
            var code = (exception as PluginInvocationException)?.Code;
            info.Severity = InfoBarSeverity.Warning;
            info.Message = N(code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "AUTH_PROFILE_MISSING"
                ? "CreatorLoginRequiredDetail" : "CreatorQueryFailedDetail", "查询失败，请重试或检查插件设置。") + " " + exception.Message;
            info.IsOpen = true;
        }
        void InvalidateQuery()
        {
            version++; queryCancellation?.Cancel();
        }
        void Filter()
        {
            var input = search.Text.Trim();
            var candidates = (request?.AuthorsOnly == true
                ? string.IsNullOrEmpty(input) ? Enumerable.Empty<NotificationCandidateRow>() : authors
                : local.AsEnumerable())
                .Where(row => string.IsNullOrEmpty(input) || row.DisplayName.Contains(input, StringComparison.CurrentCultureIgnoreCase)
                    || row.Detail.Contains(input, StringComparison.CurrentCultureIgnoreCase)
                    || row.Creator?.CreatorKey == input || row.Creator?.ProfileUrl == input);
            var filtered = candidates.ToArray();
            updatingCandidates = true;
            try
            {
                visible.Clear(); foreach (var row in filtered) visible.Add(row);
                list.SelectedItem = selected is not null && filtered.Contains(selected) ? selected : null;
            }
            finally { updatingCandidates = false; }
            if (selected is not null && !filtered.Contains(selected)) Choose(null);
            list.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Choose(NotificationCandidateRow? choice)
        {
            selected = choice;
            info.IsOpen = false;
            types.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = choice is not null;
            dialog.PrimaryButtonText = N("SaveButton", "保存");
            if (choice is null) return;
            if (choice.IsExisting)
            {
                dialog.PrimaryButtonText = N("NotificationViewConfiguration", "查看设置");
                return;
            }
            if (choice.Creator is null && choice.Source?.IsAvailable != true)
            {
                dialog.PrimaryButtonText = N("NotificationOpenSettings", "打开设置");
                return;
            }
            if (choice.Source?.Policy is not null)
            {
                dialog.PrimaryButtonText = N("NotificationViewConfiguration", "查看设置");
                return;
            }
            var supportsUpdates = choice.Creator is { } creator ? creator.SupportedNotificationKinds.Any(kind => kind is "video" or "updates")
                : choice.Source!.SupportsUpdates;
            var supportsLive = choice.Creator is { } liveCreator ? liveCreator.SupportedNotificationKinds.Contains("live", StringComparer.Ordinal)
                : choice.Source!.SupportsLive;
            updates.IsOn = supportsUpdates; live.IsOn = supportsLive;
            updatesCard.Visibility = supportsUpdates ? Visibility.Visible : Visibility.Collapsed;
            liveCard.Visibility = supportsLive ? Visibility.Visible : Visibility.Collapsed;
            var sourcePolicy = choice.Creator is not null
                ? data.Rules.FirstOrDefault(row => row.Creator is null && row.IsPersisted && row.ProviderId == choice.Creator.ProviderId
                    && row.SourceId == choice.Creator.SourceId)?.Policy : null;
            selectionNote.Text = sourcePolicy is not null
                ? N("NotificationSavedAuthorPreference", "保存自选作者偏好会保留当前来源范围。全部范围下，这些个人开关暂不参与筛选；可在来源通知详情中修改范围。")
                : string.Empty;
            types.Visibility = Visibility.Visible;
        }
        void LeaveForSettings(NotificationCandidateRow? choice)
        {
            var owner = choice?.Creator is { } creator ? local.FirstOrDefault(row => row.Source?.ProviderId == creator.ProviderId
                && row.Source.SourceId == creator.SourceId)?.Source : choice?.Source;
            var plugin = choice?.Plugin ?? owner?.Source?.Plugin;
            navigation = plugin is not null ? () => Frame.Navigate(typeof(PluginDetailPage), plugin)
                : owner?.Source is { } source ? () => Frame.Navigate(typeof(SourceDetailPage), source.Source)
                : () => Frame.Navigate(typeof(SourcesSettingsPage));
            dialog.Hide();
        }
        async Task LoadAuthorsAsync(NotificationCreatorTarget target)
        {
            if (closed || saving || token.IsCancellationRequested || !IsLoaded) return;
            InvalidateQuery(); Choose(null); list.SelectedItem = null; authors.Clear(); Filter();
            var currentVersion = ++membershipVersion;
            // Typing cancels direct ID queries, not the initial following-list load.
            bool CurrentMembership() => !closed && !saving && IsLoaded && !token.IsCancellationRequested
                && membershipVersion == currentVersion;
            status.Text = target.SupportsFollowing ? N("NotificationLoadingAuthors", "正在加载已关注作者，请输入名称搜索。")
                : N("NotificationEnterAuthorId", "此插件支持按作者 ID 或链接查询。请输入后按 Enter 或点击搜索。");
            if (!target.SupportsFollowing) return;
            try
            {
                var page = await NotificationSession.GetFollowingNotificationCreatorsAsync(target, token);
                if (!CurrentMembership()) return;
                authors.AddRange(page.Creators.Where(creator => !authors.Any(row => row.Creator?.ProviderId == creator.ProviderId
                    && row.Creator.SourceId == creator.SourceId && row.Creator.CreatorKey == creator.CreatorKey)).Select(creator => new NotificationCandidateRow { Creator = creator, Target = target,
                    IsExisting = data.Rules.Any(row => row.Creator?.ProviderId == creator.ProviderId
                        && row.Creator.SourceId == creator.SourceId && row.Creator.CreatorKey == creator.CreatorKey) }));
                Filter();
                status.Text = page.IsComplete ? N("NotificationSearchHint", "输入已关注作者名称搜索，或输入作者 ID、链接后查询。")
                    : N("NotificationPartialAuthors", "作者列表不完整，可输入作者 ID 或链接直接查询。缺项不代表未关注。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { if (CurrentMembership()) { status.Text = N("NotificationAuthorLoadFailed", "已关注作者加载失败，可输入作者 ID 或链接重试查询。"); ShowError(exception); } }
        }
        async Task QueryAsync()
        {
            if (closed || saving || token.IsCancellationRequested) return;
            var input = search.Text.Trim();
            if (request?.AuthorsOnly != true)
            {
                if (visible.Count == 1) list.SelectedItem = visible[0];
                return;
            }
            if (string.IsNullOrWhiteSpace(input) || input.Length > 2048) return;
            var exactMatches = visible.Where(row => row.DisplayName.Equals(input, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (exactMatches.Length == 1) { list.SelectedItem = exactMatches[0]; return; }
            if (CreatorInputRouting.TryGetWebUri(input, out var uri))
            {
                var matching = targets.Where(target => target.SupportsResolve && CreatorInputRouting.MatchesHost(uri!, target.InputHosts)).ToArray();
                if (matching.Length == 1)
                {
                    routing = true;
                    sourcePicker.SelectedItem = sourcePicker.Items.Cast<ComboBoxItem>().First(item => ReferenceEquals(item.Tag, matching[0]));
                    routing = false;
                }
            }
            if (SelectedTarget() is not { } target)
            {
                status.Text = N("CreatorAmbiguousSource", "请选择作者所属来源，再查询作者 ID 或链接。");
                return;
            }
            InvalidateQuery(); var currentVersion = version;
            queryCancellation?.Dispose(); queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var queryToken = queryCancellation.Token;
            status.Text = N("FetchingCreatorInfo", "正在获取作者资料……");
            try
            {
                var result = await NotificationSession.ResolveNotificationCreatorAsync(target, input, queryToken);
                if (!Current(currentVersion) || queryToken.IsCancellationRequested) return;
                if (result.FollowState != CreatorFollowState.Followed || result.Creator is null)
                {
                    Choose(null);
                    status.Text = N(result.FollowState == CreatorFollowState.NotFollowed ? "CreatorNotFollowedDetail" : "CreatorFollowUnknownDetail",
                        "无法确认已关注关系，请检查账号或稍后重试。");
                    return;
                }
                var creator = result.Creator;
                var candidate = new NotificationCandidateRow { Creator = creator, Target = target,
                    IsExisting = data.Rules.Any(row => row.Creator?.ProviderId == creator.ProviderId && row.Creator.SourceId == creator.SourceId
                        && row.Creator.CreatorKey == creator.CreatorKey) };
                authors.RemoveAll(row => row.Creator?.ProviderId == creator.ProviderId && row.Creator.SourceId == creator.SourceId
                    && row.Creator.CreatorKey == creator.CreatorKey);
                authors.Insert(0, candidate);
                visible.Clear(); visible.Add(candidate); list.Visibility = Visibility.Visible; list.SelectedItem = candidate;
                status.Text = N("NotificationSearchHint", "输入已关注作者名称搜索，或输入作者 ID、链接后查询。");
            }
            catch (OperationCanceledException) when (queryToken.IsCancellationRequested) { }
            catch (Exception exception) { if (Current(currentVersion)) ShowError(exception); }
        }
        list.SelectionChanged += (_, _) => { if (!updatingCandidates) Choose(list.SelectedItem as NotificationCandidateRow); };
        search.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { InvalidateQuery(); Filter(); Choose(null); } };
        search.QuerySubmitted += async (_, _) => await QueryAsync();
        sourcePicker.SelectionChanged += async (_, _) => { if (!routing && SelectedTarget() is { } target) await LoadAuthorsAsync(target); };
        dialog.Closing += (_, _) => { closed = true; cancellation.Cancel(); queryCancellation?.Cancel(); };
        dialog.Opened += async (_, _) =>
        {
            search.Focus(FocusState.Programmatic);
            if (request?.AuthorsOnly != true) return;
            try
            {
                targets = (await NotificationSession.GetCreatorNotificationTargetsAsync(token))
                    .Where(target => target.Subscription.ProviderId == request.ProviderId
                        && target.Subscription.SourceId == request.SourceId).ToArray();
                if (closed || token.IsCancellationRequested || !IsLoaded) return;
                foreach (var target in targets) sourcePicker.Items.Add(new ComboBoxItem { Content = target.DisplayName, Tag = target });
                sourcePicker.Visibility = Visibility.Collapsed;
                sourcePicker.Margin = new Thickness(0);
                if (request is not null)
                    sourcePicker.SelectedItem = sourcePicker.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is NotificationCreatorTarget target
                        && target.Subscription.ProviderId == request.ProviderId && target.Subscription.SourceId == request.SourceId);
                else if (targets.Count == 1) sourcePicker.SelectedIndex = 0;
                if (request is not null && SelectedTarget() is null)
                    status.Text = N("CreatorSourceUnavailable", "来源暂不可用，请打开插件设置恢复登录或启用插件。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { if (!closed && IsLoaded) ShowError(exception); }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (saving || closed || selected is not { } choice) return;
            InvalidateQuery(); // A pending author-list refresh must not change the captured choice or toggles during save.
            saving = true; dialog.IsPrimaryButtonEnabled = false;
            search.IsEnabled = false; list.IsEnabled = false; sourcePicker.IsEnabled = false;
            updates.IsEnabled = false; live.IsEnabled = false;
            try
            {
                var fresh = await NotificationSettingsData.LoadAsync(panel, token);
                if (closed || token.IsCancellationRequested || !IsLoaded) return;
                var old = choice.Creator is { } author ? fresh.Rules.FirstOrDefault(row => row.Creator?.ProviderId == author.ProviderId
                    && row.Creator.SourceId == author.SourceId && row.Creator.CreatorKey == author.CreatorKey)
                    : choice.Source is { } source ? fresh.Rules.FirstOrDefault(row => row.Creator is null && row.Key == source.Key) : null;
                if (old is not null)
                {
                    navigation = () => Frame.Navigate(typeof(NotificationDetailPage), old); dialog.Hide(); return;
                }
                if (choice.Creator is { } creator && choice.Target is { } target)
                {
                    var result = await NotificationSession.ResolveNotificationCreatorAsync(target, creator.CreatorKey, token);
                    if (closed || token.IsCancellationRequested || !IsLoaded) return;
                    if (result.FollowState != CreatorFollowState.Followed || result.Creator is not { } verified
                        || verified.ProviderId != creator.ProviderId || verified.SourceId != creator.SourceId || verified.CreatorKey != creator.CreatorKey)
                        throw new InvalidOperationException(N("CreatorFollowUnknownDetail", "请重新确认作者的关注关系。"));
                    if (verified.SupportedNotificationKinds.Count == 0)
                        throw new InvalidOperationException(N("CreatorNotificationUnavailableDetail", "此作者暂不支持通知。"));
                    await NotificationSession.EnsureCreatorNotificationPolicyAsync(target, token);
                    if (closed || token.IsCancellationRequested || !IsLoaded) return;
                    // Read LocalSettings at commit time; another window may have edited other authors.
                    var saved = AppSettings.CustomNotificationCreators.ToList();
                    if (saved.Any(row => row.ProviderId == verified.ProviderId && row.SourceId == verified.SourceId && row.CreatorKey == verified.CreatorKey))
                    {
                        var latest = await NotificationSettingsData.LoadAsync(panel, token);
                        var duplicate = latest.Rules.FirstOrDefault(row => row.Creator?.ProviderId == verified.ProviderId
                            && row.Creator.SourceId == verified.SourceId && row.Creator.CreatorKey == verified.CreatorKey);
                        if (closed || token.IsCancellationRequested || !IsLoaded) return;
                        if (duplicate is not null) { navigation = () => Frame.Navigate(typeof(NotificationDetailPage), duplicate); dialog.Hide(); return; }
                        throw new InvalidOperationException(N("NotificationAlreadyConfigured", "该通知项已添加，请重新打开窗口查看。"));
                    }
                    saved.Add(new NotificationCreatorSubscription { ProviderId = verified.ProviderId, SourceId = verified.SourceId,
                            CreatorKey = verified.CreatorKey, Name = verified.DisplayName, AvatarUrl = verified.AvatarUrl ?? string.Empty,
                            VideoNotificationsEnabled = updates.IsOn && verified.SupportedNotificationKinds.Any(kind => kind is "video" or "updates"),
                            LiveNotificationsEnabled = live.IsOn && verified.SupportedNotificationKinds.Contains("live", StringComparer.Ordinal) });
                    AppSettings.CustomNotificationCreators = saved;
                }
                else if (choice.Source is { } sourceRow)
                {
                    var current = fresh.AvailableSources.FirstOrDefault(row => row.Key == sourceRow.Key);
                    if (current is null || !current.IsAvailable) { LeaveForSettings(choice); return; }
                    if (current.Policy is { } policy)
                    {
                        // The picker opens configuration; scope and type edits belong to the detail page.
                        await panel.SaveCreatorNotificationPolicyAsync(policy, token);
                    }
                    else if (current.Rule is { } rule)
                        await panel.SaveSourceNotificationRuleAsync(rule with { UpdateNotificationsEnabled = updates.IsOn && current.SupportsUpdates,
                            LiveNotificationsEnabled = live.IsOn && current.SupportsLive }, token);
                    else throw new InvalidOperationException(N("NotificationUnavailable", "暂不可用"));
                }
                else { LeaveForSettings(choice); return; }
                if (choice.Source?.Policy is not null)
                {
                    var latest = await NotificationSettingsData.LoadAsync(panel, token);
                    var configured = latest.Rules.FirstOrDefault(row => row.Creator is null && row.Key == choice.Source.Key);
                    if (configured is not null) navigation = () => Frame.Navigate(typeof(NotificationDetailPage), configured);
                }
                if (!closed && IsLoaded && !token.IsCancellationRequested) dialog.Hide();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { if (!closed && IsLoaded) ShowError(exception); }
            finally
            {
                saving = false;
                if (!closed && IsLoaded) { search.IsEnabled = true; list.IsEnabled = true; sourcePicker.IsEnabled = true;
                    updates.IsEnabled = true; live.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = selected is not null; }
            }
        };
        Filter(); status.Text = request?.AuthorsOnly == true
            ? N("NotificationSearchHint", "输入已关注作者名称搜索，或输入作者 ID、链接后查询。")
            : N("NotificationSourceSearchHint", "选择已添加的来源；插件的作者通知在其详情中配置。");
        try { await dialog.ShowAsync(); }
        finally { closed = true; cancellation.Cancel(); queryCancellation?.Cancel(); queryCancellation?.Dispose(); queryCancellation = null; }
        if (!IsLoaded || pageToken.IsCancellationRequested) return;
        if (navigation is not null) navigation();
    }
}
