using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace VaultToFlashcard;

public partial class MediaExtractor
{
	// Regex for [[filename]] or [[filename|size]] wikilinks (image or audio)
	[GeneratedRegex(@"\!\[\[(.+?)(?:\|.+?)?\]\]")]
	private static partial Regex WikilinkMediaRegex();

	// Regex for ![alt](url) external images
	[GeneratedRegex(@"!\[[^\]]*\]\(([^)]+)\)")]
	private static partial Regex ExternalImageRegex();

	public record ExtractionResult(string CleanedContent, List<MediaItem> Media);

	public ExtractionResult Extract(string content, string vaultPath, string? customAssetsPath)
	{
		var mediaItems = new List<MediaItem>();

		// Extract external images first (no file lookup needed)
		foreach (Match match in ExternalImageRegex().Matches(content))
		{
			var url = match.Groups[1].Value;
			if (IsImageUrl(url))
			{
				var filename = ExtractFilenameFromUrl(url);
				mediaItems.Add(new MediaItem(
					MediaType.Picture,
					filename,
					null,
					url,
					null,
					null
				));
			}
		}

		// Remove external image syntax from content
		content = ExternalImageRegex().Replace(content, "");

		// Extract wikilinks
		foreach (Match match in WikilinkMediaRegex().Matches(content))
		{
			var raw = match.Groups[1].Value;
			var filename = raw.Trim();
			var mediaType = DetermineMediaType(filename);

			if (mediaType == null) continue;

			// Try to resolve the file path
			var (filePath, data, skipHash) = ResolveLocalFile(filename, vaultPath, customAssetsPath);

			if (filePath == null)
			{
				// File not found — skip, but still strip from content
				AnsiConsole.MarkupLine($"[yellow]Warning: Media file not found: {filename}[/]");
				continue;
			}

			mediaItems.Add(new MediaItem(
				mediaType.Value,
				filename,
				data,
				null,
				filePath,
				skipHash
			));
		}

		// Remove wikilink syntax from content (size params already stripped by regex)
		content = WikilinkMediaRegex().Replace(content, "");

		return new ExtractionResult(content, mediaItems);
	}

	internal static string ExtractFilenameFromUrl(string url)
	{
		var uri = new Uri(url);
		return Path.GetFileName(uri.LocalPath);
	}

	internal static bool IsImageUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
		var ext = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
		return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp";
	}

	internal static MediaType? DetermineMediaType(string filename)
	{
		var ext = Path.GetExtension(filename).ToLowerInvariant();
		return ext switch
		{
			".mp3" or ".ogg" or ".wav" or ".m4a" or ".flac" => MediaType.Audio,
			".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp" => MediaType.Picture,
			".mp4" or ".webm" or ".mkv" or ".avi" => MediaType.Video,
			_ => null
		};
	}

	internal static (string? filePath, string? data, string? skipHash) ResolveLocalFile(
		string filename, string vaultPath, string? customAssetsPath)
	{
		var searchPaths = new List<string>();

		if (!string.IsNullOrEmpty(customAssetsPath))
			searchPaths.Add(customAssetsPath);

		searchPaths.Add(Path.Combine(vaultPath, "assets"));
		searchPaths.Add(Path.Combine(vaultPath, "admin", "assets"));

		foreach (var basePath in searchPaths)
		{
			// Search recursively in subdirectories
			var files = Directory.GetFiles(basePath, filename, SearchOption.AllDirectories);
			if (files.Length > 0)
			{
				var filePath = files[0];
				var fileData = File.ReadAllBytes(filePath);
				var data = Convert.ToBase64String(fileData);
				var skipHash = ComputeMd5SkipHash(fileData);
				return (filePath, data, skipHash);
			}
		}

		return (null, null, null);
	}

	private static string ComputeMd5SkipHash(byte[] data)
	{
		using var md5 = MD5.Create();
		var hash = md5.ComputeHash(data);
		return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
	}
}