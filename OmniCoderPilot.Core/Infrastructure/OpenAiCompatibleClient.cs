using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Universal client for ANY OpenAI-compatible API endpoint:
/// - OpenRouter
/// - DeepSeek Direct (api.deepseek.com)
/// - Groq Direct (api.groq.com)
/// - OpenAI Direct (api.openai.com)
/// - Together AI, Mistral, GitHub Models, or any custom self-hosted endpoint (vLLM, LiteLLM, Ollama OpenAI mode, LM Studio)
/// </summary>
public sealed class OpenAiCompatibleClient(
    HttpClient http,
    string providerName,
    string defaultBaseUrl,
    string initialApiKey = "") : IOllamaClient
{
    public string ProviderName { get; } = providerName;
    public string BaseUrl { get; set; } = defaultBaseUrl;
    public string ApiKey { get; set; } = initialApiKey;
    public List<string> CustomModels { get; set; } = [];

    public string GetEffectiveApiKey()
    {
        var key = ApiKey?.Trim() ?? "";
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = key[7..].Trim();
        return key;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(GetEffectiveApiKey()));

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        var models = new List<OllamaModel>();

        // Include any explicitly declared custom models
        foreach (var cm in CustomModels)
        {
            if (!string.IsNullOrWhiteSpace(cm))
                models.Add(new OllamaModel(cm.Trim(), 0, DateTime.UtcNow));
        }

        if (string.IsNullOrWhiteSpace(key)) return models;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/models");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return models;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id) && !models.Any(x => x.Name.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        models.Add(new OllamaModel(id, 0, DateTime.UtcNow));
                    }
                }
            }
        }
        catch { }

        return models;
    }

    // ── Streaming chat ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return $"⚠️ [{ProviderName}] No API Key configured. Please open '⚙ Settings' in the bottom-left sidebar, enter your {ProviderName} API Key, and click 'Save & Apply'.";
            yield break;
        }

        var body = BuildRequestBody(model, messages, stream: true);
        await foreach (var token in PostSseAsync(body, ct))
            yield return token;
    }

    // ── Tool calling with graceful fallback ──────────────────────────────────

    public async Task<OllamaToolResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken ct)
    {
        var key = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new OllamaToolResponse($"⚠️ [{ProviderName}] No API Key configured. Please open '⚙ Settings' in the bottom-left sidebar, enter your {ProviderName} API Key, and click 'Save & Apply'.", []);

        // Attempt 1: Call with native function calling
        var body = BuildRequestBody(model, messages, stream: false, tools: tools);
        using var req = CreateRequest(JsonSerializer.Serialize(body));
        using var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 401)
            {
                return new OllamaToolResponse($"⚠️ [{ProviderName} 401 Unauthorized]: Invalid or missing API Key. Please open '⚙ Settings' in the bottom-left sidebar, verify your {ProviderName} API Key, and click 'Save & Apply'.", []);
            }

            // Fallback for models without tools support (e.g. 400 Bad Request / 404 / 422)
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

            return new OllamaToolResponse($"[{ProviderName} error {(int)res.StatusCode}]: {json}", []);
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
            yield return new StreamingToolChunk($"⚠️ [{ProviderName}] No API Key configured. Please open '⚙ Settings' in the bottom-left sidebar, enter your {ProviderName} API Key, and click 'Save & Apply'.", null);
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
        var msgs = messages.Select(BuildMessage).ToList();

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
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/chat/completions");
        var key = GetEffectiveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        req.Headers.Add("X-Title", "OmniCoderPilot");
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
                yield return $"⚠️ [{ProviderName} 401 Unauthorized]: Missing or invalid API Key. Please open '⚙ Settings' in the bottom-left sidebar, enter your {ProviderName} API Key, and click 'Save & Apply'.";
                yield break;
            }
            yield return $"[{ProviderName} error {(int)res.StatusCode}]: {errBody}";
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
            catch { }
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

            // DeepSeek R1 reasoning extraction
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
