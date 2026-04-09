namespace VaultToFlashcard;

public class DynamicFlashcard
{
    public string ModelName { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
    public string Source { get; set; } = "";

    public DynamicFlashcard() { }

    public DynamicFlashcard(string modelName, Dictionary<string, string> fields, string source = "")
    {
        ModelName = modelName;
        Fields = fields;
        Source = source;
    }
}