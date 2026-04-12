using System.Reflection;
using NUnit.Framework;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class MediaExtractorTests
{
    private static MediaExtractor CreateExtractor() => new();

    // DetermineMediaType tests

    [Test]
    [TestCase(".mp3", MediaType.Audio)]
    [TestCase(".ogg", MediaType.Audio)]
    [TestCase(".wav", MediaType.Audio)]
    [TestCase(".m4a", MediaType.Audio)]
    [TestCase(".flac", MediaType.Audio)]
    public void DetermineMediaType_AudioExtensions_ReturnsAudio(string ext, MediaType expected)
    {
        var result = InvokeStaticPrivate<MediaType?>("DetermineMediaType", ext);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase(".png", MediaType.Picture)]
    [TestCase(".jpg", MediaType.Picture)]
    [TestCase(".jpeg", MediaType.Picture)]
    [TestCase(".gif", MediaType.Picture)]
    [TestCase(".webp", MediaType.Picture)]
    [TestCase(".svg", MediaType.Picture)]
    [TestCase(".bmp", MediaType.Picture)]
    public void DetermineMediaType_PictureExtensions_ReturnsPicture(string ext, MediaType expected)
    {
        var result = InvokeStaticPrivate<MediaType?>("DetermineMediaType", ext);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase(".mp4", MediaType.Video)]
    [TestCase(".webm", MediaType.Video)]
    [TestCase(".mkv", MediaType.Video)]
    [TestCase(".avi", MediaType.Video)]
    public void DetermineMediaType_VideoExtensions_ReturnsVideo(string ext, MediaType expected)
    {
        var result = InvokeStaticPrivate<MediaType?>("DetermineMediaType", ext);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase(".xyz")]
    [TestCase(".txt")]
    [TestCase(".pdf")]
    [TestCase(".doc")]
    public void DetermineMediaType_UnknownExtension_ReturnsNull(string ext)
    {
        var result = InvokeStaticPrivate<MediaType?>("DetermineMediaType", ext);
        Assert.That(result, Is.Null);
    }

    [Test]
    [TestCase(".MP3")]
    [TestCase(".PNG")]
    [TestCase(".MP4")]
    public void DetermineMediaType_CaseInsensitive_ReturnsCorrectType(string ext)
    {
        var result = InvokeStaticPrivate<MediaType?>("DetermineMediaType", ext);
        Assert.That(result, Is.Not.Null);
    }

    // IsImageUrl tests

    [Test]
    [TestCase("https://example.com/image.png")]
    [TestCase("https://example.com/image.jpg")]
    [TestCase("https://example.com/image.jpeg")]
    [TestCase("https://example.com/image.gif")]
    [TestCase("https://example.com/image.webp")]
    [TestCase("https://example.com/image.svg")]
    [TestCase("https://example.com/image.bmp")]
    public void IsImageUrl_ValidImageUrl_ReturnsTrue(string url)
    {
        var result = InvokeStaticPrivate<bool>("IsImageUrl", url);
        Assert.That(result, Is.True);
    }

    [Test]
    [TestCase("https://example.com/audio.mp3")]
    [TestCase("https://example.com/file.txt")]
    [TestCase("https://example.com/video.mp4")]
    public void IsImageUrl_NonImageUrl_ReturnsFalse(string url)
    {
        var result = InvokeStaticPrivate<bool>("IsImageUrl", url);
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsImageUrl_RelativeUrl_ReturnsFalse()
    {
        var result = InvokeStaticPrivate<bool>("IsImageUrl", "image.png");
        Assert.That(result, Is.False);
    }

    // ExtractFilenameFromUrl tests

    [Test]
    [TestCase("https://example.com/path/to/image.png", "image.png")]
    [TestCase("https://example.com/path/to/photo.jpg?query=1", "photo.jpg")]
    [TestCase("https://example.com/file.mp3?token=abc", "file.mp3")]
    public void ExtractFilenameFromUrl_ValidUrl_ReturnsFilename(string url, string expected)
    {
        var result = InvokeStaticPrivate<string>("ExtractFilenameFromUrl", url);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ExtractFilenameFromUrl_UrlWithNoFilename_ReturnsEmpty()
    {
        var result = InvokeStaticPrivate<string>("ExtractFilenameFromUrl", "https://example.com/");
        Assert.That(result, Is.Empty);
    }

    // ResolveLocalFile tests

    [Test]
    public void ResolveLocalFile_FileNotFound_ReturnsNull()
    {
        // Use a real temp directory that exists but has no matching files
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var result = InvokeStaticPrivate<(string?, string?, string?)>(
                "ResolveLocalFile",
                "nonexistent_file_12345.mp3",
                tempPath,
                null);
            Assert.That(result.Item1, Is.Null);
            Assert.That(result.Item2, Is.Null);
            Assert.That(result.Item3, Is.Null);
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    // Extract tests

    [Test]
    public void Extract_EmptyContent_ReturnsEmptyMedia()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var extractor = CreateExtractor();
            var result = extractor.Extract("", tempPath, null);
            Assert.That(result.Media, Is.Empty);
            Assert.That(result.CleanedContent, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void Extract_ExternalImage_ExtractsAndStrips()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var extractor = CreateExtractor();
            var content = "Some text before ![an image](https://example.com/photo.png) some text after";
            var result = extractor.Extract(content, tempPath, null);

            Assert.That(result.Media.Count, Is.EqualTo(1));
            Assert.That(result.Media[0].Type, Is.EqualTo(MediaType.Picture));
            Assert.That(result.Media[0].Url, Is.EqualTo("https://example.com/photo.png"));
            Assert.That(result.CleanedContent, Does.Not.Contain("![]("));
            Assert.That(result.CleanedContent, Does.Contain("Some text before"));
            Assert.That(result.CleanedContent, Does.Contain("some text after"));
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void Extract_WikilinkMedia_ExtractsAndStrips()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var extractor = CreateExtractor();
            // Wikilink with known media extension - file won't be found but syntax should be stripped
            var content = "Before ![[audio.mp3]] after ![[image.png]] end";
            var result = extractor.Extract(content, tempPath, null);

            // Wikilinks should be stripped from content (file not found warnings are console输出)
            Assert.That(result.CleanedContent, Does.Not.Contain("![["));
            Assert.That(result.CleanedContent, Does.Contain("Before"));
            Assert.That(result.CleanedContent, Does.Contain("end"));
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void Extract_MultipleExternalImages_ExtractsAll()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var extractor = CreateExtractor();
            var content = "![first](https://example.com/a.png) text ![second](https://example.com/b.jpg) more";
            var result = extractor.Extract(content, tempPath, null);

            Assert.That(result.Media.Count, Is.EqualTo(2));
            Assert.That(result.Media[0].Url, Is.EqualTo("https://example.com/a.png"));
            Assert.That(result.Media[1].Url, Is.EqualTo("https://example.com/b.jpg"));
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void Extract_NonMediaWikilink_StillStripsFromContent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        Directory.CreateDirectory(Path.Combine(tempPath, "assets"));
        Directory.CreateDirectory(Path.Combine(tempPath, "admin", "assets"));
        try
        {
            var extractor = CreateExtractor();
            // .txt is not a media type - wikilink stripped from content but not added to media
            var content = "See ![[notes.txt]] for details";
            var result = extractor.Extract(content, tempPath, null);

            // Wikilink syntax is always stripped via regex Replace, regardless of media type
            Assert.That(result.CleanedContent, Does.Not.Contain("![[notes.txt]]"));
            Assert.That(result.Media, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    // Helper to invoke private static methods via reflection
    private static T? InvokeStaticPrivate<T>(string methodName, params object[] args)
    {
        var method = typeof(MediaExtractor).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ArgumentException($"Method {methodName} not found");
        return (T?)method.Invoke(null, args);
    }

    private static T InvokeStaticPrivate<T>(string methodName, object arg1)
    {
        var method = typeof(MediaExtractor).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ArgumentException($"Method {methodName} not found");
        return (T)method.Invoke(null, new[] { arg1 })!;
    }
}
