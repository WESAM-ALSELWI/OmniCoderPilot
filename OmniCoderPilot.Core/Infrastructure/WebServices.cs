using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

// ── Web Fetch ─────────────────────────────────────────────────────────────────

public sealed class WebFetchService(HttpClient http) : IWebFetchService
{
    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; OmniCoderPilot/1.0)");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                raw = StripHtml(raw);

            const int maxChars = 25_000;
            if (raw.Length > maxChars)
                raw = raw[..maxChars] + $"\n\n[...truncated — total {raw.Length:N0} chars]";

            return raw;
        }
        catch (Exception ex)
        {
            return $"Error fetching {url}: {ex.Message}";
        }
    }

    private static string StripHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode
            .SelectNodes("//script|//style|//nav|//header|//footer|//aside|//noscript") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var sb = new System.Text.StringBuilder();
        foreach (var node in doc.DocumentNode.DescendantsAndSelf())
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
        }
        return sb.ToString();
    }
}

// ── Web Search (Brave primary, DuckDuckGo fallback, HTML scrape fallback) ─────

public sealed class WebSearchService(HttpClient http, IConfiguration config) : IWebSearchService
{
    private readonly string _braveApiKey = config["BraveSearch:ApiKey"] ?? "";

    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        // 1. Try Brave Search API (best results, requires free API key)
        if (!string.IsNullOrWhiteSpace(_braveApiKey))
        {
            var braveResult = await TryBraveSearchAsync(query, ct);
            if (braveResult is not null) return braveResult;
        }

        // 2. DuckDuckGo Instant Answer API (limited but no key needed)
        var ddgResult = await TryDuckDuckGoAsync(query, ct);
        if (ddgResult is not null) return ddgResult;

        // 3. HTML scrape fallback
        return await TryHtmlSearchAsync(query, ct);
    }

    private async Task<string?> TryBraveSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.search.brave.com/res/v1/web/search?q={encoded}&count=8&result_filter=web");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("X-Subscription-Token", _braveApiKey);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var lines = new List<string>();

            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("results", out var results))
            {
                foreach (var r in results.EnumerateArray().Take(6))
                {
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() : "";
                    var url = r.TryGetProperty("url", out var u) ? u.GetString() : "";
                    var desc = r.TryGetProperty("description", out var d) ? d.GetString() : "";
                    lines.Add($"**{title}**\n{url}\n{desc}\n");
                }
            }

            return lines.Count > 0
                ? $"Brave Search results for '{query}':\n\n" + string.Join("\n", lines)
                : null;
        }
        catch { return null; }
    }

    private async Task<string?> TryDuckDuckGoAsync(string query, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1";
            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var lines = new List<string>();

            if (root.TryGetProperty("AbstractText", out var abs) &&
                abs.GetString() is { Length: > 0 } abstractText)
                lines.Add($"Summary: {abstractText}");

            if (root.TryGetProperty("AbstractURL", out var absUrl) &&
                absUrl.GetString() is { Length: > 0 } abstractUrl)
                lines.Add($"Source: {abstractUrl}");

            if (root.TryGetProperty("RelatedTopics", out var topics))
            {
                var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 5) break;
                    if (topic.TryGetProperty("Text", out var txt) &&
                        topic.TryGetProperty("FirstURL", out var furl))
                    {
                        lines.Add($"- {txt.GetString()} ({furl.GetString()})");
                        count++;
                    }
                }
            }

            return lines.Count > 0
                ? "DuckDuckGo results for '" + query + "':\n\n" + string.Join("\n", lines)
                : null;
        }
        catch { return null; }
    }

    private async Task<string?> TryHtmlSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            // Use Google's "I'm Feeling Lucky" equivalent via Bing
            var encoded = Uri.EscapeDataString(query);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.bing.com/search?q={encoded}&format=rss");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; OmniCoderPilot/1.0)");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync(ct);

            // Extract result titles and snippets from Bing HTML
            var matches = Regex.Matches(html, @"<a[^>]+href=""(https?://[^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var lines = matches
                .Where(m => !m.Groups[1].Value.Contains("bing.com"))
                .Take(5)
                .Select(m => $"- {Regex.Replace(m.Groups[2].Value, "<.*?>", "").Trim()}\n  {m.Groups[1].Value}")
                .ToList();

            return lines.Count > 0
                ? $"Web search results for '{query}':\n\n" + string.Join("\n", lines) +
                  $"\n\nUse WebFetch to read any of these URLs for full content."
                : $"No results found for '{query}'. Try using WebFetch with a direct URL.";
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}. Try using WebFetch with a direct URL like 'https://learn.microsoft.com' or 'https://stackoverflow.com/search?q={Uri.EscapeDataString(query)}'";
        }
    }
}

// ── NuGet Search ──────────────────────────────────────────────────────────────

public interface INuGetSearchService
{
    Task<string> SearchAsync(string query, int take, CancellationToken ct);
}

public sealed class NuGetSearchService(HttpClient http) : INuGetSearchService
{
    private const string NuGetSearchUrl = "https://azuresearch-usnc.nuget.org/query";

    public async Task<string> SearchAsync(string query, int take, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"{NuGetSearchUrl}?q={encoded}&take={take}&prerelease=false&semVerLevel=2.0.0";
            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"NuGet packages matching '{query}':\n");

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var pkg in data.EnumerateArray().Take(take))
                {
                    var id = pkg.TryGetProperty("id", out var i) ? i.GetString() : "";
                    var version = pkg.TryGetProperty("version", out var v) ? v.GetString() : "";
                    var desc = pkg.TryGetProperty("description", out var d) ? d.GetString() : "";
                    var downloads = pkg.TryGetProperty("totalDownloads", out var dl) ? dl.GetInt64() : 0;
                    var desc2 = desc?.Length > 150 ? desc[..150] + "…" : desc;
                    sb.AppendLine($"📦 **{id}** v{version} ({downloads:N0} downloads)");
                    sb.AppendLine($"   {desc2}");
                    sb.AppendLine($"   Install: dotnet add package {id}");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"NuGet search failed: {ex.Message}";
        }
    }
}
