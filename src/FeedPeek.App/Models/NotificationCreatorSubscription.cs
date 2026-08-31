namespace FeedPeek.Models;

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Text.Json.Serialization;

public sealed class NotificationCreatorSubscription
{
    private ImageSource? _avatarImage;
    private string _avatarUrl = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string CreatorKey { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Mid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string AvatarUrl
    {
        get => _avatarUrl;
        set
        {
            _avatarUrl = value ?? string.Empty;
            _avatarImage = null;
        }
    }

    public bool VideoNotificationsEnabled { get; set; } = true;

    public bool LiveNotificationsEnabled { get; set; } = true;

    [JsonIgnore]
    public string IdentityKey => $"{ProviderId}\n{SourceId}\n{CreatorKey}";

    [JsonIgnore]
    public ImageSource? AvatarImage
    {
        get
        {
            if (_avatarImage is not null)
            {
                return _avatarImage;
            }

            if (string.IsNullOrWhiteSpace(_avatarUrl))
            {
                return null;
            }

            _avatarImage = new BitmapImage(new Uri(_avatarUrl));
            return _avatarImage;
        }
    }
}
