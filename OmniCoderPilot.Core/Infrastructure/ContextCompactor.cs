using System.Text;
using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Three-tier context compaction based on reference project architecture:
/// 1. Microcompact: Clear old tool results (lightweight, no LLM call)
/// 2. Full compact: Summarize conversation via LLM (heavy, frees most space)
/// 3. Reactive compact: Triggered on prompt-too-long error (emergency recovery)
/// 
/// Key thresholds from reference project:
/// - Auto-compact buffer: 13,000 tokens below effective context window
/// - Blocking limit: 3,000 tokens below effective context window
/// - Circuit breaker: Max 3 consecutive failures
/// </summary>
public sealed class ContextCompactor(
    IDbContextFactory<AppDbContext> dbFactory,
    IOllamaClient ollama,
    IContextCompressionService compression) : IContextCompactor
{
    // ── Thresholds (chars, not tokens — we estimate ~4 chars/token) ──────
    private const int MicrocompactThresholdChars = 50_000;   // ~12.5K tokens
    private const int FullCompactThresholdChars = 100_000;   // ~25K tokens
    private const int ReactiveCompactThresholdChars = 150_000; // ~37.5K tokens (prompt-too-long)
    private const int ToolResultTruncateLines = 20;
    private const int MaxCompactConsecutiveFailures = 3;

    /// <summary>
    /// Check if compaction is needed and perform the appropriate tier.
    /// Returns true if compaction was performed.
    /// </summary>
    public async Task<bool> CompactIfNeededAsync(
        Guid conversationId,
        List<OllamaChatMessage> messages,
        string model,
        CancellationToken ct)
    {
        var totalChars = EstimateTotalChars(messages);

        if (totalChars < MicrocompactThresholdChars)
            return false;

        if (totalChars >= FullCompactThresholdChars)
        {
            await FullCompactAsync(conversationId, messages, model, ct);
            return true;
        }

        // Microcompact: truncate old tool results in-place
        MicrocompactMessages(messages);
        return true;
    }

    /// <summary>
    /// Microcompact: truncate content of old tool results to free space
    /// without a full LLM summarization call.
    /// </summary>
    public void MicrocompactMessages(List<OllamaChatMessage> messages)
    {
        // Keep the last 10 messages intact, truncate older tool results
        var cutoffIndex = Math.Max(0, messages.Count - 10);

        for (var i = 0; i < cutoffIndex; i++)
        {
            var msg = messages[i];
            if (msg.Role == "tool" && msg.Content.Length > 2000)
            {
                var lines = msg.Content.Split('\n');
                if (lines.Length > ToolResultTruncateLines * 2)
                {
                    var truncated = string.Join("\n",
                        lines.Take(ToolResultTruncateLines)
                            .Concat(["… (truncated) …"])
                            .Concat(lines.Skip(lines.Length - ToolResultTruncateLines)));
                    messages[i] = msg with { Content = truncated };
                }
            }
        }
    }

    /// <summary>
    /// Full compact: use LLM to summarize the conversation into a system message.
    /// </summary>
    public async Task FullCompactAsync(
        Guid conversationId,
        List<OllamaChatMessage> messages,
        string model,
        CancellationToken ct)
    {
        if (messages.Count < 4) return;

        // Build a summary request from the older messages
        var messagesToSummarize = messages.Take(messages.Count - 6).ToList();
        if (messagesToSummarize.Count < 2) return;

        var summaryPrompt = BuildCompactPrompt(messagesToSummarize);

        try
        {
            var summaryMessages = new List<OllamaChatMessage>
            {
                new("system", "You are a conversation summarizer. Summarize the following conversation concisely, preserving key decisions, file paths, errors encountered, and current state. Be factual and brief."),
                new("user", summaryPrompt)
            };

            var summary = "";
            await foreach (var token in ollama.StreamChatAsync(model, summaryMessages, ct))
                summary += token;

            if (string.IsNullOrWhiteSpace(summary)) return;

            // Find the compact boundary and replace old messages
            var keepMessages = messages.TakeLast(6).ToList();
            messages.Clear();

            // Insert a system message marking the compact boundary
            messages.Add(new OllamaChatMessage("system",
                $"[CONTEXT COMPACTED — prior conversation summarized]\n\nSummary of earlier conversation:\n{summary}"));
            messages.AddRange(keepMessages);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // If summarization fails, fall back to microcompact
            MicrocompactMessages(messages);
        }
    }

    /// <summary>
    /// Reactive compact: triggered when API returns prompt-too-long error.
    /// More aggressive truncation.
    /// </summary>
    public async Task ReactiveCompactAsync(
        Guid conversationId,
        List<OllamaChatMessage> messages,
        string model,
        CancellationToken ct)
    {
        // First try microcompact
        MicrocompactMessages(messages);

        // If still too large, do full compact
        if (EstimateTotalChars(messages) >= MicrocompactThresholdChars)
        {
            await FullCompactAsync(conversationId, messages, model, ct);
        }

        // If STILL too large, aggressively truncate old messages
        if (EstimateTotalChars(messages) >= FullCompactThresholdChars && messages.Count > 8)
        {
            var keepCount = 4;
            var kept = messages.Skip(messages.Count - keepCount).ToList();
            var summary = BuildQuickSummary(messages.Take(messages.Count - keepCount).ToList());
            messages.Clear();
            messages.Add(new OllamaChatMessage("system",
                $"[CONTEXT COMPACTED — aggressive truncation]\n\nQuick summary:\n{summary}"));
            messages.AddRange(kept);
        }
    }

    private static string BuildCompactPrompt(IReadOnlyList<OllamaChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize this conversation. Include:");
        sb.AppendLine("- Key decisions made");
        sb.AppendLine("- Files created/modified (with paths)");
        sb.AppendLine("- Errors encountered and how they were resolved");
        sb.AppendLine("- Current state of the task");
        sb.AppendLine("- Any pending items");
        sb.AppendLine();
        sb.AppendLine("CONVERSATION:");
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                "system" => "System",
                "user" => "User",
                "assistant" => "Assistant",
                "tool" => $"Tool({msg.ToolName ?? "unknown"})",
                _ => msg.Role
            };
            var content = msg.Content.Length > 1000 ? msg.Content[..1000] + "…" : msg.Content;
            sb.AppendLine($"[{role}]: {content}");
        }
        return sb.ToString();
    }

    private static string BuildQuickSummary(IReadOnlyList<OllamaChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages.Where(m => m.Role == "assistant" || m.Role == "user").TakeLast(6))
        {
            var role = msg.Role == "user" ? "User" : "Assistant";
            var content = msg.Content.Length > 200 ? msg.Content[..200] + "…" : msg.Content;
            sb.AppendLine($"[{role}]: {content}");
        }
        return sb.ToString();
    }

    private static int EstimateTotalChars(IReadOnlyList<OllamaChatMessage> messages) =>
        messages.Sum(m => m.Content.Length);
}
