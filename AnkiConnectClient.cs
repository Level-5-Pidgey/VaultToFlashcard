using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultToFlashcard;

public class AnkiConnectClient
{
    private readonly HttpClient HttpClient;
    private const string AnkiConnectUrl = "http://127.0.0.1:8765";
    
    private static readonly SemaphoreSlim AnkiConcurrencyLimiter = new(3, 3);

    public AnkiConnectClient()
    {
        HttpClient = new HttpClient();
        HttpClient.Timeout = TimeSpan.FromSeconds(300);
        // AnkiConnect's server can't handle the 'Expect' header
        HttpClient.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await HttpClient.GetAsync(AnkiConnectUrl, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task CreateDeckAsync(string deckName)
    {
        var action = new AnkiAction("createDeck", new { deck = deckName }, Version: null);
        await PostAsync(action);
    }

    public async Task<IReadOnlyCollection<long>> AddNotesAsync(IReadOnlyCollection<Flashcard> flashcards, string deckName, IReadOnlyCollection<string> tags)
    {
        var notes = new List<AnkiNote>();
        var allTags = new[] { "obsidian-auto-generated" }
            .Concat(tags.Select(tag => tag.Replace(' ', '_')))
            .ToHashSet();
        
        foreach (var card in flashcards)
        {
            switch (card)
            {
                case BasicFlashcard basic:
                    notes.Add(new AnkiNote(deckName, "Basic", new { basic.Front, basic.Back, SourceNote = card.SourceNote, SourceSection = card.SourceSection }, allTags));
                    break;
                case ClozeFlashcard cloze:
                    notes.Add(new AnkiNote(deckName, "Cloze", new { cloze.Text, SourceNote = card.SourceNote, SourceSection = card.SourceSection }, allTags));
                    break;
            }
        }
        var action = new AnkiAction("addNotes", new { notes });
        var result = await PostAsync(action);

        return result.ValueKind == JsonValueKind.Array ? 
            result.EnumerateArray()
                .Select(e => e.GetInt64())
                .ToArray() 
            : [];
    }

    public async Task DeleteNotesAsync(IReadOnlyCollection<long> noteIds)
    {
        if (!noteIds.Any()) return;
        var action = new AnkiAction("deleteNotes", new { notes = noteIds });
        await PostAsync(action);
    }

    public async Task ChangeDeckAsync(IReadOnlyCollection<long> noteIds, string deck)
    {
        if (!noteIds.Any()) return;
        var action = new AnkiAction("changeDeck", new { notes = noteIds, deck = deck });
        await PostAsync(action);
    }
    
    public async Task<IReadOnlyCollection<long>> FindAllTaggedNotesAsync()
    {
        var action = new AnkiAction("findNotes", new { query = "tag:obsidian-auto-generated" });
        var result = await PostAsync(action);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
                .Select(e => e.GetInt64())
                .ToArray()
            : [];
    }
    
    public async Task EnsureFieldsExist(string modelName, IEnumerable<string> requiredFields)
    {
        var modelFieldNames = await GetModelFieldNamesAsync(modelName);
        var missingFields = requiredFields.Except(modelFieldNames).ToList();

        foreach (var field in missingFields)
        {
            Console.WriteLine($"Field '{field}' not found in model '{modelName}'. Attempting to add it...");
            try
            {
                var action = new AnkiAction("modelFieldAdd", new { modelName, fieldName = field });
                await PostAsync(action);
                Console.WriteLine($"Successfully added field '{field}' to model '{modelName}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to add field '{field}' to model '{modelName}'. Please add it manually via Anki's 'Tools > Manage Note Types' menu.");
                Console.WriteLine($"Error: {ex.Message}");
                throw new Exception($"Failed to automatically add required field '{field}' to Anki model '{modelName}'. Please add it manually and restart the process.", ex);
            }
        }
    }

    public async Task<List<string>> GetModelFieldNamesAsync(string modelName)
    {
        var action = new AnkiAction("modelFieldNames", new { modelName });
        var result = await PostAsync(action);
        if (result.ValueKind == JsonValueKind.Array)
        {
            return result.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        return new List<string>();
    }
    
    
    private async Task<JsonElement> PostAsync(AnkiAction action)
    {
        await AnkiConcurrencyLimiter.WaitAsync();
        try
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            var jsonPayload = JsonSerializer.Serialize(action, options);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync(AnkiConnectUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"AnkiConnect request failed with status code {response.StatusCode}: {errorContent}");
            }

            var responseBody = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Trim() == "null")
            {
                return default;
            }

            using var jsonDoc = JsonDocument.Parse(responseBody);
            var root = jsonDoc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                throw new Exception($"AnkiConnect error: {errorElement.GetString()}");
            }

            return root.TryGetProperty("result", out var resultElement) ? 
                resultElement.Clone() : 
                default;
        }
        finally
        {
            AnkiConcurrencyLimiter.Release();
        }
    }
}

public record AnkiAction(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("params")] object Params,
    [property: JsonPropertyName("version")] int? Version = 6
);

public record AnkiNote(
    [property: JsonPropertyName("deckName")] string DeckName,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("fields")] object Fields,
    [property: JsonPropertyName("tags")] IReadOnlyCollection<string> Tags
);