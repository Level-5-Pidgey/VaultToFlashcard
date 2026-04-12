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
            new() { ModelName = "Basic", JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" } },
            new() { ModelName = "Cloze", JsonSchemaProperties = new() { ["text"] = "c" } }
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
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Programming",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "Code", JsonSchemaProperties = new() { ["snippet"] = "code snippet" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.FindBestMatch(new[] { "Programming" });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Category, Is.EqualTo("Programming"));
    }

    [Test]
    public void FindBestMatch_NoMatch_ReturnsNull()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Programming",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "Code", JsonSchemaProperties = new() { ["snippet"] = "code snippet" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.FindBestMatch(new[] { "NonExistent" });

        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindBestMatch_PriorityOrdering_ReturnsHighestPriority()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Test",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "LowPriority", JsonSchemaProperties = new() { ["field"] = "desc" } }
                }
            },
            new()
            {
                Category = "Programming",
                Priority = 10,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "HighPriority", JsonSchemaProperties = new() { ["field"] = "desc" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        // When searching for ["Programming"], it should find the Programming config at Priority=10
        var result = registry.FindBestMatch(new[] { "Programming" });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Priority, Is.EqualTo(10));
            Assert.That(result.CardTypes[0].ModelName, Is.EqualTo("HighPriority"));
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
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Programming",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "Code", JsonSchemaProperties = new() { ["snippet"] = "code snippet" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetEffectiveConfiguration(new[] { "NonExistent" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Category, Is.EqualTo("Default"));
            Assert.That(result.CardTypes.Select(ct => ct.ModelName), Is.EquivalentTo(new[] { "Basic", "Cloze" }));
        });
    }

    [Test]
    public void GetEffectiveConfiguration_Match_AddsNewCardTypes()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Programming",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "Code", JsonSchemaProperties = new() { ["snippet"] = "code snippet" }, ExampleOutput = "{\"snippet\":\"...\"}" }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetEffectiveConfiguration(new[] { "Programming" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Category, Is.EqualTo("Programming"));
            Assert.That(result.CardTypes.Select(ct => ct.ModelName), Is.EquivalentTo(new[] { "Basic", "Cloze", "Code" }));
        });
    }

    [Test]
    public void GetEffectiveConfiguration_MatchWithSameName_OverwritesDefault()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Programming",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new()
                    {
                        ModelName = "Cloze",
                        JsonSchemaProperties = new() { ["text"] = "custom cloze description" },
                        ExampleOutput = "{\"text\":\"custom\"}"
                    }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetEffectiveConfiguration(new[] { "Programming" });

        var clozeCard = result.CardTypes.First(ct => ct.ModelName == "Cloze");
        Assert.Multiple(() =>
        {
            Assert.That(result.CardTypes.Count, Is.EqualTo(2)); // Basic + modified Cloze
            Assert.That(clozeCard.JsonSchemaProperties["text"], Is.EqualTo("custom cloze description"));
            Assert.That(clozeCard.ExampleOutput, Is.EqualTo("{\"text\":\"custom\"}"));
        });
    }

    [Test]
    public void GetAllRequiredModelNames_ReturnsAllUniqueNames()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Config1",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "CustomType1", JsonSchemaProperties = new() { ["f1"] = "d1" } },
                    new() { ModelName = "SharedType", JsonSchemaProperties = new() { ["f2"] = "d2" } }
                }
            },
            new()
            {
                Category = "Config2",
                Priority = 1,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "CustomType2", JsonSchemaProperties = new() { ["f3"] = "d3" } },
                    new() { ModelName = "SharedType", JsonSchemaProperties = new() { ["f4"] = "d4" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetAllRequiredModelNames();

        Assert.That(result, Is.EquivalentTo(new[]
        {
            "CustomType1", "SharedType", "CustomType2", "Basic", "Cloze"
        }));
    }

    [Test]
    public void GetEffectiveConfiguration_SkipBasicTypesWithCustomTypes_OnlyCustomTypes()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Custom",
                Priority = 1,
                SkipBasicTypes = true,
                CardTypes = new List<CardTypeDefinition>
                {
                    new() { ModelName = "CustomCard", JsonSchemaProperties = new() { ["field"] = "custom field" } }
                }
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetEffectiveConfiguration(new[] { "Custom" });

        Assert.Multiple(() =>
        {
            Assert.That(result.CardTypes.Select(ct => ct.ModelName), Is.EquivalentTo(new[] { "CustomCard" }));
            Assert.That(result.CardTypes.Any(ct => ct.ModelName == "Basic"), Is.False);
            Assert.That(result.CardTypes.Any(ct => ct.ModelName == "Cloze"), Is.False);
        });
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
    public void GetEffectiveConfiguration_SkipBasicTypesWithoutCustomTypes_FallsBackToDefaults()
    {
        var configurations = new List<CategoryPromptConfiguration>
        {
            new()
            {
                Category = "Custom",
                Priority = 1,
                SkipBasicTypes = true
                // No CardTypes defined
            }
        };
        var registry = new CategoryPromptRegistry(configurations);

        var result = registry.GetEffectiveConfiguration(new[] { "Custom" });

        Assert.Multiple(() =>
        {
            Assert.That(result.CardTypes.Select(ct => ct.ModelName), Is.EquivalentTo(new[] { "Basic", "Cloze" }));
        });
    }
}
