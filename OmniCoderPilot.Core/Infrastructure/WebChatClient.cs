using System.Runtime.CompilerServices;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Chat2API-style IOllamaClient that drives an embedded browser (WebView2)
/// to interact with a web chat UI instead of calling the official API.
///
/// Model name convention: "webchat/deepseek", "webchat/chatgpt", etc.
/// </summary>
public sealed class WebChatClient(IWebChatService webChat) : IOllamaClient
{
    public const string ModelPrefix = "webchat/";

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(true); // always available — no server needed

    public Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<OllamaModel> models =
        [
            new OllamaModel($"{ModelPrefix}deepseek", 0, DateTime.UtcNow),
        ];
        return Task.FromResult(models);
    }

    /// <summary>
    /// Stream the last user message to the web chat UI and yield response tokens.
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Ensure the user is logged in (shows login window if not)
        if (!webChat.IsLoggedIn)
        {
            var loggedIn = await webChat.ShowLoginAsync(ct);
            if (!loggedIn)
            {
                yield return "⚠️ Not logged in to " + webChat.ProviderName + ". Please log in and try again.";
                yield break;
            }
        }

        // Compile conversation into a single prompt (last user message + brief context)
        var prompt = BuildPrompt(messages);

        await foreach (var token in webChat.SendMessageAsync(prompt, ct).WithCancellation(ct))
        {
            yield return token;
        }
    }

    /// <summary>
    /// Non-streaming call — collect all tokens from the streaming implementation.
    /// </summary>
    public async Task<OllamaToolResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var token in StreamChatAsync(model, messages, ct))
            sb.Append(token);

        return new OllamaToolResponse(sb.ToString(), []);
    }

    /// <summary>
    /// Streaming with tools — web chat doesn't support tool calls natively,
    /// so we stream the text and return no tool calls.
    /// </summary>
    public async IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var token in StreamChatAsync(model, messages, ct).WithCancellation(ct))
        {
            yield return new StreamingToolChunk(token);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string BuildPrompt(IReadOnlyList<OllamaChatMessage> messages)
    {
        // Send only the last user message to the web chat (it already has conversation context)
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user")
                return messages[i].Content;
        }
        return messages.LastOrDefault()?.Content ?? string.Empty;
    }
}
