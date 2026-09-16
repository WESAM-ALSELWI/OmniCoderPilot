using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Wpf.Services;

public sealed class WpfEventSink : IAgentEventSink
{
    private readonly IEventAggregator _events;

    public WpfEventSink(IEventAggregator events)
    {
        _events = events;
    }

    public Task TokenAsync(string connectionId, Guid conversationId, string token)
    {
        _events.Publish(new TokenReceivedEvent(conversationId, token));
        return Task.CompletedTask;
    }

    public Task AgentActivityAsync(string connectionId, Guid conversationId, string activity, string activityType = "thinking")
    {
        _events.Publish(new AgentActivityEvent(conversationId, activity, activityType));
        return Task.CompletedTask;
    }

    public Task ThinkingAsync(string connectionId, Guid conversationId, string text)
    {
        _events.Publish(new AgentActivityEvent(conversationId, text, "thinking"));
        return Task.CompletedTask;
    }

    public Task ThinkingStepAsync(string connectionId, Guid conversationId, string thinkingText, int elapsedMs, int turnNumber, AgentLoopPhase phase)
    {
        _events.Publish(new ThinkingStepEvent(conversationId, thinkingText, elapsedMs, turnNumber, phase));
        return Task.CompletedTask;
    }

    public Task ToolStepAsync(string connectionId, Guid conversationId, string toolName, string filePath, string operation, int addedLines, int removedLines, int elapsedMs, bool isExpandable)
    {
        _events.Publish(new ToolStepEvent(conversationId, toolName, filePath, operation, addedLines, removedLines, elapsedMs, isExpandable));
        return Task.CompletedTask;
    }

    public Task ToolStepsBatchAsync(string connectionId, Guid conversationId, IReadOnlyList<ToolStepGroup> groups)
    {
        _events.Publish(new ToolStepsBatchEvent(conversationId, groups));
        return Task.CompletedTask;
    }

    public Task StatusAsync(string connectionId, Guid taskId, string status, int progress)
    {
        _events.Publish(new StatusEvent(taskId, status, progress));
        return Task.CompletedTask;
    }

    public Task ToolAsync(string connectionId, string toolName, string status, string content, string? icon = null)
    {
        _events.Publish(new ToolEvent(toolName, status, content, icon ?? ""));
        return Task.CompletedTask;
    }

    public Task DiffAsync(string connectionId, Guid changeSetId)
    {
        _events.Publish(new DiffEvent(changeSetId));
        return Task.CompletedTask;
    }

    public Task ErrorAsync(string connectionId, string message)
    {
        _events.Publish(new ErrorEvent(message));
        return Task.CompletedTask;
    }

    public Task TodoAsync(string connectionId, Guid conversationId, IReadOnlyList<TodoItem> todos)
    {
        _events.Publish(new TodoEvent(conversationId, todos));
        return Task.CompletedTask;
    }

    public Task<bool> RequestPermissionAsync(string connectionId, PermissionRequest request)
    {
        var tcs = new TaskCompletionSource<bool>();
        _events.Publish(new PermissionRequestEvent(request, tcs));
        return tcs.Task;
    }
}

// ── Event DTOs ──────────────────────────────────────────────────────────────
public sealed record TokenReceivedEvent(Guid ConversationId, string Token);
public sealed record AgentActivityEvent(Guid ConversationId, string Activity, string ActivityType);
public sealed record ThinkingStepEvent(Guid ConversationId, string Text, int ElapsedMs, int TurnNumber, AgentLoopPhase Phase);
public sealed record ToolStepEvent(Guid ConversationId, string ToolName, string FilePath, string Operation, int Added, int Removed, int ElapsedMs, bool IsExpandable);
public sealed record ToolStepsBatchEvent(Guid ConversationId, IReadOnlyList<ToolStepGroup> Groups);
public sealed record StatusEvent(Guid TaskId, string Status, int Progress);
public sealed record ToolEvent(string ToolName, string Status, string Content, string Icon);
public sealed record DiffEvent(Guid ChangeSetId);
public sealed record ErrorEvent(string Message);
public sealed record TodoEvent(Guid ConversationId, IReadOnlyList<TodoItem> Todos);
public sealed record PermissionRequestEvent(PermissionRequest Request, TaskCompletionSource<bool> CompletionSource);
public sealed record ModelSelectedEvent(string ModelName);
