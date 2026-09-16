using System.Text.Json.Nodes;

namespace OmniCoderPilot.Application.Tools;

public sealed class WebFetchTool(IWebFetchService web) : IAgentTool
{
    public string Name => "WebFetch";
    public string Description => "Fetch a URL and return its text content (HTML stripped to readable text, max 20 000 chars). Use for reading documentation, NuGet package pages, GitHub READMEs, API docs, StackOverflow answers, etc.";
    public JsonObject Schema => ToolJson.Schema(
        ("url", "string", "Full URL to fetch (must start with http:// or https://)."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var url = ToolJson.String(input, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out _) || (!url.StartsWith("http://") && !url.StartsWith("https://")))
            return new(Name, false, "Invalid URL — must be an absolute http:// or https:// address.");

        var content = await web.FetchAsync(url, ct);
        return new(Name, true, $"Content from {url}:\n\n{content}");
    }
}

public sealed class WebSearchTool(IWebSearchService search) : IAgentTool
{
    public string Name => "WebSearch";
    public string Description => "Search the web using DuckDuckGo and return the top results with snippets and URLs. Use for finding documentation, error solutions, packages, APIs, or any information you don't know. Follow up with WebFetch on relevant URLs.";
    public JsonObject Schema => ToolJson.Schema(
        ("query", "string", "Search query. Be specific — include technology name, error message, or exact topic."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var query = ToolJson.String(input, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new(Name, false, "Query cannot be empty.");

        var results = await search.SearchAsync(query, ct);
        return new(Name, true, $"Search results for '{query}':\n\n{results}");
    }
}
