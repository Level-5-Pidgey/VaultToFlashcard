using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace VaultToFlashcard;

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

