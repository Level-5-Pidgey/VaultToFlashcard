using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultToFlashcard;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlashcardType
{
    Unknown = 0,
    Basic = 1,
    Cloze = 2,
}

public abstract class FlashcardTransport
{
    [JsonPropertyName("cardType")]
    public FlashcardType Type { get; set; } = FlashcardType.Unknown;

    [JsonPropertyName("front")]
    public string? Front { get; set; } = "";

    [JsonPropertyName("back")]
    public string? Back { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; } = "";
}

public abstract class Flashcard(FlashcardType type)
{
    public FlashcardType Type { get; set; } = type;
}

public class BasicFlashcard() : Flashcard(FlashcardType.Basic)
{
    public BasicFlashcard(FlashcardTransport transport) : this()
    {
        Front = transport.Front ?? string.Empty;
        Back = transport.Back ?? string.Empty;    
    }
    
    [JsonPropertyName("front")]
    public string Front { get; init; } = "";
    [JsonPropertyName("back")]
    public string Back { get; init; } = "";
}

public class ClozeFlashcard() : Flashcard(FlashcardType.Cloze)
{
    public ClozeFlashcard(FlashcardTransport transport) : this()
    {
        Text = transport.Text ?? string.Empty;
    }
    
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

public class FlashcardConverter : JsonConverter<Flashcard>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(Flashcard).IsAssignableFrom(typeToConvert);
    }

    public override Flashcard Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        var jsonObject = jsonDoc.RootElement;
        
        var tempOptions = new JsonSerializerOptions(options);
        tempOptions.Converters.Remove(this);

        if (jsonObject.TryGetProperty("front", out _) && jsonObject.TryGetProperty("back", out _))
        {
            return JsonSerializer.Deserialize<BasicFlashcard>(jsonObject.GetRawText(), tempOptions)!;
        }
        if (jsonObject.TryGetProperty("text", out _))
        {
            return JsonSerializer.Deserialize<ClozeFlashcard>(jsonObject.GetRawText(), tempOptions)!;
        }
        throw new JsonException("Unknown flashcard type. JSON: " + jsonObject.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, Flashcard value, JsonSerializerOptions options)
    {
        var tempOptions = new JsonSerializerOptions(options);
        tempOptions.Converters.Remove(this);
        JsonSerializer.Serialize(writer, value as object, tempOptions);
    }
}