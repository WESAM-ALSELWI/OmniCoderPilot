using System.Text.Json.Nodes;
using OmniCoderPilot.Infrastructure;

namespace OmniCoderPilot.Application.Tools;

/// <summary>Search NuGet for packages by keyword.</summary>
public sealed class NuGetSearchTool(INuGetSearchService nuget) : IAgentTool
{
    public string Name => "NuGetSearch";
    public string Description =>
        "Search the NuGet package registry for .NET packages by keyword. " +
        "Returns package names, latest versions, download counts, and dotnet add package commands. " +
        "Use when you need a library and don't know the exact package name.";
    public JsonObject Schema => ToolJson.Schema(
        ("query", "string", "Search terms, e.g. 'json serialization', 'HTTP client', 'Entity Framework SQLite'."),
        ("limit", "number", "Number of results to return (default 8, max 20)."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var query = ToolJson.String(input, "query");
        if (string.IsNullOrWhiteSpace(query)) return new(Name, false, "query is required.");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 8), 1, 20);
        var result = await nuget.SearchAsync(query, limit, ct);
        return new(Name, true, result);
    }
}
