using System.Runtime.CompilerServices;
using System.Text;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Chat2API-style IOllamaClient that drives an embedded WebView2 browser
/// to interact with web chat UIs (ChatGPT, DeepSeek) as the main reasoning brain of the agent.
/// Formats messages naturally to avoid robotic agentic style in web chat.
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
        if (!webChat.IsLoggedInFor(model))
        {
            var loggedIn = await webChat.ShowLoginForModelAsync(model, ct);
            if (!loggedIn)
            {
                yield return $"⚠️ Not logged in to {webChat.GetDisplayNameFor(model)}. Please log in and try again.";
                yield break;
            }
        }

        bool hasToolHistory = messages.Any(m => m.Role == "tool");
        bool isNewChat = !hasToolHistory;
        var prompt = BuildNaturalAgentPrompt(messages, isNewChat);

        await foreach (var token in webChat.SendMessageForModelAsync(model, prompt, ct, startNewChat: isNewChat).WithCancellation(ct))
            yield return token;
    }

    public async Task<OllamaToolResponse> ChatWithToolsAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
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

    private static string BuildNaturalAgentPrompt(IReadOnlyList<OllamaChatMessage> messages, bool isNewChat)
    {
        if (isNewChat)
        {
            // Extract the user's task
            string userTask = "";
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == "user")
                {
                    userTask = messages[i].Content;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(userTask))
                userTask = messages.LastOrDefault()?.Content ?? "Help with the project";

            // Extract workspace context from system message if available
            var systemMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            string workspaceInfo = "";
            var wsMatch = System.Text.RegularExpressions.Regex.Match(systemMsg, @"Workspace Root:?\s*([^\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (wsMatch.Success)
            {
                workspaceInfo = $"Workspace: {wsMatch.Groups[1].Value.Trim()}\n";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Hi! I'm working on a coding project and I need your help as the main reasoning brain.");
            if (!string.IsNullOrEmpty(workspaceInfo))
                sb.AppendLine(workspaceInfo);
            sb.AppendLine($"Task: {userTask}\n");
            sb.AppendLine("You will provide the reasoning, planning, decisions, and next actions. I will act as your execution runtime on my local machine and execute any tools you need.");
            sb.AppendLine("When you want me to perform an action, format it as a simple JSON block in your response like this:");
            sb.AppendLine("```json");
            sb.AppendLine("{\"tool\": \"ReadFile\", \"arguments\": {\"relativePath\": \"src/Program.cs\"}}");
            sb.AppendLine("```\n");
            sb.AppendLine("Available tools:");
            sb.AppendLine("- ReadFile: {\"relativePath\": \"path\"}");
            sb.AppendLine("- EditFile: {\"relativePath\": \"path\", \"targetContent\": \"exact lines to replace\", \"replacementContent\": \"new lines\"}");
            sb.AppendLine("- WriteFile: {\"relativePath\": \"path\", \"content\": \"full content\"}");
            sb.AppendLine("- ExecuteCommand: {\"command\": \"dotnet test\"}");
            sb.AppendLine("- ListDirectory: {\"relativePath\": \"src\"}");
            sb.AppendLine("- FindFiles: {\"pattern\": \"*.cs\"}");
            sb.AppendLine("- SearchFiles: {\"query\": \"SearchTerm\"}");
            sb.AppendLine("- GitStatus: {}");
            sb.AppendLine("- GitDiff: {}");
            sb.AppendLine("\nYou can explain your thoughts, and specify the action to run. I will run it and give you the output so we can proceed. If no action is needed, just answer directly.");
            sb.AppendLine("Please analyze the task, plan the steps, and tell me what action we should perform first.");
            return sb.ToString();
        }
        else
        {
            // Continuation turn: return the tool results naturally
            var sb = new StringBuilder();

            int lastAssistantIdx = -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == "assistant")
                {
                    lastAssistantIdx = i;
                    break;
                }
            }

            var recentToolMessages = messages
                .Skip(lastAssistantIdx + 1)
                .Where(m => m.Role == "tool")
                .ToList();

            if (recentToolMessages.Count > 0)
            {
                if (recentToolMessages.Count == 1)
                {
                    var tm = recentToolMessages[0];
                    var toolName = tm.ToolName ?? "action";
                    sb.AppendLine($"I executed the '{toolName}' action. Here is the output:");
                    sb.AppendLine("```");
                    sb.AppendLine(tm.Content);
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("I executed the actions you requested. Here are the outputs:\n");
                    foreach (var tm in recentToolMessages)
                    {
                        var toolName = tm.ToolName ?? "action";
                        sb.AppendLine($"--- Output of '{toolName}' ---");
                        sb.AppendLine(tm.Content);
                        sb.AppendLine("----------------------------\n");
                    }
                }
            }
            else
            {
                var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                sb.AppendLine(lastUser);
            }

            sb.AppendLine("\nWhat is our next step?");
            return sb.ToString();
        }
    }
}
