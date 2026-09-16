using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Application;

/// <summary>
/// Executes tools as their tool_use blocks complete during streaming.
/// Read-only tools run concurrently; write tools run after stream completes.
/// </summary>
public sealed class StreamingToolExecutor
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ToolExecutionContext _execCtx;
    private readonly IAgentEventSink _sink;
    private readonly string _connectionId;
    private readonly Guid _conversationId;
    private readonly int _maxConcurrency = 4;

    private readonly List<(OllamaToolCall Call, Task<ToolResult> Task)> _pendingReads = new();
    private readonly List<OllamaToolCall> _pendingWrites = new();
    private readonly ConcurrentDictionary<int, ToolResult> _completedResults = new();
    private int _toolIndex;

    public StreamingToolExecutor(
        IReadOnlyDictionary<string, IAgentTool> tools,
        ToolExecutionContext execCtx,
        IAgentEventSink sink,
        string connectionId,
        Guid conversationId)
    {
        _tools = tools;
        _execCtx = execCtx;
        _sink = sink;
        _connectionId = connectionId;
        _conversationId = conversationId;
    }

    /// <summary>
    /// Submit a tool call for execution. Read-only tools start immediately.
    /// Write tools are queued for after the stream completes.
    /// </summary>
    public void SubmitToolCall(OllamaToolCall call)
    {
        var idx = Interlocked.Increment(ref _toolIndex) - 1;

        if (_tools.TryGetValue(call.Name, out var tool) && tool.IsReadOnly && !_execCtx.IsPlanMode)
        {
            // Read-only tool: start immediately
            var task = ExecuteSingleToolAsync(call, tool);
            lock (_pendingReads) _pendingReads.Add((call, task));
        }
        else
        {
            // Write tool: queue for after stream
            lock (_pendingWrites) _pendingWrites.Add(call);
        }
    }

    /// <summary>
    /// Wait for all in-flight read-only tools to complete.
    /// </summary>
    public async Task DrainReadsAsync()
    {
        List<(OllamaToolCall Call, Task<ToolResult> Task)> snapshot;
        lock (_pendingReads)
        {
            snapshot = _pendingReads.ToList();
            _pendingReads.Clear();
        }

        if (snapshot.Count > 0)
        {
            await Task.WhenAll(snapshot.Select(x => x.Task));
            foreach (var (call, task) in snapshot)
            {
                if (task.IsCompletedSuccessfully)
                    _completedResults[_toolIndex] = task.Result;
            }
        }
    }

    /// <summary>
    /// Execute all queued write tools sequentially after stream completes.
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> ExecutePendingWritesAsync()
    {
        List<OllamaToolCall> snapshot;
        lock (_pendingWrites)
        {
            snapshot = _pendingWrites.ToList();
            _pendingWrites.Clear();
        }

        var results = new List<ToolResult>();
        foreach (var call in snapshot)
        {
            IAgentTool? tool = null;
            _tools.TryGetValue(call.Name, out tool);

            if (tool is null)
            {
                var available = string.Join(", ", _tools.Keys.OrderBy(k => k));
                var unknown = new ToolResult(call.Name, false,
                    $"ERROR: Unknown tool '{call.Name}'. Available tools: {available}");
                await _sink.ToolAsync(_connectionId, call.Name, "failed", unknown.Content, "❓");
                results.Add(unknown);
                continue;
            }

            if (_execCtx.IsPlanMode && !tool.IsReadOnly)
            {
                var planText = $"[PLAN] Would call {tool.Name} with: {call.Arguments.ToJsonString()}";
                await _sink.ToolAsync(_connectionId, tool.Name, "planned", planText);
                results.Add(new ToolResult(tool.Name, true, planText));
                continue;
            }

            if (tool.IsDangerous && !_execCtx.IsPlanMode)
            {
                var permReq = new PermissionRequest(
                    Guid.NewGuid().ToString(), tool.Name,
                    $"Allow {tool.Name}?",
                    call.Arguments.ToJsonString().Length > 500
                        ? call.Arguments.ToJsonString()[..500] + "…"
                        : call.Arguments.ToJsonString());

                await _sink.AgentActivityAsync(_connectionId, _conversationId,
                    $"🔐 Requesting permission for {tool.Name}…", "permission");
                var approved = await _sink.RequestPermissionAsync(_connectionId, permReq);

                if (!approved)
                {
                    var denied = new ToolResult(tool.Name, false,
                        $"Permission denied for {tool.Name}. Choose a different approach.");
                    await _sink.ToolAsync(_connectionId, tool.Name, "denied", denied.Content);
                    results.Add(denied);
                    continue;
                }
            }

            var result = await ExecuteSingleToolAsync(call, tool);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Get all results collected so far (from completed reads + writes).
    /// </summary>
    public IReadOnlyList<ToolResult> GetAllResults()
    {
        return _completedResults.Values.ToList();
    }

    private async Task<ToolResult> ExecuteSingleToolAsync(OllamaToolCall call, IAgentTool tool)
    {
        await _sink.ToolAsync(_connectionId, tool.Name, "running",
            call.Arguments.ToJsonString().Length > 500
                ? call.Arguments.ToJsonString()[..500] + "…"
                : call.Arguments.ToJsonString());

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(call.Arguments, _execCtx, CancellationToken.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ToolResult(tool.Name, false,
                $"TOOL EXCEPTION in {tool.Name}: {ex.GetType().Name}: {ex.Message}");
        }

        await _sink.ToolAsync(_connectionId, tool.Name,
            result.Success ? "completed" : "failed",
            result.Content.Length > 2000 ? result.Content[..2000] + "…" : result.Content);

        return result;
    }
}
