using System.Text.Json;

namespace VaultToFlashcard;

public readonly struct CardValidationResult(bool isValid, HashSet<string> invalidFields, HashSet<string> missingFields)
{
	public bool IsValid { get; } = isValid;
	public HashSet<string> InvalidFields { get; } = invalidFields;
	public HashSet<string> MissingFields { get; } = missingFields;
}

public static class CardValidation
{
	public static CardValidationResult ValidateCard(CardTypeDefinition cardType, JsonElement card)
	{
		var invalidFields = new HashSet<string>();
		var missingFields = new HashSet<string>();

		var expectedFields = cardType.JsonSchemaProperties.Keys.ToHashSet();
		var actualFields = new HashSet<string>();

		foreach (var prop in card.EnumerateObject())
			actualFields.Add(prop.Name);

		foreach (var field in expectedFields)
			if (!actualFields.Contains(field))
				missingFields.Add(field);

		foreach (var field in actualFields)
			if (!expectedFields.Contains(field))
				invalidFields.Add(field);

		return new CardValidationResult(missingFields.Count == 0 && invalidFields.Count == 0, invalidFields,
			missingFields);
	}
}