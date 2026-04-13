using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class MediaMergerTests
{
	[Test]
	public void GetFieldNamesForType_Audio_ReturnsAudioAndSound()
	{
		var result = MediaMerger.GetFieldNamesForType(MediaType.Audio);

		Assert.That(result, Is.EquivalentTo(new[] { "audio", "sound" }));
	}

	[Test]
	public void GetFieldNamesForType_Picture_ReturnsImageAndPicture()
	{
		var result = MediaMerger.GetFieldNamesForType(MediaType.Picture);

		Assert.That(result, Is.EquivalentTo(new[] { "image", "picture" }));
	}

	[Test]
	public void GetFieldNamesForType_Video_ReturnsImageAndPicture()
	{
		var result = MediaMerger.GetFieldNamesForType(MediaType.Video);

		Assert.That(result, Is.EquivalentTo(new[] { "image", "picture" }));
	}

	[Test]
	public void DetermineFieldName_FindsMatchingFieldCaseInsensitive_ReturnsFieldName()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["AUDIO"] = "value" });

		var result = MediaMerger.DetermineFieldName(MediaType.Audio, card);

		Assert.That(result, Is.EqualTo("AUDIO"));
	}

	[Test]
	public void DetermineFieldName_NoMatchingField_Throws()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "value" });

		Assert.Throws<InvalidOperationException>(() =>
			MediaMerger.DetermineFieldName(MediaType.Audio, card));
	}

	[Test]
	public void DetermineFieldName_PictureType_FindsImageField()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["Image"] = "value" });

		var result = MediaMerger.DetermineFieldName(MediaType.Picture, card);

		Assert.That(result, Is.EqualTo("Image"));
	}

	[Test]
	public void FindTargetCard_FindsCardByFieldName_ReturnsCard()
	{
		var card1 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "a" });
		var card2 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["AUDIO"] = "b" });
		var card3 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["back"] = "c" });
		var flashcards = new[] { card1, card2, card3 };
		var media = new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null);

		var result = MediaMerger.FindTargetCard(flashcards, media);

		Assert.That(result, Is.EqualTo(card2));
	}

	[Test]
	public void FindTargetCard_CaseInsensitive_ReturnsCard()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["Sound"] = "value" });
		var flashcards = new[] { card };
		var media = new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null);

		var result = MediaMerger.FindTargetCard(flashcards, media);

		Assert.That(result, Is.EqualTo(card));
	}

	[Test]
	public void FindTargetCard_NoMatch_ReturnsNull()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "a" });
		var flashcards = new[] { card };
		var media = new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null);

		var result = MediaMerger.FindTargetCard(flashcards, media);

		Assert.That(result, Is.Null);
	}

	[Test]
	public void Merge_EmptyFlashcards_NoAttachment()
	{
		var flashcards = Array.Empty<DynamicFlashcard>();
		var mediaItems = new[] { new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null) };

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.That(mediaItems[0].Fields, Is.Empty);
	}

	[Test]
	public void Merge_EmptyMedia_NoChange()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "a" });
		var flashcards = new[] { card };
		var mediaItems = Array.Empty<MediaItem>();

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.That(card.Media, Is.Empty);
	}

	[Test]
	public void Merge_MediaAttachedToFirstCardWithMatchingField()
	{
		var card1 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "a" });
		var card2 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["AUDIO"] = "b" });
		var flashcards = new List<DynamicFlashcard> { card1, card2 };
		var mediaItems = new[] { new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null) };

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.That(card2.Media, Does.Contain(mediaItems[0]));
		Assert.That(card1.Media, Is.Empty);
	}

	[Test]
	public void Merge_MediaAttachedToFirstCardWhenNoFieldMatch()
	{
		var card1 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["front"] = "a" });
		var card2 = new DynamicFlashcard("Model", new Dictionary<string, string> { ["back"] = "b" });
		var flashcards = new List<DynamicFlashcard> { card1, card2 };
		var mediaItems = new[] { new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null) };

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.That(card1.Media, Does.Contain(mediaItems[0]));
	}

	[Test]
	public void Merge_MediaItemFieldsPropertySetAfterMerge()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["AUDIO"] = "value" });
		var flashcards = new[] { card };
		var mediaItems = new[] { new MediaItem(MediaType.Audio, "test.mp3", null, null, null, null) };

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.That(mediaItems[0].Fields, Is.EquivalentTo(new[] { "AUDIO" }));
	}

	[Test]
	public void Merge_MultipleMediaItems_AttachesAll()
	{
		var card = new DynamicFlashcard("Model", new Dictionary<string, string> { ["AUDIO"] = "a", ["IMAGE"] = "b" });
		var flashcards = new[] { card };
		var mediaItems = new[]
		{
			new MediaItem(MediaType.Audio, "test1.mp3", null, null, null, null),
			new MediaItem(MediaType.Picture, "test2.jpg", null, null, null, null)
		};

		var merger = new MediaMerger();
		merger.Merge(flashcards, mediaItems);

		Assert.Multiple(() =>
		{
			Assert.That(card.Media, Has.Count.EqualTo(2));
			Assert.That(mediaItems[0].Fields, Is.EquivalentTo(new[] { "AUDIO" }));
			Assert.That(mediaItems[1].Fields, Is.EquivalentTo(new[] { "IMAGE" }));
		});
	}
}