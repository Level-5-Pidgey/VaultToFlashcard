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
    
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    private static readonly FixedWindowRateLimiter GeminiRateLimiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1,
        Window = TimeSpan.FromSeconds(15),
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

        
        await ankiClient.EnsureFieldsExist("Basic", new[] { "SourceNote", "SourceSection" });
        await ankiClient.EnsureFieldsExist("Cloze", new[] { "SourceNote", "SourceSection" });

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

                var relativePath = Path.GetRelativePath(vaultPath, filePath);
                IReadOnlyCollection<Flashcard> flashcards;
                switch (aiMode)
                {
                    case "cli":
                        flashcards = await GenerateFlashcardsCliAsync(content, frontMatter, relativePath, header, model);
                        break;
                    case "api":
                        flashcards = await GenerateFlashcardsApiAsync(content, frontMatter, relativePath, header, model, apiKey);
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
            new(ChatRole.System, """
                                 You are an Anki Instructional Designer. Create atomic, self-contained cards.
                                 1. NO HIDDEN CONTEXT: Use specific names; never "it" or "this".
                                 2. ATOMICITY: One card = One discrete fact.
                                 3. LIST HOOKS: If converting a list, the text outside the cloze MUST contain a unique characteristic (function/keyword) to make the card uniquely guessable.
                                 4. CLOZES: Use {{c1::answer::hint}}. Never cloze-delete the primary topic word, and only use hints if required for context.
                                 """),

            new(ChatRole.User,
                $"Context: This note has the following categories: '{noteCategories}' and is titled '{fileName}'."),
        };

        if (!string.Equals(Path.GetFileNameWithoutExtension(fileName), header, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new(ChatRole.User, $"Section Name: {header}"));
        }

        // Add example responses to help generate better results
        messages.Add(new(ChatRole.Assistant, """
                                             Examples:
                                             - Set: [{"text": "{{c1::Canberra::city}} was founded in {{c2::1913}}"}]
                                             - Vocab: [{"text": "{{c1::Bonjour::French}} is used for {{c2::greeting someone in the morning}}."}]
                                             - Q&A: [{"front": "When should a Trie be used over a Hash Map?", "back": "When you need efficient prefix-based searching/auto-complete."}]
                                             - List: "text": "The three main concurrency primitives in Go are: <ul><li>{{c1::Goroutines::lightweight threads}}</li><li>{{c2::Channels::communication mechanism}}</li><li>{{c3::Select Statement::multiplexing mechanism}}</li></ul>"
                                             """));
        
        // Add actual text extracted from document
        messages.Add(new (ChatRole.User, $@"Content to convert:\n{content}\n\nTask: Create atomic Anki flashcards from this content."));
        
        return messages;
    }
    
    private async Task<IReadOnlyCollection<Flashcard>> GenerateFlashcardsApiAsync(string content, Dictionary<object, object> frontMatter, string fileName, string header, string model, string apiKey)
    {
        using var lease = await GeminiRateLimiter.AcquireAsync();
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
                builder.SetMinimumLevel(LogLevel.Warning);
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
                return transport.Select(x =>
                    {
                        Flashcard card = x.Type switch
                        {
                            FlashcardType.Basic => new BasicFlashcard(x),
                            FlashcardType.Cloze => new ClozeFlashcard(x),
                            _ => throw new InvalidOperationException(),
                        };
                        card.SourceNote = fileName; // which is now the relative path
                        card.SourceSection = header;
                        return card;
                    })
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
            lease.Dispose();
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
            var flashcards = JsonSerializer.Deserialize<List<Flashcard>>(jsonOutput, jsonOptions) ?? new List<Flashcard>();
            foreach (var card in flashcards)
            {
                card.SourceNote = fileName;
                card.SourceSection = header;
            }
            return flashcards;
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
                if (fencedCodeBlock.Lines.Lines != null)
                {
                    foreach (var line in fencedCodeBlock.Lines.Lines)
                    {
                        sb.AppendLine(line.ToString());
                    }
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
            case ContainerBlock container:
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
