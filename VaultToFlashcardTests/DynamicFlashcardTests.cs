using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class DynamicFlashcardTests
{
	[Test]
	public void DefaultConstructor_InitializesAllPropertiesWithDefaults()
	{
		var flashcard = new DynamicFlashcard();

		Assert.Multiple(() =>
		{
			Assert.That(flashcard.ModelName, Is.EqualTo(""));
			Assert.That(flashcard.Source, Is.EqualTo(""));
			Assert.That(flashcard.Fields, Is.Empty);
			Assert.That(flashcard.Media, Is.Empty);
		});
	}

	[Test]
	public void ParameterizedConstructor_InitializesAllPropertiesCorrectly()
	{
		var fields = new Dictionary<string, string> { ["front"] = "question", ["back"] = "answer" };
		var flashcard = new DynamicFlashcard("Basic", fields, "TestSource");

		Assert.Multiple(() =>
		{
			Assert.That(flashcard.ModelName, Is.EqualTo("Basic"));
			Assert.That(flashcard.Fields, Is.EqualTo(fields));
			Assert.That(flashcard.Source, Is.EqualTo("TestSource"));
			Assert.That(flashcard.Media, Is.Empty);
		});
	}

	[Test]
	public void ParameterizedConstructor_WithDefaultSource_InitializesSourceAsEmpty()
	{
		var fields = new Dictionary<string, string>();
		var flashcard = new DynamicFlashcard("Basic", fields);

		Assert.That(flashcard.Source, Is.EqualTo(""));
	}

	[Test]
	public void MediaList_Independence_BetweenTwoFlashcards()
	{
		var flashcard1 = new DynamicFlashcard { Media = new List<MediaItem>() };
		var flashcard2 = new DynamicFlashcard { Media = new List<MediaItem>() };

		var mediaItem1 = new MediaItem(MediaType.Picture, "test.jpg", null, null, null, null);
		flashcard1.Media.Add(mediaItem1);

		Assert.Multiple(() =>
		{
			Assert.That(flashcard2.Media, Is.Empty);
			Assert.That(flashcard1.Media.Count, Is.EqualTo(1));
		});
	}
}

public class MediaItemTests
{
	[Test]
	public void Constructor_InitializesAllPositionalProperties()
	{
		var mediaItem = new MediaItem(MediaType.Audio, "sound.mp3", "databytes", "http://url", "/path", "hash123");

		Assert.Multiple(() =>
		{
			Assert.That(mediaItem.Type, Is.EqualTo(MediaType.Audio));
			Assert.That(mediaItem.Filename, Is.EqualTo("sound.mp3"));
			Assert.That(mediaItem.Data, Is.EqualTo("databytes"));
			Assert.That(mediaItem.Url, Is.EqualTo("http://url"));
			Assert.That(mediaItem.Path, Is.EqualTo("/path"));
			Assert.That(mediaItem.SkipHash, Is.EqualTo("hash123"));
			Assert.That(mediaItem.Fields, Is.Empty);
		});
	}

	[Test]
	public void Fields_CanBeModified_AfterConstruction()
	{
		var mediaItem = new MediaItem(MediaType.Picture, "image.png", null, null, null, null);
		var fields = new[] { "Front", "Back" };

		mediaItem.Fields = fields;

		Assert.That(mediaItem.Fields, Is.SameAs(fields));
	}

	[Test]
	public void Fields_DefaultValue_IsEmptyArray()
	{
		var mediaItem = new MediaItem(MediaType.Video, "video.mp4", null, null, null, null);

		Assert.That(mediaItem.Fields, Is.Empty);
	}

	[Test]
	public void PositionalParameters_AreImmutable()
	{
		var mediaItem = new MediaItem(MediaType.Picture, "test.jpg", "data", "http://example.com", "/path", "hash");

		var type = mediaItem.Type;
		var filename = mediaItem.Filename;
		var data = mediaItem.Data;
		var url = mediaItem.Url;
		var path = mediaItem.Path;
		var skipHash = mediaItem.SkipHash;

		Assert.Multiple(() =>
		{
			Assert.That(type, Is.EqualTo(MediaType.Picture));
			Assert.That(filename, Is.EqualTo("test.jpg"));
			Assert.That(data, Is.EqualTo("data"));
			Assert.That(url, Is.EqualTo("http://example.com"));
			Assert.That(path, Is.EqualTo("/path"));
			Assert.That(skipHash, Is.EqualTo("hash"));
		});
	}
}