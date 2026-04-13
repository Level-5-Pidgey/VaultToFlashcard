using System.Text.Json;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class CategoryPromptRegistryTests
{
	[Test]
	public void BuildGroupedJsonSchema_WithTwoCardTypes_ReturnsObjectWithBothKeys()
	{
		var cardTypes = new List<CardTypeDefinition>
		{
			new()
			{
				ModelName = "Basic",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["front"] = "question",
					["back"] = "answer"
				}
			},
			new()
			{
				ModelName = "Cloze",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["text"] = "cloze content"
				}
			}
		};

		var schema = CategoryPromptRegistry.BuildGroupedJsonSchema(cardTypes);

		Assert.Multiple(() =>
		{
			Assert.That(schema.ValueKind, Is.EqualTo(JsonValueKind.Object));
			Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
			Assert.That(props.TryGetProperty("Basic", out _), Is.True);
			Assert.That(props.TryGetProperty("Cloze", out _), Is.True);
		});
	}

	[Test]
	public void BuildGroupedJsonSchema_SingleType_HasOneKey()
	{
		var cardTypes = new List<CardTypeDefinition>
		{
			new()
			{
				ModelName = "Basic",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["front"] = "q",
					["back"] = "a"
				}
			}
		};

		var schema = CategoryPromptRegistry.BuildGroupedJsonSchema(cardTypes);

		Assert.Multiple(() =>
		{
			Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
			Assert.That(props.TryGetProperty("Basic", out var basicProp), Is.True);
			Assert.That(basicProp.ValueKind, Is.EqualTo(JsonValueKind.Object));
			Assert.That(props.TryGetProperty("Cloze", out _), Is.False);
		});
	}

	[Test]
	public void BuildGroupedSchemaDescription_ReturnsReadableDescription()
	{
		var cardTypes = new List<CardTypeDefinition>
		{
			new()
			{
				ModelName = "Basic",
				JsonSchemaProperties = new Dictionary<string, string> { ["front"] = "q", ["back"] = "a" }
			},
			new() { ModelName = "Cloze", JsonSchemaProperties = new Dictionary<string, string> { ["text"] = "c" } }
		};

		var desc = CategoryPromptRegistry.BuildGroupedSchemaDescription(cardTypes);

		Assert.Multiple(() =>
		{
			Assert.That(desc, Does.Contain("Basic"));
			Assert.That(desc, Does.Contain("Cloze"));
			Assert.That(desc, Does.Contain("array"));
		});
	}

	[Test]
	public void FindBestMatch_ExactMatchCaseInsensitive_ReturnsMatchingConfig()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Programming",
					Priority = 1,
					CardTypes = new List<string> { "Basic" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.FindBestMatch(new[] { "Programming" });

		Assert.That(result, Is.Not.Null);
		Assert.That(result!.Category, Is.EqualTo("Programming"));
	}

	[Test]
	public void FindBestMatch_NoMatch_ReturnsNull()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Programming",
					Priority = 1,
					CardTypes = new List<string> { "Basic" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.FindBestMatch(new[] { "NonExistent" });

		Assert.That(result, Is.Null);
	}

	[Test]
	public void FindBestMatch_PriorityOrdering_ReturnsHighestPriority()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Test",
					Priority = 1,
					CardTypes = new List<string> { "Basic" }
				},
				new()
				{
					Category = "Programming",
					Priority = 10,
					CardTypes = new List<string> { "Basic" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.FindBestMatch(new[] { "Programming" });

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.Not.Null);
			Assert.That(result!.Priority, Is.EqualTo(10));
		});
	}

	[Test]
	public void FindBestMatch_NullCategories_ReturnsNull()
	{
		var registry = new CategoryPromptRegistry();

		var result = registry.FindBestMatch(null);

		Assert.That(result, Is.Null);
	}

	[Test]
	public void FindBestMatch_EmptyCategories_ReturnsNull()
	{
		var registry = new CategoryPromptRegistry();

		var result = registry.FindBestMatch(Array.Empty<string>());

		Assert.That(result, Is.Null);
	}

	[Test]
	public void GetEffectiveConfiguration_NoMatch_ReturnsDefaultConfiguration()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Programming",
					Priority = 1,
					CardTypes = new List<string> { "Basic" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetEffectiveConfiguration(new[] { "NonExistent" });

		Assert.Multiple(() =>
		{
			Assert.That(result.Category, Is.EqualTo("Default"));
			Assert.That(result.CardTypes, Is.EquivalentTo(new[] { "Basic", "Cloze" }));
		});
	}

	[Test]
	public void GetEffectiveConfiguration_Match_AddsCategoryCardTypes()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Programming",
					Priority = 1,
					CardTypes = new List<string> { "Code" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetEffectiveConfiguration(new[] { "Programming" });

		Assert.Multiple(() =>
		{
			Assert.That(result.Category, Is.EqualTo("Programming"));
			Assert.That(result.CardTypes, Is.EquivalentTo(new[] { "Basic", "Cloze", "Code" }));
		});
	}

	[Test]
	public void GetEffectiveConfiguration_SkipBasicTypesWithCustomTypes_OnlyCustomTypes()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Custom",
					Priority = 1,
					SkipBasicTypes = true,
					CardTypes = new List<string> { "CustomCard" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetEffectiveConfiguration(new[] { "Custom" });

		Assert.Multiple(() =>
		{
			Assert.That(result.CardTypes, Is.EquivalentTo(new[] { "CustomCard" }));
			Assert.That(result.CardTypes.Contains("Basic"), Is.False);
			Assert.That(result.CardTypes.Contains("Cloze"), Is.False);
		});
	}

	[Test]
	public void GetEffectiveConfiguration_SkipBasicTypesWithoutCustomTypes_FallsBackToDefaults()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Custom",
					Priority = 1,
					SkipBasicTypes = true
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetEffectiveConfiguration(new[] { "Custom" });

		Assert.Multiple(() => { Assert.That(result.CardTypes, Is.EquivalentTo(new[] { "Basic", "Cloze" })); });
	}

	[Test]
	public void GetEffectiveCardTypes_ReturnsCardTypesWithSchema()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			CardTypes = new List<CardTemplateDefinition>
			{
				new()
				{
					Name = "Custom",
					JsonSchemaProperties = new Dictionary<string, string> { ["front"] = "q", ["back"] = "a" },
					ExampleOutput = "{\"front\":\"q\",\"back\":\"a\"}",
					Templates = new List<CardTemplateItem>
					{
						new() { Name = "Custom", Front = "{{Front}}", Back = "{{Back}}" }
					}
				}
			},
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Test",
					Priority = 1,
					SkipBasicTypes = true,
					CardTypes = new List<string> { "Custom" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetEffectiveCardTypes(new[] { "Test" });

		Assert.Multiple(() =>
		{
			Assert.That(result.Count, Is.EqualTo(1));
			Assert.That(result[0].ModelName, Is.EqualTo("Custom"));
			Assert.That(result[0].JsonSchemaProperties["front"], Is.EqualTo("q"));
			Assert.That(result[0].ExampleOutput, Is.EqualTo("{\"front\":\"q\",\"back\":\"a\"}"));
		});
	}

	[Test]
	public void GetAllRequiredModelNames_ReturnsAllUniqueNames()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Config1",
					Priority = 1,
					CardTypes = new List<string> { "CustomType1", "SharedType" }
				},
				new()
				{
					Category = "Config2",
					Priority = 1,
					CardTypes = new List<string> { "CustomType2", "SharedType" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetAllRequiredModelNames();

		Assert.That(result, Is.EquivalentTo(new[]
		{
			"CustomType1", "SharedType", "CustomType2", "Basic", "Cloze"
		}));
	}

	[Test]
	public void BuildGroupedJsonSchema_FiltersMediaFields_ReturnsSchemaWithoutMediaFields()
	{
		var cardTypes = new List<CardTypeDefinition>
		{
			new()
			{
				ModelName = "Sentences",
				JsonSchemaProperties = new Dictionary<string, string>
				{
					["front"] = "Japanese sentence",
					["back"] = "English translation",
					["audio"] = "Audio file for pronunciation",
					["picture"] = "Image of concept"
				}
			}
		};

		var schema = CategoryPromptRegistry.BuildGroupedJsonSchema(cardTypes);

		Assert.Multiple(() =>
		{
			Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
			Assert.That(props.TryGetProperty("Sentences", out var sentenceProp), Is.True);
			Assert.That(sentenceProp.TryGetProperty("items", out var items), Is.True);
			Assert.That(items.TryGetProperty("properties", out var sentProps), Is.True);

			// Verify media fields are filtered
			Assert.That(sentProps.TryGetProperty("front", out _), Is.True);
			Assert.That(sentProps.TryGetProperty("back", out _), Is.True);
			Assert.That(sentProps.TryGetProperty("audio", out _), Is.False);
			Assert.That(sentProps.TryGetProperty("picture", out _), Is.False);

			// Verify media fields are not in required array
			Assert.That(items.TryGetProperty("required", out var required), Is.True);
			Assert.That(required.EnumerateArray().Select(e => e.GetString()).ToList(), Does.Not.Contains("audio"));
			Assert.That(required.EnumerateArray().Select(e => e.GetString()).ToList(), Does.Not.Contains("picture"));
		});
	}

	[Test]
	public void GetCardTemplate_ReturnsTemplateByName()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			CardTypes = new List<CardTemplateDefinition>
			{
				new()
				{
					Name = "Custom",
					IsCloze = false,
					Templates = new List<CardTemplateItem>
					{
						new() { Name = "Custom", Front = "{{Front}}", Back = "{{Back}}" }
					}
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetCardTemplate("Custom");

		Assert.That(result, Is.Not.Null);
		Assert.That(result!.Name, Is.EqualTo("Custom"));
		Assert.That(result.IsCloze, Is.False);
	}

	[Test]
	public void GetCardTemplate_FallsBackToDefault_WhenNotFound()
	{
		var registry = new CategoryPromptRegistry();

		var result = registry.GetCardTemplate("Basic");

		Assert.That(result, Is.Not.Null);
		Assert.That(result!.Name, Is.EqualTo("Basic"));
	}

	[Test]
	public void GetAllRequiredModelsWithTemplates_ReturnsTemplates()
	{
		var vaultConfig = new VaultPromptConfiguration
		{
			CardTypes = new List<CardTemplateDefinition>
			{
				new()
				{
					Name = "Custom",
					IsCloze = false,
					Templates = new List<CardTemplateItem>
					{
						new() { Name = "Custom", Front = "{{Front}}", Back = "{{Back}}" }
					}
				}
			},
			Categories = new List<CategoryPromptConfiguration>
			{
				new()
				{
					Category = "Test",
					Priority = 1,
					CardTypes = new List<string> { "Custom" }
				}
			}
		};
		var registry = new CategoryPromptRegistry(vaultConfig);

		var result = registry.GetAllRequiredModelsWithTemplates();

		var customTemplate = result.FirstOrDefault(r => r.Name == "Custom");
		Assert.Multiple(() =>
		{
			Assert.That(result.Any(r => r.Name == "Custom"), Is.True);
			Assert.That(customTemplate.Template, Is.Not.Null);
			Assert.That(customTemplate.Template!.Templates[0].Front, Is.EqualTo("{{Front}}"));
		});
	}
}