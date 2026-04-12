using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using YamlDotNet.Serialization;

namespace VaultToFlashcard;

public class VaultProcessor(
	AnkiConnectClient ankiClient,
	IChatClient chatClient,
	bool readOnly,
	string skipToken = "SKIP_TOKEN",
	CategoryPromptRegistry? promptRegistry = null,
	ILogger? logger = null)
{
	private readonly CategoryAnalyzer CategoryAnalyzer = new();
	private readonly CategoryPromptRegistry PromptRegistry = promptRegistry ?? new CategoryPromptRegistry();
	private readonly MediaExtractor MediaExtractor = new();
	private readonly MediaMerger MediaMerger = new();
	private ConcurrentDictionary<string, CacheEntry> Cache = new();

	private const string CacheFileName = ".obsidian-anki-cache.json";

	/// <summary>
	/// Creates an Obsidian deeplink URL wrapped in an HTML anchor tag for clickable links in Anki.
	/// Format: <a href="obsidian://open?vault={vaultName}&file={encodedFilePath}[#{encodedHeader}]">{displayText}</a>
	/// </summary>
	private static string CreateObsidianDeeplink(string vaultName, string relativePath, string? header = null)
	{
		var encodedVaultName = Uri.EscapeDataString(vaultName);
		var encodedFilePath = Uri.EscapeDataString(relativePath);

		var deeplink = $"obsidian://open?vault={encodedVaultName}&file={encodedFilePath}";

		if (!string.IsNullOrEmpty(header))
		{
			var encodedHeader = Uri.EscapeDataString(header);
			deeplink += $"#{encodedHeader}";
		}

		// Use filename and optionally header as display text
		var displayText = Path.GetFileName(relativePath);
		if (!string.IsNullOrEmpty(header)) displayText += $" #{header}";

		return $"<a href=\"{deeplink}\">{displayText}</a>";
	}

	private static readonly IDeserializer YamlDeserializer =
		new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

	// Color mappings for file update results
	private static readonly Dictionary<FileUpdateType, string> UpdateResultColors = new()
	{
		[FileUpdateType.Unchanged] = "Grey42",
		[FileUpdateType.Modified] = "Grey70",
		[FileUpdateType.Deleted] = "Red3_1",
		[FileUpdateType.Created] = "SeaGreen2",
		[FileUpdateType.Suspended] = "DeepPink4_1",
		[FileUpdateType.Unsuspended] = "DeepPink3_1"
	};

	private static readonly Dictionary<FileUpdateType, string> ExtraTextColors = new()
	{
		[FileUpdateType.Unchanged] = "Grey",
		[FileUpdateType.Modified] = "Grey",
		[FileUpdateType.Deleted] = "DarkRed_1",
		[FileUpdateType.Created] = "DarkSeaGreen4_1",
		[FileUpdateType.Suspended] = "DeepPink4",
		[FileUpdateType.Unsuspended] = "DeepPink3"
	};

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

	private static readonly FixedWindowRateLimiter GeminiRateLimiter = new(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 1,
		Window = TimeSpan.FromSeconds(15),
		AutoReplenishment = true
	});

	private static readonly SemaphoreSlim FileProcessingSemaphore = new(10);

	private static readonly Regex WikilinkHtmlRegex = new(
		@"\[\[(?:.*[|/])?(.*?)\]\]",
		RegexOptions.Compiled);

	private async Task AnalyzeAllCategoriesAsync(IEnumerable<string> markdownFiles, ProgressTask task)
	{
		task.StartTask();

		foreach (var filePath in markdownFiles)
		{
			task.Increment(1);
			var fileContent = await File.ReadAllTextAsync(filePath);
			var parsed = TryParseYamlFrontMatter(fileContent);

			if (parsed is null) continue;

			var (frontMatter, _) = parsed.Value;
			if (frontMatter == null) continue;

			if (EvaluateShouldStudy(frontMatter.GetValueOrDefault("study")))
			{
				var categories = ExtractCategories(frontMatter);
				if (categories.Count > 0) CategoryAnalyzer.Analyze(categories);
			}
		}

		CategoryAnalyzer.FinalizeAnalysis();
		task.StopTask();
	}

	public async Task ProcessVault(string vaultPath, string? assetsPath)
	{
		AnsiConsole.MarkupLine($"Starting vault processing at: [blue]{vaultPath}[/]");
		var cachePath = Path.Combine(vaultPath, CacheFileName);

		if (File.Exists(cachePath))
		{
			AnsiConsole.MarkupLine("Loading cache...");
			var json = await File.ReadAllTextAsync(cachePath);
			Cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheEntry>>(json) ??
			        new ConcurrentDictionary<string, CacheEntry>();
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
				new SpinnerColumn()
			})
			.StartAsync(async ctx =>
			{
				var analysisTask = ctx.AddTask("[green]Analyzing categories[/]", false, markdownFiles.Count);
				await AnalyzeAllCategoriesAsync(markdownFiles, analysisTask);

				var preScanTask = ctx.AddTask("[green]Ensuring card types exist[/]", true, 1);
				await EnsureRequiredModelsExistAsync();
				preScanTask.Increment(1);
				preScanTask.StopTask();

				var processingTask = ctx.AddTask("[green]Processing files[/]",
					new ProgressTaskSettings { MaxValue = markdownFiles.Count, AutoStart = false });
				var allValidNoteIds = new ConcurrentBag<long>();
				var processingTasks = new List<Task>();

				foreach (var filePath in markdownFiles)
				{
					await FileProcessingSemaphore.WaitAsync();
					processingTasks.Add(Task.Run(async () =>
					{
						try
						{
							var (fileSummary, tree, relativePath) = await ProcessFileAsync(filePath,
								vaultPath, assetsPath, allValidNoteIds, summary);
							summary.Aggregate(fileSummary);
							if (tree != null && relativePath != null) allResults.Add((relativePath, tree));
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

		foreach (var result in allResults.OrderBy(r => r.RelativePath)) AnsiConsole.Write(result.Tree);

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
			AnsiConsole.MarkupLine(
				"[yellow][[Read-Only]][/] Would ensure 'Source' field exists on 'Basic' and 'Cloze' models.");
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
				await ankiClient.DeleteNotesAsync(orphanedIds);
			else
				AnsiConsole.MarkupLine($"[yellow][[Read-Only]][/] Would delete {orphanedIds.Count} orphaned notes.");
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
		Suspended = 4,
		Unsuspended = 5
	}

	private static string GetFileUpdateResultString(string item, FileUpdateType fileUpdateType,
		string? extraText = null)
	{
		const string formattedTemplate = "[{0}]{1}[/] {2}";

		if (!UpdateResultColors.TryGetValue(fileUpdateType, out var textColor))
			throw new ArgumentOutOfRangeException(nameof(fileUpdateType), fileUpdateType, extraText);

		var extraTextFormatted = string.Empty;
		if (extraText is not null && extraText.Length > 0)
		{
			var extraTextColor = ExtraTextColors.TryGetValue(fileUpdateType, out var color)
				? color
				: "Grey";
			extraTextFormatted = $"[{extraTextColor}]({Markup.Escape(extraText)})[/]";
		}

		return string.Format(formattedTemplate, textColor, Markup.Escape(item), extraTextFormatted).Trim();
	}

	#region Helper Methods

	/// <summary>
	/// Parses YAML front matter from markdown file content.
	/// Returns a tuple with: (frontMatter, markdownContent) or (null, null) if no front matter found.
	/// </summary>
	private static (Dictionary<object, object>? FrontMatter, string MarkdownContent)? TryParseYamlFrontMatter(
		string fileContent)
	{
		var match = RegexPatterns.YamlHeaderRegex().Match(fileContent);
		if (!match.Success) return null;

		var yamlContent = match.Groups[1].Value;
		var markdownContent = fileContent.Substring(match.Length);
		var frontMatter = YamlDeserializer.Deserialize<Dictionary<object, object>>(yamlContent);

		return (frontMatter, markdownContent);
	}

	/// <summary>
	/// Extracts categories from front matter, trying 'categories' first, then falling back to 'tags'.
	/// </summary>
	private static List<string> ExtractCategories(Dictionary<object, object> frontMatter)
	{
		if (frontMatter.TryGetValue("categories", out var cats) && cats is List<object> catList)
			return catList.Select(c => c.ToString()!).ToList();

		if (frontMatter.TryGetValue("tags", out var tags) && tags is List<object> tagList)
			return tagList.Select(t => t.ToString()!).ToList();

		return new List<string>();
	}

	/// <summary>
	/// Evaluates if the 'study' front matter value indicates the note should be studied.
	/// </summary>
	private static bool EvaluateShouldStudy(object? studyValue)
	{
		if (studyValue is null) return false;
		if (studyValue is bool b) return b;
		return studyValue.ToString()?.ToLower() == "true";
	}

	/// <summary>
	/// Adds note IDs to a concurrent bag. Helper to reduce repetition.
	/// </summary>
	private static void AddNoteIdsToBag(ConcurrentBag<long> bag, IEnumerable<long> noteIds)
	{
		foreach (var noteId in noteIds) bag.Add(noteId);
	}

	#endregion

	/// <summary>
	/// Per-file context: stable data that doesn't change across chunks.
	/// </summary>
	private record FileProcessingContext(
		string VaultName,
		string VaultPath,
		string AssetsPath,
		string RelativePath,
		string DeckName,
		IReadOnlyCollection<string> DeckTags,
		CategoryPromptConfiguration PromptConfig
	);

	/// <summary>
	/// Per-chunk context: data that varies for each content chunk within a file.
	/// </summary>
	private record ChunkProcessingContext(
		string Header,
		string Content,
		string CacheKey,
		string ContentHash
	);

	private async Task<(ProcessingSummary? summary, Tree? tree, string? relativePath)> ProcessFileAsync(string filePath,
		string vaultPath, string? assetsPath, ConcurrentBag<long> allValidNoteIds, ProcessingSummary summary)
	{
		var relativePath = Path.GetRelativePath(vaultPath, filePath);

		try
		{
			var fileContent = await File.ReadAllTextAsync(filePath);
			var parsed = TryParseYamlFrontMatter(fileContent);

			if (parsed is null) return (null, null, null);

			var (frontMatter, markdownContent) = parsed.Value;
			if (frontMatter == null) return (null, null, null);

			var (deckName, deckTags) = ResolveDeckName(filePath, frontMatter);
			if (!frontMatter.TryGetValue("study", out var studyValue)) return (null, null, null);

			var shouldStudy = EvaluateShouldStudy(studyValue);
			var noteCategories = ExtractCategories(frontMatter);

			var promptConfig = PromptRegistry.GetEffectiveConfiguration(noteCategories);
			var cardTypes = promptConfig.CardTypes.Select(x => x.ModelName);
			var tree = new Tree(
				$"[blue]{Markup.Escape(relativePath)}[/] [Grey70]({Markup.Escape(string.Join(", ", cardTypes))})[/]");

			var contentChunks = ParseAndSanitize(markdownContent);

			if (!readOnly && shouldStudy) await ankiClient.CreateDeckAsync(deckName);

			foreach (var (header, content) in contentChunks)
			{
				if (string.IsNullOrWhiteSpace(content)) continue;

				// Check if section contains skip token
				if (skipToken != null && content.Contains(skipToken))
				{
					tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Unchanged, "skipped (skip token)"));
					continue;
				}

				var cacheKey = $"{Path.GetRelativePath(vaultPath, filePath)}#{header}";
				var contentHash = CalculateHash(content);

				Cache.TryGetValue(cacheKey, out var cachedEntry);

				// Extract vault name from vault path
				var vaultName = Path.GetFileName(vaultPath);

				// Create contexts for suspend/unsuspend operations
				var fileContext = new FileProcessingContext(
					vaultName, vaultPath, assetsPath ?? string.Empty, relativePath, deckName, deckTags, promptConfig);
				var chunkContext = new ChunkProcessingContext(header, content, cacheKey, contentHash);

				// File was in Anki (cached) and now study=false -> suspend
				if (cachedEntry != null && !shouldStudy)
				{
					await HandleSuspendStateChangeAsync(cachedEntry, fileContext, chunkContext, tree, summary,
						allValidNoteIds, false);
					continue;
				}

				// File was never in Anki and study=false
				if (cachedEntry == null && !shouldStudy) continue;

				// At this point, shouldStudy = true
				// Handle suspended notes being re-activated -> unsuspend
				if (cachedEntry != null && cachedEntry.IsSuspendedState)
				{
					await HandleSuspendStateChangeAsync(cachedEntry, fileContext, chunkContext, tree, summary,
						allValidNoteIds, true);
					continue;
				}

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

					foreach (var newNoteId in cachedEntry.NoteIds) allValidNoteIds.Add(newNoteId);

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
									Cache[cacheKey] = cachedEntry with { NoteIds = validNoteIds };
								else
									Cache.TryRemove(cacheKey, out _);
							}
						}

						foreach (var noteInfo in notesInfoResult.Succeeded)
						{
							if (!readOnly) await ankiClient.MergeTagsAsync(noteInfo.NoteId, deckTags);

							allValidNoteIds.Add(noteInfo.NoteId);
						}
					}

					continue;
				}

				// New or Changed content
				var wasCached = cachedEntry != null;
				if (wasCached && !readOnly) await ankiClient.DeleteNotesAsync(cachedEntry!.NoteIds);

				IReadOnlyCollection<DynamicFlashcard> flashcards;
				if (readOnly)
					flashcards = new List<DynamicFlashcard>
						{ new("Basic", new Dictionary<string, string>()) };
				else
					flashcards =
						await GenerateFlashcardsAsync(chunkContext, fileContext);

				if (flashcards.Any())
				{
					IReadOnlyCollection<long> newNoteIds = new List<long>();
					if (!readOnly)
					{
						newNoteIds = await ankiClient.AddDynamicNotesAsync(flashcards, deckName, deckTags);
						summary.NewFlashcards += newNoteIds.Count;
						Cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName);
						foreach (var newNoteId in newNoteIds) allValidNoteIds.Add(newNoteId);
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
						if (!readOnly) Cache.TryRemove(cacheKey, out _);

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

	private async Task HandleSuspendStateChangeAsync(
		CacheEntry cachedEntry,
		FileProcessingContext fileContext,
		ChunkProcessingContext chunkContext,
		Tree tree,
		ProcessingSummary summary,
		ConcurrentBag<long> allValidNoteIds,
		bool unsuspend)
	{
		if (!cachedEntry.NoteIds.Any()) return;

		var header = chunkContext.Header;
		var cacheKey = chunkContext.CacheKey;
		var contentHash = chunkContext.ContentHash;
		var deckName = fileContext.DeckName;
		var deckTags = fileContext.DeckTags;
		var cardIds = await ankiClient.GetCardsForNotesAsync(cachedEntry.NoteIds);

		if (unsuspend)
		{
			// Handle unsuspend - either just unsuspend or delete and recreate
			if (cachedEntry.ContentHash == contentHash)
			{
				// Content unchanged - just unsuspend
				if (!readOnly && cardIds.Any())
				{
					await ankiClient.UnsuspendCardsAsync(cardIds);
					Cache[cacheKey] = cachedEntry with { IsSuspended = false };
					summary.NotesUnsuspended++;
				}

				tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Unsuspended,
					$"unsuspended {cachedEntry.NoteIds.Count} cards"));
				AddNoteIdsToBag(allValidNoteIds, cachedEntry.NoteIds);

				if (!readOnly)
					foreach (var noteId in cachedEntry.NoteIds)
						await ankiClient.MergeTagsAsync(noteId, deckTags);
			}
			else
			{
				// Content changed - delete old and create new
				if (!readOnly) await ankiClient.DeleteNotesAsync(cachedEntry.NoteIds);

				var flashcards = readOnly
					? new List<DynamicFlashcard>
						{ new("Basic", new Dictionary<string, string>()) }
					: await GenerateFlashcardsAsync(chunkContext, fileContext);

				if (flashcards.Count > 0)
				{
					if (!readOnly)
					{
						var newNoteIds = await ankiClient.AddDynamicNotesAsync(flashcards, deckName, deckTags);
						summary.NewFlashcards += newNoteIds.Count;
						Cache[cacheKey] = new CacheEntry(contentHash, newNoteIds, deckName, false);
						summary.NotesUnsuspended++;
						AddNoteIdsToBag(allValidNoteIds, newNoteIds);
					}
					else
					{
						AddNoteIdsToBag(allValidNoteIds, Array.Empty<long>());
					}

					tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Modified,
						$"content changed, {cachedEntry.NoteIds.Count} old -> {(readOnly ? "some" : flashcards.Count.ToString())} new (unsuspended)"));
				}
				else
				{
					if (!readOnly) Cache.TryRemove(cacheKey, out _);
					tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Deleted,
						"content changed, no flashcards generated"));
				}
			}
		}
		else
		{
			// Handle suspend
			if (!readOnly && cardIds.Any())
			{
				await ankiClient.SuspendCardsAsync(cardIds);
				Cache[cacheKey] = cachedEntry with { IsSuspended = true };
				summary.NotesSuspended++;
			}

			tree.AddNode(GetFileUpdateResultString(header, FileUpdateType.Suspended,
				$"suspended {cachedEntry.NoteIds.Count} cards"));
		}
	}

	private (string DeckName, IReadOnlyCollection<string> Tags) ResolveDeckName(string filePath,
		Dictionary<object, object> frontMatter)
	{
		if (!frontMatter.TryGetValue("categories", out var cats) || cats is not List<object> catList)
			return (Path.GetFileNameWithoutExtension(filePath), new List<string>());

		var categories = catList.Select(c => c.ToString()!).ToList();
		return categories.Any()
			? CategoryAnalyzer.ResolveDeckName(categories)
			: (Path.GetFileNameWithoutExtension(filePath), new List<string>());
	}

	private IReadOnlyCollection<ChatMessage> GetPromptMessages(string content, CategoryPromptConfiguration? config,
		string relativePath, string header)
	{
		var containsList = Regex.IsMatch(content, @"^\s*-\s+", RegexOptions.Multiline);

		var systemPrompt = new StringBuilder("""
		                                     You are an expert Anki Instructional Designer. Your goal is to transform provided text into high-quality, long-term memory flashcards. Important rules:
		                                     1. BREVITY: Capture only the "load-bearing" facts. Omit fluff, opinions, or introductory filler.
		                                     2. ATOMICITY: Each card must test exactly one discrete idea. If a section has multiple facts, create multiple cards.
		                                     3. NO HIDDEN CONTEXT: Use specific nouns. Never use "it," "this," or "they" unless the antecedent is inside the card.
		                                     4. FORMATTING: Format fields using HTML. Use <code> tags for technical terms/data and <anki_mathjax> for maths/formulae. Respect <code>, <em>, <strong>, <ul>, <li>, <table> tags when extracting facts.
		                                     """);
		var systemPromptTenantNumber = 4;

		var assistantPrompt = new StringBuilder();

		// Use configuration if available, otherwise use default
		var cardTypes = config?.CardTypes ?? PromptRegistry.GetDefaultConfiguration().CardTypes;

		if (cardTypes.Any(x => x.ModelName == "Cloze"))
		{
			systemPromptTenantNumber++;
			systemPrompt.AppendLine(
				$"{systemPromptTenantNumber}. CLOZES: Use {{c1::answer::hint}}. Clozes cards must have at *least* two -- never have a flashcard with a single cloze. Never cloze-delete the primary topic word, and only use hints if required for context.");
		}

		// Build examples from configured card types
		var configuredCardTypeDefs = cardTypes.Where(cardType => !string.IsNullOrEmpty(cardType.ExampleOutput));
		foreach (var cardType in configuredCardTypeDefs)
		{
			assistantPrompt.AppendLine($"- {cardType.ModelName}: [{cardType.ExampleOutput}]");
		}

		// Add category-specific addendums
		if (config != null && !string.IsNullOrEmpty(config.AssistantPromptAddendum))
		{
			if (assistantPrompt.Length > 0)
			{
				assistantPrompt.AppendLine();
			}

			assistantPrompt.AppendLine(config.AssistantPromptAddendum);
		}

		if (containsList)
		{
			systemPrompt.AppendLine(
				$"{systemPromptTenantNumber}. LIST HOOKS: If converting a list, the text outside the cloze MUST contain a unique characteristic (function/keyword) to make the card uniquely guessable.");
			assistantPrompt.AppendLine(
				"""- List: [{"text": "The three main concurrency primitives in Go are: <ul><li>{{c1::Goroutines::lightweight threads}}</li><li>{{c2::Channels::communication mechanism}}</li><li>{{c3::Select Statement::multiplexing mechanism}}</li></ul>"}]""");
		}

		// Add CARD TYPE SELECTION rule
		systemPromptTenantNumber++;
		systemPrompt.AppendLine(
			$"{systemPromptTenantNumber}. CARD TYPE SELECTION: Use whichever card type(s) best serve the content. You may create cards from only one type, multiple types, or none if the content doesn't warrant it.");

		// Add system prompt addendum from config
		if (config != null && !string.IsNullOrEmpty(config.SystemPromptAddendum))
		{
			if (systemPrompt.Length > 0)
			{
				systemPrompt.AppendLine();
			}

			systemPrompt.AppendLine(config.SystemPromptAddendum);
		}

		var messages = new List<ChatMessage>
		{
			new(ChatRole.System, systemPrompt.ToString())
		};

		if (config != null)
		{
			messages.Add(new ChatMessage(ChatRole.User,
				$"Context: This note has the following categories: '{config.Category}'."));
		}

		if (!string.Equals(Path.GetFileNameWithoutExtension(relativePath), header, StringComparison.OrdinalIgnoreCase))
		{
			messages.Add(new ChatMessage(ChatRole.User, $"Section Name: {header}"));
		}

		messages.Add(new ChatMessage(ChatRole.Assistant, assistantPrompt.ToString()));
		messages.Add(new ChatMessage(ChatRole.User,
			$"Content to convert:{Environment.NewLine}" +
			$"{content}{Environment.NewLine}{Environment.NewLine}" +
			$"Task: Create atomic Anki flashcards from this content.")
		);

		return messages;
	}

	private async Task<IReadOnlyCollection<DynamicFlashcard>> GenerateFlashcardsAsync(
		ChunkProcessingContext chunkContext,
		FileProcessingContext fileContext)
	{
		using var lease = await GeminiRateLimiter.AcquireAsync();

		var content = chunkContext.Content;
		var header = chunkContext.Header;
		var promptConfig = fileContext.PromptConfig;
		var relativePath = fileContext.RelativePath;
		var vaultName = fileContext.VaultName;

		// Extract media from content before sending to AI
		var extracted = MediaExtractor.Extract(content, fileContext.VaultPath, fileContext.AssetsPath);
		var cleanedContent = extracted.CleanedContent;
		var mediaItems = extracted.Media;

		// Identify all-media card types (these skip AI inference)
		var allCardTypes = promptConfig.CardTypes;
		var allMediaCardTypes = allCardTypes
			.Where(MediaMerger.IsAllMediaCardType)
			.ToList();

		var mixedCardTypes = allCardTypes
			.Where(ct => !MediaMerger.IsAllMediaCardType(ct))
			.ToList();

		// Handle all-media card types: create cards directly from media items
		var mediaOnlyCards = new List<DynamicFlashcard>();
		foreach (var mediaCardType in allMediaCardTypes)
		{
			var matchingMedia = mediaItems
				.Where(m => MediaMerger.GetFieldNamesForType(m.Type)
					.Any(f => mediaCardType.JsonSchemaProperties.ContainsKey(f)))
				.ToList();

			if (matchingMedia.Count == 0) continue; // Skip if no matching media

			var fields = new Dictionary<string, string>();
			foreach (var key in mediaCardType.JsonSchemaProperties.Keys)
				fields[key] = "";

			var card = new DynamicFlashcard(mediaCardType.ModelName, fields,
				CreateObsidianDeeplink(vaultName, relativePath, header));

			foreach (var media in matchingMedia)
			{
				media.Fields = new[] { MediaMerger.DetermineFieldName(media.Type, card) };
				card.Media.Add(media);
			}

			mediaOnlyCards.Add(card);
		}

		// If all card types are all-media and we have cards, return them directly
		if (mixedCardTypes.Count == 0 && mediaOnlyCards.Count > 0)
		{
			return mediaOnlyCards;
		}

		// Create filtered config with only mixed card types for prompt generation
		var mixedConfig = new CategoryPromptConfiguration
		{
			Category = promptConfig.Category,
			Priority = promptConfig.Priority,
			SystemPromptAddendum = promptConfig.SystemPromptAddendum,
			AssistantPromptAddendum = promptConfig.AssistantPromptAddendum,
			SkipBasicTypes = promptConfig.SkipBasicTypes,
			CardTypes = mixedCardTypes
		};
		List<ChatMessage> promptMessages = GetPromptMessages(cleanedContent, mixedConfig, relativePath, header).ToList();

		// Build grouped JSON schema from all configured card types
		var groupedSchema = CategoryPromptRegistry.BuildGroupedJsonSchema(mixedCardTypes);
		var schemaDescription = CategoryPromptRegistry.BuildGroupedSchemaDescription(mixedCardTypes);

		var options = new ChatOptions
		{
			ResponseFormat = ChatResponseFormat.ForJsonSchema(groupedSchema, mixedCardTypes.First().ModelName, schemaDescription),
			Temperature = 0.15f
		};

		const int MaxRetriesForInvalidCards = 3;
		var invalidCardRetryCount = 0;

		// Retry-on-failure loop: retry until at least one valid card or a schema-valid empty response
		while (true)
		{
			var response = await chatClient.GetResponseAsync(promptMessages, options);

			if (response.FinishReason != ChatFinishReason.Stop)
			{
				AnsiConsole.MarkupLine(
					$"[red]Error generating flashcards for chunk '{Markup.Escape(header)}': {response.FinishReason}[/]");
				return Array.Empty<DynamicFlashcard>();
			}

			try
			{
				using var doc = JsonDocument.Parse(response.Text);
				var root = doc.RootElement;

				if (root.ValueKind != JsonValueKind.Object)
				{
					AnsiConsole.MarkupLine(
						$"[red]Invalid response for chunk '{Markup.Escape(header)}': expected object[/]");
					return Array.Empty<DynamicFlashcard>();
				}

				var allCards = new List<DynamicFlashcard>();
				var totalInvalid = 0;

				foreach (var cardType in mixedCardTypes)
				{
					var typeName = cardType.ModelName;
					if (!root.TryGetProperty(typeName, out var cardArray) || cardArray.ValueKind != JsonValueKind.Array)
						continue;

					foreach (var card in cardArray.EnumerateArray())
					{
						var validation = CardValidation.ValidateCard(cardType, card);
						if (!validation.IsValid)
						{
							totalInvalid++;
							continue;
						}

						var fields = new Dictionary<string, string>();
						foreach (var prop in cardType.JsonSchemaProperties.Keys)
							if (card.TryGetProperty(prop, out var propValue) && propValue.ValueKind == JsonValueKind.String)
								fields[prop] = propValue.GetString() ?? "";

						allCards.Add(new DynamicFlashcard(typeName, fields,
							CreateObsidianDeeplink(vaultName, relativePath, header)));
					}
				}

				if (totalInvalid > 0)
				{
					AnsiConsole.MarkupLine(
						$"[yellow]Warning: Discarding {totalInvalid} card(s) for chunk '{Markup.Escape(header)}' — unexpected fields[/]");

					if (++invalidCardRetryCount >= MaxRetriesForInvalidCards)
					{
						AnsiConsole.MarkupLine(
							$"[red]Max retries exceeded for chunk '{Markup.Escape(header)}' — skipping. Check your prompt config and card type schemas.[/]");
						return Array.Empty<DynamicFlashcard>();
					}

					// Append retry warning to prompt messages and retry once
					promptMessages = promptMessages.Take(promptMessages.Count - 1).ToList(); // remove last user message
					promptMessages.Add(new ChatMessage(ChatRole.User,
						"Warning: previous response had invalid cards. Ensure each card only contains fields that match the type declared in the response. Discard any field not belonging to the declared card type.\n\nContent to convert:\n" + cleanedContent + "\n\nTask: Create atomic Anki flashcards from this content."));
					continue; // retry
				}

				// Empty response: accept it as valid
				if (allCards.Count == 0)
				{
					AnsiConsole.MarkupLine(
						$"[dim]No fact-worthy content in chunk '{Markup.Escape(header)}' — skipping[/]");
					return Array.Empty<DynamicFlashcard>();
				}

				if (mediaItems.Count > 0)
				{
					MediaMerger.Merge(allCards, mediaItems);
				}

				allCards.AddRange(mediaOnlyCards);

				return allCards;
			}
			catch (JsonException ex)
			{
				AnsiConsole.MarkupLine(
					$"[red]Error deserializing JSON for chunk '{Markup.Escape(header)}': {ex.Message}[/]");
				AnsiConsole.MarkupLine(
					$"[red]-- Invalid JSON --[/] {Markup.Escape(response.Text)} [red]------------------[/]");
				return new List<DynamicFlashcard>();
			}
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
			.AddRow("Notes Suspended", summary.NotesSuspended.ToString())
			.AddRow("Notes Unsuspended", summary.NotesUnsuspended.ToString())
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
		var content = new StringBuilder();

		foreach (var block in document)
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
				var html = ExtractHtml(block);
				if (string.IsNullOrEmpty(html)) continue;
				html = WikilinkHtmlRegex.Replace(html, "$1");
				content.Append(html);
			}

		if (content.Length > 0) chunks[lastHeading] = content.ToString().Trim();

		return chunks;
	}

	private string ExtractText(MarkdownObject obj)
	{
		var sb = new StringBuilder();
		ExtractTextRecursive(obj, sb);

		var content = sb.ToString();

		content = Regex.Replace(content, @"\[\[(?:.*[|/])?(.*?)\]\]", "$1");

		return content;
	}

	private string ExtractHtml(MarkdownObject obj)
	{
		try
		{
			using var writer = new StringWriter();
			var renderer = new HtmlRenderer(writer);
			renderer.Write(obj);
			return writer.ToString();
		}
		catch (Exception ex)
		{
			logger?.LogWarning(ex, "Markdig HTML conversion failed, falling back to plain text");
			return ExtractText(obj); // Don't apply regex here - ParseAndSanitize will do it
		}
	}

	private void ExtractTextRecursive(MarkdownObject? obj, StringBuilder sb)
	{
		if (obj is null) return;

		switch (obj)
		{
			case HeadingBlock heading:
				if (heading.Inline != null)
					foreach (var inline in heading.Inline)
						ExtractTextRecursive(inline, sb);

				break;
			case ParagraphBlock paragraph:
				if (paragraph.Inline != null)
					foreach (var inline in paragraph.Inline)
						ExtractTextRecursive(inline, sb);

				sb.AppendLine();
				break;
			case FencedCodeBlock fencedCodeBlock:
				sb.AppendLine("```" + (fencedCodeBlock.Info ?? string.Empty));
				if (fencedCodeBlock.Lines.Lines != null)
					foreach (var line in fencedCodeBlock.Lines.Lines)
						sb.AppendLine(line.ToString());

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
				if (sb.Length > 0 && sb[^1] != ' ') sb.AppendLine();
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
	[property: JsonPropertyName("contentHash")]
	string ContentHash,
	[property: JsonPropertyName("noteIds")]
	IReadOnlyCollection<long> NoteIds,
	[property: JsonPropertyName("deckName")]
	string DeckName,
	[property: JsonPropertyName("isSuspended")]
	bool? IsSuspended = null
)
{
	/// <summary>
	/// Returns true if this entry represents a suspended note.
	/// Null IsSuspended is treated as false for backward compatibility with older cache entries.
	/// </summary>
	public bool IsSuspendedState => IsSuspended ?? false;
}