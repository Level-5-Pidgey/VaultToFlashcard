using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class MediaExtractorTests
{
	private static readonly string TestDirectoryPath;

	private static MediaExtractor CreateExtractor()
	{
		return new MediaExtractor();
	}

	static MediaExtractorTests()
	{
		TestDirectoryPath = Path.Combine(Path.GetTempPath(), "MediaExtractorTests_" + Guid.NewGuid());
		Directory.CreateDirectory(TestDirectoryPath);
		Directory.CreateDirectory(Path.Combine(TestDirectoryPath, "assets"));
		Directory.CreateDirectory(Path.Combine(TestDirectoryPath, "admin", "assets"));
	}

	[OneTimeTearDown]
	public static void OneTimeCleanup()
	{
		if (Directory.Exists(TestDirectoryPath))
			Directory.Delete(TestDirectoryPath, true);
	}

	// DetermineMediaType tests

	[Test]
	[TestCase(".mp3", MediaType.Audio)]
	[TestCase(".ogg", MediaType.Audio)]
	[TestCase(".wav", MediaType.Audio)]
	[TestCase(".m4a", MediaType.Audio)]
	[TestCase(".flac", MediaType.Audio)]
	public void DetermineMediaType_AudioExtensions_ReturnsAudio(string ext, MediaType expected)
	{
		var result = MediaExtractor.DetermineMediaType(ext);
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
		var result = MediaExtractor.DetermineMediaType(ext);
		Assert.That(result, Is.EqualTo(expected));
	}

	[Test]
	[TestCase(".mp4", MediaType.Video)]
	[TestCase(".webm", MediaType.Video)]
	[TestCase(".mkv", MediaType.Video)]
	[TestCase(".avi", MediaType.Video)]
	public void DetermineMediaType_VideoExtensions_ReturnsVideo(string ext, MediaType expected)
	{
		var result = MediaExtractor.DetermineMediaType(ext);
		Assert.That(result, Is.EqualTo(expected));
	}

	[Test]
	[TestCase(".xyz")]
	[TestCase(".txt")]
	[TestCase(".pdf")]
	[TestCase(".doc")]
	public void DetermineMediaType_UnknownExtension_ReturnsNull(string ext)
	{
		var result = MediaExtractor.DetermineMediaType(ext);
		Assert.That(result, Is.Null);
	}

	[Test]
	[TestCase(".MP3")]
	[TestCase(".PNG")]
	[TestCase(".MP4")]
	public void DetermineMediaType_CaseInsensitive_ReturnsCorrectType(string ext)
	{
		var result = MediaExtractor.DetermineMediaType(ext);
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
		var result = MediaExtractor.IsImageUrl(url);
		Assert.That(result, Is.True);
	}

	[Test]
	[TestCase("https://example.com/audio.mp3")]
	[TestCase("https://example.com/file.txt")]
	[TestCase("https://example.com/video.mp4")]
	public void IsImageUrl_NonImageUrl_ReturnsFalse(string url)
	{
		var result = MediaExtractor.IsImageUrl(url);
		Assert.That(result, Is.False);
	}

	[Test]
	public void IsImageUrl_RelativeUrl_ReturnsFalse()
	{
		var result = MediaExtractor.IsImageUrl("image.png");
		Assert.That(result, Is.False);
	}

	// ExtractFilenameFromUrl tests

	[Test]
	[TestCase("https://example.com/path/to/image.png", "image.png")]
	[TestCase("https://example.com/path/to/photo.jpg?query=1", "photo.jpg")]
	[TestCase("https://example.com/file.mp3?token=abc", "file.mp3")]
	public void ExtractFilenameFromUrl_ValidUrl_ReturnsFilename(string url, string expected)
	{
		var result = MediaExtractor.ExtractFilenameFromUrl(url);
		Assert.That(result, Is.EqualTo(expected));
	}

	[Test]
	public void ExtractFilenameFromUrl_UrlWithNoFilename_ReturnsEmpty()
	{
		var result = MediaExtractor.ExtractFilenameFromUrl("https://example.com/");
		Assert.That(result, Is.Empty);
	}

	// ResolveLocalFile tests

	[Test]
	public void ResolveLocalFile_FileNotFound_ReturnsNull()
	{
		var result = MediaExtractor.ResolveLocalFile(
			"nonexistent_file_12345.mp3",
			TestDirectoryPath,
			null);

		Assert.Multiple(() =>
		{
			Assert.That(result.filePath, Is.Null);
			Assert.That(result.data, Is.Null);
			Assert.That(result.skipHash, Is.Null);
		});
	}

	// Extract tests

	[Test]
	public void Extract_EmptyContent_ReturnsEmptyMedia()
	{
		var extractor = CreateExtractor();
		var result = extractor.Extract("", TestDirectoryPath, null);
		Assert.That(result.Media, Is.Empty);
		Assert.That(result.CleanedContent, Is.Empty);
	}

	[Test]
	public void Extract_ExternalImage_ExtractsAndStrips()
	{
		var extractor = CreateExtractor();
		var content = "Some text before ![an image](https://example.com/photo.png) some text after";
		var result = extractor.Extract(content, TestDirectoryPath, null);

		Assert.Multiple(() =>
		{
			Assert.That(result.Media.Count, Is.EqualTo(1));
			Assert.That(result.Media[0].Type, Is.EqualTo(MediaType.Picture));
			Assert.That(result.Media[0].Url, Is.EqualTo("https://example.com/photo.png"));
			Assert.That(result.CleanedContent, Does.Not.Contain("![]("));
			Assert.That(result.CleanedContent, Does.Contain("Some text before"));
			Assert.That(result.CleanedContent, Does.Contain("some text after"));
		});
	}

	[Test]
	public void Extract_WikilinkMedia_ExtractsAndStrips()
	{
		var extractor = CreateExtractor();
		// Wikilink with known media extension - file won't be found but syntax should be stripped
		var content = "Before ![[audio.mp3]] after ![[image.png]] end";
		var result = extractor.Extract(content, TestDirectoryPath, null);

		Assert.Multiple(() =>
		{
			Assert.That(result.CleanedContent, Does.Not.Contain("![["));
			Assert.That(result.CleanedContent, Does.Contain("Before"));
			Assert.That(result.CleanedContent, Does.Contain("end"));
		});
	}

	[Test]
	public void Extract_MultipleExternalImages_ExtractsAll()
	{
		var extractor = CreateExtractor();
		var content = "![first](https://example.com/a.png) text ![second](https://example.com/b.jpg) more";
		var result = extractor.Extract(content, TestDirectoryPath, null);

		Assert.Multiple(() =>
		{
			Assert.That(result.Media.Count, Is.EqualTo(2));
			Assert.That(result.Media[0].Url, Is.EqualTo("https://example.com/a.png"));
			Assert.That(result.Media[1].Url, Is.EqualTo("https://example.com/b.jpg"));
		});
	}

	[Test]
	public void Extract_NonMediaWikilink_StillStripsFromContent()
	{
		var extractor = CreateExtractor();
		// .txt is not a media type - wikilink stripped from content but not added to media
		var content = "See ![[notes.txt]] for details";
		var result = extractor.Extract(content, TestDirectoryPath, null);

		Assert.Multiple(() =>
		{
			Assert.That(result.CleanedContent, Does.Not.Contain("![[notes.txt]]"));
			Assert.That(result.Media, Is.Empty);
		});
	}
}