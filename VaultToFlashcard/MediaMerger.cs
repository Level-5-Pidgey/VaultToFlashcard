namespace VaultToFlashcard;

public class MediaMerger
{
	public static readonly HashSet<string> MediaFieldNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"audio", "sound",
		"image", "picture",
		"video", "visualization"
	};

	private static readonly HashSet<string> AudioFieldNames = new(StringComparer.OrdinalIgnoreCase)
		{ "audio", "sound" };

	private static readonly HashSet<string> ImageFieldNames = new(StringComparer.OrdinalIgnoreCase)
		{ "image", "picture" };

	public void Merge(IReadOnlyCollection<DynamicFlashcard> flashcards, IReadOnlyCollection<MediaItem> mediaItems)
	{
		if (!flashcards.Any() || !mediaItems.Any()) return;

		foreach (var media in mediaItems)
		{
			var targetCard = FindTargetCard(flashcards, media);

			if (targetCard != null)
			{
				// Set Fields on the MediaItem based on the target card's model
				var fieldName = DetermineFieldName(media.Type, targetCard);
				media.Fields = new[] { fieldName };
			}

			// Attach to first card (or any card that has the matching field)
			var cardToAttach = targetCard ?? flashcards.First();
			cardToAttach.Media.Add(media);
		}
	}

	public static DynamicFlashcard? FindTargetCard(IReadOnlyCollection<DynamicFlashcard> flashcards, MediaItem media)
	{
		var fieldNames = GetFieldNamesForType(media.Type);

		return flashcards.FirstOrDefault(card =>
			card.Fields.Keys.Any(k => fieldNames.Contains(k, StringComparer.OrdinalIgnoreCase)));
	}

	public static string DetermineFieldName(MediaType type, DynamicFlashcard card)
	{
		var fieldNames = GetFieldNamesForType(type);
		return card.Fields.Keys.First(k => fieldNames.Contains(k, StringComparer.OrdinalIgnoreCase));
	}

	public static HashSet<string> GetFieldNamesForType(MediaType type)
	{
		return type switch
		{
			MediaType.Audio => AudioFieldNames,
			MediaType.Picture => ImageFieldNames,
			MediaType.Video => ImageFieldNames,
			_ => ImageFieldNames
		};
	}

	public static bool IsAllMediaCardType(CardTypeDefinition cardType)
	{
		return cardType.JsonSchemaProperties.Keys.All(k =>
			MediaFieldNames.Contains(k, StringComparer.OrdinalIgnoreCase));
	}
}