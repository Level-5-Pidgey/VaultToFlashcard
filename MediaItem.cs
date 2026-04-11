namespace VaultToFlashcard;

public enum MediaType
{
    Audio,
    Picture,
    Video
}

public record MediaItem(
    MediaType Type,
    string Filename,
    string? Data,
    string? Url,
    string? Path,
    string? SkipHash)
{
    public string[] Fields { get; set; } = Array.Empty<string>();
}
