using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OmniCoderPilot.Application;

/// <summary>
/// Enhanced tool orchestration with batching, concurrency control,
/// and in-progress tracking. Based on the reference project's
/// partition-and-batch architecture.
/// </summary>
public sealed class ToolOrchestrator
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly int _maxConcurrency = 10;

    public ToolOrchestrator(IReadOnlyDictionary<string, IAgentTool> tools, int maxConcurrency = 10)
    {
        _tools = tools;
        _maxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// Partition tool calls into batches based on concurrency safety.
    /// Read-only (concurrent-safe) tools run in parallel batches.
    /// Write (non-concurrent-safe) tools run serially.
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> ExecuteToolCallsAsync(
        IReadOnlyList<OllamaToolCall> calls,
        ToolExecutionContext context,
        Func<OllamaToolCall, IAgentTool, Task<ToolResult>> executor,
        Func<OllamaToolCall, IAgentTool, Task<bool>>? permissionCheck = null,
        CancellationToken ct = default)
    {
        var batches = PartitionIntoBatches(calls);
        var allResults = new ToolResult[calls.Count];

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            if (batch.IsConcurrencySafe)
            {
                // Run concurrent-safe tools in parallel
                var tasks = batch.Indices.Select(async idx =>
                {
                    var call = calls[idx];
                    if (!_tools.TryGetValue(call.Name, out var tool))
                    {
                        allResults[idx] = new ToolResult(call.Name, false,
                            $"Unknown tool: {call.Name}. Available: {string.Join(", ", _tools.Keys.OrderBy(k => k))}");
                        return;
                    }

                    if (permissionCheck is not null)
                    {
                        var approved = await permissionCheck(call, tool);
                        if (!approved)
                        {
                            allResults[idx] = new ToolResult(tool.Name, false,
                                $"Permission denied for {tool.Name}.");
                            return;
                        }
                    }

                    allResults[idx] = await executor(call, tool);
                });

                await Task.WhenAll(tasks);
            }
            else
            {
                // Run non-concurrent tools serially
                foreach (var idx in batch.Indices)
                {
                    ct.ThrowIfCancellationRequested();
                    var call = calls[idx];

                    if (!_tools.TryGetValue(call.Name, out var tool))
                    {
                        allResults[idx] = new ToolResult(call.Name, false,
                            $"Unknown tool: {call.Name}. Available: {string.Join(", ", _tools.Keys.OrderBy(k => k))}");
                        continue;
                    }

                    if (permissionCheck is not null)
                    {
                        var approved = await permissionCheck(call, tool);
                        if (!approved)
                        {
                            allResults[idx] = new ToolResult(tool.Name, false,
                                $"Permission denied for {tool.Name}.");
                            continue;
                        }
                    }

                    allResults[idx] = await executor(call, tool);
                }
            }
        }

        return allResults;
    }

    /// <summary>
    /// Partition tool calls into batches. Consecutive concurrency-safe tools
    /// are grouped together. Non-safe tools each get their own batch.
    /// </summary>
    private IReadOnlyList<ToolBatch> PartitionIntoBatches(IReadOnlyList<OllamaToolCall> calls)
    {
        var batches = new List<ToolBatch>();
        ToolBatch? currentBatch = null;

        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var isSafe = _tools.TryGetValue(call.Name, out var tool) && tool.IsReadOnly;

            if (isSafe && currentBatch?.IsConcurrencySafe == true)
            {
                // Extend current concurrent batch
                currentBatch.Indices.Add(i);
            }
            else
            {
                // Start new batch
                currentBatch = new ToolBatch(isSafe, [i]);
                batches.Add(currentBatch);
            }
        }

        return batches;
    }

    /// <summary>
    /// Check if a tool is concurrency-safe (read-only).
    /// </summary>
    public bool IsConcurrencySafe(string toolName)
    {
        return _tools.TryGetValue(toolName, out var tool) && tool.IsReadOnly;
    }

    /// <summary>
    /// Get a human-readable description of the tool batch.
    /// </summary>
    public static string DescribeBatch(IReadOnlyList<OllamaToolCall> calls)
    {
        if (calls.Count == 1)
            return $"Running {calls[0].Name}";

        var names = calls.Take(4).Select(c => c.Name);
        return $"Running {calls.Count} tools: {string.Join(", ", names)}";
    }

    private sealed class ToolBatch(bool isConcurrencySafe, List<int> indices)
    {
        public bool IsConcurrencySafe { get; } = isConcurrencySafe;
        public List<int> Indices { get; } = indices;
    }
}
