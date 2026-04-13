using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VaultToFlashcard;

public class CategoryPromptRegistry
{
	private readonly List<CategoryPromptConfiguration> _configurations = new();
	private readonly Dictionary<string, CardTemplateDefinition> _cardTemplates = new();

	private static readonly Dictionary<string, CardTemplateDefinition> DefaultTemplates = new()
	{
		["Basic"] = new CardTemplateDefinition
		{
			Name = "Basic",
			IsCloze = false,
			Templates = new List<CardTemplateItem>
			{
				new() { Name = "Basic", Front = "{{Front}}", Back = "{{Back}}" }
			},
			JsonSchemaProperties = new Dictionary<string, string>
			{
				["front"] = "The question or prompt for the front of the card",
				["back"] = "The answer for the back of the card"
			},
			ExampleOutput =
				"""{"front": "When should a Trie be used over a Hash Map?", "back": "When you need efficient prefix-based searching/auto-complete."}"""
		},
		["Cloze"] = new CardTemplateDefinition
		{
			Name = "Cloze",
			IsCloze = true,
			Templates = new List<CardTemplateItem>
			{
				new() { Name = "Cloze", Front = "{{text}}", Back = "{{text}}" }
			},
			JsonSchemaProperties = new Dictionary<string, string>
			{
				["text"] =
					"Content with cloze deletions using {{c1::answer::hint}} format. Must have at least two clozes."
			},
			ExampleOutput =
				"""{"text": "The three main concurrency primitives in Go are: {{c1::Goroutines::lightweight threads}}, {{c2::Channels::communication mechanism}, and {{c3::Select Statement::multiplexing mechanism}}"}"""
		}
	};

	private static readonly CategoryPromptConfiguration DefaultConfiguration = new()
	{
		Category = "Default",
		Priority = -1,
		SystemPromptAddendum = "",
		AssistantPromptAddendum = "",
		CardTypes = new List<string> { "Basic", "Cloze" }
	};

	public CategoryPromptRegistry(VaultPromptConfiguration vaultConfig)
	{
		// Load templates from top-level cardTypes
		foreach (var template in vaultConfig.CardTypes)
			_cardTemplates[template.Name] = template;

		// Load categories
		_configurations.AddRange(vaultConfig.Categories);
	}

	/// <summary>
	/// Creates a registry with default card types (Basic, Cloze) and no custom categories.
	/// </summary>
	public CategoryPromptRegistry()
	{
		// Use default templates from DefaultTemplates
	}

	public CategoryPromptConfiguration GetDefaultConfiguration()
	{
		return DefaultConfiguration;
	}

	public CardTemplateDefinition? GetCardTemplate(string name)
	{
		if (_cardTemplates.TryGetValue(name, out var template))
			return template;
		if (DefaultTemplates.TryGetValue(name, out var defaultTemplate))
			return defaultTemplate;
		return null;
	}

	public CategoryPromptConfiguration GetEffectiveConfiguration(IReadOnlyCollection<string>? noteCategories)
	{
		var matched = FindBestMatch(noteCategories);
		if (matched == null) return DefaultConfiguration;

		// Start with defaults (Basic, Cloze) unless skipped
		var effective = new CategoryPromptConfiguration
		{
			Category = matched.Category,
			Priority = matched.Priority,
			SystemPromptAddendum = matched.SystemPromptAddendum,
			AssistantPromptAddendum = matched.AssistantPromptAddendum,
			SkipBasicTypes = matched.SkipBasicTypes,
			CardTypes = matched.SkipBasicTypes
				? new List<string>()
				: new List<string>(DefaultConfiguration.CardTypes)
		};

		// Add/override with category's card types
		foreach (var typeName in matched.CardTypes)
			if (!effective.CardTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
				effective.CardTypes.Add(typeName);

		// If skipBasicTypes but no custom types, revert to defaults
		if (matched.SkipBasicTypes && effective.CardTypes.Count == 0)
			effective.CardTypes = new List<string>(DefaultConfiguration.CardTypes);

		return effective;
	}

	/// <summary>
	/// Returns effective CardTypeDefinition objects for AI schema generation.
	/// </summary>
	/// <param name="noteCategories">Categories to match for configuration.</param>
	/// <param name="excludeMediaFields">If true, excludes media fields (front, back, image, audio) from JsonSchemaProperties.</param>
	public IReadOnlyList<CardTypeDefinition> GetEffectiveCardTypes(
		IReadOnlyCollection<string>? noteCategories,
		bool excludeMediaFields = false)
	{
		var effectiveConfig = GetEffectiveConfiguration(noteCategories);
		var result = new List<CardTypeDefinition>();
		var excludeSet = excludeMediaFields ? MediaMerger.MediaFieldNames : null;

		foreach (var typeName in effectiveConfig.CardTypes)
		{
			var template = GetCardTemplate(typeName);
			if (template != null)
			{
				var properties = excludeMediaFields
					? template.JsonSchemaProperties
						.Where(kvp => !MediaMerger.MediaFieldNames.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
						.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
					: template.JsonSchemaProperties;

				result.Add(new CardTypeDefinition
				{
					ModelName = template.Name,
					JsonSchemaProperties = new Dictionary<string, string>(properties),
					ExampleOutput = template.ExampleOutput,
					Front = template.Templates.FirstOrDefault()?.Front,
					Back = template.Templates.FirstOrDefault()?.Back,
					IsCloze = template.IsCloze
				});
			}
		}

		return result;
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

		// Return highest priority match (clone to prevent mutation of stored config)
		var matched = matchedConfigs
			.OrderByDescending(c => c.Priority)
			.FirstOrDefault();

		if (matched == null) return null;

		// Return a shallow clone to prevent mutation of the stored configuration
		return new CategoryPromptConfiguration
		{
			Category = matched.Category,
			Priority = matched.Priority,
			SystemPromptAddendum = matched.SystemPromptAddendum,
			AssistantPromptAddendum = matched.AssistantPromptAddendum,
			SkipBasicTypes = matched.SkipBasicTypes,
			CardTypes = new List<string>(matched.CardTypes)
		};
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
			models.Add(cardType);

		// Add from default configuration
		foreach (var cardType in DefaultConfiguration.CardTypes) models.Add(cardType);

		return models;
	}

	/// <summary>
	/// Returns all required model names with their template definitions.
	/// Only includes templates that exist (custom or default).
	/// </summary>
	public IReadOnlyCollection<(string Name, CardTemplateDefinition Template)> GetAllRequiredModelsWithTemplates()
	{
		var result = new Dictionary<string, CardTemplateDefinition>(StringComparer.OrdinalIgnoreCase);

		// Add from custom configurations (templates by name reference)
		foreach (var config in _configurations)
		foreach (var typeName in config.CardTypes)
			if (!result.ContainsKey(typeName))
			{
				var template = GetCardTemplate(typeName);
				if (template != null)
					result[typeName] = template;
			}

		// Add default templates
		foreach (var (name, template) in DefaultTemplates)
			if (!result.ContainsKey(name))
				result[name] = template;

		return result.Select(kvp => (kvp.Key, kvp.Value)).ToList();
	}

	private static JsonElement BuildProperties(Dictionary<string, string> properties,
		HashSet<string>? excludeFields = null)
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