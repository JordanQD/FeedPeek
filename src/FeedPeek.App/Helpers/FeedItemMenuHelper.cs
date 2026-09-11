using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;

namespace FeedPeek.Helpers;

internal static class FeedItemMenuHelper
{
    public static async Task ShowItemMenuAsync(
        FrameworkElement target,
        RightTappedRoutedEventArgs args,
        FeedItemRow item,
        FeedPeekPanelSession session,
        MainPanelSection section)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(session);
        var position = args.GetPosition(target);
        args.Handled = true;

        var menu = new MenuFlyout();

        var openInBackground = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("OpenItemInBackground"),
            Icon = new FontIcon { Glyph = "\uE8A7" },
        };
        openInBackground.Click += async (_, _) => await IgnoreSessionErrorsAsync(
            () => session.OpenItemInBackgroundAsync(item));
        menu.Items.Add(openInBackground);

        // "稍后再看" is a toggle: the remove command only makes sense on its own page.
        var isViewLaterSection = section == MainPanelSection.ViewLater;
        var viewLaterItem = new MenuFlyoutItem
        {
            Text = isViewLaterSection
                ? LocalizationHelper.GetString("RemoveFromViewLaterMenuItem")
                : LocalizationHelper.GetString("AddToViewLaterButton"),
            Icon = new FontIcon { Glyph = isViewLaterSection ? "\uE74D" : "\uE710" },
        };
        viewLaterItem.Click += async (_, _) => await IgnoreSessionErrorsAsync(
            () => session.SetSavedAsync(item, !isViewLaterSection));
        menu.Items.Add(viewLaterItem);

        var creatorItems = new List<MenuFlyoutItem>();
        if (Uri.TryCreate(item.AuthorUrl, UriKind.Absolute, out var authorUri)
            && authorUri.Scheme is "http" or "https")
        {
            var openCreator = new MenuFlyoutItem
            {
                Text = "打开作者主页",
                Icon = new FontIcon { Glyph = "\uE77B" },
            };
            openCreator.Click += async (_, _) => await Launcher.LaunchUriAsync(authorUri);
            creatorItems.Add(openCreator);
        }

        try
        {
            var action = await session.GetCreatorRelationActionAsync(item);
            if (action is not null)
            {
                var relationItem = new MenuFlyoutItem
                {
                    Text = action.FallbackText,
                    Icon = new FontIcon
                    {
                        // E8FA AddFriend / E8F8 BlockContact read as the follow / unfollow pair.
                        Glyph = action.ActionId == "creator.follow" ? "\uE8FA" : "\uE8F8",
                    },
                };
                relationItem.Click += async (_, _) => await IgnoreSessionErrorsAsync(
                    () => session.ExecuteCreatorActionAsync(item, action));
                creatorItems.Add(relationItem);
            }
        }
        catch
        {
            // Keep the item level commands available when relation lookup fails.
        }

        if (creatorItems.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var creatorItem in creatorItems)
            {
                menu.Items.Add(creatorItem);
            }
        }

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    private static async Task IgnoreSessionErrorsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // The session reports the actionable error through the panel status InfoBar.
        }
    }
}
