using System.ComponentModel;
using Spectre.Console.Cli;

namespace VaultToFlashcard;

public class CommandSettings : Spectre.Console.Cli.CommandSettings
{
	[CommandOption("-v|--vault <VAULT_PATH>")]
	[Description("The path to the Obsidian vault.")]
	public string VaultPath { get; set; } = string.Empty;

	[CommandOption("-m|--model <MODEL>")]
	[Description("The Gemini model to use.")]
	[DefaultValue("gemini-3-flash-preview")]
	public string Model { get; set; } = "gemini-3-flash-preview";

	[CommandOption("--read-only")]
	[Description("Enable read-only mode, which simulates changes without making them.")]
	[DefaultValue(false)]
	public bool ReadOnly { get; set; }

	[CommandOption("-c|--config <CONFIG_PATH>")]
	[Description("Path to a JSON configuration file for category-specific prompts.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--assets <ASSETS_PATH>")]
	[Description("Custom path to the Obsidian vault's assets folder (defaults to {vault}/assets/ and {vault}/admin/assets/).")]
	public string? AssetsPath { get; set; }
}