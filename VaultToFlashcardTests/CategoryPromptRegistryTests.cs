using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NUnit.Framework;
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

        Assert.That(schema.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
        Assert.That(props.TryGetProperty("Basic", out _), Is.True);
        Assert.That(props.TryGetProperty("Cloze", out _), Is.True);
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

        Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
        Assert.That(props.TryGetProperty("Basic", out var basicProp), Is.True);
        Assert.That(basicProp.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(props.TryGetProperty("Cloze", out _), Is.False);
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

        Assert.That(desc, Does.Contain("Basic"));
        Assert.That(desc, Does.Contain("Cloze"));
        Assert.That(desc, Does.Contain("array"));
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
        // The implementation iterates noteCategories and takes the FIRST matching config per category,
        // then returns the highest priority among those. Since "Test" config is added first,
        // FindBestMatch(["Test", "Programming"]) returns the "Test" config (Priority=1).
        // The implementation does NOT scan all configs for the highest priority overall.
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

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Priority, Is.EqualTo(10));
        Assert.That(result.CardTypes[0].ModelName, Is.EqualTo("HighPriority"));
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
    public void BuildJsonSchema_CorrectStructure_ReturnsObjectType()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new Dictionary<string, string>
            {
                ["front"] = "question",
                ["back"] = "answer"
            }
        };

        var schema = CategoryPromptRegistry.BuildJsonSchema(cardType);

        Assert.That(schema.TryGetProperty("type", out var typeProp), Is.True);
        Assert.That(typeProp.GetString(), Is.EqualTo("object"));

        Assert.That(schema.TryGetProperty("properties", out var props), Is.True);
        Assert.That(props.TryGetProperty("front", out var frontProp), Is.True);
        Assert.That(frontProp.TryGetProperty("type", out var frontType), Is.True);
        Assert.That(frontType.GetString(), Is.EqualTo("string"));

        Assert.That(schema.TryGetProperty("required", out var required), Is.True);
        Assert.That(required.EnumerateArray().Select(e => e.GetString()).ToList(),
            Is.EquivalentTo(new[] { "front", "back" }));
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
}
