using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VaultToFlashcard;

public class CategoryPromptRegistry
{
	private readonly List<CategoryPromptConfiguration> _configurations = new();

	private static readonly CategoryPromptConfiguration DefaultConfiguration = new()
	{
		Category = "Default",
		Priority = -1,
		SystemPromptAddendum = "",
		AssistantPromptAddendum = "",
		CardTypes = new List<CardTypeDefinition>
		{
			new()
			{
				ModelName = "Basic",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["front"] = "The question or prompt for the front of the card",
					["back"] = "The answer for the back of the card"
				},
				ExampleOutput =
					"""{"front": "When should a Trie be used over a Hash Map?", "back": "When you need efficient prefix-based searching/auto-complete."}"""
			},
			new()
			{
				ModelName = "Cloze",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["text"] =
						"Content with cloze deletions using {{c1::answer::hint}} format. Must have at least two clozes."
				},
				ExampleOutput =
					"""{"text": "The three main concurrency primitives in Go are: {{c1::Goroutines::lightweight threads}}, {{c2::Channels::communication mechanism}}, and {{c3::Select Statement::multiplexing mechanism}}"}"""
			}
		}
	};

	public CategoryPromptRegistry(IEnumerable<CategoryPromptConfiguration>? configurations = null)
	{
		if (configurations != null) _configurations.AddRange(configurations);
	}

	public CategoryPromptConfiguration GetDefaultConfiguration()
	{
		return DefaultConfiguration;
	}

	public CategoryPromptConfiguration GetEffectiveConfiguration(IReadOnlyCollection<string>? noteCategories)
	{
		var matched = FindBestMatch(noteCategories);
		if (matched == null) return DefaultConfiguration;

		// Start with a copy of defaults only if not skipping
		var effective = new CategoryPromptConfiguration
		{
			Category = matched.Category,
			Priority = matched.Priority,
			SystemPromptAddendum = matched.SystemPromptAddendum,
			AssistantPromptAddendum = matched.AssistantPromptAddendum,
			CardTypes = matched.SkipBasicTypes
				? new List<CardTypeDefinition>()
				: DefaultConfiguration.CardTypes.Select(ct => new CardTypeDefinition
				  {
					  ModelName = ct.ModelName,
					  JsonSchemaProperties = new Dictionary<string, string>(ct.JsonSchemaProperties),
					  ExampleOutput = ct.ExampleOutput
				  }).ToList()
		};

		// Overlay or add category card types
		foreach (var categoryCardType in matched.CardTypes)
		{
			var existing = effective.CardTypes.FirstOrDefault(ct =>
				ct.ModelName.Equals(categoryCardType.ModelName, StringComparison.OrdinalIgnoreCase));
			if (existing != null)
			{
				existing.JsonSchemaProperties = new Dictionary<string, string>(categoryCardType.JsonSchemaProperties);
				existing.ExampleOutput = categoryCardType.ExampleOutput;
			}
			else
			{
				effective.CardTypes.Add(new CardTypeDefinition
				{
					ModelName = categoryCardType.ModelName,
					JsonSchemaProperties = new Dictionary<string, string>(categoryCardType.JsonSchemaProperties),
					ExampleOutput = categoryCardType.ExampleOutput
				});
			}
		}

		// If skipBasicTypes but no custom types added, revert to defaults
		if (matched.SkipBasicTypes && effective.CardTypes.Count == 0)
		{
			effective.CardTypes = DefaultConfiguration.CardTypes.Select(ct => new CardTypeDefinition
			{
				ModelName = ct.ModelName,
				JsonSchemaProperties = new Dictionary<string, string>(ct.JsonSchemaProperties),
				ExampleOutput = ct.ExampleOutput
			}).ToList();
		}

		return effective;
	}

	public CategoryPromptConfiguration? FindBestMatch(IReadOnlyCollection<string>? noteCategories)
	{
		if (noteCategories == null || !noteCategories.Any()) return null;

		var matchedConfigs = new List<CategoryPromptConfiguration>();

		foreach (var noteCat in noteCategories)
		{
			var config = _configurations
				.FirstOrDefault(c => c.Category.Equals(noteCat, StringComparison.OrdinalIgnoreCase));

			if (config != null) matchedConfigs.Add(config);
		}

		// Return highest priority match
		return matchedConfigs
			.OrderByDescending(c => c.Priority)
			.FirstOrDefault();
	}

	public IReadOnlyCollection<string> GetAllConfiguredCategoryNames()
	{
		return _configurations.Select(c => c.Category).ToList();
	}

	public IReadOnlyCollection<string> GetAllRequiredModelNames()
	{
		var models = new HashSet<string>();

		// Add from custom configurations
		foreach (var config in _configurations)
		foreach (var cardType in config.CardTypes)
			models.Add(cardType.ModelName);

		// Add from default configuration
		foreach (var cardType in DefaultConfiguration.CardTypes) models.Add(cardType.ModelName);

		return models;
	}

	private static JsonElement BuildProperties(Dictionary<string, string> properties, HashSet<string>? excludeFields = null)
	{
		var propsObj = new Dictionary<string, object>();
		foreach (var (key, description) in properties)
		{
			if (excludeFields != null && excludeFields.Contains(key, StringComparer.OrdinalIgnoreCase))
				continue;
			propsObj[key] = new Dictionary<string, string>
			{
				["type"] = "string",
				["description"] = description
			};
		}

		var json = JsonSerializer.Serialize(propsObj);
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.Clone();
	}

	public static string BuildSchemaDescription(CardTypeDefinition cardType)
	{
		var fieldList = string.Join(", ", cardType.JsonSchemaProperties.Keys);
		return $"Anki {cardType.ModelName} card with fields: {fieldList}";
	}

	public static JsonElement BuildGroupedJsonSchema(IReadOnlyList<CardTypeDefinition> cardTypes)
	{
		var properties = new Dictionary<string, object>();

		foreach (var cardType in cardTypes)
		{
			var itemSchema = new Dictionary<string, object>
			{
				["type"] = "object",
				["additionalProperties"] = false,
				["properties"] = BuildProperties(cardType.JsonSchemaProperties, MediaMerger.MediaFieldNames),
				["required"] = cardType.JsonSchemaProperties.Keys
					.Where(k => !MediaMerger.MediaFieldNames.Contains(k, StringComparer.OrdinalIgnoreCase))
					.ToList()
			};

			properties[cardType.ModelName] = new Dictionary<string, object>
			{
				["type"] = "array",
				["items"] = itemSchema
			};
		}

		var schemaObj = new Dictionary<string, object>
		{
			["type"] = "object",
			["properties"] = properties
		};

		var json = JsonSerializer.Serialize(schemaObj);
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.Clone();
	}

	public static string BuildGroupedSchemaDescription(IReadOnlyList<CardTypeDefinition> cardTypes)
	{
		var typeList = string.Join(", ", cardTypes.Select(ct => ct.ModelName));
		return $"Anki cards with types: {typeList}. Each type is an array of cards.";
	}
}