using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Manages isolated sub-agent instances that can run in background
/// with their own conversation context and tools.
/// </summary>
public sealed class SubAgentManager(
    IDbContextFactory<AppDbContext> dbFactory,
    IOllamaClient ollama,
    IEnumerable<IAgentTool> tools,
    IAgentEventSink sink,
    IProjectUnderstandingService projectUnderstanding,
    IContextCompressionService compression,
    IConfiguration config) : ISubAgentManager
{
    private readonly Dictionary<Guid, SubAgentState> _activeAgents = new();
    private readonly object _lock = new();
    private readonly int _maxTurns = int.TryParse(config["Agent:MaxTurns"], out var t) ? t : 120;

    private IReadOnlyDictionary<string, IAgentTool> ToolDict =>
        tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Spawn a new sub-agent with its own isolated context.
    /// </summary>
    public async Task<SubAgentResult> SpawnAsync(SubAgentRequest request, CancellationToken ct)
    {
        var agentId = Guid.NewGuid();
        var toolDefs = tools.Select(t => new OllamaToolDefinition(t.Name, t.Description, t.Schema)).ToList();
        var toolDict = ToolDict;

        var state = new SubAgentState
        {
            AgentId = agentId,
            Description = request.Description,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };

        lock (_lock) _activeAgents[agentId] = state;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ws = await db.Workspaces.FindAsync([request.WorkspaceId], ct)
                ?? throw new InvalidOperationException("Workspace not found.");

            // Build isolated message history
            var systemPrompt = $"You are a sub-agent working on: {request.Description}\n" +
                               $"Workspace: {ws.RootPath}\n" +
                               $"You have access to file, search, and command tools. Complete your task autonomously.";

            var messages = new List<OllamaChatMessage>
            {
                new("system", systemPrompt),
                new("user", request.Prompt)
            };

            var allResults = new List<ToolResult>();

            // Agent execution loop (simplified version of main orchestrator)
            for (var turn = 1; turn <= _maxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                OllamaToolResponse response;
                try
                {
                    response = await ollama.ChatWithToolsAsync(
                        request.Model ?? "", messages, toolDefs, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    break; // Sub-agent stops on LLM error
                }

                var toolCalls = response.ToolCalls ?? [];
                if (toolCalls.Count == 0)
                {
                    // No tools = task complete (or final answer)
                    state.Status = "completed";
                    return new SubAgentResult(agentId, response.TextContent ?? "", true, allResults);
                }

                messages.Add(new OllamaChatMessage("assistant", response.TextContent ?? "", ToolCalls: toolCalls));

                // Execute tools (reads parallel, writes sequential)
                var results = await ExecuteToolsAsync(toolCalls, ws.RootPath, request.WorkspaceId,
                    request.ConversationId, toolDict, ct);

                allResults.AddRange(results);

                for (var idx = 0; idx < toolCalls.Count; idx++)
                {
                    var toolCall = toolCalls[idx];
                    var result = results[idx];
                    messages.Add(new OllamaChatMessage("tool", result.Content, toolCall.Id, toolCall.Name));
                }
            }

            state.Status = "completed";
            return new SubAgentResult(agentId, "Sub-agent completed.", true, allResults);
        }
        catch (OperationCanceledException)
        {
            state.Status = "cancelled";
            return new SubAgentResult(agentId, "Sub-agent was cancelled.", false, []);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            return new SubAgentResult(agentId, $"Sub-agent failed: {ex.Message}", false, []);
        }
        finally
        {
            lock (_lock) _activeAgents.Remove(agentId);
        }
    }

    public IReadOnlyList<SubAgentInfo> GetActiveAgents()
    {
        lock (_lock)
        {
            return _activeAgents.Values
                .Select(a => new SubAgentInfo(a.AgentId, a.Description, a.Status, a.StartedAt))
                .ToList();
        }
    }

    public Task StopAsync(Guid agentId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_activeAgents.TryGetValue(agentId, out var state))
            {
                state.Status = "cancelled";
                _activeAgents.Remove(agentId);
            }
        }
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<ToolResult>> ExecuteToolsAsync(
        IReadOnlyList<OllamaToolCall> calls,
        string root,
        Guid workspaceId,
        Guid conversationId,
        IReadOnlyDictionary<string, IAgentTool> toolDict,
        CancellationToken ct)
    {
        var results = new ToolResult[calls.Count];
        var ctx = new ToolExecutionContext(workspaceId, conversationId, root, Application.ToolPermissionMode.WorkspaceWrite);

        // Read-only in parallel
        var readOnlyIndices = calls
            .Select((c, i) => toolDict.TryGetValue(c.Name, out var t) && t.IsReadOnly ? i : -1)
            .Where(i => i >= 0).ToList();

        if (readOnlyIndices.Count > 0)
        {
            await Task.WhenAll(readOnlyIndices.Select(async idx =>
            {
                var call = calls[idx];
                if (toolDict.TryGetValue(call.Name, out var tool))
                {
                    try { results[idx] = await tool.ExecuteAsync(call.Arguments, ctx, ct); }
                    catch { results[idx] = new ToolResult(call.Name, false, "Tool execution failed"); }
                }
                else
                {
                    results[idx] = new ToolResult(call.Name, false, $"Unknown tool: {call.Name}");
                }
            }));
        }

        // Write tools sequentially
        var writeIndices = calls
            .Select((c, i) => !toolDict.TryGetValue(c.Name, out var t) || !t.IsReadOnly ? i : -1)
            .Where(i => i >= 0).ToList();

        foreach (var idx in writeIndices)
        {
            var call = calls[idx];
            if (toolDict.TryGetValue(call.Name, out var tool))
            {
                try { results[idx] = await tool.ExecuteAsync(call.Arguments, ctx, ct); }
                catch { results[idx] = new ToolResult(call.Name, false, "Tool execution failed"); }
            }
            else
            {
                results[idx] = new ToolResult(call.Name, false, $"Unknown tool: {call.Name}");
            }
        }

        return results;
    }

    private sealed class SubAgentState
    {
        public Guid AgentId { get; init; }
        public string Description { get; init; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset StartedAt { get; init; }
    }
}
