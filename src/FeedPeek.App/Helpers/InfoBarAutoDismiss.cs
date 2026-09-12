using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FeedPeek.Helpers;

/// <summary>
/// Shared auto-dismiss for transient <see cref="InfoBar"/> notifications: shows the bar
/// and closes it again after <see cref="Delay"/>. Showing another message restarts the
/// countdown instead of stacking timers.
/// </summary>
internal static class InfoBarAutoDismiss
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Holds the per-bar timer state. The state lives in an attached property on the bar
    /// itself, so it is collected together with the bar instead of keeping it alive.
    /// </summary>
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(DismissState),
            typeof(InfoBarAutoDismiss),
            new PropertyMetadata(null));

    /// <summary>Marks a bar whose <see cref="InfoBar.IsOpen"/> is already being watched.</summary>
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked",
            typeof(bool),
            typeof(InfoBarAutoDismiss),
            new PropertyMetadata(false));

    /// <summary>Shows <paramref name="message"/> and schedules the dismissal.</summary>
    /// <param name="onDismissed">Runs after the bar closes, whether by timeout or by its close button.</param>
    public static void Show(
        InfoBar infoBar,
        string message,
        InfoBarSeverity severity,
        Action? onDismissed = null)
    {
        ArgumentNullException.ThrowIfNull(infoBar);
        infoBar.Message = message;
        infoBar.Severity = severity;
        Show(infoBar, onDismissed);
    }

    /// <summary>
    /// Shows an <paramref name="infoBar"/> whose content the caller populated itself
    /// (for example a custom text block) and schedules the dismissal.
    /// </summary>
    /// <param name="onDismissed">Runs after the bar closes, whether by timeout or by its close button.</param>
    public static void Show(InfoBar infoBar, Action? onDismissed = null)
    {
        ArgumentNullException.ThrowIfNull(infoBar);
        var state = GetState(infoBar);
        state.OnDismissed = onDismissed;
        CollapseWhenClosed(infoBar);
        infoBar.IsOpen = true;
        state.Restart();
    }

    /// <summary>
    /// Makes <paramref name="infoBar"/> stop occupying layout while it is closed. A closed
    /// InfoBar only collapses its template content: the element itself stays visible and keeps
    /// its <c>Margin</c>, so it still eats 4px of the gap around it. This follows
    /// <see cref="InfoBar.IsOpen"/>, so every close path (auto-dismiss, close button or code)
    /// collapses the element as well.
    /// </summary>
    public static void CollapseWhenClosed(InfoBar infoBar)
    {
        ArgumentNullException.ThrowIfNull(infoBar);
        if ((bool)infoBar.GetValue(HookedProperty))
        {
            return;
        }

        infoBar.SetValue(HookedProperty, true);
        infoBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnIsOpenChanged);
        SyncVisibility(infoBar);
    }

    private static void OnIsOpenChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (sender is InfoBar infoBar)
        {
            SyncVisibility(infoBar);
        }
    }

    private static void SyncVisibility(InfoBar infoBar)
        => infoBar.Visibility = infoBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Closes <paramref name="infoBar"/> immediately and cancels its pending auto-dismiss.</summary>
    public static void Dismiss(InfoBar infoBar)
    {
        ArgumentNullException.ThrowIfNull(infoBar);
        (infoBar.GetValue(StateProperty) as DismissState)?.Dismiss();
    }

    private static DismissState GetState(InfoBar infoBar)
    {
        if (infoBar.GetValue(StateProperty) is DismissState existing)
        {
            return existing;
        }

        var state = new DismissState(infoBar);
        infoBar.SetValue(StateProperty, state);
        return state;
    }

    private sealed class DismissState
    {
        private readonly InfoBar _infoBar;
        private readonly DispatcherQueueTimer _timer;

        public DismissState(InfoBar infoBar)
        {
            _infoBar = infoBar;
            _timer = infoBar.DispatcherQueue.CreateTimer();
            _timer.Interval = Delay;
            _timer.IsRepeating = false;
            _timer.Tick += OnTick;
        }

        public Action? OnDismissed { get; set; }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Dismiss()
        {
            _timer.Stop();
            _infoBar.IsOpen = false;
            OnDismissed?.Invoke();
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            Dismiss();
        }
    }
}
