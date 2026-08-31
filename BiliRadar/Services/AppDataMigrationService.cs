using System;
using System.Diagnostics;
using Windows.Storage;

namespace BiliRadar.Services;

/// <summary>
/// Runs host-owned, retry-safe migrations for LocalSettings data. SQLite keeps
/// its own schema version in FeedPeek.Core and credentials stay in Credential Locker.
/// </summary>
internal static class AppDataMigrationService
{
    private const string SchemaVersionKey = "FeedPeek.DataSchemaVersion";
    private const int CurrentSchemaVersion = 1;

    public static void Run()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var version = ReadVersion(values.TryGetValue(SchemaVersionKey, out var value) ? value : null);
            if (version >= CurrentSchemaVersion)
            {
                return;
            }

            // Rewriting through the current serializers upgrades legacy newline lists,
            // creator identifiers and duplicate records without changing user choices.
            AppSettings.KnownVideoUpdateIds = AppSettings.KnownVideoUpdateIds;
            AppSettings.KnownLiveRoomIds = AppSettings.KnownLiveRoomIds;
            AppSettings.CustomNotificationCreators = AppSettings.CustomNotificationCreators;

            // Written last: an interrupted migration is retried safely on next launch.
            values[SchemaVersionKey] = CurrentSchemaVersion;
        }
        catch (Exception ex)
        {
            // A settings migration must never prevent the tray application from starting.
            // The version marker remains unchanged, so the next launch retries it.
            Debug.WriteLine($"[AppDataMigration] Migration deferred: {ex.Message}");
        }
    }

    private static int ReadVersion(object? value)
        => value switch
        {
            int number => Math.Max(0, number),
            long number when number <= int.MaxValue => Math.Max(0, (int)number),
            _ => 0,
        };
}
