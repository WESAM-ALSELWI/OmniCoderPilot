using System.Runtime.CompilerServices;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Chat2API-style IOllamaClient that drives an embedded WebView2 browser
/// to interact with web chat UIs instead of calling the official API.
///
/// Supported model names:
///   • "webchat/deepseek" → https://chat.deepseek.com
///   • "webchat/chatgpt"  → https://chatgpt.com/
/// </summary>
public sealed class WebChatClient(IWebChatService webChat) : IOllamaClient
{
    public const string ModelPrefix = "webchat/";

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<OllamaModel> models =
        [
            new OllamaModel($"{ModelPrefix}deepseek", 0, DateTime.UtcNow),
            new OllamaModel($"{ModelPrefix}chatgpt",  0, DateTime.UtcNow),
        ];
        return Task.FromResult(models);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Ensure the user is logged in for this specific site
        if (!webChat.IsLoggedInFor(model))
        {
            var loggedIn = await webChat.ShowLoginForModelAsync(model, ct);
            if (!loggedIn)
            {
                yield return $"⚠️ Not logged in to {webChat.GetDisplayNameFor(model)}. Please log in and try again.";
                yield break;
            }
        }

        var prompt = BuildPrompt(messages);
        await foreach (var token in webChat.SendMessageForModelAsync(model, prompt, ct).WithCancellation(ct))
            yield return token;
    }

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

    public async IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var token in StreamChatAsync(model, messages, ct).WithCancellation(ct))
            yield return new StreamingToolChunk(token);
    }

    private static string BuildPrompt(IReadOnlyList<OllamaChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == "user")
                return messages[i].Content;
        return messages.LastOrDefault()?.Content ?? string.Empty;
    }
}
