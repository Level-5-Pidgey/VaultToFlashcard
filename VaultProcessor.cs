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

public class VaultProcessor(AnkiConnectClient ankiClient, bool readOnly, CategoryPromptRegistry? promptRegistry = null)
{
    private readonly CategoryAnalyzer CategoryAnalyzer = new();
    private readonly CategoryPromptRegistry PromptRegistry = promptRegistry ?? new CategoryPromptRegistry();
    private ConcurrentDictionary<string, CacheEntry> Cache = new();

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
            task.Increment(1);
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
        task.StopTask();
    }

    public async Task ProcessVault(string vaultPath, string apiKey, string model)
    {
        AnsiConsole.MarkupLine($"Starting vault processing at: [blue]{vaultPath}[/]");
        var cachePath = Path.Combine(vaultPath, CacheFileName);

        if (File.Exists(cachePath))
        {
            AnsiConsole.MarkupLine("Loading cache...");
            var json = await File.ReadAllTextAsync(cachePath);
            Cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheEntry>>(json) ?? new();
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
                var analysisTask = ctx.AddTask("[green]Analyzing categories[/]", false, markdownFiles.Count);
                await AnalyzeAllCategoriesAsync(markdownFiles, analysisTask);

                var preScanTask = ctx.AddTask("[green]Ensuring card types exist[/]", true, 1);
                await EnsureRequiredModelsExistAsync();
                preScanTask.Increment(1);
                preScanTask.StopTask();

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
                            var (fileSummary, tree, relativePath) = await ProcessFileAsync(filePath, apiKey, model, vaultPath, allValidNoteIds);
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
                
                var cleanupTask = ctx.AddTask("[green]Cleaning up orphaned notes[/]", false, 1);
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
            var newJson = JsonSerializer.Serialize(Cache, JsonOptions);
            await File.WriteAllTextAsync(cachePath, newJson);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow][[Read-Only]][/] Skipping cache save.");
        }

        DisplaySummary(summary);
        AnsiConsole.MarkupLine("[bold green]Vault processing complete.[/]");
    }

    private async Task EnsureRequiredModelsExistAsync()
    {
        if (!readOnly)
        {
            // Ensure Source field exists on all standard models
            await ankiClient.EnsureFieldsExist("Basic", new[] { "Source" });
            await ankiClient.EnsureFieldsExist("Cloze", new[] { "Source" });
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow][[Read-Only]][/] Would ensure 'Source' field exists on 'Basic' and 'Cloze' models.");
        }

        // Get all required model names from the registry
        var requiredModels = PromptRegistry.GetAllRequiredModelNames();
        
        foreach (var modelName in requiredModels)
        {
            // Find the card type definition for this model
            CardTypeDefinition? cardType = null;
            
            // Check custom configurations
            foreach (var config in PromptRegistry.GetAllConfiguredCategoryNames())
            {
                var matchedConfig = PromptRegistry.FindBestMatch(new[] { config });
                if (matchedConfig != null)
                {
                    cardType = matchedConfig.CardTypes.FirstOrDefault(ct => ct.ModelName == modelName);
                    if (cardType != null) break;
                }
            }
            
            // If not found, check default config
            if (cardType == null)
            {
                var defaultConfig = PromptRegistry.GetDefaultConfiguration();
                cardType = defaultConfig.CardTypes.FirstOrDefault(ct => ct.ModelName == modelName);
            }
            
            if (cardType != null)
            {
                var requiredFields = cardType.JsonSchemaProperties.Keys.Append("Source").ToList();
                await ankiClient.EnsureModelExistsAsync(modelName, requiredFields, readOnly);
            }
        }
    }

    private async Task<int> CleanUpOrphanedNotesAsync(ConcurrentBag<long> validNoteIds, ProgressTask task)
    {
        task.StartTask();
        var ankiNoteIds = await ankiClient.FindAllTaggedNotesAsync();
    
        var validIdSet = new HashSet<long>(validNoteIds);
        var orphanedIds = ankiNoteIds.Where(id => !validIdSet.Contains(id)).ToList();

        if (orphanedIds.Any())
        {
            AnsiConsole.MarkupLine($"Found {orphanedIds.Count} orphaned notes to delete.");
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
            AnsiConsole.MarkupLine("[SeaGreen2]No orphaned notes found.[/]");
        }
        task.Increment(1);
        task.StopTask();
        return orphanedIds.Count;
    }

    private enum FileUpdateType
    {
        Unchanged = 0,
        Modified = 1,
        Deleted = 2,
        Created = 3,
    }

    private static string
        GetFileUpdateResultString(string item, FileUpdateType fileUpdateType, string? extraText = null)
    {
        const string formattedTemplate = "[{0}]{1}[/] {2}";
        var textColor = fileUpdateType switch
        {
            FileUpdateType.Unchanged => "Grey42",
            FileUpdateType.Modified => "Grey70",
            FileUpdateType.Deleted => "Red3_1",
            FileUpdateType.Created => "SeaGreen2",
            _ => throw new ArgumentOutOfRangeException(nameof(fileUpdateType), fileUpdateType, extraText)
        };

        var extraTextFormatted = string.Empty;
        if (extraText is not null)
        {
            var extraTextColor = fileUpdateType switch
            {
                FileUpdateType.Unchanged => "Grey",
                FileUpdateType.Modified => "Grey",
                FileUpdateType.Deleted => "DarkRed_1",
                FileUpdateType.Created => "DarkSeaGreen4_1",
                _ => throw new ArgumentOutOfRangeException(nameof(fileUpdateType), fileUpdateType, extraText)
            };
                
            extraTextFormatted = extraText.Length > 0 ? 
                $"[{extraTextColor}]({Markup.Escape(extraText)})[/]" : 
                string.Empty;
        }

        return string.Format(formattedTemplate, textColor, Markup.Escape(item), extraTextFormatted).Trim();
    }

    private async Task<(ProcessingSummary? summary, Tree? tree, string? relativePath)> ProcessFileAsync(string filePath, string apiKey, string model, string vaultPath, ConcurrentBag<long> allValidNoteIds)
    {
        var summary = new ProcessingSummary();
        var relativePath = Path.GetRelativePath(vaultPath, filePath);

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
            
            // Extract note categories for prompt matching
            var noteCategories = new List<string>();
            if (frontMatter.TryGetValue("categories", out var cats) && cats is List<object> catList)
            {
                noteCategories = catList.Select(c => c.ToString()!).ToList();
            }
            else if (frontMatter.TryGetValue("tags", out var tags) && tags is List<object> tagList)
            {
                noteCategories = tagList.Select(t => t.ToString()!).ToList();
            }

            var promptConfig = PromptRegistry.FindBestMatch(noteCategories) ?? PromptRegistry.GetDefaultConfiguration();
            var cardTypes = promptConfig.CardTypes.Select(x => x.ModelName);
            var tree = new Tree($"[blue]{Markup.Escape(relativePath)}[/] [Grey70]({Markup.Escape(string.Join(", ", cardTypes))})[/]");

            var contentChunks = ParseAndSanitize(markdownContent);

            var (deckName, deckTags) = ResolveDeckName(filePath, frontMatter);
            if (!readOnly)
            {
                await ankiClient.CreateDeckAsync(deckName);
            }

            foreach (var (header, content) in contentChunks)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var cacheKey = $"{Path.GetRelativePath(vaultPath, filePath)}#{header}";
                var contentHash = CalculateHash(content);

                Cache.TryGetValue(cacheKey, out var cachedEntry);

                if (cachedEntry != null && cachedEntry.DeckName != deckName && cachedEntry.ContentHash == contentHash)
                {
                    tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Modified,
                        $"{cachedEntry.DeckName} -> {deckName}"));
                    summary.NotesMoved++;
                    if (!readOnly)
                    {
                        await ankiClient.ChangeDeckAsync(cachedEntry.NoteIds, deckName);
                        Cache[cacheKey] = cachedEntry with { DeckName = deckName };
                    }

                    foreach (var newNoteId in cachedEntry.NoteIds)
                    {
                        allValidNoteIds.Add(newNoteId);
                    }
                    continue;
                }
                
                if (cachedEntry != null && cachedEntry.ContentHash == contentHash)
                {
                    tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Unchanged));

                    if (cachedEntry.NoteIds.Any())
                    {
                        var notesInfoResult = await ankiClient.GetNotesInfoResilientAsync(cachedEntry.NoteIds);

                        if (notesInfoResult.NotFound.Any())
                        {
                            tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Deleted,
                                $"{notesInfoResult.NotFound.Count} manually deleted notes in section '{header}'"));
                            var validNoteIds = cachedEntry.NoteIds.Except(notesInfoResult.NotFound).ToList();
                            if (!readOnly)
                            {
                                if (validNoteIds.Any())
                                {
                                    Cache[cacheKey] = cachedEntry with { NoteIds = validNoteIds };
                                }
                                else
                                {
                                    Cache.TryRemove(cacheKey, out _);
                                }
                            }
                        }

                        foreach (var noteInfo in notesInfoResult.Succeeded)
                        {
                            if (!readOnly)
                            {
                                await ankiClient.MergeTagsAsync(noteInfo.NoteId, deckTags);
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

                IReadOnlyCollection<DynamicFlashcard> flashcards;
                if (readOnly)
                {
                    flashcards = new List<DynamicFlashcard> { new DynamicFlashcard("Basic", new Dictionary<string, string>()) };
                }
                else
                {
                    flashcards = await GenerateFlashcardsAsync(content, relativePath, header, model, apiKey, promptConfig);
                }

                if (flashcards.Any())
                {
                    IReadOnlyCollection<long> newNoteIds = new List<long>();
                    if (!readOnly)
                    {
                        newNoteIds = await ankiClient.AddDynamicNotesAsync(flashcards, deckName, deckTags);
                        summary.NewFlashcards += newNoteIds.Count;
                        Cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName);
                        foreach (var newNoteId in newNoteIds)
                        {
                            allValidNoteIds.Add(newNoteId);
                        }
                    }

                    if (wasCached)
                    {
                        var oldCount = cachedEntry!.NoteIds.Count;
                        var newCount = readOnly ? "some" : $"{newNoteIds.Count}";
                        tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Modified,
                            $"{oldCount} old -> {newCount} new"));
                    }
                    else
                    {
                        var newCount = readOnly ? "some" : $"{newNoteIds.Count}";
                        tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Created,
                            $"+{newCount} flashcards"));
                    }
                }
                else
                {
                    if (wasCached)
                    {
                        if (!readOnly)
                        {
                            Cache.TryRemove(cacheKey, out _);
                        }
                        tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Deleted,
                            $"-{cachedEntry!.NoteIds.Count} flashcards"));
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
            CategoryAnalyzer.ResolveDeckName(categories) : 
            (Path.GetFileNameWithoutExtension(filePath), new List<string>());
    }
    
    private IReadOnlyCollection<ChatMessage> GetPromptMessages(string content, CategoryPromptConfiguration? config, string relativePath, string header)
    {
        var containsList = Regex.IsMatch(content, @"^\s*-\s+", RegexOptions.Multiline);

        var systemPrompt = new StringBuilder("""
                                             You are an expert Anki Instructional Designer. Your goal is to transform provided text into high-quality, long-term memory flashcards. Important rules:
                                             1. BREVITY: Capture only the "load-bearing" facts. Omit fluff, opinions, or introductory filler.
                                             2. ATOMICITY: Each card must test exactly one discrete idea. If a section has multiple facts, create multiple cards.
                                             3. NO HIDDEN CONTEXT: Use specific nouns. Never use "it," "this," or "they" unless the antecedent is inside the card.
                                             4. FORMATTING: Format fields using HTML. Use <code> tags for technical terms/data and <anki_mathjax> for maths/formulae.
                                             """);
        var systemPromptTenantNumber = 4;

        var assistantPrompt = new StringBuilder();

        // Use configuration if available, otherwise use default
        var cardTypes = config?.CardTypes ?? PromptRegistry.GetDefaultConfiguration().CardTypes;

        if (cardTypes.Any(x => x.ModelName == "Cloze"))
        {
            systemPromptTenantNumber++;
            systemPrompt.AppendLine($"{systemPromptTenantNumber}. CLOZES: Use {{c1::answer::hint}}. Clozes cards must have at *least* two -- never have a flashcard with a single cloze. Never cloze-delete the primary topic word, and only use hints if required for context.");
        }
        
        // Build examples from configured card types
        foreach (var cardType in cardTypes)
        {
            if (!string.IsNullOrEmpty(cardType.ExampleOutput))
            {
                assistantPrompt.AppendLine($"- {cardType.ModelName}: [{cardType.ExampleOutput}]");
            }
        }
        
        // Add category-specific addendums
        if (config != null && !string.IsNullOrEmpty(config.AssistantPromptAddendum))
        {
            assistantPrompt.AppendLine();
            assistantPrompt.AppendLine(config.AssistantPromptAddendum);
        }

        if (containsList)
        {
            systemPromptTenantNumber++;
            systemPrompt.AppendLine(
                $"{systemPromptTenantNumber}. LIST HOOKS: If converting a list, the text outside the cloze MUST contain a unique characteristic (function/keyword) to make the card uniquely guessable.");
            assistantPrompt.AppendLine("""- List: [{"text": "The three main concurrency primitives in Go are: <ul><li>{{c1::Goroutines::lightweight threads}}</li><li>{{c2::Channels::communication mechanism}}</li><li>{{c3::Select Statement::multiplexing mechanism}}</li></ul>"}]""");
        }

        // Add system prompt addendum from config
        if (config != null && !string.IsNullOrEmpty(config.SystemPromptAddendum))
        {
            systemPrompt.AppendLine();
            systemPrompt.AppendLine(config.SystemPromptAddendum);
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt.ToString()),
        };

        if (config != null)
        {
            messages.Add(new(ChatRole.User, $"Context: This note has the following categories: '{config.Category}'."));
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(relativePath), header, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new(ChatRole.User, $"Section Name: {header}"));
        }

        messages.Add(new(ChatRole.Assistant, assistantPrompt.ToString()));
        messages.Add(new (ChatRole.User, $@"Content to convert:\n{content}\n\nTask: Create atomic Anki flashcards from this content."));
        
        return messages;
    }
    
    private async Task<IReadOnlyCollection<DynamicFlashcard>> GenerateFlashcardsAsync(string content,
        string relativePath, string header, string model,
        string apiKey, CategoryPromptConfiguration promptConfig)
    {
        using var lease = await GeminiRateLimiter.AcquireAsync();
        
        // Use first card type from config (could be extended to pick based on content)
        var cardTypes = promptConfig.CardTypes;
        var selectedCardType = cardTypes.First();

        var promptMessages = GetPromptMessages(content, promptConfig, relativePath, header);
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

        // Build JSON schema for the selected card type
        var schema = CategoryPromptRegistry.BuildJsonSchema(selectedCardType);
        var schemaDescription = CategoryPromptRegistry.BuildSchemaDescription(selectedCardType);

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, selectedCardType.ModelName, schemaDescription),
            Temperature = 0.15f,
        };

        var response = await client.GetResponseAsync(promptMessages, options);

        if (response.FinishReason != ChatFinishReason.Stop)
        {
            AnsiConsole.MarkupLine($"[red]Error generating flashcards for chunk '{Markup.Escape(header)}': {response.FinishReason}[/]");
            return Array.Empty<DynamicFlashcard>();
        }

        try
        {
            var cards = JsonSerializer.Deserialize<JsonElement[]>(response.Text) ?? [];
            return cards.Select(card =>
            {
                var fields = new Dictionary<string, string>();
                
                // Extract fields based on the schema properties
                foreach (var prop in selectedCardType.JsonSchemaProperties.Keys)
                {
                    if (card.TryGetProperty(prop, out var propValue) && propValue.ValueKind == JsonValueKind.String)
                    {
                        fields[prop] = propValue.GetString() ?? "";
                    }
                }
                
                return new DynamicFlashcard(selectedCardType.ModelName, fields, string.Format(CardSourceFormat, relativePath, header));
            }).ToArray();
        }
        catch(JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deserializing JSON for chunk '{Markup.Escape(header)}': {ex.Message}[/]");
            AnsiConsole.MarkupLine($"[red]-- Invalid JSON --[/] {Markup.Escape(response.Text)} [red]------------------[/]");
            return new List<DynamicFlashcard>();
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