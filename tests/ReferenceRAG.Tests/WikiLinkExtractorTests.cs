using ReferenceRAG.Core.Services.Graph;
using Xunit;

namespace ReferenceRAG.Tests;

public class WikiLinkExtractorTests
{
    private readonly WikiLinkExtractor _extractor = new();

    [Fact]
    public void Extract_BasicWikiLink_ReturnsWikilink()
    {
        var links = _extractor.Extract("See [[Projects/MyNote]]");
        Assert.Single(links);
        Assert.Equal("Projects/MyNote.md", links[0].target);
        Assert.Equal("wikilink", links[0].type);
    }

    [Fact]
    public void Extract_WikiLinkWithAlias_IgnoresAlias()
    {
        var links = _extractor.Extract("See [[Projects/MyNote|My Display Name]]");
        Assert.Single(links);
        Assert.Equal("Projects/MyNote.md", links[0].target);
    }

    [Fact]
    public void Extract_WikiLinkWithHeading_IgnoresHeading()
    {
        var links = _extractor.Extract("See [[Projects/MyNote#Section 1]]");
        Assert.Single(links);
        Assert.Equal("Projects/MyNote.md", links[0].target);
    }

    [Fact]
    public void Extract_EmbedLink_ReturnsEmbedType()
    {
        var links = _extractor.Extract("![[images/diagram.png]]");
        Assert.Single(links);
        Assert.Equal("embed", links[0].type);
    }

    [Fact]
    public void Extract_Tag_ReturnsTagType()
    {
        var links = _extractor.Extract("This is a #project note");
        Assert.Contains(links, l => l.type == "tag" && l.target == "project");
    }

    [Fact]
    public void Extract_MultipleLinks_ReturnAll()
    {
        var md = "See [[Note1]] and [[Note2]]\n#tag1";
        var links = _extractor.Extract(md);
        Assert.Equal(3, links.Count);
    }

    [Fact]
    public void Extract_InsideCodeBlock_IgnoresLinks()
    {
        var md = "```\n[[ignored]]\n```";
        var links = _extractor.Extract(md);
        Assert.Empty(links);
    }

    [Fact]
    public void Extract_ReportsLineNumbers()
    {
        var md = "Line1\n[[Note1]]\nLine3\n[[Note2]]";
        var links = _extractor.Extract(md);
        var wikiLinks = links.Where(l => l.type == "wikilink").ToList();
        Assert.Equal(2, wikiLinks.Count);
        Assert.Equal(2, wikiLinks[0].line);
        Assert.Equal(4, wikiLinks[1].line);
    }

    [Fact]
    public void Extract_AlreadyHasMdExtension_DoesNotDoubleAdd()
    {
        var links = _extractor.Extract("[[Note.md]]");
        Assert.Single(links);
        Assert.Equal("Note.md", links[0].target);
    }
}
