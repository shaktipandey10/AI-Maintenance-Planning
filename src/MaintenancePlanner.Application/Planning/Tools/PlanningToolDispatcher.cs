using System.Text.Json;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Routes a model's tool call to the named tool. Unknown tools, malformed arguments, tool failures and malformed tool
/// results become error results for the model instead of exceptions; only cancellation propagates.
/// </summary>
public sealed class PlanningToolDispatcher
{
    private readonly Dictionary<string, IPlanningTool> _tools;

    public PlanningToolDispatcher(IEnumerable<IPlanningTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = new Dictionary<string, IPlanningTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !_tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"Planning tool names must be nonblank and unique: '{tool.Name}'.");
        }
    }

    public IReadOnlyCollection<IPlanningTool> Tools => _tools.Values;

    /// <summary>Runs one tool call. Blank arguments are treated as an empty arguments object.</summary>
    public async Task<PlanningToolResult> ExecuteAsync(
        string? toolName,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (toolName is null || !_tools.TryGetValue(toolName, out var tool))
        {
            return PlanningToolResult.Failure(
                PlanningToolErrors.UnknownTool,
                $"Unknown tool '{toolName}'. Available tools: {string.Join(", ", _tools.Keys.Order(StringComparer.Ordinal))}.");
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, "Arguments are not valid JSON.");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, "Arguments must be a JSON object.");

        PlanningToolResult? result;
        try
        {
            result = await tool.ExecuteAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The exception text is not passed to the model: it may describe internals rather than application data
            return PlanningToolResult.Failure(PlanningToolErrors.ToolExecutionFailed, $"Tool '{tool.Name}' failed.");
        }

        return IsWellFormed(result)
            ? result!
            : PlanningToolResult.Failure(
                PlanningToolErrors.InvalidToolResult,
                $"Tool '{tool.Name}' returned a malformed result.");
    }

    private static bool IsWellFormed(PlanningToolResult? result) =>
        result is not null &&
        (result.Ok
            ? result.Data is not null && result.ErrorCode is null
            : result.Data is null && !string.IsNullOrWhiteSpace(result.ErrorCode) && !string.IsNullOrWhiteSpace(result.ErrorMessage));
}
