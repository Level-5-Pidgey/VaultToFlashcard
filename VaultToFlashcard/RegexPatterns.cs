using System.Text.RegularExpressions;

namespace VaultToFlashcard;

public static partial class RegexPatterns
{
	[GeneratedRegex(@"\[\[(?:.*[|/])?(.*?)\]\]")]
	public static partial Regex WikiLinkRegex();

	[GeneratedRegex(@"^---\s*(.*?)---\s*", RegexOptions.Singleline)]
	public static partial Regex YamlHeaderRegex();
}