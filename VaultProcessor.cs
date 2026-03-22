using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace VaultToFlashcard;

public class VaultProcessor(AnkiConnectClient ankiClient)
{
    private readonly CategoryAnalyzer CategoryAnalyzer = new();
    private ConcurrentDictionary<string, CacheEntry> Cache = new();

    private const string CacheFileName = ".obsidian-anki-cache.json";
    private const int ApiRateLimit = 5;
    
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    private static readonly FixedWindowRateLimiter GeminiRateLimiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = ApiRateLimit,
        Window = TimeSpan.FromSeconds(60),
        AutoReplenishment = true
    });
    private static readonly SemaphoreSlim FileProcessingSemaphore = new(10);

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
                    CategoryAnalyzer.Analyze(categories);
                }
            }
        }
        CategoryAnalyzer.FinalizeAnalysis();
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
            Cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheEntry>>(json) ?? new();
        }

        var markdownFiles = Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(p => !p.Contains(CacheFileName))
            .ToList();
        
        await AnalyzeAllCategoriesAsync(markdownFiles);

        var allValidNoteIds = new ConcurrentBag<long>();
        var processingTasks = new List<Task>();
        foreach (var filePath in markdownFiles)
        {
            await FileProcessingSemaphore.WaitAsync();
            processingTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessFileAsync(filePath, aiMode, apiKey, model, vaultPath, allValidNoteIds);
                }
                finally
                {
                    FileProcessingSemaphore.Release();
                }
            }));
        }
        await Task.WhenAll(processingTasks);

        await CleanUpOrphanedNotesAsync(allValidNoteIds);
        
        Console.WriteLine("Saving cache...");
        var newJson = JsonSerializer.Serialize(Cache, JsonOptions);
        await File.WriteAllTextAsync(cachePath, newJson);

        Console.WriteLine("Vault processing complete.");
    }

    private async Task CleanUpOrphanedNotesAsync(ConcurrentBag<long> validNoteIds)
    {
        Console.WriteLine("Cleaning up orphaned notes...");
        var ankiNoteIds = await ankiClient.FindAllTaggedNotesAsync();
    
        var validIdSet = new HashSet<long>(validNoteIds);
        var orphanedIds = ankiNoteIds.Where(id => !validIdSet.Contains(id)).ToList();

        if (orphanedIds.Any())
        {
            Console.WriteLine($"  > Found {orphanedIds.Count} orphaned notes to delete.");
            await ankiClient.DeleteNotesAsync(orphanedIds);
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

            var (deckName, tags) = ResolveDeckName(filePath, frontMatter);
            await ankiClient.CreateDeckAsync(deckName);

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

                Cache.TryGetValue(cacheKey, out var cachedEntry);

                if (cachedEntry != null && cachedEntry.DeckName != deckName && cachedEntry.ContentHash == contentHash)
                {
                    Console.WriteLine($"  - MOVING: '{header}' from deck '{cachedEntry.DeckName}' to '{deckName}'");
                    await ankiClient.ChangeDeckAsync(cachedEntry.NoteIds, deckName);
                    Cache[cacheKey] = cachedEntry with { DeckName = deckName };

                    foreach (var newNoteId in cachedEntry.NoteIds)
                    {
                        allValidNoteIds.Add(newNoteId);
                    }

                    continue;
                }
                
                if (cachedEntry != null && cachedEntry.ContentHash == contentHash)
                {
                    Console.WriteLine($"  - SKIPPING: '{header}' (unchanged)");

                    foreach (var newNoteId in cachedEntry.NoteIds)
                    {
                        allValidNoteIds.Add(newNoteId);
                    }

                    continue;
                }

                Console.WriteLine($"  - PROCESSING: '{header}' (new or changed)");

                if (cachedEntry != null)
                {
                    Console.WriteLine($"    > Deleting {cachedEntry.NoteIds.Count} old note(s).");
                    await ankiClient.DeleteNotesAsync(cachedEntry.NoteIds);
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
                    var newNoteIds = await ankiClient.AddNotesAsync(flashcards, deckName, tags);
                    Console.WriteLine($"    > Synced {newNoteIds.Count} flashcards to Anki deck '{deckName}'.");
                    Cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName);

                    foreach (var newNoteId in newNoteIds)
                    {
                        allValidNoteIds.Add(newNoteId);
                    }
                }
                else if (Cache.ContainsKey(cacheKey))
                {
                    Cache.TryRemove(cacheKey, out _);
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

    private (string DeckName, IReadOnlyCollection<string> Tags) ResolveDeckName(string filePath, Dictionary<object, object> frontMatter)
    {
        if (!frontMatter.TryGetValue("categories", out var cats) || cats is not List<object> catList)
        {
            return (Path.GetFileNameWithoutExtension(filePath), new List<string>());
        }

        var categories = catList.Select(c => c.ToString()!).ToList();
        return categories.Any() ? 
            CategoryAnalyzer.ResolveDeckName(categories) : 
            (Path.GetFileNameWithoutExtension(filePath), new List<string>());
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
        await GeminiRateLimiter.AcquireAsync();
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
                builder.SetMinimumLevel(LogLevel.Trace);
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
        var document = Markdown.Parse(markdownContent, Pipeline);
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

public record CacheEntry(
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("noteIds")] IReadOnlyCollection<long> NoteIds,
    [property: JsonPropertyName("deckName")] string DeckName
);
