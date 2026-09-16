using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

public sealed class OllamaClient(HttpClient http, IConfiguration config) : IOllamaClient
{
    private readonly string _model = config["Ollama:Model"] ?? "qwen3:4b";
    private readonly int _numCtx = int.TryParse(config["Ollama:NumCtx"], out var nc) ? nc : 8192;
    private readonly TimeSpan _healthCheckTimeout = TimeSpan.FromSeconds(
        int.TryParse(config["Ollama:HealthCheckTimeoutSeconds"], out var seconds)
            ? Math.Clamp(seconds, 1, 60)
            : 5);

    // ── Plain streaming chat (no tools) ──────────────────────────────────────
    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new
        {
            model = ResolveModel(model),
            stream = true,
            options = new { num_ctx = _numCtx },
            messages = messages.Select(m => new
            {
                role = m.Role == "tool" ? "user" : m.Role,
                content = m.Role == "tool" ? $"[Tool Result ({m.ToolName ?? m.ToolCallId ?? "tool"})]: {m.Content}" : m.Content
            })
        };

        await foreach (var token in PostStreamAsync("/api/chat", body, ct))
            yield return token;
    }

    // ── Native tool calling (OpenAI-compatible Ollama API) ───────────────────
    public async Task<OllamaToolResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken ct)
    {
        var body = new
        {
            model = ResolveModel(model),
            stream = false,
            options = new { num_ctx = _numCtx },
            messages = SerializeMessages(messages),
            tools = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                }
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(body)
        };
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            var errMsg = ExtractErrorMessage(errBody, res.StatusCode.ToString());
            throw new HttpRequestException($"Ollama error ({(int)res.StatusCode} {res.StatusCode}): {errMsg}");
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var textContent = "";
        var toolCalls = new List<OllamaToolCall>();

        if (root.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var contentEl))
                textContent = contentEl.GetString() ?? "";

            if (msg.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcArr.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : Guid.NewGuid().ToString();
                    if (!tc.TryGetProperty("function", out var fn)) continue;
                    var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    JsonObject args;
                    if (fn.TryGetProperty("arguments", out var argsEl))
                    {
                        if (argsEl.ValueKind == JsonValueKind.Object)
                            args = JsonNode.Parse(argsEl.GetRawText())?.AsObject() ?? new JsonObject();
                        else if (argsEl.ValueKind == JsonValueKind.String)
                        {
                            try { args = JsonNode.Parse(argsEl.GetString() ?? "{}")?.AsObject() ?? new JsonObject(); }
                            catch { args = new JsonObject(); }
                        }
                        else args = new JsonObject();
                    }
                    else args = new JsonObject();

                    if (!string.IsNullOrWhiteSpace(name))
                        toolCalls.Add(new OllamaToolCall(id, name, args));
                }
            }
        }

        return new OllamaToolResponse(textContent, toolCalls);
    }

    // ── Streaming for long-running tool-assisted turns ───────────────────────
    /// <summary>
    /// Streams both content tokens and native tool_calls from Ollama.
    /// Unlike PostStreamAsync which only yields content strings, this method
    /// extracts message.tool_calls from each streaming JSON line and yields
    /// StreamingToolChunk objects that carry both content and tool calls.
    /// </summary>
    public async IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new
        {
            model = ResolveModel(model),
            stream = true,
            options = new { num_ctx = _numCtx },
            messages = SerializeMessages(messages),
            tools = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                }
            })
        };

        var response = await http.PostAsJsonAsync("/api/chat", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            var errMsg = ExtractErrorMessage(errBody, response.StatusCode.ToString());
            throw new HttpRequestException($"Ollama error ({(int)response.StatusCode} {response.StatusCode}): {errMsg}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            StreamingToolChunk? chunk = ParseStreamingLine(line);
            if (chunk is not null)
                yield return chunk;
        }
    }

    private static StreamingToolChunk? ParseStreamingLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg))
                return null;

            var content = "";
            if (msg.TryGetProperty("content", out var contentEl))
                content = contentEl.GetString() ?? "";

            List<OllamaToolCall>? toolCalls = null;
            if (msg.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                toolCalls = new List<OllamaToolCall>();
                foreach (var tc in tcArr.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : Guid.NewGuid().ToString();
                    if (!tc.TryGetProperty("function", out var fn)) continue;
                    var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    JsonObject args;
                    if (fn.TryGetProperty("arguments", out var argsEl))
                    {
                        if (argsEl.ValueKind == JsonValueKind.Object)
                            args = JsonNode.Parse(argsEl.GetRawText())?.AsObject() ?? new JsonObject();
                        else if (argsEl.ValueKind == JsonValueKind.String)
                        {
                            try { args = JsonNode.Parse(argsEl.GetString() ?? "{}")?.AsObject() ?? new JsonObject(); }
                            catch { args = new JsonObject(); }
                        }
                        else args = new JsonObject();
                    }
                    else args = new JsonObject();

                    if (!string.IsNullOrWhiteSpace(name))
                        toolCalls.Add(new OllamaToolCall(id, name, args));
                }
            }

            return new StreamingToolChunk(content, toolCalls);
        }
        catch { return null; }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_healthCheckTimeout);
            using var res = await http.GetAsync("/api/tags", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_healthCheckTimeout);
            using var res = await http.GetAsync("/api/tags", cts.Token);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var modelsEl))
                return [];

            var list = new List<OllamaModel>();
            foreach (var m in modelsEl.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var size = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var modified = m.TryGetProperty("modified_at", out var mt)
                    ? DateTime.Parse(mt.GetString() ?? DateTime.UtcNow.ToString("o"))
                    : DateTime.UtcNow;
                list.Add(new OllamaModel(name, size, modified));
            }
            return list;
        }
        catch { return []; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private string ResolveModel(string model) =>
        string.IsNullOrWhiteSpace(model) ? _model : model;

    private static IEnumerable<object> SerializeMessages(IReadOnlyList<OllamaChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (m.ToolCallId is not null)
            {
                // Tool result message
                yield return new
                {
                    role = "tool",
                    content = m.Content,
                    tool_call_id = m.ToolCallId
                };
            }
            else if (m.ToolCalls is not null && m.ToolCalls.Count > 0)
            {
                // Assistant message declaring the tool calls
                yield return new
                {
                    role = "assistant",
                    content = m.Content ?? "",
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = tc.Arguments
                        }
                    })
                };
            }
            else
            {
                yield return new { role = m.Role, content = m.Content };
            }
        }
    }

    private static string ExtractErrorMessage(string json, string defaultMsg)
    {
        if (string.IsNullOrWhiteSpace(json)) return defaultMsg;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errEl))
                return errEl.GetString() ?? json;
        }
        catch { }
        return json;
    }

    private async IAsyncEnumerable<string> PostStreamAsync(
        string endpoint, object body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            var errMsg = ExtractErrorMessage(errBody, res.StatusCode.ToString());
            throw new HttpRequestException($"Ollama error ({(int)res.StatusCode} {res.StatusCode}): {errMsg}");
        }
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
                yield return content.GetString() ?? "";
        }
    }
}
