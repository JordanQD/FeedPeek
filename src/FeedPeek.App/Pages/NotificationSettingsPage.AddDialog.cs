using System.Threading;
using FeedPeek.Models;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace FeedPeek.Pages;

public sealed partial class NotificationSettingsPage
{
    private async Task ShowAddNotificationDialogAsync(CancellationToken token, NotificationAddRequest request)
    {
        var dialog = new NotificationAddDialog(this,
            (DataTemplate)Resources["NotificationCandidateTemplate"], NotificationSession);
        await dialog.ShowAsync(token, request);
        if (IsLoaded && !token.IsCancellationRequested)
            await ReloadNotificationRulesAsync();
    }
}
