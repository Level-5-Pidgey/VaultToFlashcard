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

		var apiKey = configuration["GeminiApiKey"];

		if (string.IsNullOrEmpty(apiKey))
		{
			AnsiConsole.MarkupLine("[red]Error: API key is not configured.[/]");
			AnsiConsole.MarkupLine("Please set the 'GeminiApiKey' in user secrets, for example:");
			AnsiConsole.MarkupLine("[yellow]dotnet user-secrets set \"GeminiApiKey\" \"<YOUR_API_KEY>\"[/]");
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
				AnsiConsole.MarkupLine($"[red]Error: Config file not found at '{settings.ConfigPath}'[/]");
				return -1;
			}

			try
			{
				var json = await File.ReadAllTextAsync(settings.ConfigPath);
				var configs = JsonSerializer.Deserialize<List<CategoryPromptConfiguration>>(json);
				if (configs != null)
				{
					promptRegistry = new CategoryPromptRegistry(configs);
					AnsiConsole.MarkupLine(
						$"[green]Loaded {configs.Count} category prompt configurations from '{settings.ConfigPath}'[/]");
				}
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[red]Error loading config file: {ex.Message}[/]");
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

		var processor = new VaultProcessor(ankiClient, settings.ReadOnly, promptRegistry);
		await processor.ProcessVault(settings.VaultPath, apiKey ?? string.Empty, settings.Model, settings.AssetsPath);

		return 0;
	}
}