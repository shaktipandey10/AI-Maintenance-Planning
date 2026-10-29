namespace MaintenancePlanner.Infrastructure.Planning;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Lets the model call the planning tools before its final interpretation (AI03). Off by default: the interpreter
    /// then sends its single JSON-mode request exactly as before.
    /// </summary>
    public bool EnableTools { get; set; }

    /// <summary>Most tool calls one interpretation may make; a model asking for more fails the interpretation.</summary>
    public int MaxToolCalls { get; set; } = 6;
}
