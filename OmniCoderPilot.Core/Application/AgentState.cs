namespace OmniCoderPilot.Application;

/// <summary>
/// State machine for the agent execution loop.
/// Tracks recovery attempts, compaction state, and transition reasons.
/// Based on the reference project's query loop architecture.
/// </summary>
public sealed class AgentState
{
    // ── Core state ────────────────────────────────────────────────────────
    public int TurnCount { get; set; }
    public int MaxTurns { get; set; } = 120;
    public bool ExecutedAnyTool { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveNoTools { get; set; }
    public int TotalToolFailures { get; set; }
    public int NoToolRedirects { get; set; }
    public bool IsPlanMode { get; set; }

    // ── Recovery state ────────────────────────────────────────────────────
    /// <summary>Number of max-output-tokens recovery attempts (max 3).</summary>
    public int MaxOutputTokensRecoveryCount { get; set; }

    /// <summary>Whether we've already attempted reactive compaction this turn.</summary>
    public bool HasAttemptedReactiveCompact { get; set; }

    /// <summary>Override for max output tokens (escalate from 8K to 64K).</summary>
    public int? MaxOutputTokensOverride { get; set; }

    /// <summary>Whether a prompt-too-long error was withheld for recovery.</summary>
    public bool WithheldPromptTooLong { get; set; }

    /// <summary>Whether a max-output-tokens error was withheld for recovery.</summary>
    public bool WithheldMaxOutputTokens { get; set; }

    // ── EditFile retry tracking ──────────────────────────────────────────
    /// <summary>Tracks how many times we've tried to edit each file (to prevent loops).</summary>
    public Dictionary<string, int> EditFileRetryCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum retries per file before forcing a different approach.</summary>
    public const int MaxEditFileRetriesPerFile = 3;

    /// <summary>Whether we're currently stuck in an EditFile loop.</summary>
    public bool IsInEditFileLoop { get; set; }

    /// <summary>The file we're currently stuck on (if in a loop).</summary>
    public string? StuckOnFile { get; set; }

    /// <summary>
    /// Record an EditFile attempt for a file and check if we're exceeding limits.
    /// Returns true if we should force a different approach.
    /// </summary>
    public bool RecordEditFileAttempt(string filePath)
    {
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        if (!EditFileRetryCounts.ContainsKey(normalized))
            EditFileRetryCounts[normalized] = 0;

        EditFileRetryCounts[normalized]++;

        if (EditFileRetryCounts[normalized] >= MaxEditFileRetriesPerFile)
        {
            IsInEditFileLoop = true;
            StuckOnFile = filePath;
            return true; // Should force different approach
        }

        return false;
    }

    /// <summary>
    /// Reset EditFile retry tracking when moving to a different file.
    /// </summary>
    public void ResetEditFileRetriesIfChanged(string newFilePath)
    {
        var normalized = newFilePath.Replace('\\', '/').ToLowerInvariant();
        if (StuckOnFile is not null)
        {
            var stuckNormalized = StuckOnFile.Replace('\\', '/').ToLowerInvariant();
            if (normalized != stuckNormalized)
            {
                // Moving to a different file - reset loop tracking
                IsInEditFileLoop = false;
                StuckOnFile = null;
            }
        }
    }

    /// <summary>
    /// Get a summary of which files we've struggled with.
    /// </summary>
    public string GetEditFileRetrySummary()
    {
        var struggles = EditFileRetryCounts
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"  {kv.Key}: {kv.Value} attempts")
            .ToList();

        return struggles.Count > 0
            ? $"Files with multiple edit attempts:\n{string.Join("\n", struggles)}"
            : "";
    }

    // ── Compaction state ──────────────────────────────────────────────────
    /// <summary>Whether compaction was performed this session.</summary>
    public bool HasCompacted { get; set; }

    /// <summary>Turns since last compaction.</summary>
    public int TurnsSinceCompact { get; set; }

    /// <summary>Consecutive compaction failures (circuit breaker).</summary>
    public int CompactConsecutiveFailures { get; set; }

    /// <summary>Max consecutive failures before circuit breaker trips.</summary>
    public const int MaxCompactConsecutiveFailures = 3;

    // ── Stop hook state ───────────────────────────────────────────────────
    /// <summary>Whether stop hooks are currently executing.</summary>
    public bool StopHookActive { get; set; }

    /// <summary>Blocking errors from stop hooks to inject as user messages.</summary>
    public List<string> StopHookBlockingErrors { get; set; } = [];

    // ── Transition tracking ───────────────────────────────────────────────
    /// <summary>Why the previous iteration continued (for debugging).</summary>
    public ContinueReason? LastTransition { get; set; }

    /// <summary>Total tokens estimated this session.</summary>
    public int EstimatedTokensUsed { get; set; }

    // ── Methods ───────────────────────────────────────────────────────────

    public bool ShouldStop() =>
        TurnCount >= MaxTurns ||
        ConsecutiveFailures >= 4 ||
        (ConsecutiveNoTools > 3 && ExecutedAnyTool);

    public bool ShouldAutoCompact(int estimatedTokens, int contextWindow)
    {
        if (HasCompacted && TurnsSinceCompact < 3) return false;
        if (CompactConsecutiveFailures >= MaxCompactConsecutiveFailures) return false;

        var threshold = contextWindow - 13_000; // AUTOCOMPACT_BUFFER_TOKENS
        return estimatedTokens >= threshold;
    }

    public bool ShouldReactiveCompact(int estimatedTokens, int contextWindow)
    {
        if (HasAttemptedReactiveCompact) return false;
        if (CompactConsecutiveFailures >= MaxCompactConsecutiveFailures) return false;

        var threshold = contextWindow - 3_000; // MANUAL_COMPACT_BUFFER_TOKENS
        return estimatedTokens >= threshold;
    }

    public bool CanRecoverMaxOutputTokens() =>
        MaxOutputTokensRecoveryCount < 3;

    public void RecordCompactionSuccess()
    {
        HasCompacted = true;
        TurnsSinceCompact = 0;
        CompactConsecutiveFailures = 0;
    }

    public void RecordCompactionFailure()
    {
        CompactConsecutiveFailures++;
    }

    public void RecordTurn()
    {
        TurnCount++;
        TurnsSinceCompact++;
    }
}

/// <summary>
/// Why the agent loop continued to the next iteration.
/// </summary>
public enum ContinueReason
{
    /// <summary>Normal continuation after tool execution.</summary>
    NextTurn,

    /// <summary>Prompt-too-long: context collapse drained staged collapses.</summary>
    CollapseDrainRetry,

    /// <summary>Prompt-too-long: reactive compaction produced summary.</summary>
    ReactiveCompactRetry,

    /// <summary>Hit 8K output cap, escalating to 64K.</summary>
    MaxOutputTokensEscalate,

    /// <summary>Injected "continue where you left off" message (max 3).</summary>
    MaxOutputTokensRecovery,

    /// <summary>Stop hook returned blocking errors.</summary>
    StopHookBlocking,

    /// <summary>Token budget not yet met, continuing.</summary>
    TokenBudgetContinuation,

    /// <summary>No tools called, redirecting to force tool usage.</summary>
    NoToolRedirect,

    /// <summary>Error recovery: retrying after failure.</summary>
    ErrorRecovery,

    /// <summary>Deep strategy reset after many failures.</summary>
    StrategyReset,

    /// <summary>Model fallback triggered (e.g., Opus -> Sonnet).</summary>
    ModelFallback
}
