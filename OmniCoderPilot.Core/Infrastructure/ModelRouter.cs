using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Routes LLM requests to either Local Ollama, OpenRouter, Groq, DeepSeek Direct, OpenAI Direct,
/// or a Custom User Endpoint depending on the model name and active provider settings.
/// </summary>
public sealed class ModelRouter(
    OllamaClient local,
    OpenRouterClient openRouter,
    OpenAiCompatibleClient groq,
    OpenAiCompatibleClient deepseekDirect,
    OpenAiCompatibleClient openAiDirect,
    OpenAiCompatibleClient customEndpoint) : IOllamaClient
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => local.IsAvailableAsync(ct);

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        var localModels = await local.ListModelsAsync(ct);
        var openRouterModels = await openRouter.ListModelsAsync(ct);
        var groqModels = await groq.ListModelsAsync(ct);
        var deepseekModels = await deepseekDirect.ListModelsAsync(ct);
        var openAiModels = await openAiDirect.ListModelsAsync(ct);
        var customModels = await customEndpoint.ListModelsAsync(ct);

        var list = new List<OllamaModel>();
        list.AddRange(localModels);
        list.AddRange(openRouterModels);
        list.AddRange(groqModels);
        list.AddRange(deepseekModels);
        list.AddRange(openAiModels);
        list.AddRange(customModels);

        return list;
    }

    public IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, CancellationToken ct)
    {
        var client = ResolveClient(model);
        return client.StreamChatAsync(model, messages, ct);
    }

    public Task<OllamaToolResponse> ChatWithToolsAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct)
    {
        var client = ResolveClient(model);
        return client.ChatWithToolsAsync(model, messages, tools, ct);
    }

    public IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct)
    {
        var client = ResolveClient(model);
        return client.StreamChatWithToolsAsync(model, messages, tools, ct);
    }

    private IOllamaClient ResolveClient(string model)
    {
        // 1. Custom endpoint explicit models
        if (customEndpoint.CustomModels.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)) ||
            model.StartsWith("custom/", StringComparison.OrdinalIgnoreCase))
        {
            return customEndpoint;
        }

        // 2. Groq Direct models
        if (model.StartsWith("groq/", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("deepseek-r1-distill-llama-70b", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(groq.GetEffectiveApiKey()))
                return groq;
        }

        // 3. DeepSeek Direct models
        if (model.StartsWith("deepseek-direct/", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(deepseekDirect.GetEffectiveApiKey()))
                return deepseekDirect;
        }

        // 4. OpenAI Direct models
        if (model.StartsWith("openai-direct/", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(openAiDirect.GetEffectiveApiKey()))
                return openAiDirect;
        }

        // 5. OpenRouter (all other cloud prefixes)
        if (IsRemoteCloudModel(model))
        {
            return openRouter;
        }

        // 6. Default to local Ollama
        return local;
    }

    private static bool IsRemoteCloudModel(string model) =>
        model.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("meta-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("mistral", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("google/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("groq/", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("custom/", StringComparison.OrdinalIgnoreCase);
}
