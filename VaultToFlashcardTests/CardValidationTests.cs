using System.Text.Json;
using NUnit.Framework;
using VaultToFlashcard;

namespace VaultToFlashcardTests;

public class CardValidationTests
{
    [Test]
    public void ValidateCard_ValidBasicCard_ReturnsTrue()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("""{"front": "q", "back": "a"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void ValidateCard_BasicCardWithExtraField_ReturnsFalse()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("""{"front": "q", "back": "a", "text": "wrong"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.InvalidFields, Does.Contain("text"));
        });
    }

    [Test]
    public void ValidateCard_ClozeCardWithExtraField_ReturnsFalse()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Cloze",
            JsonSchemaProperties = new() { ["text"] = "c" }
        };
        var cardJson = JsonDocument.Parse("""{"text": "cloze", "front": "wrong"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.InvalidFields, Does.Contain("front"));
        });
    }

    [Test]
    public void ValidateCard_ClozeCardMissingRequiredField_ReturnsFalse()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Cloze",
            JsonSchemaProperties = new() { ["text"] = "c" }
        };
        var cardJson = JsonDocument.Parse("{}").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.MissingFields, Does.Contain("text"));
        });
    }

    [Test]
    public void ValidateCard_EmptyCardObject_ReturnsFalse()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("{}").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.MissingFields, Does.Contain("front"));
            Assert.That(result.MissingFields, Does.Contain("back"));
        });
    }

    [Test]
    public void ValidateCard_CardMissingOneOfMultipleRequiredFields_ReportsOnlyMissing()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a", ["tags"] = "t" }
        };
        var cardJson = JsonDocument.Parse("""{"front": "q", "tags": "mytag"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.MissingFields, Does.Contain("back"));
            Assert.That(result.MissingFields, Does.Not.Contain("front"));
            Assert.That(result.MissingFields, Does.Not.Contain("tags"));
        });
    }

    [Test]
    public void ValidateCard_CardWithCaseMismatchedFieldNames_ReturnsFalse()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("""{"Front": "q", "Back": "a"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.InvalidFields, Does.Contain("Front"));
            Assert.That(result.InvalidFields, Does.Contain("Back"));
            Assert.That(result.MissingFields, Does.Contain("front"));
            Assert.That(result.MissingFields, Does.Contain("back"));
        });
    }

    [Test]
    public void ValidateCard_CardWithMultipleExtraFields_ReportsAllExtraFields()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("""{"front": "q", "back": "a", "extra1": "x", "extra2": "y"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.InvalidFields, Does.Contain("extra1"));
            Assert.That(result.InvalidFields, Does.Contain("extra2"));
        });
    }

    [Test]
    public void ValidateCard_ValidCardWithOnlyRequiredFields_ReturnsTrue()
    {
        var cardType = new CardTypeDefinition
        {
            ModelName = "Basic",
            JsonSchemaProperties = new() { ["front"] = "q", ["back"] = "a" }
        };
        var cardJson = JsonDocument.Parse("""{"front": "q", "back": "a"}""").RootElement;

        var result = CardValidation.ValidateCard(cardType, cardJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.InvalidFields, Is.Empty);
            Assert.That(result.MissingFields, Is.Empty);
        });
    }
}
