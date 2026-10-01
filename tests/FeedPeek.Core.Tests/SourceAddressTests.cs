using FeedPeek.Core.Models;

namespace FeedPeek.Core.Tests;

public sealed class SourceAddressTests
{
    [Theory]
    [InlineData("https://www.douyu.com/74751")]
    [InlineData("https://douyu.com/74751")]
    [InlineData("douyu.com/74751")]
    [InlineData("www.douyu.com/74751")]
    [InlineData("//douyu.com/74751")]
    [InlineData("http://www.douyu.com/74751")]
    [InlineData("https://DOUYU.com/74751/?share=1#top")]
    public void EquivalentRoomAddressesUseTheManifestCanonicalAddress(string input)
    {
        Assert.True(SourceAddress.TryParseWebUri(input, out var uri));
        Assert.True(SourceAddress.TryMatchRoomTemplate("https://www.douyu.com/{roomId}", uri, out var id, out var address));
        Assert.Equal("74751", id);
        Assert.Equal("https://www.douyu.com/74751", address);
    }

    [Theory]
    [InlineData("https://m.douyu.com/74751")]
    [InlineData("https://www.douyu.com.evil.test/74751")]
    [InlineData("https://evil.test/74751")]
    [InlineData("https://www.douyu.com:8443/74751")]
    [InlineData("https://www.douyu.com/74751/more")]
    [InlineData("https://www.douyu.com/name74751")]
    [InlineData("https://www.douyu.com/７４７５１")]
    public void HostAndRoomIdentityRemainStrict(string input)
    {
        Assert.True(SourceAddress.TryParseWebUri(input, out var uri));
        Assert.False(SourceAddress.TryMatchRoomTemplate("https://www.douyu.com/{roomId}", uri, out _, out _));
    }

    [Theory]
    [InlineData("ftp://www.douyu.com/74751")]
    [InlineData("file:///C:/test")]
    [InlineData("https://user:secret@www.douyu.com/74751")]
    [InlineData("https://www.douyu.com/74 751")]
    [InlineData("javascript:alert(1)")]
    public void InvalidWebAddressesAreRejected(string input)
        => Assert.False(SourceAddress.TryParseWebUri(input, out _));

    [Fact]
    public void DomainSubdomainsAndTemplatePathsAreNotInterchangeable()
    {
        Assert.True(SourceAddress.TryParseWebUri("https://douyin.com/123", out var apex));
        Assert.False(SourceAddress.TryMatchRoomTemplate("https://live.douyin.com/{roomId}", apex, out _, out _));
        Assert.True(SourceAddress.TryParseWebUri("https://live.demo.test/room/123.html?x=1", out var room));
        Assert.True(SourceAddress.TryMatchRoomTemplate("https://live.demo.test/room/{roomId}.html", room, out var id, out var address));
        Assert.Equal("123", id);
        Assert.Equal("https://live.demo.test/room/123.html", address);
    }

    [Fact]
    public void OldSingleRoomSourcesGetAnEditableAddressButMultiRoomSourcesDoNot()
    {
        var source = new SourceDefinition("demo", "source", "Name", null, [], new Dictionary<string, string> { ["roomIds"] = "74751" });
        Assert.True(SourceAddress.TryGetRoomAddress(source, "https://www.douyu.com/{roomId}", out var address));
        Assert.Equal("https://www.douyu.com/74751", address);
        Assert.True(SourceAddress.TryGetRoomAddress(source with
            { Options = new Dictionary<string, string> { ["roomIds"] = "https://douyu.com/74751?share=1" } },
            "https://www.douyu.com/{roomId}", out var oldLink));
        Assert.Equal(address, oldLink);
        Assert.False(SourceAddress.TryGetRoomAddress(source with { Options = new Dictionary<string, string> { ["roomIds"] = "74751,12345" } },
            "https://www.douyu.com/{roomId}", out _));
    }
}
