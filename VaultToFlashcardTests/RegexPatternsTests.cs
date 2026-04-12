using NUnit.Framework;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class RegexPatternsTests
{
    [Test]
    public void WikiLinkRegex_SimpleLink_CapturesLink()
    {
        var match = RegexPatterns.WikiLinkRegex().Match("[[link]]");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("link"));
        });
    }

    [Test]
    public void WikiLinkRegex_LinkWithAlias_CapturesLinkNotAlias()
    {
        var match = RegexPatterns.WikiLinkRegex().Match("[[alias|link]]");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("link"));
        });
    }

    [Test]
    public void WikiLinkRegex_PathWithSlashes_CapturesLastSegment()
    {
        var match = RegexPatterns.WikiLinkRegex().Match("[[path/to/file]]");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("file"));
        });
    }

    [Test]
    public void WikiLinkRegex_MultiplePipes_CapturesLastSegment()
    {
        var match = RegexPatterns.WikiLinkRegex().Match("[[a|b|c]]");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("c"));
        });
    }

    [Test]
    public void WikiLinkRegex_EmptyBrackets_ReturnsEmptyMatch()
    {
        var match = RegexPatterns.WikiLinkRegex().Match("[[]]");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.Empty);
        });
    }

    [Test]
    public void YamlHeaderRegex_FrontMatterWithContent_CapturesContent()
    {
        var match = RegexPatterns.YamlHeaderRegex().Match("---\nkey: value\n---\ncontent");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("key: value\n"));
        });
    }

    [Test]
    public void YamlHeaderRegex_FrontMatterOnly_CapturesContent()
    {
        var match = RegexPatterns.YamlHeaderRegex().Match("---\nfoo: bar\n---");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("foo: bar\n"));
        });
    }

    [Test]
    public void YamlHeaderRegex_MultilineFrontMatter_CapturesBetweenDelimiters()
    {
        var match = RegexPatterns.YamlHeaderRegex().Match("---\nmultiline\n---\n");
        Assert.Multiple(() =>
        {
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups[1].Value, Is.EqualTo("multiline\n"));
        });
    }

    [Test]
    public void YamlHeaderRegex_NoFrontMatter_ReturnsNoMatch()
    {
        var match = RegexPatterns.YamlHeaderRegex().Match("no front matter");
        Assert.That(match.Success, Is.False);
    }

    [Test]
    public void YamlHeaderRegex_EmptyFrontMatter_CapturesEmptyString()
    {
        var match = RegexPatterns.YamlHeaderRegex().Match("---\n---\ncontent");
        Assert.That(match.Success, Is.True);
        Assert.That(match.Groups[1].Value, Is.Empty);
    }
}
