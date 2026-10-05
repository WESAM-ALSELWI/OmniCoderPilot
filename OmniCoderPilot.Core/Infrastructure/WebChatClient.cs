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

        var convId = webChat.ActiveConversationId;
        bool hasKnownThread = convId.HasValue && !string.IsNullOrWhiteSpace(webChat.GetThreadUrlForConversation(convId.Value));

        // Determine if this is Turn 1 of Prompt 1 (start of a new conversation thread)
        int userCount = messages.Count(m => m.Role == "user");
        bool hasRecentTools = messages.LastOrDefault()?.Role == "tool";
        bool isFirstPromptFirstTurn;

        if (convId.HasValue)
        {
            // Direct 1-to-1 conversation mapping:
            // If the thread URL already exists for this conversation, this is NEVER the first turn!
            // If it does not exist yet, and no tools have run, it IS the first turn.
            isFirstPromptFirstTurn = !hasKnownThread && !hasRecentTools && !messages.Any(m => m.Role == "tool");
        }
        else
        {
            isFirstPromptFirstTurn = userCount <= 1 && !hasRecentTools && !messages.Any(m => m.Role == "tool");
        }

        bool startNewChat = isFirstPromptFirstTurn;
        var prompt = BuildNaturalAgentPrompt(messages, isFirstPromptFirstTurn);

        await foreach (var token in webChat.SendMessageForModelAsync(model, prompt, ct, conversationId: convId, startNewChat: startNewChat).WithCancellation(ct))
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

    private static string BuildNaturalAgentPrompt(IReadOnlyList<OllamaChatMessage> messages, bool isFirstPromptFirstTurn)
    {
        // ── Case 1: First turn of Prompt 1 (New Conversation) ──
        if (isFirstPromptFirstTurn)
        {
            string userTask = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "Help with the project";

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
            sb.AppendLine("You will provide the reasoning, planning, and decisions. My software is your local execution runtime and will execute tools automatically.");
            sb.AppendLine("CRITICAL INSTRUCTION: When you need to inspect the workspace, read files, edit code, or run commands, you MUST output a simple JSON block in your response:");
            sb.AppendLine("```json");
            sb.AppendLine("{\"tool\": \"ListDirectory\", \"arguments\": {\"relativePath\": \"\"}}");
            sb.AppendLine("```\n");
            sb.AppendLine("Available tools:");
            sb.AppendLine("- ListDirectory: {\"relativePath\": \"\"} (list files in workspace or folder)");
            sb.AppendLine("- ReadFile: {\"relativePath\": \"path\"} (read a file)");
            sb.AppendLine("- EditFile: {\"relativePath\": \"path\", \"targetContent\": \"exact lines to replace\", \"replacementContent\": \"new lines\"}");
            sb.AppendLine("- WriteFile: {\"relativePath\": \"path\", \"content\": \"full content\"}");
            sb.AppendLine("- ExecuteCommand: {\"command\": \"dotnet build\"}");
            sb.AppendLine("- FindFiles: {\"pattern\": \"*.cs\"}");
            sb.AppendLine("- SearchFiles: {\"query\": \"SearchTerm\"}");
            sb.AppendLine("- GitStatus: {}");
            sb.AppendLine("- GitDiff: {}");
            sb.AppendLine("\nRule: Explain your thinking briefly, then immediately provide the tool call JSON block. Do NOT ask me to run commands manually in my terminal — output the JSON block so my software can run it automatically. If the task is purely conversational or already completed, provide your final answer directly without any JSON.");
            sb.AppendLine("Please analyze the task, plan the steps, and output the first tool JSON block to run.");
            return sb.ToString();
        }

        // ── Case 2: Tool execution results turn ──
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
            var sb = new StringBuilder();
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
            sb.AppendLine("\nWhat is our next step? (If another action is needed, output the tool JSON block. If finished, provide the final answer.)");
            return sb.ToString();
        }

        // ── Case 3: Endless subsequent user prompts (Prompt 2, Prompt 3, Prompt 4...) ──
        var lastUserMsg = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // Check if this is an internal orchestrator redirect
        if (lastUserMsg.Contains("You have not called any tools") || lastUserMsg.Contains("CRITICAL:") || lastUserMsg.Contains("TodoWrite"))
        {
            var sb = new StringBuilder();
            sb.AppendLine("Please specify the tool to execute using a JSON block so I can run it for you locally on the workspace. For example:");
            sb.AppendLine("```json");
            sb.AppendLine("{\"tool\": \"ListDirectory\", \"arguments\": {\"relativePath\": \"\"}}");
            sb.AppendLine("```");
            sb.AppendLine("What tool should I execute first?");
            return sb.ToString();
        }

        // The user's new prompt in the ongoing conversation!
        var userSb = new StringBuilder();
        userSb.AppendLine(lastUserMsg);
        userSb.AppendLine("\n(If you need to perform an action, output the tool JSON block so my software can run it for you. If no action is needed, answer directly.)");
        return userSb.ToString();
    }
}
