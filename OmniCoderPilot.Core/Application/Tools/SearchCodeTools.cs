using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Infrastructure;

namespace OmniCoderPilot.Application.Tools;

// ── Code Search Tools ─────────────────────────────────────────────────────────

/// <summary>
/// Full-text semantic search over the indexed codebase.
/// Much faster than GrepText for "find all places X is used" queries.
/// </summary>
public sealed class CodeSearchTool(IRepositoryIndexer indexer) : IAgentTool
{
    public string Name => "CodeSearch";
    public string Description =>
        "Search the indexed codebase semantically — find files that contain a class, method, interface, type, or concept. " +
        "Much faster than GrepText for broad queries like 'find files related to authentication' or 'where is IUserService used'. " +
        "Returns matching file paths with a brief summary of what each file contains. " +
        "Run IndexWorkspace first if you get no results (it indexes the codebase).";
    public JsonObject Schema => ToolJson.Schema(
        ("query", "string", "What to search for: a class name, method name, concept, or phrase."),
        ("limit", "number", "Maximum results to return (default 15, max 50)."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var query = ToolJson.String(input, "query");
        if (string.IsNullOrWhiteSpace(query)) return new(Name, false, "query is required.");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 15), 1, 50);

        var results = await indexer.SearchAsync(context.WorkspaceId, query, limit, ct);
        if (results.Count == 0)
            return new(Name, true,
                $"No indexed results for '{query}'.\n" +
                "The workspace index may be empty. Run IndexWorkspace to build the index first, " +
                "then retry. Alternatively, use GrepText for real-time regex search.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} file(s) matching '{query}':\n");
        foreach (var r in results)
        {
            sb.AppendLine($"📄 {r.RelativePath}  [{r.Language}]");
            if (!string.IsNullOrWhiteSpace(r.Summary))
            {
                var preview = r.Summary.Length > 200 ? r.Summary[..200] + "…" : r.Summary;
                sb.AppendLine($"   {preview}");
            }
            sb.AppendLine();
        }
        return new(Name, true, sb.ToString());
    }
}

/// <summary>
/// Build or refresh the workspace code index.
/// </summary>
public sealed class IndexWorkspaceTool(IRepositoryIndexer indexer) : IAgentTool
{
    public string Name => "IndexWorkspace";
    public string Description =>
        "Build or refresh the code index for the current workspace. " +
        "This extracts symbols, summaries, and content from all source files to enable fast CodeSearch. " +
        "Run this once when starting on a new workspace, or after major structural changes.";
    public JsonObject Schema => ToolJson.Schema();
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        await indexer.IndexAsync(context.WorkspaceId, ct);
        return new(Name, true, "✅ Workspace index built successfully. You can now use CodeSearch to find files and symbols.");
    }
}

/// <summary>
/// Search for where a specific symbol (class/interface/method) is defined or used.
/// </summary>
public sealed class SymbolSearchTool(IRepositoryIndexer indexer) : IAgentTool
{
    public string Name => "SymbolSearch";
    public string Description =>
        "Find where a specific code symbol (class, interface, method, function, type) is defined or used across the workspace. " +
        "More precise than CodeSearch for exact symbol lookup. " +
        "Example: SymbolSearch('IAgentTool') returns all files that define or reference IAgentTool.";
    public JsonObject Schema => ToolJson.Schema(
        ("symbolName", "string", "Exact name of the symbol to search for (e.g. 'UserService', 'IRepository', 'HandleLogin')."),
        ("kind", "string", "Optional: filter by kind — 'definition' (where it's declared) or 'usage' (where it's referenced). Default: both."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var symbolName = ToolJson.String(input, "symbolName");
        if (string.IsNullOrWhiteSpace(symbolName)) return new(Name, false, "symbolName is required.");

        // Search both in RelativePath and Summary/SymbolsJson
        var results = await indexer.SearchAsync(context.WorkspaceId, symbolName, 30, ct);
        if (results.Count == 0)
            return new(Name, true,
                $"Symbol '{symbolName}' not found in index. Run IndexWorkspace first, or use GrepText for real-time search: " +
                $"GrepText with pattern '{System.Text.RegularExpressions.Regex.Escape(symbolName)}'");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Symbol '{symbolName}' found in {results.Count} file(s):\n");
        foreach (var r in results)
        {
            sb.AppendLine($"📄 {r.RelativePath}");
        }
        sb.AppendLine($"\nUse ReadFile or GrepText on these files to see exact line numbers.");
        return new(Name, true, sb.ToString());
    }
}
