using Spectre.Console.Cli;

namespace VaultToFlashcard;

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		var app = new CommandApp<ProcessVaultCommand>();
		app.Configure(config =>
		{
			config.SetApplicationName("vault-to-flashcard");
			config.AddExample(new[] { "--vault", "C:\\MyVault" });
		});

		return await app.RunAsync(args);
	}
}