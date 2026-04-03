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
using Spectre.Console;
using YamlDotNet.Serialization;

namespace VaultToFlashcard;

public class VaultProcessor(AnkiConnectClient ankiClient, bool readOnly)
{
    private readonly CategoryAnalyzer _categoryAnalyzer = new();
    private ConcurrentDictionary<string, CacheEntry> _cache = new();

    private const string CacheFileName = ".obsidian-anki-cache.json";
    private const string CardSourceFormat = "{0}#{1}";
    
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    private static readonly FixedWindowRateLimiter GeminiRateLimiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1,
        Window = TimeSpan.FromSeconds(15),
        AutoReplenishment = true
    });
    private static readonly SemaphoreSlim FileProcessingSemaphore = new(10);

    private async Task AnalyzeAllCategoriesAsync(IEnumerable<string> markdownFiles, ProgressTask task)
    {
        task.StartTask();
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
        task.StopTask();
    }

    public async Task ProcessVault(string vaultPath, string aiMode, string apiKey, string model)
    {
        AnsiConsole.MarkupLine($"Starting vault processing at: [blue]{vaultPath}[/]");
        var cachePath = Path.Combine(vaultPath, CacheFileName);

        if (File.Exists(cachePath))
        {
            AnsiConsole.MarkupLine("Loading cache...");
            var json = await File.ReadAllTextAsync(cachePath);
            _cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheEntry>>(json) ?? new();
        }

        if (!readOnly)
        {
            await ankiClient.EnsureFieldsExist("Basic", new[] { "Source" });
            await ankiClient.EnsureFieldsExist("Cloze", new[] { "Source" });
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow][[Read-Only]][/] Would ensure 'Source' field exists on 'Basic' and 'Cloze' models.");
        }

        var markdownFiles = Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(p => !p.Contains(CacheFileName))
            .ToList();
        
        var summary = new ProcessingSummary
        {
            TotalFiles = markdownFiles.Count
        };
        var allResults = new ConcurrentBag<(string RelativePath, Tree Tree)>();
        
        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var analysisTask = ctx.AddTask("[green]Analyzing categories[/]");
                await AnalyzeAllCategoriesAsync(markdownFiles, analysisTask);

                var processingTask = ctx.AddTask("[green]Processing files[/]", new ProgressTaskSettings { MaxValue = markdownFiles.Count, AutoStart = false });
                var allValidNoteIds = new ConcurrentBag<long>();
                var processingTasks = new List<Task>();
                
                foreach (var filePath in markdownFiles)
                {
                    await FileProcessingSemaphore.WaitAsync();
                    processingTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var (fileSummary, tree, relativePath) = await ProcessFileAsync(filePath, aiMode, apiKey, model, vaultPath, allValidNoteIds);
                            summary.Aggregate(fileSummary);
                            if (tree != null && relativePath != null)
                            {
                                allResults.Add((relativePath, tree));
                            }
                        }
                        finally
                        {
                            FileProcessingSemaphore.Release();
                            processingTask.Increment(1);
                        }
                    }));
                }

                processingTask.StartTask();
                await Task.WhenAll(processingTasks);
                
                var cleanupTask = ctx.AddTask("[green]Cleaning up orphaned notes[/]");
                var orphanedCount = await CleanUpOrphanedNotesAsync(allValidNoteIds, cleanupTask);
                summary.OrphanedNotesDeleted = orphanedCount;
            });
        
        foreach (var result in allResults.OrderBy(r => r.RelativePath))
        {
            AnsiConsole.Write(result.Tree);
        }

        if (!readOnly)
        {
            AnsiConsole.MarkupLine("Saving cache...");
            var newJson = JsonSerializer.Serialize(_cache, JsonOptions);
            await File.WriteAllTextAsync(cachePath, newJson);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow][[Read-Only]][/] Skipping cache save.");
        }

        DisplaySummary(summary);
        AnsiConsole.MarkupLine("[bold green]Vault processing complete.[/]");
    }

    private async Task<int> CleanUpOrphanedNotesAsync(ConcurrentBag<long> validNoteIds, ProgressTask task)
    {
        task.StartTask();
        var ankiNoteIds = await ankiClient.FindAllTaggedNotesAsync();
    
        var validIdSet = new HashSet<long>(validNoteIds);
        var orphanedIds = ankiNoteIds.Where(id => !validIdSet.Contains(id)).ToList();

        if (orphanedIds.Any())
        {
            AnsiConsole.MarkupLine($"  > Found {orphanedIds.Count} orphaned notes to delete.");
            if (!readOnly)
            {
                await ankiClient.DeleteNotesAsync(orphanedIds);
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow][[Read-Only]][/] Would delete {orphanedIds.Count} orphaned notes.");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  > No orphaned notes found.");
        }
        task.StopTask();
        return orphanedIds.Count;
    }

    private async Task<(ProcessingSummary? summary, Tree? tree, string? relativePath)> ProcessFileAsync(string filePath, string aiMode, string apiKey, string model, string vaultPath, ConcurrentBag<long> allValidNoteIds)
    {
        var summary = new ProcessingSummary();
        var relativePath = Path.GetRelativePath(vaultPath, filePath);
        var tree = new Tree($"[yellow]Processing:[/] [blue]{Markup.Escape(relativePath)}[/]");

        try
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            var yamlHeaderMatch = new Regex(@"^---\s*(.*?)---\s*", RegexOptions.Singleline);
            var match = yamlHeaderMatch.Match(fileContent);

            if (!match.Success) return (null, null, null);

            var yamlContent = match.Groups[1].Value;
            var markdownContent = fileContent.Substring(match.Length); 

            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            var frontMatter = deserializer.Deserialize<Dictionary<object, object>>(yamlContent);

            if (!frontMatter.TryGetValue("study", out var studyValue))
            {
                return (null, null, null);
            }

            var shouldStudy = studyValue switch { true => true, "true" => true, _ => false };
            if (!shouldStudy)
            {
                return (null, null, null);
            }
            
            var contentChunks = ParseAndSanitize(markdownContent);

            var (deckName, tags) = ResolveDeckName(filePath, frontMatter);
            if (!readOnly)
            {
                await ankiClient.CreateDeckAsync(deckName);
            }
            else
            {
                tree.AddNode($"[yellow][[Read-Only]][/] Would create deck '{Markup.Escape(deckName)}'.");
            }

            foreach (var (header, content) in contentChunks)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var cacheKey = $"{Path.GetRelativePath(vaultPath, filePath)}#{header}";
                var contentHash = CalculateHash(content);

                _cache.TryGetValue(cacheKey, out var cachedEntry);

                if (cachedEntry != null && cachedEntry.DeckName != deckName && cachedEntry.ContentHash == contentHash)
                {
                    tree.AddNode($"[yellow]MOVED:[/] '{Markup.Escape(header)}' from deck '{Markup.Escape(cachedEntry.DeckName)}' to '{Markup.Escape(deckName)}'");
                    summary.NotesMoved++;
                    if (!readOnly)
                    {
                        await ankiClient.ChangeDeckAsync(cachedEntry.NoteIds, deckName);
                        _cache[cacheKey] = cachedEntry with { DeckName = deckName };
                    }

                    foreach (var newNoteId in cachedEntry.NoteIds)
                    {
                        allValidNoteIds.Add(newNoteId);
                    }
                    continue;
                }
                
                if (cachedEntry != null && cachedEntry.ContentHash == contentHash)
                {
                    tree.AddNode($"[grey]CHECKING:[/] '{Markup.Escape(header)}' (unchanged content)");

                    if (cachedEntry.NoteIds.Any())
                    {
                        var notesInfoResult = await ankiClient.GetNotesInfoResilientAsync(cachedEntry.NoteIds);

                        if (notesInfoResult.NotFound.Any())
                        {
                            tree.AddNode($"[red]PRUNING:[/] Detected {notesInfoResult.NotFound.Count} manually deleted notes for section '{Markup.Escape(header)}'.");
                            var validNoteIds = cachedEntry.NoteIds.Except(notesInfoResult.NotFound).ToList();
                            if (!readOnly)
                            {
                                if (validNoteIds.Any())
                                {
                                    _cache[cacheKey] = cachedEntry with { NoteIds = validNoteIds };
                                }
                                else
                                {
                                    _cache.TryRemove(cacheKey, out _);
                                }
                            }
                        }

                        foreach (var noteInfo in notesInfoResult.Succeeded)
                        {
                            if (!readOnly)
                            {
                                await ankiClient.MergeTagsAsync(noteInfo.NoteId, tags);
                            }
                            allValidNoteIds.Add(noteInfo.NoteId);
                        }
                    }
                    continue;
                }

                // New or Changed content
                var wasCached = cachedEntry != null;
                if (wasCached && !readOnly)
                {
                    await ankiClient.DeleteNotesAsync(cachedEntry!.NoteIds);
                }

                IReadOnlyCollection<Flashcard> flashcards;
                if (readOnly)
                {
                    flashcards = new List<Flashcard> { new BasicFlashcard() };
                }
                else
                {
                    switch (aiMode)
                    {
                        case "cli":
                            flashcards = await GenerateFlashcardsCliAsync(content, frontMatter, relativePath, Path.GetFileName(filePath), header, model);
                            break;
                        case "api":
                            flashcards = await GenerateFlashcardsApiAsync(content, frontMatter, relativePath, Path.GetFileName(filePath), header, model, apiKey);
                            break;
                        default:
                            AnsiConsole.MarkupLine($"[red]Unknown ai-mode: {aiMode}[/]");
                            continue;
                    }
                }

                if (flashcards.Any())
                {
                    IReadOnlyCollection<long> newNoteIds = new List<long>();
                    if (!readOnly)
                    {
                        newNoteIds = await ankiClient.AddNotesAsync(flashcards, deckName, tags);
                        summary.NewFlashcards += newNoteIds.Count;
                        _cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName);
                        foreach (var newNoteId in newNoteIds)
                        {
                            allValidNoteIds.Add(newNoteId);
                        }
                    }

                    if (wasCached)
                    {
                        var oldCount = cachedEntry!.NoteIds.Count;
                        var newCount = readOnly ? "some" : $"{newNoteIds.Count}";
                        tree.AddNode($"[yellow]MODIFIED:[/] '{Markup.Escape(header)}' ({oldCount} old -> {newCount} new)");
                    }
                    else
                    {
                        var newCount = readOnly ? "some" : $"{newNoteIds.Count}";
                        tree.AddNode($"[green]ADDED:[/] '{Markup.Escape(header)}' ({newCount} flashcards)");
                    }
                }
                else
                {
                    if (wasCached)
                    {
                        if (!readOnly)
                        {
                            _cache.TryRemove(cacheKey, out _);
                        }
                        tree.AddNode($"[red]DELETED:[/] '{Markup.Escape(header)}' ({cachedEntry!.NoteIds.Count} flashcards)");
                    }
                    // If not wasCached and no flashcards, do nothing.
                }
            }
            
            return (summary, tree, relativePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error processing file {filePath}[/]");
            AnsiConsole.WriteException(ex);
            return (null, null, null);
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
            _categoryAnalyzer.ResolveDeckName(categories) : 
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

        var containsList = Regex.IsMatch(content, @"^\s*-\s+", RegexOptions.Multiline);

        var systemPrompt = new StringBuilder("""
                                             You are an Anki Instructional Designer. Create self-contained cards on the content provided using either Cloze or Basic format. Utilize HTML tags for styling. Important rules:
                                             1. BREVITY: Ensure the facts being placed into flashcards are the most crucial parts of the text. Skip sections of text that are superfluous or not assessing a fact (e.g. opinionated).
                                             2. NO HIDDEN CONTEXT: Use specific names; never "it" or "this".
                                             3. ATOMICITY: One card = One discrete fact. 
                                             4. CLOZES: Use {{c1::answer::hint}}. Clozes cards must have at *least* two -- never have a flashcard with a single cloze. Never cloze-delete the primary topic word, and only use hints if required for context.
                                             """);

        var assistantPrompt = new StringBuilder("""
                                                Examples:
                                                - Set: [{"text": "{{c1::Canberra::city}} was founded in {{c2::1913}}"}]
                                                - Vocab: [{"text": "{{c1::Bonjour::French}} is used for {{c2::greeting someone in the morning}}."}]
                                                - Q&A: [{"front": "When should a Trie be used over a Hash Map?", "back": "When you need efficient prefix-based searching/auto-complete."}]
                                                """);

        if (containsList)
        {
            systemPrompt.AppendLine(
                "5. LIST HOOKS: If converting a list, the text outside the cloze MUST contain a unique characteristic (function/keyword) to make the card uniquely guessable.");
            assistantPrompt.AppendLine("""- List: [{"text": "The three main concurrency primitives in Go are: <ul><li>{{c1::Goroutines::lightweight threads}}</li><li>{{c2::Channels::communication mechanism}}</li><li>{{c3::Select Statement::multiplexing mechanism}}</li></ul>"}]""");
        }
        
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt.ToString()),
            new(ChatRole.User, $"Context: This note has the following categories: '{noteCategories}' and is titled '{fileName}'."),
        };

        if (!string.Equals(Path.GetFileNameWithoutExtension(fileName), header, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new(ChatRole.User, $"Section Name: {header}"));
        }

        messages.Add(new(ChatRole.Assistant, assistantPrompt.ToString()));
        messages.Add(new (ChatRole.User, $@"Content to convert:\n{content}\n\nTask: Create atomic Anki flashcards from this content."));
        
        return messages;
    }
    
    private async Task<IReadOnlyCollection<Flashcard>> GenerateFlashcardsApiAsync(string content,
        Dictionary<object, object> frontMatter, string relativePath, string fileName, string header, string model,
        string apiKey)
    {
        using var lease = await GeminiRateLimiter.AcquireAsync();
        var promptMessages = GetPromptMessages(content, frontMatter, relativePath, header);
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
            AnsiConsole.MarkupLine($"[red]Error deserializing JSON for chunk '{Markup.Escape(header)}': {response.FinishReason}[/]");
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
                    
                    card.Source = string.Format(CardSourceFormat, fileName, header); // which is now the relative path
                    return card;
                })
                .ToArray();
        }
        catch(JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deserializing JSON for chunk '{Markup.Escape(header)}': {ex.Message}[/]");
            AnsiConsole.MarkupLine($"[red]-- Invalid JSON --[/] {Markup.Escape(response.Text)} [red]------------------[/]");
            return new List<Flashcard>();
        }
    }

    private async Task<IReadOnlyCollection<Flashcard>> GenerateFlashcardsCliAsync(string content,
        Dictionary<object, object> frontMatter, string relativePath, string fileName, string header, string model)
    {
        var promptMessages = GetPromptMessages(content, frontMatter, relativePath, header)
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
            AnsiConsole.MarkupLine($"[yellow]Warning: Could not find JSON array in the output from Gemini CLI for chunk '{Markup.Escape(header)}'. Skipping.[/]");
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
                card.Source = string.Format(CardSourceFormat, fileName, header);
            }
            return flashcards;
        }
        catch(JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deserializing JSON for chunk '{Markup.Escape(header)}': {ex.Message}[/]");
            return new List<Flashcard>();
        }
    }
    
    private void DisplaySummary(ProcessingSummary summary)
    {
        var table = new Table()
            .AddColumn("Metric")
            .AddColumn("Value")
            .AddRow("Total Files", summary.TotalFiles.ToString())
            .AddRow("Files Processed", summary.FilesProcessed.ToString())
            .AddRow("New Flashcards", summary.NewFlashcards.ToString())
            .AddRow("Notes Moved", summary.NotesMoved.ToString())
            .AddRow("Orphaned Notes Deleted", summary.OrphanedNotesDeleted.ToString());

        AnsiConsole.Write(
            new Panel(table)
                .Header("Processing Summary")
                .Border(BoxBorder.Rounded));
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
                if (heading.Inline != null)
                {
                    foreach(var inline in heading.Inline) ExtractTextRecursive(inline, sb);
                }
                break;
            case ParagraphBlock paragraph:
                if (paragraph.Inline != null)
                {
                    foreach (var inline in paragraph.Inline) ExtractTextRecursive(inline, sb);
                }
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
