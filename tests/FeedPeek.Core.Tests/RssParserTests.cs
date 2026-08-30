using FeedPeek.Rss;
using System.Text;

namespace FeedPeek.Core.Tests;

public sealed class RssParserTests
{
    [Fact]
    public void Rss2_UsesGuidResolvesLinksAndSanitizesSummary()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/">
              <channel>
                <title>Example feed</title>
                <item>
                  <guid>post-1</guid>
                  <title>First post</title>
                  <link>/posts/1</link>
                  <description><![CDATA[<script>alert(1)</script><p>Hello &amp; welcome</p>]]></description>
                  <pubDate>Wed, 02 Oct 2024 10:00:00 GMT</pubDate>
                  <media:thumbnail url="/images/1.jpg" />
                </item>
              </channel>
            </rss>
            """;

        var parsed = Parse(xml);
        var item = Assert.Single(parsed.Items);

        Assert.Equal("rss", parsed.Format);
        Assert.StartsWith("guid:", item.ItemId);
        Assert.Equal("https://example.test/posts/1", item.Url);
        Assert.Equal("Hello & welcome", item.Summary);
        Assert.Equal("https://example.test/images/1.jpg", item.ImageUrl);
    }

    [Fact]
    public void Atom_MapsAuthorAndFallsBackToStableLinkIdentity()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom feed</title>
              <entry>
                <title>Entry</title>
                <link href="articles/2" />
                <updated>2024-10-03T11:30:00Z</updated>
                <author><name>Ada</name><uri>/authors/ada</uri></author>
                <summary type="html">&lt;b&gt;Summary&lt;/b&gt;</summary>
              </entry>
            </feed>
            """;

        var item = Assert.Single(Parse(xml).Items);

        Assert.StartsWith("link:", item.ItemId);
        Assert.Equal("Ada", item.AuthorName);
        Assert.Equal("https://example.test/authors/ada", item.AuthorUrl);
        Assert.Equal("Summary", item.Summary);
    }

    private static ParsedFeed Parse(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return RssFeedParser.Parse(stream, new Uri("https://example.test/feed.xml"), "source-1");
    }
}
