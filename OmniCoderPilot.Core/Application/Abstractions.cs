using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Application;

public sealed record ChatRequest(Guid WorkspaceId, Guid ConversationId, string Prompt, string ConnectionId, CancellationToken CancellationToken, string? Model = null);
public sealed record OllamaChatMessage(string Role, string Content, string? ToolCallId = null, string? ToolName = null, IReadOnlyList<OllamaToolCall>? ToolCalls = null);
public sealed record ToolResult(string ToolName, bool Success, string Content, JsonObject? Data = null);
public sealed record FileNodeDto(string Name, string Path, bool IsDirectory, IReadOnlyList<FileNodeDto> Children);
public sealed record ConversationContext(string ProjectSummary, string MemorySummary, string CompressedHistory, string ProjectInstructions);
public sealed record OllamaModel(string Name, long Size, DateTime ModifiedAt);

// ── Ollama native tool calling types ─────────────────────────────────────────
public sealed record OllamaToolDefinition(string Name, string Description, JsonObject Parameters);
public sealed record OllamaToolCall(string Id, string Name, JsonObject Arguments);
public sealed record OllamaToolResponse(string TextContent, IReadOnlyList<OllamaToolCall> ToolCalls);

/// <summary>
/// A chunk from a streaming response that may contain both content tokens and tool calls.
/// Used by StreamChatWithToolsAsync to properly extract native tool_calls from streaming JSON.
/// </summary>
public sealed record StreamingToolChunk(
    string Content,
    IReadOnlyList<OllamaToolCall>? ToolCalls = null);

// ── Permission types ──────────────────────────────────────────────────────────
public sealed record PermissionRequest(string RequestId, string ToolName, string Description, string Arguments);

// ── Permission mode ───────────────────────────────────────────────────────────
public enum ToolPermissionMode
{
    /// <summary>All workspace reads and writes allowed without prompting.</summary>
    WorkspaceWrite,
    /// <summary>Only reads allowed; writes require explicit approval.</summary>
    ReadOnly,
    /// <summary>Ask user before every write/execute operation.</summary>
    AskEverything
}

// ── Plan mode state ───────────────────────────────────────────────────────────

public sealed class PlanModeState
{
    private readonly Dictionary<Guid, bool> _modes = new();

    public bool IsActive(Guid conversationId)
    {
        lock (_modes) return _modes.TryGetValue(conversationId, out var v) && v;
    }

    public void SetActive(Guid conversationId, bool active)
    {
        lock (_modes) _modes[conversationId] = active;
    }
}

// ── Interfaces ────────────────────────────────────────────────────────────────
public interface IOllamaClient
{
    IAsyncEnumerable<string> StreamChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, CancellationToken ct);
    Task<OllamaToolResponse> ChatWithToolsAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct);
    IAsyncEnumerable<StreamingToolChunk> StreamChatWithToolsAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct);
    Task<bool> IsAvailableAsync(CancellationToken ct);
    Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct);
}

public interface IAgentOrchestrator
{
    Task RunTurnAsync(ChatRequest request);
}

public interface IAgentEventSink
{
    /// <summary>Stream a final answer token to the user.</summary>
    Task TokenAsync(string connectionId, Guid conversationId, string token);

    /// <summary>Show what the agent is currently doing (think/plan note).</summary>
    Task AgentActivityAsync(string connectionId, Guid conversationId, string activity, string activityType = "thinking");

    /// <summary>Legacy alias kept for compatibility.</summary>
    Task ThinkingAsync(string connectionId, Guid conversationId, string text);

    /// <summary>Send a thinking/reasoning step to render inline in the chat bubble (collapsible).</summary>
    Task ThinkingStepAsync(string connectionId, Guid conversationId, string thinkingText, int elapsedMs, int turnNumber, AgentLoopPhase phase);

    /// <summary>Send an individual tool step line to render inline in the chat bubble.</summary>
    Task ToolStepAsync(string connectionId, Guid conversationId, string toolName, string filePath, string operation, int addedLines, int removedLines, int elapsedMs, bool isExpandable);

    /// <summary>Send a batch of grouped tool steps (read tools → "Explored N files" group, write tools → standalone lines).</summary>
    Task ToolStepsBatchAsync(string connectionId, Guid conversationId, IReadOnlyList<ToolStepGroup> groups);

    /// <summary>Update the overall task status bar.</summary>
    Task StatusAsync(string connectionId, Guid taskId, string status, int progress);

    /// <summary>Report a tool execution event (running / completed / failed).</summary>
    Task ToolAsync(string connectionId, string toolName, string status, string content, string? icon = null);

    /// <summary>Emit a diff event after a file was changed.</summary>
    Task DiffAsync(string connectionId, Guid changeSetId);

    /// <summary>Emit an error message.</summary>
    Task ErrorAsync(string connectionId, string message);

    /// <summary>Update the todo list in the UI.</summary>
    Task TodoAsync(string connectionId, Guid conversationId, IReadOnlyList<TodoItem> todos);

    /// <summary>Ask the user to approve or deny a dangerous tool action.</summary>
    Task<bool> RequestPermissionAsync(string connectionId, PermissionRequest request);
}

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    JsonObject Schema { get; }
    bool IsReadOnly { get; }
    /// <summary>True = this tool makes destructive workspace changes and needs user approval.</summary>
    bool IsDangerous => false;
    Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct);
}

public sealed record ToolExecutionContext(
    Guid WorkspaceId,
    Guid ConversationId,
    string WorkspaceRoot,
    ToolPermissionMode PermissionMode,
    bool IsPlanMode = false);

public interface IWorkspaceFileService
{
    Task<FileNodeDto> GetTreeAsync(Guid workspaceId, CancellationToken ct);
    Task<string> ReadFileAsync(Guid workspaceId, string relativePath, CancellationToken ct);
    Task WriteFileAsync(Guid workspaceId, string relativePath, string content, CancellationToken ct);
    Task DeleteFileAsync(Guid workspaceId, string relativePath, CancellationToken ct);
    string ResolveInsideWorkspace(string root, string relativePath);
}

public interface IRepositoryIndexer
{
    Task IndexAsync(Guid workspaceId, CancellationToken ct);
    Task<IReadOnlyList<FileIndexEntry>> SearchAsync(Guid workspaceId, string query, int limit, CancellationToken ct);
}

public interface IDiffService
{
    string CreateUnifiedDiff(string relativePath, string oldText, string newText);
    Task<ChangeSet> CreatePreviewAsync(Guid workspaceId, Guid conversationId, string description, IReadOnlyDictionary<string, string> proposedFiles, CancellationToken ct);
    Task ApplyAsync(Guid changeSetId, CancellationToken ct);
}

public interface ITerminalService
{
    IAsyncEnumerable<string> RunPowerShellAsync(string root, string command, CancellationToken ct, int timeoutSeconds = 300);
}

public interface IPersistentShell : IAsyncDisposable
{
    IAsyncEnumerable<string> RunAsync(string command, CancellationToken ct, int timeoutSeconds = 120);
    bool IsAlive { get; }
}

public interface IPersistentShellFactory
{
    IPersistentShell Create(string workingDirectory);
}

public interface IProjectUnderstandingService
{
    Task<string> BuildProjectSummaryAsync(Guid workspaceId, CancellationToken ct);
}

public interface IProjectMemoryService
{
    Task<string> LoadProjectInstructionsAsync(string workspaceRoot, CancellationToken ct);
}

public interface IMemoryService
{
    Task<string> GetRelevantMemoriesAsync(Guid workspaceId, string prompt, CancellationToken ct);
    Task CaptureTurnAsync(Guid workspaceId, string userPrompt, string assistantResponse, CancellationToken ct);
}

public interface IContextCompressionService
{
    string Compress(IReadOnlyList<ChatMessage> messages, int maxCharacters);
}

// ── Phase 1: Streaming tool execution ──────────────────────────────────────
public interface IContextCompactor
{
    Task<bool> CompactIfNeededAsync(Guid conversationId, List<OllamaChatMessage> messages, string model, CancellationToken ct);
    void MicrocompactMessages(List<OllamaChatMessage> messages);
    Task FullCompactAsync(Guid conversationId, List<OllamaChatMessage> messages, string model, CancellationToken ct);
    Task ReactiveCompactAsync(Guid conversationId, List<OllamaChatMessage> messages, string model, CancellationToken ct);
}

// ── Phase 5: Memory system ─────────────────────────────────────────────────
public enum MemoryType { User, Feedback, Project, Reference }
public sealed record MemoryEntry(string Name, string Description, MemoryType Type, string Content, IReadOnlyList<string> Keywords);

public interface IMemoryStore
{
    string LoadMemoryIndex();
    void SaveMemory(string name, string description, MemoryType type, string content);
    IReadOnlyList<MemoryEntry> SearchRelevant(string query, int maxResults = 5);
    void ExtractFromConversation(string userPrompt, string assistantResponse);
}

// ── Phase 6: Chat history ──────────────────────────────────────────────────
public sealed record ConversationGroup(string Label, IReadOnlyList<Domain.Conversation> Conversations);
public sealed record ConversationSearchResult(Guid ConversationId, string Title, DateTimeOffset Timestamp, string MatchType, string Snippet);

public interface IChatHistoryStore
{
    Task<IReadOnlyList<ConversationGroup>> GetGroupedConversationsAsync(Guid workspaceId, CancellationToken ct);
    Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(Guid workspaceId, string query, int maxResults = 20, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRecentPromptsAsync(Guid conversationId, int count = 50, CancellationToken ct = default);
    Task<string> GenerateTitleAsync(Guid conversationId, CancellationToken ct);
    Task DeleteAsync(Guid conversationId, CancellationToken ct);
}

// ── Phase 7: Skills system ─────────────────────────────────────────────────
public sealed class SkillDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string WhenToUse { get; init; } = "";
    public required string PromptTemplate { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> FilePatterns { get; init; } = [];
    public string? Model { get; init; }
    public string? ArgumentHint { get; init; }
    public string SourcePath { get; init; } = "";

    public string Render(string arguments)
    {
        return PromptTemplate
            .Replace("$ARGUMENTS", arguments)
            .Replace("$1", arguments);
    }
}

public interface ISkillLoader
{
    IReadOnlyList<SkillDefinition> LoadAll();
    SkillDefinition? FindByName(string name);
    IReadOnlyList<SkillDefinition> Search(string query);
    IReadOnlyList<SkillDefinition> GetRelevant(IReadOnlyList<string> filePaths);
    string FormatForSystemPrompt();
}

// ── Phase 8: SubAgent system ───────────────────────────────────────────────
public interface ISubAgentManager
{
    Task<SubAgentResult> SpawnAsync(SubAgentRequest request, CancellationToken ct);
    IReadOnlyList<SubAgentInfo> GetActiveAgents();
    Task StopAsync(Guid agentId, CancellationToken ct);
}

public sealed record SubAgentRequest(
    string Description,
    string Prompt,
    Guid WorkspaceId,
    Guid ConversationId,
    string? Model = null,
    bool IsBackground = false);

public sealed record SubAgentResult(
    Guid AgentId,
    string Output,
    bool Success,
    IReadOnlyList<ToolResult> ToolResults);

public sealed record SubAgentInfo(
    Guid AgentId,
    string Description,
    string Status,
    DateTimeOffset StartedAt);

public interface IWebFetchService
{
    Task<string> FetchAsync(string url, CancellationToken ct);
}

public interface IWebSearchService
{
    Task<string> SearchAsync(string query, CancellationToken ct);
}

public interface ITodoService
{
    Task<IReadOnlyList<TodoItem>> GetAsync(Guid conversationId, CancellationToken ct);
    Task UpsertAsync(Guid conversationId, IReadOnlyList<(string Content, string Status, int Order)> items, CancellationToken ct);
}

public sealed record ToolStepEntry(
    string Label,
    string FilePath,
    string Lang,
    string Detail,
    string Operation,
    string DiffContent = "");

public sealed record ToolStepGroup(
    string Header,
    bool Expandable,
    IReadOnlyList<ToolStepEntry> Steps,
    int TurnNumber = 0,
    AgentLoopPhase Phase = AgentLoopPhase.Act);

public enum AgentLoopPhase
{
    Observe,
    Think,
    Act,
    Observe_Results
}
