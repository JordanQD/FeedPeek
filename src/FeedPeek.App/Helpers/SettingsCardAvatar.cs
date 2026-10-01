using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace FeedPeek.Helpers;

/// <summary>Uses the card's native icon slot without changing its template or text layout.</summary>
public static class SettingsCardAvatar
{
    private const double IconSize = 20;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ImageSource), typeof(SettingsCardAvatar), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SettingsCardAvatar), new PropertyMetadata(false, OnSourceChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(AvatarState), typeof(SettingsCardAvatar), new PropertyMetadata(null));

    public static ImageSource? GetSource(DependencyObject element) => (ImageSource?)element.GetValue(SourceProperty);
    public static void SetSource(DependencyObject element, ImageSource? value) => element.SetValue(SourceProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement card || (card is not SettingsCard && card is not SettingsExpander))
            return;
        if (card.GetValue(StateProperty) is not AvatarState state)
        {
            state = new AvatarState(card);
            card.SetValue(StateProperty, state);
        }
        state.Render();
    }

    private sealed class AvatarState
    {
        private readonly FrameworkElement _card;
        private readonly DependencyProperty _headerIconProperty;
        private BitmapImage? _observedImage;

        public AvatarState(FrameworkElement card)
        {
            _card = card;
            _headerIconProperty = card is SettingsExpander ? SettingsExpander.HeaderIconProperty : SettingsCard.HeaderIconProperty;
            card.Loaded += (_, _) => Render();
            // Cached images must not retain unloaded cards through their event handlers.
            card.Unloaded += (_, _) => StopObserving();
        }

        public void Render()
        {
            StopObserving();
            if (!GetIsEnabled(_card))
            {
                _card.ClearValue(_headerIconProperty);
                return;
            }
            var source = GetSource(_card);
            if (source is null || ImageSourceCache.HasFailed(source))
            {
                ShowFallback();
                return;
            }

            _card.SetValue(_headerIconProperty, new PathIcon
            {
                Width = IconSize,
                Height = IconSize,
                IsHitTestVisible = false,
                Data = new EllipseGeometry
                {
                    Center = new Point(IconSize / 2, IconSize / 2),
                    RadiusX = IconSize / 2,
                    RadiusY = IconSize / 2,
                },
                Foreground = new ImageBrush
                {
                    ImageSource = source,
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                },
            });

            if (_card.IsLoaded && source is BitmapImage image)
            {
                _observedImage = image;
                ImageSourceCache.AddFailureHandler(image, ImageFailed);
            }
        }

        private void ImageFailed()
        {
            StopObserving();
            ShowFallback();
        }

        private void ShowFallback() => _card.SetValue(_headerIconProperty, new FontIcon
        {
            Glyph = "\uE774",
            IsHitTestVisible = false,
        });

        private void StopObserving()
        {
            if (_observedImage is not null)
                ImageSourceCache.RemoveFailureHandler(_observedImage, ImageFailed);
            _observedImage = null;
        }
    }
}
