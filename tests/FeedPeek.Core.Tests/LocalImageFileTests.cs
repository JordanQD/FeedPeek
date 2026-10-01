using FeedPeek.Helpers;

namespace FeedPeek.Core.Tests;

public sealed class LocalImageFileTests
{
    [Fact]
    public async Task RetainedImageBytesDoNotLockThePluginDirectory()
    {
        using var temporary = new ImageTestDirectory();
        var plugin = Path.Combine(temporary.Path, "plugin");
        Directory.CreateDirectory(plugin);
        var original = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(plugin, "icon.ico"), original);

        var bytes = await LocalImageFile.ReadDetachedAsync(Path.Combine(plugin, "icon.ico"));
        var backup = Path.Combine(temporary.Path, "backup");
        Directory.Move(plugin, backup);
        Directory.Delete(backup, true);

        Assert.Equal(original, bytes);
        Assert.False(Directory.Exists(plugin));
        Assert.False(Directory.Exists(backup));
    }

    [Theory]
    [InlineData("local", "local")]
    [InlineData("roaming", "roaming")]
    [InlineData("temp", "temp")]
    public void AppDataUrisResolveToTheirDeclaredFolder(string folder, string expectedFolder)
    {
        using var temporary = new ImageTestDirectory();
        var result = LocalImageFile.ResolvePath(new Uri($"ms-appdata:///{folder}/icons/image%20name.ico"),
            Path.Combine(temporary.Path, "local"), Path.Combine(temporary.Path, "roaming"), Path.Combine(temporary.Path, "temp"));

        Assert.Equal(Path.Combine(temporary.Path, expectedFolder, "icons", "image name.ico"), result);
    }

    [Fact]
    public void FileUriResolvesWithoutAnAppDataRedirect()
    {
        using var temporary = new ImageTestDirectory();
        var imagePath = Path.Combine(temporary.Path, "image name.ico");
        var result = LocalImageFile.ResolvePath(new Uri(imagePath), temporary.Path, temporary.Path, temporary.Path);
        Assert.Equal(imagePath, result);
    }

    [Theory]
    [InlineData("ms-appdata:///local/%2e%2e%2foutside.ico")]
    [InlineData("ms-appdata:///local/%2e%2e%5coutside.ico")]
    [InlineData("ms-appdata://external/local/image.ico")]
    [InlineData("ms-appdata:///unknown/image.ico")]
    [InlineData("ms-appdata:///local/image.ico?revision=1")]
    [InlineData("ms-appdata:///local/image.ico#fragment")]
    [InlineData("https://example.test/image.ico")]
    public void InvalidOrEscapingAppDataUrisAreRejected(string uri)
    {
        using var temporary = new ImageTestDirectory();
        Assert.Throws<ArgumentException>(() => LocalImageFile.ResolvePath(new Uri(uri),
            temporary.Path, temporary.Path, temporary.Path));
    }

    [Fact]
    public async Task MissingFileRemainsAReadFailure()
    {
        using var temporary = new ImageTestDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => LocalImageFile.ReadDetachedAsync(Path.Combine(temporary.Path, "missing.ico")));
    }

    private sealed class ImageTestDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FeedPeek.ImageTests"));

        public ImageTestDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            if (!fullPath.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
        }
    }
}
