using FeedPeek.PluginRuntime.Manifest;
using FeedPeek.PluginContracts.Manifest;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeedPeek.Core.Tests;

public sealed class CreatorResolutionManifestTests
{
    [Fact]
    public async Task MissingCreatorResolutionDeclarationRemainsBackwardCompatible()
    {
        using var package = await CreatePackageAsync(capabilities: []);

        var loaded = await new PluginManifestLoader().LoadAsync(package.Path);

        Assert.Null(loaded.Manifest.CreatorResolution);
    }

    [Fact]
    public async Task CreatorResolutionAcceptsExactInputHostsWhenCapabilityIsDeclared()
    {
        using var package = await CreatePackageAsync(
            capabilities: ["creator.resolve"],
            inputHosts: ["example.test", "www.example.test"]);

        var loaded = await new PluginManifestLoader().LoadAsync(package.Path);

        Assert.Equal(["example.test", "www.example.test"], loaded.Manifest.CreatorResolution!.InputHosts);
    }

    [Fact]
    public async Task CreatorResolutionRequiresItsCapability()
    {
        using var package = await CreatePackageAsync(
            capabilities: [],
            inputHosts: ["example.test"]);

        var error = await Assert.ThrowsAsync<PluginManifestException>(
            () => new PluginManifestLoader().LoadAsync(package.Path));

        Assert.Equal("CAPABILITY_REQUIRED", error.Code);
    }

    [Theory]
    [InlineData("[]", "CREATOR_INPUT_HOSTS_INVALID")]
    [InlineData("[\"*.example.test\"]", "HOST_INVALID")]
    [InlineData("[\"example.test:443\"]", "HOST_INVALID")]
    [InlineData("[\"https://example.test\"]", "HOST_INVALID")]
    public async Task CreatorResolutionRejectsInvalidInputHosts(string hostsJson, string expectedCode)
    {
        using var package = await CreatePackageAsync(
            capabilities: ["creator.resolve"],
            inputHostsJson: hostsJson);

        var error = await Assert.ThrowsAsync<PluginManifestException>(
            () => new PluginManifestLoader().LoadAsync(package.Path));

        Assert.Equal(expectedCode, error.Code);
    }

    private static async Task<TemporaryDirectory> CreatePackageAsync(
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string>? inputHosts = null,
        string? inputHostsJson = null)
    {
        var package = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(package.Path, "index.js"), "globalThis.FeedPeekPlugin = {}; ");

        var manifest = new JsonObject
        {
            ["pluginId"] = "demo.creator",
            ["version"] = "1.0.0",
            ["apiVersion"] = 1,
            ["displayName"] = "Creator Demo",
            ["entry"] = "index.js",
            ["preloadScripts"] = new JsonArray(),
            ["capabilities"] = JsonSerializer.SerializeToNode(capabilities),
            ["auth"] = null,
            ["network"] = new JsonObject
            {
                ["allowedHosts"] = new JsonArray(),
                ["allowRedirectHosts"] = new JsonArray(),
            },
            ["views"] = new JsonArray
            {
                new JsonObject { ["id"] = "feed", ["kind"] = "feed", ["title"] = "Updates" },
            },
        };
        if (inputHostsJson is not null)
            manifest["creatorResolution"] = new JsonObject { ["inputHosts"] = JsonNode.Parse(inputHostsJson) };
        else if (inputHosts is not null)
            manifest["creatorResolution"] = new JsonObject { ["inputHosts"] = JsonSerializer.SerializeToNode(inputHosts) };

        await File.WriteAllTextAsync(
            Path.Combine(package.Path, PluginManifestRules.FileName),
            manifest.ToJsonString());
        return package;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FeedPeek.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
