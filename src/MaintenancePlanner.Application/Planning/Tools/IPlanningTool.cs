using System.Text.Json;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// An application capability the planning model may call (AI03). A tool validates its own arguments, reads actual
/// application data through existing services and validates what those services return. Tools never write.
/// </summary>
public interface IPlanningTool
{
    /// <summary>The name the model uses to call the tool.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema of the tool's arguments object, as offered to the model.</summary>
    JsonElement ParametersSchema { get; }

    /// <summary>Runs the tool. <paramref name="arguments"/> is always a JSON object.</summary>
    Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>The outcome of one tool call: data on success, otherwise an error code and message for the model.</summary>
public sealed record PlanningToolResult(bool Ok, object? Data, string? ErrorCode, string? ErrorMessage)
{
    public static PlanningToolResult Success(object data) => new(true, data, null, null);

    public static PlanningToolResult Failure(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}

public static class PlanningToolErrors
{
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string InvalidArguments = "INVALID_ARGUMENTS";
    public const string InvalidToolResult = "INVALID_TOOL_RESULT";
    public const string ToolExecutionFailed = "TOOL_EXECUTION_FAILED";
    public const string UnknownAsset = "UNKNOWN_ASSET";
    public const string UnknownWindow = "UNKNOWN_WINDOW";
    public const string UnknownPart = "UNKNOWN_PART";
    public const string UnknownTechnician = "UNKNOWN_TECHNICIAN";
}
