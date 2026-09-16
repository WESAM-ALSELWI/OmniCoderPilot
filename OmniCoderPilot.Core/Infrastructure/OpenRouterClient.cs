using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// OpenRouter client — compatible with OpenAI's API format.
/// Supports streaming, native tool calling, and 200+ models.
/// Configure via appsettings.json: OpenRouter:ApiKey
/// Get a free key at https://openrouter.ai
/// </summary>
public sealed class OpenRouterClient(HttpClient http, IConfiguration config) : IOllamaClient
{
    public string ApiKey { get; set; } = config["OpenRouter:ApiKey"] ?? "";
    public string BaseUrl { get; set; } = config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
    private const string AppName = "OmniCoderPilot";

    public string GetEffectiveApiKey()
    {
        var key = ApiKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            key = config["OpenRouter:ApiKey"]?.Trim() ?? "";
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            // Check appsettings.json from disk as fallback
            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OmniCoderPilot.Core", "appsettings.json"),
                @"C:\Users\AMB\source\repos\OmniCoderPilot_v2\OmniCoderPilot.Core\appsettings.json"
            };
            foreach (var p in possiblePaths)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var json = File.ReadAllText(p);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("OpenRouter", out var orSec) &&
                            orSec.TryGetProperty("ApiKey", out var k) &&
                            k.GetString() is { Length: > 0 } fileKey)
                        {
                            key = fileKey.Trim();
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = key[7..].Trim();

        return key;
    }

    // ── Health / Model listing ───────────────────────────────────────────────

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(GetEffectiveApiKey()));

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key)) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return [];
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = new List<OllamaModel>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    // Only include popular coding-capable models to keep the list manageable
                    if (!IsCodingModel(id)) continue;
                    models.Add(new OllamaModel(id, 0, DateTime.UtcNow));
                }
            }
            return models;
        }
        catch { return []; }
    }

    private static bool IsCodingModel(string id) =>
        id.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("gpt-4", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("mistral", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("llama", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("codestral", StringComparison.OrdinalIgnoreCase);

    // ── Streaming chat (no tools) ────────────────────────────────────────────

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return "⚠️ [OpenRouter] No API Key configured. Please click '⚙ Settings' in the bottom-left sidebar, enter your OpenRouter API Key (sk-or-v1-...), and click 'Save & Apply'.";
            yield break;
        }

        var body = BuildRequestBody(model, messages, stream: true);
        await foreach (var token in PostSseAsync(body, ct))
            yield return token;
    }

    // ── Tool calling ─────────────────────────────────────────────────────────

    public async Task<OllamaToolResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new OllamaToolResponse("⚠️ [OpenRouter] No API Key configured. Please click '⚙ Settings' in the bottom-left sidebar, enter your OpenRouter API Key (sk-or-v1-...), and click 'Save & Apply'.", []);

        // Attempt 1: Call with native tools
        var body = BuildRequestBody(model, messages, stream: false, tools: tools);
        using var req = CreateRequest(JsonSerializer.Serialize(body));
        using var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 401)
            {
                return new OllamaToolResponse("⚠️ [OpenRouter 401 Unauthorized]: Invalid or missing API Key. Please open '⚙ Settings' in the bottom-left sidebar, verify your OpenRouter API Key (sk-or-v1-...), and click 'Save & Apply'.", []);
            }

            // Fallback for models (like deepseek-r1:free or free reasoning models) that don't support tools parameter
            if ((int)res.StatusCode == 400 || (int)res.StatusCode == 404 || (int)res.StatusCode == 422)
            {
                var noToolsBody = BuildRequestBody(model, messages, stream: false, tools: null);
                using var fallbackReq = CreateRequest(JsonSerializer.Serialize(noToolsBody));
                using var fallbackRes = await http.SendAsync(fallbackReq, ct);
                var fallbackJson = await fallbackRes.Content.ReadAsStringAsync(ct);
                if (fallbackRes.IsSuccessStatusCode)
                {
                    return ParseToolResponse(fallbackJson);
                }
            }

            return new OllamaToolResponse($"[OpenRouter error {(int)res.StatusCode}]: {json}", []);
        }

        return ParseToolResponse(json);
    }

    public async IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return new StreamingToolChunk("⚠️ [OpenRouter] No API Key configured. Please click '⚙ Settings' in the bottom-left sidebar, enter your OpenRouter API Key (sk-or-v1-...), and click 'Save & Apply'.", null);
            yield break;
        }

        var response = await ChatWithToolsAsync(model, messages, tools, ct);
        if (response.ToolCalls.Count > 0)
            yield return new StreamingToolChunk(response.TextContent, response.ToolCalls);
        else
            yield return new StreamingToolChunk(response.TextContent, null);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private object BuildRequestBody(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        bool stream,
        IReadOnlyList<OllamaToolDefinition>? tools = null)
    {
        var msgs = messages.Select(m => BuildMessage(m)).ToList();

        if (tools is { Count: > 0 })
        {
            return new
            {
                model,
                stream,
                max_tokens = 4096,
                messages = msgs,
                tools = tools.Select(t => new
                {
                    type = "function",
                    function = new { name = t.Name, description = t.Description, parameters = t.Parameters }
                }).ToList()
            };
        }

        return new { model, stream, max_tokens = 4096, messages = msgs };
    }

    private static object BuildMessage(OllamaChatMessage m)
    {
        if (m.Role == "tool")
        {
            return new
            {
                role = "tool",
                tool_call_id = m.ToolCallId ?? "tool",
                content = m.Content
            };
        }
        if (m.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = m.Content,
                tool_calls = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.Arguments.ToJsonString() }
                }).ToList()
            };
        }
        return new { role = m.Role, content = m.Content };
    }

    private HttpRequestMessage CreateRequest(string jsonBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
        var key = GetEffectiveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        req.Headers.Add("X-Title", AppName);
        req.Headers.Add("HTTP-Referer", "https://github.com/mycoder");
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    private async IAsyncEnumerable<string> PostSseAsync(object body, [EnumeratorCancellation] CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = CreateRequest(json);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode == 401)
            {
                yield return "⚠️ [OpenRouter 401 Unauthorized]: Missing or invalid API Key. Please open '⚙ Settings' in the bottom-left sidebar, enter your OpenRouter API Key (sk-or-v1-...), and click 'Save & Apply'.";
                yield break;
            }
            yield return $"[OpenRouter error {(int)res.StatusCode}]: {errBody}";
            yield break;
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            string? textToEmit = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    textToEmit = content.GetString();
                }
                else if (delta.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                {
                    textToEmit = reasoning.GetString();
                }
                else if (delta.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
                {
                    textToEmit = reasoningContent.GetString();
                }
            }
            catch { /* skip malformed SSE chunks */ }
            if (!string.IsNullOrEmpty(textToEmit)) yield return textToEmit;
        }
    }

    private static OllamaToolResponse ParseToolResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return new("", []);

            var msg = choices[0].GetProperty("message");
            var text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

            // Handle DeepSeek R1 reasoning properties if content is blank
            if (string.IsNullOrWhiteSpace(text))
            {
                if (msg.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                    text = r.GetString() ?? "";
                else if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    text = rc.GetString() ?? "";
            }

            var toolCalls = new List<OllamaToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var fn = tc.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var argsStr = fn.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "{}" : "{}";
                    var args = JsonNode.Parse(argsStr) as JsonObject ?? new JsonObject();
                    toolCalls.Add(new OllamaToolCall(id, name, args));
                }
            }

            return new OllamaToolResponse(text, toolCalls);
        }
        catch (Exception ex)
        {
            return new OllamaToolResponse($"[Parse error]: {ex.Message} — raw: {json[..Math.Min(200, json.Length)]}", []);
        }
    }
}
