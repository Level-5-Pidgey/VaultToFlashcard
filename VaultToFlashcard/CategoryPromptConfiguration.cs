using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultToFlashcard;

public class CardTypeDefinition
{
	[JsonPropertyName("modelName")] public string ModelName { get; set; } = "";

	[JsonPropertyName("jsonSchemaProperties")]
	public Dictionary<string, string> JsonSchemaProperties { get; set; } = new();

	[JsonPropertyName("exampleOutput")] public string ExampleOutput { get; set; } = "";
}

public class CategoryPromptConfiguration
{
	[JsonPropertyName("category")] public string Category { get; set; } = "";

	[JsonPropertyName("priority")] public int Priority { get; set; } = 0;

	[JsonPropertyName("systemPromptAddendum")]
	public string SystemPromptAddendum { get; set; } = "";

	[JsonPropertyName("assistantPromptAddendum")]
	public string AssistantPromptAddendum { get; set; } = "";

		[JsonPropertyName("skipBasicTypes")]
	public bool SkipBasicTypes { get; set; } = false;

	[JsonPropertyName("cardTypes")] public List<CardTypeDefinition> CardTypes { get; set; } = new();
}