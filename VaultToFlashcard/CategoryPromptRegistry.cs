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
				new CardTemplateItem { Name = "Basic", Front = "{{Front}}", Back = "{{Back}}" }
			}
		},
		["Cloze"] = new CardTemplateDefinition
		{
			Name = "Cloze",
			IsCloze = true,
			Templates = new List<CardTemplateItem>
			{
				new CardTemplateItem { Name = "Cloze", Front = "{{text}}", Back = "{{text}}" }
			}
		}
	};

	private static readonly CategoryPromptConfiguration DefaultConfiguration = new()
	{
		Category = "Default",
		Priority = -1,
		SystemPromptAddendum = "",
		AssistantPromptAddendum = "",
		CardTypeDefinitions = new List<CardTypeDefinition>
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

	public CategoryPromptRegistry(VaultPromptConfiguration? vaultConfig = null)
	{
		if (vaultConfig != null)
		{
			// Load templates from top-level cardTypes
			foreach (var template in vaultConfig.CardTypes)
				_cardTemplates[template.Name] = template;

			// Load categories
			_configurations.AddRange(vaultConfig.Categories);
		}
	}

	public CategoryPromptRegistry(IEnumerable<CategoryPromptConfiguration>? configurations = null)
	{
		if (configurations != null)
		{
			foreach (var config in configurations)
			{
				_configurations.Add(config);
				// Also register inline card type definitions as templates
				foreach (var ct in config.CardTypeDefinitions)
				{
					if (!_cardTemplates.ContainsKey(ct.ModelName))
					{
						var front = ct.Front ?? $"{{{{{ct.JsonSchemaProperties.Keys.First()}}}}}";
						var back = ct.Back ?? $"{{{{{ct.JsonSchemaProperties.Keys.Last()}}}}}";
						_cardTemplates[ct.ModelName] = new CardTemplateDefinition
						{
							Name = ct.ModelName,
							IsCloze = ct.IsCloze,
							Templates = new List<CardTemplateItem>
							{
								new CardTemplateItem
								{
									Name = ct.ModelName,
									Front = front,
									Back = back
								}
							}
						};
					}
				}
			}
		}
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

		// Start with a copy of defaults only if not skipping
		var effective = new CategoryPromptConfiguration
		{
			Category = matched.Category,
			Priority = matched.Priority,
			SystemPromptAddendum = matched.SystemPromptAddendum,
			AssistantPromptAddendum = matched.AssistantPromptAddendum,
			CardTypeNames = new List<string>(matched.CardTypeNames),
			CardTypeDefinitions = matched.SkipBasicTypes
				? new List<CardTypeDefinition>()
				: DefaultConfiguration.CardTypeDefinitions.Select(ct => new CardTypeDefinition
				{
					ModelName = ct.ModelName,
					JsonSchemaProperties = new Dictionary<string, string>(ct.JsonSchemaProperties),
					ExampleOutput = ct.ExampleOutput
				}).ToList()
		};

		// Overlay or add category card types (from CardTypeNames references)
		foreach (var typeName in matched.CardTypeNames)
		{
			var template = GetCardTemplate(typeName);
			if (template != null)
			{
				var existing = effective.CardTypeDefinitions.FirstOrDefault(ct =>
					ct.ModelName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					existing.Front = template.Templates.FirstOrDefault()?.Front;
					existing.Back = template.Templates.FirstOrDefault()?.Back;
					existing.IsCloze = template.IsCloze;
				}
				else
				{
					effective.CardTypeDefinitions.Add(new CardTypeDefinition
					{
						ModelName = typeName,
						Front = template.Templates.FirstOrDefault()?.Front,
						Back = template.Templates.FirstOrDefault()?.Back,
						IsCloze = template.IsCloze,
						JsonSchemaProperties = new Dictionary<string, string>()
					});
				}
			}
		}

		// Overlay or add inline category card type definitions
		foreach (var categoryCardType in matched.CardTypeDefinitions)
		{
			var existing = effective.CardTypeDefinitions.FirstOrDefault(ct =>
				ct.ModelName.Equals(categoryCardType.ModelName, StringComparison.OrdinalIgnoreCase));
			if (existing != null)
			{
				existing.JsonSchemaProperties = new Dictionary<string, string>(categoryCardType.JsonSchemaProperties);
				existing.ExampleOutput = categoryCardType.ExampleOutput;
				if (categoryCardType.Front != null) existing.Front = categoryCardType.Front;
				if (categoryCardType.Back != null) existing.Back = categoryCardType.Back;
				existing.IsCloze = categoryCardType.IsCloze;
			}
			else
			{
				effective.CardTypeDefinitions.Add(new CardTypeDefinition
				{
					ModelName = categoryCardType.ModelName,
					JsonSchemaProperties = new Dictionary<string, string>(categoryCardType.JsonSchemaProperties),
					ExampleOutput = categoryCardType.ExampleOutput,
					Front = categoryCardType.Front,
					Back = categoryCardType.Back,
					IsCloze = categoryCardType.IsCloze
				});
			}
		}

		// If skipBasicTypes but no custom types added, revert to defaults
		if (matched.SkipBasicTypes && effective.CardTypeDefinitions.Count == 0)
		{
			effective.CardTypeDefinitions = DefaultConfiguration.CardTypeDefinitions.Select(ct => new CardTypeDefinition
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
		foreach (var cardType in config.CardTypeDefinitions)
			models.Add(cardType.ModelName);

		// Add from default configuration
		foreach (var cardType in DefaultConfiguration.CardTypeDefinitions) models.Add(cardType.ModelName);

		return models;
	}

	public IReadOnlyCollection<(string Name, CardTemplateDefinition? Template)> GetAllRequiredModelsWithTemplates()
	{
		var result = new Dictionary<string, CardTemplateDefinition?>();

		// Add from custom configurations
		foreach (var config in _configurations)
		{
			foreach (var typeName in config.CardTypeNames)
			{
				if (!result.ContainsKey(typeName))
					result[typeName] = GetCardTemplate(typeName);
			}
			foreach (var ct in config.CardTypeDefinitions)
			{
				if (!result.ContainsKey(ct.ModelName))
					result[ct.ModelName] = GetCardTemplate(ct.ModelName);
			}
		}

		// Add default templates
		foreach (var (name, template) in DefaultTemplates)
		{
			if (!result.ContainsKey(name))
				result[name] = template;
		}

		return result.Select(kvp => (kvp.Key, kvp.Value)).ToList();
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
