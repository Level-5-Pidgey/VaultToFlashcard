using Spectre.Console;
using Spectre.Console.Cli;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

namespace VaultToFlashcard;

public class ProcessVaultCommand : AsyncCommand<CommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        // Logic from Program.cs SetHandler will go here.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var apiKey = configuration["GeminiApiKey"];

        if (settings.AiMode == "api" && string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error: API key is not configured.[/]");
            AnsiConsole.MarkupLine("Please set the 'GeminiApiKey' in user secrets, for example:");
            AnsiConsole.MarkupLine("[yellow]dotnet user-secrets set \"GeminiApiKey\" \"<YOUR_API_KEY>\"[/]");
            return -1;
        }

        if (settings.ReadOnly)
        {
            AnsiConsole.MarkupLine("[yellow]Running in read-only mode. No changes will be made to Anki.[/]");
        }

        var ankiClient = new AnkiConnectClient();
        if (!await ankiClient.IsAvailableAsync())
        {
            AnsiConsole.MarkupLine("[red]AnkiConnect not available. Please ensure Anki is running with AnkiConnect installed and configured.[/]");
            return -1;
        }

        var processor = new VaultProcessor(ankiClient, settings.ReadOnly);
        await processor.ProcessVault(settings.VaultPath, settings.AiMode, apiKey ?? string.Empty, settings.Model);

        return 0;
    }
}

