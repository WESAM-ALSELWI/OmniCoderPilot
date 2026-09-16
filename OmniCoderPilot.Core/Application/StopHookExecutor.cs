using System.Diagnostics;
using System.Text;

namespace OmniCoderPilot.Application;

/// <summary>
/// Executes stop hooks after each model turn completes.
/// Based on the reference project's stopHooks.ts architecture.
/// 
/// Stop hooks are post-processing steps that run when the model
/// produces a response without tool calls (i.e., the turn is "done").
/// </summary>
public sealed class StopHookExecutor
{
    private readonly IAgentEventSink _sink;
    private readonly string _connectionId;
    private readonly Guid _conversationId;

    public StopHookExecutor(IAgentEventSink sink, string connectionId, Guid conversationId)
    {
        _sink = sink;
        _connectionId = connectionId;
        _conversationId = conversationId;
    }

    /// <summary>
    /// Execute post-turn hooks. Returns blocking errors that should be
    /// injected as user messages for the next turn.
    /// </summary>
    public async Task<StopHookResult> ExecuteAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<ToolResult> toolResults,
        AgentState state,
        CancellationToken ct)
    {
        var result = new StopHookResult();
        var sw = Stopwatch.StartNew();

        try
        {
            // ── 1. Memory extraction (fire-and-forget) ──────────────────
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExtractMemoryAsync(messages, ct);
                }
                catch { /* Non-fatal */ }
            }, ct);

            // ── 2. Verify completion if tools were used ─────────────────
            if (state.ExecutedAnyTool && toolResults.Count > 0)
            {
                var verification = await VerifyToolResultsAsync(toolResults, ct);
                if (!string.IsNullOrEmpty(verification))
                {
                    result.BlockingErrors.Add(verification);
                }
            }

            // ── 3. Check for incomplete tasks ──────────────────────────
            if (state.ExecutedAnyTool && state.ConsecutiveNoTools <= 1)
            {
                // Agent might have more work to do — inject a continuation nudge
                // Only if the agent hasn't written a final answer yet
            }

            // ── 4. Generate progress summary ──────────────────────────
            result.Summary = GenerateProgressSummary(state, toolResults);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Stop hook errors are non-fatal
            await _sink.AgentActivityAsync(_connectionId, _conversationId,
                $"⚠️ Stop hook error: {ex.Message}", "warning");
        }

        sw.Stop();
        result.DurationMs = (int)sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Verify that tool results indicate successful completion.
    /// Returns a blocking error message if there are concerning failures.
    /// </summary>
    private Task<string?> VerifyToolResultsAsync(
        IReadOnlyList<ToolResult> results,
        CancellationToken ct)
    {
        var failures = results.Where(r => !r.Success).ToList();
        var successes = results.Where(r => r.Success).ToList();

        // If all tools failed, that's a blocking issue
        if (failures.Count > 0 && successes.Count == 0)
        {
            return Task.FromResult<string?>(
                $"All {failures.Count} tool(s) failed this turn. " +
                $"Check errors and retry with a different approach.");
        }

        // If more than 50% failed, warn but don't block
        if (failures.Count > successes.Count)
        {
            return Task.FromResult<string?>(
                $"{failures.Count} of {results.Count} tools failed. " +
                $"Review errors before continuing.");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Extract memories from the conversation for future reference.
    /// </summary>
    private Task ExtractMemoryAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        CancellationToken ct)
    {
        // Memory extraction is done by FileMemoryStore.ExtractFromConversation
        // This is a placeholder for the fire-and-forget background task
        return Task.CompletedTask;
    }

    /// <summary>
    /// Generate a progress summary for the activity feed.
    /// </summary>
    private static string GenerateProgressSummary(
        AgentState state,
        IReadOnlyList<ToolResult> toolResults)
    {
        var successes = toolResults.Count(r => r.Success);
        var failures = toolResults.Count(r => !r.Success);

        var sb = new StringBuilder();
        sb.Append($"Turn {state.TurnCount}: ");

        if (failures == 0)
            sb.Append($"{successes} tool(s) succeeded");
        else
            sb.Append($"{successes} succeeded, {failures} failed");

        if (state.ConsecutiveFailures > 0)
            sb.Append($" (⚠️ {state.ConsecutiveFailures} consecutive failures)");

        return sb.ToString();
    }
}

/// <summary>
/// Result of stop hook execution.
/// </summary>
public sealed class StopHookResult
{
    public List<string> BlockingErrors { get; init; } = [];
    public string Summary { get; set; } = "";
    public int DurationMs { get; set; }
    public bool PreventContinuation { get; set; }
}
