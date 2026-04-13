using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultToFlashcard;

public class CardTemplateItem
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("front")] public string Front { get; set; } = "";
	[JsonPropertyName("back")] public string Back { get; set; } = "";
}

public class CardTemplateDefinition
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("templates")] public List<CardTemplateItem> Templates { get; set; } = new();
	[JsonPropertyName("css")] public string? Css { get; set; } = "";
	[JsonPropertyName("isCloze")] public bool IsCloze { get; set; } = false;

	[JsonPropertyName("jsonSchemaProperties")]
	public Dictionary<string, string> JsonSchemaProperties { get; set; } = new();

	[JsonPropertyName("exampleOutput")] public string ExampleOutput { get; set; } = "";
}

/// <summary>
/// Used for AI schema generation (jsonSchemaProperties, exampleOutput).
/// Template rendering uses CardTemplateDefinition.
/// </summary>
public class CardTypeDefinition
{
	[JsonPropertyName("modelName")] public string ModelName { get; set; } = "";

	[JsonPropertyName("jsonSchemaProperties")]
	public Dictionary<string, string> JsonSchemaProperties { get; set; } = new();

	[JsonPropertyName("exampleOutput")] public string ExampleOutput { get; set; } = "";
	[JsonPropertyName("front")] public string? Front { get; set; }
	[JsonPropertyName("back")] public string? Back { get; set; }
	[JsonPropertyName("isCloze")] public bool IsCloze { get; set; } = false;
}

public class CategoryPromptConfiguration
{
	[JsonPropertyName("category")] public string Category { get; set; } = "";

	[JsonPropertyName("priority")] public int Priority { get; set; } = 0;

	[JsonPropertyName("systemPromptAddendum")]
	public string SystemPromptAddendum { get; set; } = "";

	[JsonPropertyName("assistantPromptAddendum")]
	public string AssistantPromptAddendum { get; set; } = "";

	[JsonPropertyName("skipBasicTypes")] public bool SkipBasicTypes { get; set; } = false;

	[JsonPropertyName("cardTypes")] public List<string> CardTypes { get; set; } = new();
}

public class VaultPromptConfiguration
{
	[JsonPropertyName("cardTypes")] public List<CardTemplateDefinition> CardTypes { get; set; } = new();
	[JsonPropertyName("categories")] public List<CategoryPromptConfiguration> Categories { get; set; } = new();
}