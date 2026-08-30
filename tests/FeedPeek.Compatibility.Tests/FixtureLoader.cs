using System.Text.Json;

namespace FeedPeek.Compatibility.Tests;

internal static class FixtureLoader
{
    public static JsonDocument Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
