using System.ComponentModel;
using Spectre.Console.Cli;

namespace VaultToFlashcard;

public class CommandSettings : Spectre.Console.Cli.CommandSettings
{
    [CommandOption("-v|--vault <VAULT_PATH>")]
    [Description("The path to the Obsidian vault.")]
    public string VaultPath { get; set; } = string.Empty;

    [CommandOption("--ai-mode <MODE>")]
    [Description("The AI mode to use. (api, cli)")]
    [DefaultValue("api")]
    public string AiMode { get; set; } = "api";

    [CommandOption("-m|--model <MODEL>")]
    [Description("The Gemini model to use.")]
    [DefaultValue("gemini-3-flash-preview")]
    public string Model { get; set; } = "gemini-3-flash-preview";
    
    [CommandOption("--read-only")]
    [Description("Enable read-only mode, which simulates changes without making them.")]
    [DefaultValue(false)]
    public bool ReadOnly { get; set; }
}
