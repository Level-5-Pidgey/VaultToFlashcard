using Spectre.Console;
using Spectre.Console.Cli;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Threading.Tasks;

namespace VaultToFlashcard;

public class ProcessVaultCommand : AsyncCommand<CommandSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings,
		CancellationToken cancellationToken)
	{
		var configuration = new ConfigurationBuilder()
			.AddUserSecrets<Program>()
			.Build();

		var apiKey = configuration["ApiKey"];

		if (string.IsNullOrEmpty(apiKey))
		{
			AnsiConsole.MarkupLine("[red]Error: API key is not configured.[/]");
			AnsiConsole.MarkupLine("Please set the 'ApiKey' in user secrets, for example:");
			AnsiConsole.MarkupLine("[yellow]dotnet user-secrets set \"ApiKey\" \"<YOUR_API_KEY>\"[/]");
			return -1;
		}

		if (settings.ReadOnly)
			AnsiConsole.MarkupLine("[yellow]Running in read-only mode. No changes will be made to Anki.[/]");

		// Load prompt registry from config file if provided
		CategoryPromptRegistry? promptRegistry = null;
		if (!string.IsNullOrEmpty(settings.ConfigPath))
		{
			if (!File.Exists(settings.ConfigPath))
			{
				AnsiConsole.MarkupLine($"[red]Error: Config file not found at '{Markup.Escape(settings.ConfigPath)}'[/]");
				return -1;
			}

			try
			{
				var json = await File.ReadAllTextAsync(settings.ConfigPath);
				var vaultConfig = JsonSerializer.Deserialize<VaultPromptConfiguration>(json);
				if (vaultConfig != null)
				{
					promptRegistry = new CategoryPromptRegistry(vaultConfig);
					AnsiConsole.MarkupLine(
						$"[green]Loaded {vaultConfig.Categories.Count} categories and {vaultConfig.CardTypes.Count} card types from '{Markup.Escape(settings.ConfigPath)}'[/]");
				}
				else
				{
					AnsiConsole.MarkupLine($"[red]Error: Invalid config format[/]");
					return -1;
				}
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[red]Error loading config file: {Markup.Escape(ex.Message)}[/]");
				return -1;
			}
		}

		var ankiClient = new AnkiConnectClient();
		if (!await ankiClient.IsAvailableAsync())
		{
			AnsiConsole.MarkupLine(
				"[red]AnkiConnect not available. Please ensure Anki is running with AnkiConnect installed and configured.[/]");
			return -1;
		}

		// Create the AI client based on provider
		var aiClient = AiChatProviderFactory.CreateChatClient(
			settings.Provider,
			apiKey ?? string.Empty,
			settings.Model);

		var processor = new VaultProcessor(ankiClient, aiClient, settings.ReadOnly, settings.SkipToken, promptRegistry);
		await processor.ProcessVault(settings.VaultPath, settings.AssetsPath);

		return 0;
	}
}