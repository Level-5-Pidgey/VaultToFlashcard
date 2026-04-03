using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;

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
        var allTags = new[] { "Obsidian-Generated" }
            .Concat(tags.Select(tag => tag.Replace(' ', '-')))
            .ToHashSet();
        
        foreach (var card in flashcards)
        {
            switch (card)
            {
                case BasicFlashcard basic:
                    notes.Add(new AnkiNote(deckName, "Basic", new { basic.Front, basic.Back, card.Source, }, allTags));
                    break;
                case ClozeFlashcard cloze:
                    notes.Add(new AnkiNote(deckName, "Cloze", new { cloze.Text, card.Source, }, allTags));
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
            AnsiConsole.MarkupLine($"[yellow]Field '{field}' not found in model '{modelName}'. Attempting to add it...[/]");
            try
            {
                var action = new AnkiAction("modelFieldAdd", new { modelName, fieldName = field });
                await PostAsync(action);
                AnsiConsole.MarkupLine($"[green]Successfully added field '{field}' to model '{modelName}'.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to add field '{field}' to model '{modelName}'. Please add it manually via Anki's 'Tools > Manage Note Types' menu.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
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
    
    public async Task<IReadOnlyCollection<AnkiNoteInfo>> NotesInfoAsync(IReadOnlyCollection<long> noteIds)
    {
        if (!noteIds.Any()) return Array.Empty<AnkiNoteInfo>();
        var action = new AnkiAction("notesInfo", new { notes = noteIds });
        var result = await PostAsync(action);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return result.ValueKind == JsonValueKind.Array 
            ? result.Deserialize<IReadOnlyCollection<AnkiNoteInfo>>(options) ?? Array.Empty<AnkiNoteInfo>()
            : Array.Empty<AnkiNoteInfo>();
    }

    public async Task<AnkiNotesInfoResult> GetNotesInfoResilientAsync(IReadOnlyCollection<long> noteIds)
    {
        if (!noteIds.Any()) return new AnkiNotesInfoResult(Array.Empty<AnkiNoteInfo>(), Array.Empty<long>());

        try
        {
            var notesInfo = await NotesInfoAsync(noteIds);
            return new AnkiNotesInfoResult(notesInfo, Array.Empty<long>());
        }
        catch (Exception ex) when (ex.Message.Contains("notes were not found"))
        {
            var notFoundIds = new Regex(@"\d+").Matches(ex.Message)
                .Select(m => long.Parse(m.Value))
                .ToHashSet();
            
            var succeededIds = noteIds.Except(notFoundIds).ToList();
            
            if (succeededIds.Any())
            {
                var recursiveResult = await GetNotesInfoResilientAsync(succeededIds);
                return new AnkiNotesInfoResult(recursiveResult.Succeeded, notFoundIds.Concat(recursiveResult.NotFound).ToArray());
            }

            return new AnkiNotesInfoResult(Array.Empty<AnkiNoteInfo>(), notFoundIds.ToArray());
        }
    }

    public async Task<AnkiNoteInfo?> GetNoteInfoAsync(long noteId)
    {
        var result = await GetNotesInfoResilientAsync(new[] { noteId });
        return result.Succeeded.FirstOrDefault();
    }

    public async Task MergeTagsAsync(long noteId, IReadOnlyCollection<string> newSystemTags)
    {
        var noteInfo = await GetNoteInfoAsync(noteId);
        if (noteInfo == null) return; // Note was deleted or is invalid

        var currentTags = new HashSet<string>(noteInfo.Tags);
        var finalTags = new HashSet<string>(currentTags);

        // Normalize for comparison: "My-Tag" should match "My Tag"
        Func<string, string> normalize = s => s.Replace('-', ' ').Replace('_', ' ').ToLowerInvariant();

        var normalizedNewSystemTags = newSystemTags.Select(normalize).ToHashSet();
        
        // Remove any old system tags that are no longer relevant
        foreach (var tag in currentTags)
        {
            if (normalizedNewSystemTags.Contains(normalize(tag)))
            {
                finalTags.Remove(tag);
            }
        }
        
        // Add the new system tags
        foreach (var tag in newSystemTags)
        {
            finalTags.Add(tag.Replace(' ', '-'));
        }
        finalTags.Add("Obsidian-Generated");

        // Only call the API if the tags have actually changed
        if (!finalTags.SetEquals(currentTags))
        {
            var action = new AnkiAction("updateNoteTags", new { note = noteId, tags = finalTags.ToArray() });
            await PostAsync(action);
        }
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

public record AnkiNoteInfo(
    [property: JsonPropertyName("noteId")] long NoteId,
    [property: JsonPropertyName("tags")] IReadOnlyCollection<string> Tags
);

public record AnkiNotesInfoResult(IReadOnlyCollection<AnkiNoteInfo> Succeeded, IReadOnlyCollection<long> NotFound);