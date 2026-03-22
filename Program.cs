using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using YamlDotNet.Serialization;
using System.Text.RegularExpressions;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.Logging;
using VaultToFlashcard;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Obsidian to Anki flashcard generator.");

        var vaultOption = new Option<DirectoryInfo>(
            name: "--vault",
            description: "The path to the Obsidian vault.")
            { IsRequired = true };

        var aiModeOption = new Option<string>(
            name: "--ai-mode",
            description: "The AI mode to use.",
            getDefaultValue: () => "api");

        var modelOption = new Option<string>(
            name: "--model",
            description: "The Gemini model to use.",
            getDefaultValue: () => "gemini-3-flash-preview");

        rootCommand.AddOption(vaultOption);
        rootCommand.AddOption(aiModeOption);
        rootCommand.AddOption(modelOption);

        rootCommand.SetHandler(async (vaultPath, aiMode, model) =>
        {
            var configuration = new ConfigurationBuilder()
                .AddUserSecrets<Program>()
                .Build();

            var apiKey = configuration["GeminiApiKey"];

            if (aiMode == "api" && string.IsNullOrEmpty(apiKey))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: API key is not configured.");
                Console.WriteLine("Please set the 'GeminiApiKey' in user secrets, for example:");
                Console.WriteLine("dotnet user-secrets set \"GeminiApiKey\" \"<YOUR_API_KEY>\"");
                Console.ResetColor();
                return;
            }
            
            var ankiClient = new AnkiConnectClient();
            if (!await ankiClient.IsAvailableAsync())
            {
                Console.WriteLine("AnkiConnect not available. Please ensure Anki is running with AnkiConnect installed and configured.");
                return;
            }
            var processor = new VaultProcessor(ankiClient);
            await processor.ProcessVault(vaultPath.FullName, aiMode, apiKey, model);

        }, vaultOption, aiModeOption, modelOption);

        return await rootCommand.InvokeAsync(args);
    }
}

public record CacheEntry(
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("noteIds")] List<long> NoteIds,
    [property: JsonPropertyName("deckName")] string DeckName
);

public class VaultProcessor
{
    private readonly AnkiConnectClient _ankiClient;
    private readonly CategoryAnalyzer _categoryAnalyzer = new();
    private ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private const string CacheFileName = ".obsidian-anki-cache.json";
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().Build();
    
    // Rate limiter for Gemini API: 5 requests per minute
    private static readonly FixedWindowRateLimiter _geminiRateLimiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 5,
        Window = TimeSpan.FromSeconds(60),
        AutoReplenishment = true
    });

    // Semaphore to control the number of files processed concurrently
    private static readonly SemaphoreSlim _fileProcessingSemaphore = new(10);

    public VaultProcessor(AnkiConnectClient ankiClient)
    {
        _ankiClient = ankiClient;
    }

    private async Task AnalyzeAllCategoriesAsync(IEnumerable<string> markdownFiles)
    {
        Console.WriteLine("Analyzing categories across the vault...");
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var yamlHeaderMatch = new Regex(@"^---\s*(.*?)---\s*", RegexOptions.Singleline);

        foreach (var filePath in markdownFiles)
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            var match = yamlHeaderMatch.Match(fileContent);

            if (!match.Success) continue;

            var frontMatter = deserializer.Deserialize<Dictionary<object, object>>(match.Groups[1].Value);

            if (frontMatter.TryGetValue("study", out var studyValue) && (studyValue is bool b && b || studyValue.ToString()!.ToLower() == "true"))
            {
                if (frontMatter.TryGetValue("categories", out var cats) && cats is List<object> catList)
                {
                    var categories = catList.Select(c => c.ToString()!).ToList();
                    _categoryAnalyzer.Analyze(categories);
                }
            }
        }
        _categoryAnalyzer.FinalizeAnalysis();
        Console.WriteLine("Category analysis complete.");
    }

    public async Task ProcessVault(string vaultPath, string aiMode, string apiKey, string model)
    {
        Console.WriteLine($"Starting vault processing at: {vaultPath}");
        var cachePath = Path.Combine(vaultPath, CacheFileName);

        if (File.Exists(cachePath))
        {
            Console.WriteLine("Loading cache...");
            var json = await File.ReadAllTextAsync(cachePath);
            _cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheEntry>>(json) ?? new();
        }

        var markdownFiles = Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(p => !p.Contains(CacheFileName))
            .ToList();
        
        await AnalyzeAllCategoriesAsync(markdownFiles);

        var allValidNoteIds = new ConcurrentBag<long>();
        var processingTasks = new List<Task>();
        foreach (var filePath in markdownFiles)
        {
            await _fileProcessingSemaphore.WaitAsync();
            processingTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessFileAsync(filePath, aiMode, apiKey, model, vaultPath, allValidNoteIds);
                }
                finally
                {
                    _fileProcessingSemaphore.Release();
                }
            }));
        }
        await Task.WhenAll(processingTasks);

        await CleanUpOrphanedNotesAsync(allValidNoteIds);
        
        Console.WriteLine("Saving cache...");
        var newJson = JsonSerializer.Serialize(_cache, _jsonOptions);
        await File.WriteAllTextAsync(cachePath, newJson);

        Console.WriteLine("Vault processing complete.");
    }

    private async Task CleanUpOrphanedNotesAsync(ConcurrentBag<long> validNoteIds)
    {
        Console.WriteLine("Cleaning up orphaned notes...");
        var ankiNoteIds = await _ankiClient.FindAllTaggedNotesAsync();
    
        var validIdSet = new HashSet<long>(validNoteIds);
        var orphanedIds = ankiNoteIds.Where(id => !validIdSet.Contains(id)).ToList();

        if (orphanedIds.Any())
        {
            Console.WriteLine($"  > Found {orphanedIds.Count} orphaned notes to delete.");
            await _ankiClient.DeleteNotesAsync(orphanedIds);
        }
        else
        {
            Console.WriteLine("  > No orphaned notes found.");
        }
    }

    private async Task ProcessFileAsync(string filePath, string aiMode, string apiKey, string model, string vaultPath, ConcurrentBag<long> allValidNoteIds)
    {
        try
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            var yamlHeaderMatch = new Regex(@"^---\s*(.*?)---\s*", RegexOptions.Singleline);
            var match = yamlHeaderMatch.Match(fileContent);

            if (!match.Success) return;

            var yamlContent = match.Groups[1].Value;
            var markdownContent = fileContent.Substring(match.Length); 

            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            var frontMatter = deserializer.Deserialize<Dictionary<object, object>>(yamlContent);

            if (!frontMatter.TryGetValue("study", out var studyValue))
            {
                return;
            }

            var shouldStudy = studyValue switch { true => true, "true" => true, _ => false };
            if (!shouldStudy)
            {
                return;
            }
            
            Console.WriteLine($"Processing file: {filePath}");
            var contentChunks = ParseAndSanitize(markdownContent);

            var (deckName, tags) = ResolveDeckName(filePath, vaultPath, frontMatter);
            await _ankiClient.CreateDeckAsync(deckName);

            var allSectionKeys = new HashSet<string>();

            foreach (var (header, content) in contentChunks)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var cacheKey = $"{Path.GetRelativePath(vaultPath, filePath)}#{header}";
                allSectionKeys.Add(cacheKey);
                var contentHash = CalculateHash(content);

                _cache.TryGetValue(cacheKey, out var cachedEntry);

                if (cachedEntry != null && cachedEntry.DeckName != deckName && cachedEntry.ContentHash == contentHash)
                {
                    Console.WriteLine($"  - MOVING: '{header}' from deck '{cachedEntry.DeckName}' to '{deckName}'");
                    await _ankiClient.ChangeDeckAsync(cachedEntry.NoteIds, deckName);
                    _cache[cacheKey] = cachedEntry with { DeckName = deckName };
                    cachedEntry.NoteIds.ForEach(allValidNoteIds.Add);
                    continue;
                }
                
                if (cachedEntry != null && cachedEntry.ContentHash == contentHash)
                {
                    Console.WriteLine($"  - SKIPPING: '{header}' (unchanged)");
                    cachedEntry.NoteIds.ForEach(allValidNoteIds.Add);
                    continue;
                }

                Console.WriteLine($"  - PROCESSING: '{header}' (new or changed)");

                if (cachedEntry != null)
                {
                    Console.WriteLine($"    > Deleting {cachedEntry.NoteIds.Count} old note(s).");
                    await _ankiClient.DeleteNotesAsync(cachedEntry.NoteIds);
                }

                IReadOnlyCollection<Flashcard> flashcards;
                switch (aiMode)
                {
                    case "cli":
                        flashcards = await GenerateFlashcardsCliAsync(content, frontMatter, Path.GetFileName(filePath), header, model);
                        break;
                    case "api":
                        flashcards = await GenerateFlashcardsApiAsync(content, frontMatter, Path.GetFileName(filePath), header, model, apiKey);
                        break;
                    default:
                        Console.WriteLine($"Unknown ai-mode: {aiMode}");
                        continue;
                }

                if (flashcards.Any())
                {
                    var newNoteIds = await _ankiClient.AddNotesAsync(flashcards, deckName, tags);
                    Console.WriteLine($"    > Synced {newNoteIds.Count} flashcards to Anki deck '{deckName}'.");
                    _cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName);
                    newNoteIds.ForEach(allValidNoteIds.Add);
                }
                else if (_cache.ContainsKey(cacheKey))
                {
                    _cache.TryRemove(cacheKey, out _);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner Exception: {ex.InnerException.Message}");
            }
        }
    }

    private (string DeckName, List<string> Tags) ResolveDeckName(string filePath, string vaultPath, Dictionary<object, object> frontMatter)
    {
        if (frontMatter.TryGetValue("categories", out var cats) && cats is List<object> catList)
        {
            var categories = catList.Select(c => c.ToString()!).ToList();
            if (categories.Any())
            {
                return _categoryAnalyzer.ResolveDeckName(categories);
            }
        }

        return (Path.GetFileNameWithoutExtension(filePath), new List<string>());
    }
    
    private IReadOnlyCollection<ChatMessage> GetPromptMessages(string content, Dictionary<object, object> frontMatter, string fileName, string header)
    {
        var noteCategories = "";
        if (frontMatter.TryGetValue("categories", out var cats) && cats is List<object> catList)
        {
            noteCategories = string.Join(", ", catList.Select(c => c.ToString()));
        }
        else if (frontMatter.TryGetValue("tags", out var tags) && tags is List<object> tagList)
        {
            noteCategories = string.Join(", ", tagList.Select(t => t.ToString()));
        }

        // Set initial context and system persona
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, $$$"""
                                    You are a versatile Educator and Anki Instructional Designer. 
                                    Your goal is to extract atomic, high-quality knowledge from sections of notes that remain clear and "guessable".
                                    
                                    UNIVERSAL RULES:
                                    1. IDENTITY ANCHOR: Every card must be 100% self-contained. Never use "it," "this," or "the service." Use the specific name from the Note Title or Categories to provide context.
                                    2. ATOMICITY: Each card must test exactly ONE discrete fact. If a sentence has three facts, create three separate cards.
                                    3. NO "HIDDEN CONTEXT": Imagine the user sees this card 6 months from now mixed with 5,000 other cards. Ensure there is enough "clue" text in the Question/Cloze to point to the correct answer.
                                    
                                    DOMAIN-SPECIFIC BEHAVIOR:
                                    - TECHNICAL/CONCEPTS: Focus on "WHY" and "WHEN" (trade-offs and use cases).
                                    - LANGUAGES: Focus on usage in context. For vocab, include a short example sentence.
                                    - INTERVIEW PREP: Focus on the "KEY TAKEAWAY" or a specific "Action" from a behavioral response.
                                    
                                    CLOZE RULES:
                                    - Minimum of 2 clozes per card: {{c1::answer::hint}}. Hints are optional -- only use them if necessary to provide context for the card.
                                    - Never cloze-delete the only word that identifies the topic. If you must cloze the topic, provide a mandatory hint (e.g., {{c1::Bonjour::French Greeting}}).
                                    """),

            new(ChatRole.User, $"Context: This note has the following categories: '{noteCategories}' and is titled '{fileName}'."),
        };

        if (!string.Equals(Path.GetFileNameWithoutExtension(fileName), header, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new (ChatRole.User, $"Section Name: {header}"));
        }
        
        // Add example responses to help generate better results
        messages.Add(new (ChatRole.Assistant, "Example Q&A card: [{\"front\": \"When should a Trie be used over a Hash Map?\", \"back\": \"When you need efficient prefix-based searching/auto-complete.\"}]"));
        messages.Add(new (ChatRole.Assistant, "Example Cloze card: [{\"text\": \"{{c1::Canberra::city}} was founded in {{c2::1913::year}}\"}]"));
        
        // Add actual text extracted from document
        messages.Add(new (ChatRole.User, $@"Content to convert:\n{content}\n\nTask: Create atomic Anki flashcards from this content."));
        
        return messages;
    }
    
    private async Task<IReadOnlyCollection<Flashcard>> GenerateFlashcardsApiAsync(string content, Dictionary<object, object> frontMatter, string fileName, string header, string model, string apiKey)
    {
        await _geminiRateLimiter.AcquireAsync();
        try
        {
            var promptMessages = GetPromptMessages(content, frontMatter, fileName, header);
            var gemini = new GeminiChatClient(new GeminiClientOptions
            {
                ApiKey = apiKey,
                ModelId = model,
            });

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Trace); // Important: 'Trace' is needed to see message content
            });

            var client = new ChatClientBuilder(gemini)
                .UseLogging(loggerFactory)
                .Build();

            var options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<FlashcardTransport[]>(),
                Temperature = 0.15f,
            };

            var response = await client.GetResponseAsync(promptMessages, options);

            if (response.FinishReason != ChatFinishReason.Stop)
            {
                Console.WriteLine($"Error deserializing JSON for chunk '{header}': {response.FinishReason}");
                return Array.Empty<Flashcard>();
            }

            try
            {
                var transport = JsonSerializer.Deserialize<FlashcardTransport[]>(response.Text) ?? [];
                return transport.Select(x => (Flashcard)(x.Type switch
                {
                    FlashcardType.Basic => new BasicFlashcard(x),
                    FlashcardType.Cloze => new ClozeFlashcard(x),
                    _ => throw new InvalidOperationException(),
                }))
                .ToArray();
            }
            catch(JsonException ex)
            {
                Console.WriteLine($"Error deserializing JSON for chunk '{header}': {ex.Message}");
                Console.WriteLine($"-- Invalid JSON -- {response.Text} ------------------");
                return new List<Flashcard>();
            }
        }
        finally
        {
            // The permit is automatically released in FixedWindowRateLimiter
        }
    }

    private async Task<IReadOnlyCollection<Flashcard>> GenerateFlashcardsCliAsync(string content, Dictionary<object, object> frontMatter, string fileName, string header, string model)
    {
        var promptMessages = GetPromptMessages(content, frontMatter, fileName, header)
            .Select(x => x.Text);
        var prompt = string.Join(Environment.NewLine, promptMessages);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "gemini.cmd",
            Arguments = $"-m \"{model}\" -o text",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        
        using var process = new Process();

        process.StartInfo = processStartInfo;
        var output = new StringBuilder();
        var error = new StringBuilder();
        
        process.OutputDataReceived += (sender, args) => output.AppendLine(args.Data);
        process.ErrorDataReceived += (sender, args) => error.AppendLine(args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Gemini CLI failed with exit code {process.ExitCode}: {error}");
        }
        
        var fullOutput = output.ToString();
        var jsonStart = fullOutput.IndexOf('[');
        var jsonEnd = fullOutput.LastIndexOf(']');
        
        if (jsonStart == -1 || jsonEnd == -1)
        {
            Console.WriteLine($"Warning: Could not find JSON array in the output from Gemini CLI for chunk '{header}'. Skipping.");
            return new List<Flashcard>();
        }

        var jsonOutput = fullOutput.Substring(jsonStart, jsonEnd - jsonStart + 1);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        jsonOptions.Converters.Add(new FlashcardConverter());

        try
        {
            return JsonSerializer.Deserialize<List<Flashcard>>(jsonOutput, jsonOptions) ?? new List<Flashcard>();
        }
        catch(JsonException ex)
        {
            Console.WriteLine($"Error deserializing JSON for chunk '{header}': {ex.Message}");
            return new List<Flashcard>();
        }
    }

    private string CalculateHash(string text)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToString(hashedBytes).Replace("-", "").ToLowerInvariant();
    }

    private Dictionary<string, string> ParseAndSanitize(string markdownContent)
    {
        var document = Markdown.Parse(markdownContent, _pipeline);
        var chunks = new Dictionary<string, string>();
        
        var lastHeading = "Prologue";
        var content = new System.Text.StringBuilder();

        foreach (var block in document)
        {
            if (block is HeadingBlock heading)
            {
                if (content.Length > 0)
                {
                    chunks[lastHeading] = content.ToString().Trim();
                    content.Clear();
                }
                lastHeading = ExtractText(heading).Trim();
            }
            else
            {
                content.Append(ExtractText(block));
            }
        }

        if (content.Length > 0)
        {
            chunks[lastHeading] = content.ToString().Trim();
        }

        return chunks;
    }
    private string ExtractText(MarkdownObject obj)
    {
        var sb = new System.Text.StringBuilder();
        ExtractTextRecursive(obj, sb);
        
        var content = sb.ToString();
        
        content = Regex.Replace(content, @"\[\[(?:.*[|/])?(.*?)\]\]", "$1");

        return content;
    }

    private void ExtractTextRecursive(MarkdownObject? obj, System.Text.StringBuilder sb)
    {
        if (obj is null) return;
        
        switch (obj)
        {
            case HeadingBlock heading:
                 foreach(var inline in heading.Inline) ExtractTextRecursive(inline, sb);
                 break;
            case ParagraphBlock paragraph:
                foreach (var inline in paragraph.Inline) ExtractTextRecursive(inline, sb);
                sb.AppendLine();
                break;
            case FencedCodeBlock fencedCodeBlock:
                sb.AppendLine("```" + (fencedCodeBlock.Info ?? string.Empty));
                foreach (var line in fencedCodeBlock.Lines.Lines)
                {
                    sb.AppendLine(line.ToString());
                }
                sb.AppendLine("```");
                break;
            case EmphasisInline emphasis:
                foreach (var child in emphasis) ExtractTextRecursive(child, sb);
                break;
            case LiteralInline literal:
                var content = literal.Content.ToString();
                sb.Append(content);
                break;
            case LineBreakInline:
                 if(sb.Length > 0 && sb[^1] != ' ') sb.AppendLine();
                 break;
            case CodeInline codeInline:
                sb.Append(codeInline.Content);
                break;
            case ListBlock list:
                foreach (var listItem in list) ExtractTextRecursive(listItem, sb);
                break;
            case ListItemBlock listItemBlock:
                sb.Append("- ");
                foreach (var listItemParagraph in listItemBlock) ExtractTextRecursive(listItemParagraph, sb);
                break;
            case ThematicBreakBlock:
            case HtmlBlock:
                
                break;
            case ContainerBlock container when !(container is ParagraphBlock || container is HeadingBlock):
                foreach (var child in container) ExtractTextRecursive(child, sb);
                break;
            case ContainerInline containerInline when !(containerInline is EmphasisInline):
                foreach (var child in containerInline) ExtractTextRecursive(child, sb);
                break;
        }
    }
}


public class AnkiConnectClient
{
    private readonly HttpClient _httpClient;
    private const string AnkiConnectUrl = "http://127.0.0.1:8765";

    // Concurrency limiter for AnkiConnect: 3 concurrent requests
    private static readonly SemaphoreSlim _ankiConcurrencyLimiter = new(3, 3);

    public AnkiConnectClient()
    {
        _httpClient = new HttpClient();
        // AnkiConnect's server can't handle the 'Expect' header
        _httpClient.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync(AnkiConnectUrl, cts.Token);
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

    public async Task<List<long>> AddNotesAsync(IReadOnlyCollection<Flashcard> flashcards, string deckName, List<string> tags)
    {
        var notes = new List<AnkiNote>();
        var allTags = new[] { "obsidian-auto-generated" }.Concat(tags).ToList();
        foreach (var card in flashcards)
        {
            switch (card)
            {
                case BasicFlashcard basic:
                    notes.Add(new AnkiNote(deckName, "Basic", new { Front = basic.Front, Back = basic.Back }, allTags));
                    break;
                case ClozeFlashcard cloze:
                    notes.Add(new AnkiNote(deckName, "Cloze", new { Text = cloze.Text }, allTags));
                    break;
            }
        }
        var action = new AnkiAction("addNotes", new { notes });
        var result = await PostAsync(action);

        if (result.ValueKind == JsonValueKind.Array)
        {
            return result.EnumerateArray().Select(e => e.GetInt64()).ToList();
        }

        return new List<long>();
    }

    public async Task DeleteNotesAsync(List<long> noteIds)
    {
        if (!noteIds.Any()) return;
        var action = new AnkiAction("deleteNotes", new { notes = noteIds });
        await PostAsync(action);
    }

    public async Task ChangeDeckAsync(List<long> noteIds, string deck)
    {
        if (!noteIds.Any()) return;
        var action = new AnkiAction("changeDeck", new { notes = noteIds, deck = deck });
        await PostAsync(action);
    }
    
    public async Task<List<long>> FindAllTaggedNotesAsync()
    {
        var action = new AnkiAction("findNotes", new { query = "tag:obsidian-auto-generated" });
        var result = await PostAsync(action);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(e => e.GetInt64()).ToList()
            : new List<long>();
    }
    
    private async Task<JsonElement> PostAsync(AnkiAction action)
    {
        await _ankiConcurrencyLimiter.WaitAsync();
        try
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            var jsonPayload = JsonSerializer.Serialize(action, options);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(AnkiConnectUrl, content);

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

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
                {
                    throw new Exception($"AnkiConnect error: {errorElement.GetString()}");
                }

                if (root.TryGetProperty("result", out var resultElement))
                {
                    return resultElement.Clone();
                }
            }
            
            return default;
        }
        finally
        {
            _ankiConcurrencyLimiter.Release();
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
    [property: JsonPropertyName("tags")] List<string> Tags
);


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlashcardType
{
    Unknown = 0,
    Basic = 1,
    Cloze = 2,
}

public class FlashcardTransport
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
