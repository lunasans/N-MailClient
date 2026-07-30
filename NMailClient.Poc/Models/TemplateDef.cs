using System.Text.Json.Serialization;

namespace NMailClient.Poc.Models;

/// <summary>Ein Textbaustein, der im Composer eingefügt werden kann.</summary>
public class TemplateDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    public override string ToString() => Name;
}
