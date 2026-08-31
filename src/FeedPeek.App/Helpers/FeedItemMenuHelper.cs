using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace FeedPeek.Helpers;

internal static class FeedItemMenuHelper
{
    public static async Task ShowCreatorMenuAsync(
        FrameworkElement target,
        RightTappedRoutedEventArgs args,
        FeedItemRow item,
        FeedPeekPanelSession session)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(session);
        var position = args.GetPosition(target);
        args.Handled = true;

        var menu = new MenuFlyout();
        if (Uri.TryCreate(item.AuthorUrl, UriKind.Absolute, out var authorUri)
            && authorUri.Scheme is "http" or "https")
        {
            var openCreator = new MenuFlyoutItem
            {
                Text = "打开作者主页",
                Icon = new FontIcon { Glyph = "\uE77B" },
            };
            openCreator.Click += async (_, _) => await Launcher.LaunchUriAsync(authorUri);
            menu.Items.Add(openCreator);
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
                        Glyph = action.ActionId == "creator.follow" ? "\uE8FA" : "\uE8D7",
                    },
                };
                relationItem.Click += async (_, _) =>
                {
                    try
                    {
                        await session.ExecuteCreatorActionAsync(item, action);
                    }
                    catch
                    {
                        // The session reports the plugin error through the panel status area.
                    }
                };
                menu.Items.Add(relationItem);
            }
        }
        catch
        {
            // Keep the author-home command available when relation lookup fails.
        }

        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }
}
