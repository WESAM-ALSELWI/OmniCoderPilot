using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Infrastructure;

namespace OmniCoderPilot.Application;

// ═══════════════════════════════════════════════════════════════════════════════
// OmniCoderPilot AgentOrchestrator — Self-Healing Autonomous Coding Agent
// Architecture:
//   • Persistent execution loop: plan → execute → validate → fix → repeat
//   • Consecutive-failure recovery: tracks failures, escalates nudges
//   • Tool-fallback chain: native tool-calls → text-based JSON parsing → retry
//   • Strict output discipline: only emits final answer when all steps done
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AgentOrchestrator(
    IDbContextFactory<AppDbContext> dbFactory,
    IOllamaClient ollama,
    IEnumerable<IAgentTool> tools,
    IAgentEventSink sink,
    IProjectUnderstandingService projectUnderstanding,
    IProjectMemoryService projectMemory,
    IMemoryService memory,
    IContextCompressionService compression,
    ITodoService todos,
    PlanModeState planMode,
    IConfiguration config,
    IContextCompactor? compactor = null,
    ISubAgentManager? subAgentManager = null,
    ISkillLoader? skillLoader = null) : IAgentOrchestrator
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools =
        tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    private readonly int _maxTurns = int.TryParse(config["Agent:MaxTurns"], out var t) ? t : 120;
    private readonly int _contextWindow = 8192; // From Ollama config, ~8K tokens for local models

    // ── Enhanced tool orchestration (reference project pattern) ────────────
    private readonly ToolOrchestrator _toolOrchestrator = new(
        tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase));

    // ── Build Ollama tool definitions from registered IAgentTool instances ────
    private IReadOnlyList<OllamaToolDefinition> BuildToolDefinitions() =>
        _tools.Values.Select(t => new OllamaToolDefinition(t.Name, t.Description, t.Schema)).ToList();

    // ═══════════════════════════════════════════════════════════════════════════
    // Main entry point
    // ═══════════════════════════════════════════════════════════════════════════
    public async Task RunTurnAsync(ChatRequest request)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = new AgentTaskRun
        {
            WorkspaceId = request.WorkspaceId,
            ConversationId = request.ConversationId,
            Description = request.Prompt,
            Status = AgentTaskStatus.Running
        };

        db.TaskRuns.Add(run);
        db.Messages.Add(new ChatMessage
        {
            ConversationId = request.ConversationId,
            Role = ChatRole.User,
            Content = request.Prompt,
            TokenEstimate = EstimateTokens(request.Prompt)
        });
        await db.SaveChangesAsync(request.CancellationToken);
        await sink.StatusAsync(request.ConnectionId, run.Id, "Analysing request…", 3);
        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
            "🧠 Reading your request and building workspace context…", "thinking");

        try
        {
            var ws = await db.Workspaces.FindAsync([request.WorkspaceId], request.CancellationToken)
                ?? throw new InvalidOperationException("Workspace not found.");

            // Auto-title conversation
            var conv = await db.Conversations.FindAsync([request.ConversationId], request.CancellationToken);
            if (conv is not null && (conv.Title == "New conversation" || conv.Title == "New chat"))
            {
                conv.Title = request.Prompt.Length <= 60 ? request.Prompt : request.Prompt[..57] + "…";
                conv.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(request.CancellationToken);
            }

            var priorMessages = (await db.Messages
                .Where(x => x.ConversationId == request.ConversationId)
                .ToListAsync(request.CancellationToken))
                .OrderBy(x => x.CreatedAt)
                .Take(200)
                .ToList();

            await sink.StatusAsync(request.ConnectionId, run.Id, "Building context…", 8);

            var projectInstructions = await projectMemory.LoadProjectInstructionsAsync(ws.RootPath, request.CancellationToken);

            // ── File-based memory (Phase 5) ──────────────────────────────────
            var fileMemoryIndex = "";
            try
            {
                var fileMemory = new FileMemoryStore(ws.RootPath);
                fileMemoryIndex = fileMemory.LoadMemoryIndex();
            }
            catch { }

            var context = new ConversationContext(
                await projectUnderstanding.BuildProjectSummaryAsync(request.WorkspaceId, request.CancellationToken),
                await memory.GetRelevantMemoriesAsync(request.WorkspaceId, request.Prompt, request.CancellationToken),
                compression.Compress(priorMessages, 24_000),
                projectInstructions);

            var existingTodos = await todos.GetAsync(request.ConversationId, request.CancellationToken);
            var toolDefs = BuildToolDefinitions();

            // ── Skills system (Phase 7) ──────────────────────────────────────
            var skillsPrompt = "";
            if (skillLoader is not null)
            {
                skillsPrompt = skillLoader.FormatForSystemPrompt();
            }

            // Build message history
            var messages = BuildInitialMessages(ws.RootPath, context, priorMessages, existingTodos, skillsPrompt, fileMemoryIndex);

            // ── Context compaction (Phase 3) ─────────────────────────────────
            if (compactor is not null)
            {
                var compacted = await compactor.CompactIfNeededAsync(
                    request.ConversationId, messages, request.Model ?? "", request.CancellationToken);
                if (compacted)
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        "🗜️ Context compacted to free space", "info");
            }

            // ── Execution state tracking (enhanced with AgentState) ──────
            var state = new AgentState { MaxTurns = _maxTurns };
            var finalAnswer = new StringBuilder();
            var allToolResults = new List<ToolResult>();
            var turnStart = System.Diagnostics.Stopwatch.StartNew();
            StopHookExecutor? stopHooks = null;

            // ═══════════════════════════════════════════════════════════════════
            // PERSISTENT EXECUTION LOOP — state-machine driven
            // Based on reference project's query loop architecture:
            // - Observe → Think → Act → Verify phases
            // - Reactive compaction on prompt-too-long
            // - Enhanced error recovery with error withholding
            // - Stop hooks for post-turn execution
            // ═══════════════════════════════════════════════════════════════════
            while (!state.ShouldStop())
            {
                state.RecordTurn();
                request.CancellationToken.ThrowIfCancellationRequested();

                var pct = Math.Min(10 + state.TurnCount * 2, 90);
                await sink.StatusAsync(request.ConnectionId, run.Id,
                    $"🤖 Turn {state.TurnCount}/{state.MaxTurns} — Agent thinking…", pct);

                // ── Try native tool calling first ─────────────────────────────
                var llmStart = System.Diagnostics.Stopwatch.StartNew();
                OllamaToolResponse response;
                var textContent = "";
                try
                {
                    // Stream response — collect content text and native tool_calls
                    var fullResponse = new StringBuilder();
                    var nativeToolCalls = new List<OllamaToolCall>();

                    await foreach (var chunk in ollama.StreamChatWithToolsAsync(
                        request.Model ?? "", messages, toolDefs, request.CancellationToken))
                    {
                        // Accumulate content text
                        if (!string.IsNullOrEmpty(chunk.Content))
                            fullResponse.Append(chunk.Content);

                        // Collect native tool_calls from streaming response
                        if (chunk.ToolCalls is not null && chunk.ToolCalls.Count > 0)
                            nativeToolCalls.AddRange(chunk.ToolCalls);
                    }

                    // Build final response: prefer native tool_calls, fall back to text parsing
                    textContent = fullResponse.ToString();
                    var finalToolCalls = nativeToolCalls.Count > 0
                        ? nativeToolCalls
                        : ParseToolCallsFromText(textContent)
                            .Select(p => new OllamaToolCall(Guid.NewGuid().ToString(), p.Name, p.Args))
                            .ToList();

                    response = new OllamaToolResponse(textContent, finalToolCalls);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // ── Error recovery cascade (reference project pattern) ────────
                    // Withhold prompt-too-long and max-output-tokens errors
                    // and attempt recovery before failing the turn.
                    var isPromptTooLong = ex.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
                                          ex.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase);
                    var isMaxOutputTokens = ex.Message.Contains("max output tokens", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("stop_reason:max_tokens", StringComparison.OrdinalIgnoreCase);

                    if (isPromptTooLong)
                    {
                        state.WithheldPromptTooLong = true;
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            "⚠️ Prompt too long — attempting recovery (context collapse + reactive compact)…", "warning");

                        // Recovery cascade: context collapse drain → reactive compact → fail
                        if (compactor is not null)
                        {
                            await compactor.ReactiveCompactAsync(
                                request.ConversationId, messages, request.Model ?? "", request.CancellationToken);
                            state.HasAttemptedReactiveCompact = true;
                            state.RecordCompactionSuccess();
                            state.LastTransition = ContinueReason.ReactiveCompactRetry;
                            messages.Add(new OllamaChatMessage("user",
                                "[SYSTEM: Context has been compacted to fit within limits. Please continue.]"));
                            continue;
                        }
                    }
                    else if (isMaxOutputTokens && state.CanRecoverMaxOutputTokens())
                    {
                        state.WithheldMaxOutputTokens = true;
                        state.MaxOutputTokensRecoveryCount++;
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            $"⚠️ Max output tokens hit — attempting recovery (attempt {state.MaxOutputTokensRecoveryCount}/3)…", "warning");

                        // Recovery: inject "continue where you left off" message
                        messages.Add(new OllamaChatMessage("user",
                            "[SYSTEM: Your response was cut off. Please continue exactly where you left off. Do not repeat any content.]"));
                        state.LastTransition = ContinueReason.MaxOutputTokensRecovery;
                        continue;
                    }

                    // Standard error recovery for other errors
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        $"⚠️ API error ({ex.Message}) — falling back to text parsing…", "warning");

                    try
                    {
                        response = await FallbackToTextParsingAsync(
                            request.Model ?? "", messages, request.CancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception fallbackEx)
                    {
                        // Even fallback failed — inject a recovery message and continue
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            $"🔄 LLM call failed ({fallbackEx.Message}) — retrying…", "warning");
                        messages.Add(new OllamaChatMessage("user",
                            $"[SYSTEM: LLM call failed with error: {fallbackEx.Message}. Please try again with a simpler tool call.]"));
                        state.ConsecutiveFailures++;
                        state.LastTransition = ContinueReason.ErrorRecovery;
                        continue;
                    }
                }
                llmStart.Stop();
                var toolCalls = response.ToolCalls ?? [];

                // Strip <think> from visible text
                var visibleText = StripThinking(textContent, out var thinking);

                // ALWAYS send thinking/reasoning as inline collapsible step
                // Use actual <think> content if present, otherwise use the visible text as thinking
                var thinkingToShow = !string.IsNullOrWhiteSpace(thinking)
                    ? Trim(thinking, 1200)
                    : Trim(visibleText, 1200);
                if (!string.IsNullOrWhiteSpace(thinkingToShow))
                    await sink.ThinkingStepAsync(request.ConnectionId, request.ConversationId,
                        thinkingToShow, (int)llmStart.ElapsedMilliseconds, state.TurnCount, AgentLoopPhase.Think);

                // ── Fallback: parse tool calls from text if native returned none ──
                if (toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(visibleText))
                {
                    var parsed = ParseToolCallsFromText(visibleText).ToList();
                    if (parsed.Count > 0)
                    {
                        toolCalls = parsed.Select(p => new OllamaToolCall(
                            Guid.NewGuid().ToString(), p.Name, p.Args)).ToList();
                    }
                }

                // ── No tool calls path ────────────────────────────────────────
                if (toolCalls.Count == 0)
                {
                    state.ConsecutiveNoTools++;

                    // If the task looks like it needs workspace actions but no tools called yet
                    if (!state.ExecutedAnyTool && LooksLikeWorkspaceTask(request.Prompt) && state.NoToolRedirects < 3)
                    {
                        state.NoToolRedirects++;
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            $"↩️ No tools called yet — redirecting to workspace actions (attempt {state.NoToolRedirects})…", "redirect");
                        messages.Add(new OllamaChatMessage("assistant", textContent));
                        messages.Add(new OllamaChatMessage("user",
                            BuildNoToolCorrectionPrompt(request.Prompt, false, state.NoToolRedirects)));
                        state.LastTransition = ContinueReason.NoToolRedirect;
                        continue;
                    }

                    // If we've done tools and the agent is now writing a final answer
                    if (state.ExecutedAnyTool && state.ConsecutiveNoTools <= 2)
                    {
                        // Accept this as the final answer — but only if it seems substantial
                        var final = visibleText.Trim();
                        if (string.IsNullOrWhiteSpace(final))
                        {
                            final = BuildDeterministicFinalSummary(allToolResults, request.Prompt);
                        }

                        finalAnswer.Append(final);
                        await sink.TokenAsync(request.ConnectionId, request.ConversationId, final);
                        break;
                    }

                    // Still no tools and not a final answer — force tool usage
                    if (state.ConsecutiveNoTools > 2 || !state.ExecutedAnyTool)
                    {
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            $"↩️ Forcing tool usage (no-tool streak: {state.ConsecutiveNoTools})…", "redirect");
                        messages.Add(new OllamaChatMessage("assistant", textContent));
                        messages.Add(new OllamaChatMessage("user",
                            BuildNoToolCorrectionPrompt(request.Prompt, state.ExecutedAnyTool, state.ConsecutiveNoTools)));
                        state.NoToolRedirects++;
                        if (state.NoToolRedirects >= 5)
                        {
                            // Emergency: just accept whatever text we have as final
                            var emergency = string.IsNullOrWhiteSpace(visibleText)
                                ? BuildDeterministicFinalSummary(allToolResults, request.Prompt)
                                : visibleText.Trim();
                            finalAnswer.Append(emergency);
                            await sink.TokenAsync(request.ConnectionId, request.ConversationId, emergency);
                            break;
                        }
                        state.LastTransition = ContinueReason.NoToolRedirect;
                        continue;
                    }
                }

                // Reset no-tool counter since we have tool calls
                state.ConsecutiveNoTools = 0;

                // ── Describe what agent is about to do ────────────────────────
                var toolDesc = DescribeToolBatch(toolCalls);
                await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                    $"⚡ {toolDesc}", "tool-dispatch");
                await sink.StatusAsync(request.ConnectionId, run.Id,
                    $"Executing {toolCalls.Count} tool(s)…", pct + 1);

                // Add assistant turn to history (with ToolCalls for native tool response validation)
                messages.Add(new OllamaChatMessage("assistant", textContent, ToolCalls: toolCalls));

                // ── Execute tools ─────────────────────────────────────────────
                var toolResults = await ExecuteToolCallsAsync(
                    toolCalls, ws.RootPath, request,
                    planMode.IsActive(request.ConversationId));

                allToolResults.AddRange(toolResults);

                // ── Send grouped tool step events (Claude Code style) ─────
                var toolGroups = BuildToolStepGroups(toolCalls, toolResults, state.TurnCount);
                await sink.ToolStepsBatchAsync(request.ConnectionId, request.ConversationId, toolGroups);

                // ── Track failures and EditFile retries ────────────────────────
                var failedThisTurn = toolResults.Where(r => !r.Success).ToList();
                var succeededThisTurn = toolResults.Where(r => r.Success).ToList();

                // Track EditFile retries for each failed edit
                foreach (var (call, result) in toolCalls.Zip(toolResults).Where(x => x.Item1.Name == "EditFile" && !x.Item2.Success))
                {
                    var filePath = GetArg(call.Arguments, "relativePath") ?? "unknown";
                    var shouldForceDifferent = state.RecordEditFileAttempt(filePath);

                    if (shouldForceDifferent)
                    {
                        await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                            $"🔄 EditFile loop detected on {filePath} — forcing different approach", "warning");
                    }
                }

                // Reset retry tracking when moving to a different file
                if (toolCalls.Count > 0)
                {
                    var lastFilePath = GetArg(toolCalls.Last().Arguments, "relativePath");
                    if (lastFilePath is not null)
                        state.ResetEditFileRetriesIfChanged(lastFilePath);
                }

                if (failedThisTurn.Count > 0)
                {
                    state.ConsecutiveFailures++;
                    state.TotalToolFailures += failedThisTurn.Count;
                }
                else
                {
                    state.ConsecutiveFailures = 0; // Reset on any success
                }

                state.ExecutedAnyTool = true;

                // Add tool results to Ollama message history
                foreach (var (call, result) in toolCalls.Zip(toolResults))
                {
                    messages.Add(new OllamaChatMessage("tool", result.Content, call.Id, call.Name));
                }

                // Persist tool results to DB
                foreach (var result in toolResults)
                {
                    db.Messages.Add(new ChatMessage
                    {
                        ConversationId = request.ConversationId,
                        Role = ChatRole.Tool,
                        Content = Trim(result.Content, 4_000),
                        MetadataJson = $"{{\"tool\":\"{result.ToolName}\",\"success\":{result.Success.ToString().ToLower()}}}"
                    });
                }
                await db.SaveChangesAsync(request.CancellationToken);

                // ── Reactive compaction check ─────────────────────────────
                var estimatedTokens = EstimateTokens(messages);
                if (compactor is not null && state.ShouldReactiveCompact(estimatedTokens, _contextWindow))
                {
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        "🗜️ Reactive compaction triggered (approaching context limit)…", "info");
                    await compactor.ReactiveCompactAsync(
                        request.ConversationId, messages, request.Model ?? "", request.CancellationToken);
                    state.HasAttemptedReactiveCompact = true;
                    state.RecordCompactionSuccess();
                    state.LastTransition = ContinueReason.ReactiveCompactRetry;
                }

                // ── Auto-compaction check ─────────────────────────────────
                if (compactor is not null && state.ShouldAutoCompact(estimatedTokens, _contextWindow))
                {
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        "🗜️ Auto-compacting context to free space…", "info");
                    await compactor.CompactIfNeededAsync(
                        request.ConversationId, messages, request.Model ?? "", request.CancellationToken);
                    state.RecordCompactionSuccess();
                }

                // ── Stop hooks execution ──────────────────────────────────
                if (stopHooks is null)
                {
                    stopHooks = new StopHookExecutor(sink, request.ConnectionId, request.ConversationId);
                }
                state.StopHookActive = true;
                var hookResult = await stopHooks.ExecuteAsync(messages, toolResults, state, request.CancellationToken);
                state.StopHookActive = false;

                // Inject blocking errors from stop hooks
                if (hookResult.BlockingErrors.Count > 0)
                {
                    state.StopHookBlockingErrors.AddRange(hookResult.BlockingErrors);
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        $"🔍 Reviewing {hookResult.BlockingErrors.Count} issue(s) found — addressing them…", "info");
                }

                // ── Choose next nudge based on failure state ──────────────────
                string nudge;
                if (state.IsInEditFileLoop && state.StuckOnFile is not null)
                {
                    // EditFile loop override — force WriteFile approach
                    var retryCount = state.EditFileRetryCounts.GetValueOrDefault(state.StuckOnFile.Replace('\\', '/').ToLowerInvariant(), 0);
                    nudge = $"🔄 EDIT FILE LOOP DETECTED: You've tried to edit {state.StuckOnFile} {retryCount} times with EditFile and failed.\n" +
                            $"MANDATORY: You MUST use WriteFile instead of EditFile for this file.\n" +
                            $"1. Use ReadFile to read the ENTIRE file content\n" +
                            $"2. Modify the content in your response (make the desired changes)\n" +
                            $"3. Use WriteFile with the COMPLETE modified content to replace the file\n" +
                            $"Do NOT use EditFile again for this file. Use WriteFile now.";

                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        $"↩️ Switching edit strategy for {Path.GetFileName(state.StuckOnFile)}…", "info");
                    state.LastTransition = ContinueReason.ErrorRecovery;
                }
                else if (state.ConsecutiveFailures >= 4)
                {
                    // Deep failure — give the agent a full strategy reset
                    nudge = BuildStrategyResetPrompt(request.Prompt, failedThisTurn, state.ConsecutiveFailures);
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        $"🧠 Switching approach — trying a different strategy…", "info");
                    state.ConsecutiveFailures = 0; // Give it a fresh chance after the reset
                    state.LastTransition = ContinueReason.StrategyReset;
                }
                else if (failedThisTurn.Count > 0)
                {
                    // Some failures — inject targeted error recovery prompt
                    nudge = BuildErrorRecoveryPrompt(request.Prompt, failedThisTurn, succeededThisTurn, state.ConsecutiveFailures, state);
                    await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                        $"🔧 Fixing {failedThisTurn.Count} issue(s) — retrying with corrections…", "info");
                    state.LastTransition = ContinueReason.ErrorRecovery;
                }
                else if (state.StopHookBlockingErrors.Count > 0)
                {
                    // Stop hook blocking errors
                    nudge = $"Stop hook detected issues:\n{string.Join("\n", state.StopHookBlockingErrors)}\n\nPlease address these before continuing.";
                    state.StopHookBlockingErrors.Clear();
                    state.LastTransition = ContinueReason.StopHookBlocking;
                }
                else
                {
                    // All tools succeeded — standard continuation
                    nudge = BuildContinuationNudge(request.Prompt, toolResults, state.TurnCount, state.MaxTurns);
                    state.LastTransition = ContinueReason.NextTurn;
                }

                messages.Add(new OllamaChatMessage("user", nudge));
            }

            // ── Post-loop: ensure we have a final answer ──────────────────────
            var answer = finalAnswer.ToString();
            if (string.IsNullOrWhiteSpace(answer))
            {
                await sink.StatusAsync(request.ConnectionId, run.Id, "Generating completion summary…", 96);
                answer = BuildDeterministicFinalSummary(allToolResults, request.Prompt);
                await sink.TokenAsync(request.ConnectionId, request.ConversationId, answer);
            }

            db.Messages.Add(new ChatMessage
            {
                ConversationId = request.ConversationId,
                Role = ChatRole.Assistant,
                Content = answer,
                TokenEstimate = EstimateTokens(answer)
            });
            await memory.CaptureTurnAsync(request.WorkspaceId, request.Prompt, answer, request.CancellationToken);

            if (conv is not null)
                conv.UpdatedAt = DateTimeOffset.UtcNow;

            run.Status = AgentTaskStatus.Completed;
            run.ProgressPercent = 100;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(request.CancellationToken);

            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                "✅ Task completed", "completed");
            await sink.StatusAsync(request.ConnectionId, run.Id, "completed", 100);
        }
        catch (OperationCanceledException)
        {
            run.Status = AgentTaskStatus.Failed;
            run.Error = "Task was cancelled.";
            await db.SaveChangesAsync(CancellationToken.None);
            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId, "🛑 Task cancelled", "error");
            await sink.StatusAsync(request.ConnectionId, run.Id, "completed", 100);
        }
        catch (Exception ex)
        {
            run.Status = AgentTaskStatus.Failed;
            run.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                $"❌ Fatal error: {ex.Message}", "error");
            await sink.ErrorAsync(request.ConnectionId, ex.Message);
            await sink.StatusAsync(request.ConnectionId, run.Id, "completed", 100);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Tool execution — parallel reads, sequential writes, full error capture
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<IReadOnlyList<ToolResult>> ExecuteToolCallsAsync(
        IReadOnlyList<OllamaToolCall> calls,
        string root,
        ChatRequest request,
        bool isPlanMode)
    {
        var results = new ToolResult[calls.Count];
        var execCtx = new ToolExecutionContext(
            request.WorkspaceId, request.ConversationId, root,
            ToolPermissionMode.WorkspaceWrite, isPlanMode);

        var readOnlyIndices = calls
            .Select((c, i) => _tools.TryGetValue(c.Name, out var t) && t.IsReadOnly ? i : -1)
            .Where(i => i >= 0).ToList();
        var writeIndices = calls
            .Select((c, i) => !_tools.TryGetValue(c.Name, out var t) || !t.IsReadOnly ? i : -1)
            .Where(i => i >= 0).ToList();

        // Read-only in parallel
        if (readOnlyIndices.Count > 0)
        {
            await Task.WhenAll(readOnlyIndices.Select(async idx =>
            {
                results[idx] = await RunSingleToolAsync(calls[idx], execCtx, request);
            }));
        }

        // Write tools sequentially
        foreach (var idx in writeIndices)
        {
            results[idx] = await RunSingleToolAsync(calls[idx], execCtx, request);
        }

        return results;
    }

    private async Task<ToolResult> RunSingleToolAsync(
        OllamaToolCall call, ToolExecutionContext ctx, ChatRequest request)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            var available = string.Join(", ", _tools.Keys.OrderBy(k => k));
            var unknown = new ToolResult(call.Name, false,
                $"ERROR: Unknown tool '{call.Name}'. Available tools: {available}");
            await sink.ToolAsync(request.ConnectionId, call.Name, "failed", unknown.Content, "❓");
            return unknown;
        }

        // Plan mode: write/execute tools only describe, don't act
        if (ctx.IsPlanMode && !tool.IsReadOnly)
        {
            var planText = $"[PLAN] Would call {tool.Name} with: {call.Arguments.ToJsonString()}";
            await sink.ToolAsync(request.ConnectionId, tool.Name, "planned", planText);
            return new ToolResult(tool.Name, true, planText);
        }

        // Permission check for dangerous tools
        if (tool.IsDangerous && !ctx.IsPlanMode)
        {
            var permReq = new PermissionRequest(
                Guid.NewGuid().ToString(), tool.Name,
                $"Allow {tool.Name}?",
                Trim(call.Arguments.ToJsonString(), 500));

            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                $"🔐 Requesting permission for {tool.Name}…", "permission");
            var approved = await sink.RequestPermissionAsync(request.ConnectionId, permReq);

            if (!approved)
            {
                var denied = new ToolResult(tool.Name, false,
                    $"Permission denied for {tool.Name}. Choose a different approach that doesn't require this operation.");
                await sink.ToolAsync(request.ConnectionId, tool.Name, "denied", denied.Content);
                return denied;
            }
        }

        await sink.ToolAsync(request.ConnectionId, tool.Name, "running", Trim(call.Arguments.ToJsonString(), 500));
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(call.Arguments, ctx, request.CancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ToolResult(tool.Name, false,
                $"TOOL EXCEPTION in {tool.Name}: {ex.GetType().Name}: {ex.Message}");
        }

        await sink.ToolAsync(request.ConnectionId, tool.Name,
            result.Success ? "completed" : "failed",
            Trim(result.Content, 2_000));

        if (result.Success)
            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                DescribeSingleToolResult(tool.Name, call.Arguments, result), "tool-result");
        else
            await sink.AgentActivityAsync(request.ConnectionId, request.ConversationId,
                $"⚠️ {tool.Name} failed: {Trim(result.Content, 1_500)}", "tool-failed");

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Fallback: plain stream + text-based JSON parse
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<OllamaToolResponse> FallbackToTextParsingAsync(
        string model,
        List<OllamaChatMessage> messages,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var token in ollama.StreamChatAsync(model, messages, ct))
            sb.Append(token);
        var text = sb.ToString();
        var calls = ParseToolCallsFromText(text)
            .Select(p => new OllamaToolCall(Guid.NewGuid().ToString(), p.Name, p.Args))
            .ToList();
        return new OllamaToolResponse(text, calls);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Message building
    // ═══════════════════════════════════════════════════════════════════════════

    private List<OllamaChatMessage> BuildInitialMessages(
        string root,
        ConversationContext ctx,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<TodoItem> existingTodos,
        string skillsPrompt = "",
        string fileMemoryIndex = "")
    {
        var system = BuildSystemPrompt(root, ctx, existingTodos, skillsPrompt, fileMemoryIndex);
        var messages = new List<OllamaChatMessage> { new("system", system) };

        // Include recent non-tool history (tool messages are already in system compressed history)
        foreach (var m in history.TakeLast(50).Where(m => m.Role != ChatRole.Tool))
            messages.Add(new OllamaChatMessage(RoleName(m.Role), m.Content));

        return messages;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRODUCTION-GRADE SYSTEM PROMPT
    // ═══════════════════════════════════════════════════════════════════════════

    private string BuildSystemPrompt(string root, ConversationContext ctx, IReadOnlyList<TodoItem> existingTodos,
        string skillsPrompt = "", string fileMemoryIndex = "")
    {
        var todoSection = existingTodos.Count > 0
            ? "Current task list:\n" + string.Join("\n", existingTodos.Select(t =>
                $"  [{t.Status}] {t.Content}"))
            : "(no active tasks)";

        var toolList = string.Join("\n", _tools.Values.Select(t =>
            $"  • **{t.Name}**: {t.Description}"));

        var parts = new List<string>
        {
            // ── Identity ──────────────────────────────────────────────────────
            $"""
            # IDENTITY
            You are **OmniCoderPilot** — an autonomous, self-healing AI software engineer embedded directly in the developer's machine.
            You operate entirely locally via Ollama (no external cloud APIs).
            Workspace root: `{root}`

            You have full access to:
            - Local filesystem (read, write, create, delete, rename files and directories)
            - PowerShell terminal (execute commands, build projects, run tests)
            - Web (fetch URLs, search the web for documentation)
            - Task list management (TodoWrite / TodoRead)

            You are capable of completing complex, multi-step software engineering tasks **autonomously** —
            planning, building, testing, fixing errors, and verifying outcomes — without stopping.
            """,

            // ── Core laws ─────────────────────────────────────────────────────
            """
            # CORE LAWS — NEVER VIOLATE THESE

            **LAW 1 — NEVER STOP ON ERROR**
            If a tool fails, a build breaks, or a command errors: do NOT stop, do NOT apologize and quit.
            Analyze the error output, fix the root cause, and retry. Every error is just information.

            **LAW 2 — ALWAYS PLAN FIRST**
            Before writing any code or executing any command, use `TodoWrite` to list ALL steps.
            Never attempt everything at once. Execute one step at a time.

            **LAW 3 — VERIFY BEFORE REPORTING DONE**
            Never claim "done" unless you have actually executed and verified the outcome.
            Run the build. Check the output. Fix errors. Only report completion after verification.

            **LAW 4 — NEVER LEAVE STUBS**
            Every file you write must be complete and runnable. No `// TODO`, no `pass`, no placeholder bodies.

            **LAW 5 — READ BEFORE EDIT**
            Always use `ReadFile` to get the exact current content before using `EditFile`.
            Use `EditFile` (not `WriteFile`) for targeted changes to preserve surrounding code.

            **LAW 6 — STAY IN WORKSPACE**
            Never access, read, or write paths outside `{root}`.

            **LAW 7 — PROFESSIONAL STANDARDS**
            - Always use proper error handling (try/catch, null checks)
            - Follow language-specific conventions (C# naming, Java conventions, etc.)
            - Include ALL necessary imports/usings at the top of files
            - Write self-documenting code with clear variable names
            - Handle edge cases (null, empty, invalid input)
            """,

            // ── Thinking protocol ─────────────────────────────────────────────
            """
            # THINKING PROTOCOL
            At the start of EVERY turn, write a short <think>...</think> note (1-3 sentences) describing:
            - What you just observed from tool results
            - What the current state is (success / error / partial)
            - What you are about to do next and why

            Examples of GOOD think notes:
            <think>The build failed with CS0246: missing namespace. I need to add `using System.Text.Json` to Controllers/ApiController.cs.</think>
            <think>ReadFile shows the function on line 42 is missing the return type. I'll use EditFile to fix line 42.</think>
            <think>The npm install succeeded. Next I'll run npm run build to verify the frontend compiles.</think>
            <think>EditFile failed because the target string wasn't found. I need to ReadFile first to get the exact whitespace.</think>
            """,

            // ── Execution workflow ────────────────────────────────────────────
            """
            # AUTONOMOUS EXECUTION WORKFLOW

            ## PHASE 1 — UNDERSTAND THE REQUEST
            Before planning, fully understand what the user wants:
            - What type of project? (console, web, library, etc.)
            - What language/framework? (.NET, Node, Python, etc.)
            - What features/requirements?
            - Any specific patterns or conventions to follow?

            ## PHASE 2 — PLAN (always first)
            Call `TodoWrite` with ALL concrete steps before touching any file.
            Break large tasks into small verifiable steps. Include: explore, create, build, test, fix, verify steps.

            Example plan for "build a .NET console app":
              1. [pending] Explore workspace structure
              2. [pending] Create Project.csproj with correct dependencies
              3. [pending] Create Models/ folder and data classes
              4. [pending] Create Services/ folder and business logic
              5. [pending] Create Program.cs with entry point
              6. [pending] Run: dotnet restore && dotnet build
              7. [pending] Fix any build errors (repeat until success)
              8. [pending] Run: dotnet run (verify app starts)
              9. [pending] Report what was built with usage instructions

            ## PHASE 3 — EXPLORE
            Use `ListDirectory`, `SearchFiles`, `GrepText`, `ReadFile` to understand the codebase.
            Never assume file contents — verify with ReadFile before editing.
            Check what already exists before creating new files.

            ## PHASE 4 — CREATE (step by step, dependency order)
            **For .NET projects:**
            1. Create .csproj file FIRST (defines dependencies)
            2. Create model/entity classes
            3. Create service interfaces
            4. Create service implementations
            5. Create controllers/entry points
            6. Create configuration files (appsettings.json, etc.)

            **For Node.js projects:**
            1. Create package.json FIRST (defines dependencies)
            2. Run: npm install
            3. Create source files in dependency order
            4. Create configuration files

            **For Python projects:**
            1. Create requirements.txt or pyproject.toml
            2. Install dependencies
            3. Create source files
            4. Create __init__.py files if needed

            **CRITICAL RULES FOR FILE CREATION:**
            - Always include ALL imports/usings at the top
            - Write complete, runnable code (no placeholders)
            - Follow language naming conventions (PascalCase for C#, camelCase for JS)
            - Include proper error handling (try/catch, null checks)
            - Add XML documentation for public APIs (C#)

            ## PHASE 5 — BUILD AND FIX (CRITICAL — repeat until success)
            After every code change, run the appropriate build/test command:

            | Stack    | Command |
            |----------|---------|
            | .NET     | `dotnet restore && dotnet build` |
            | Node.js  | `npm install && npm run build` |
            | Python   | `python -m py_compile <file>.py` |
            | Go       | `go build ./...` |
            | Rust     | `cargo build` |

            **When a build fails — MANDATORY recovery protocol:**
            1. Read the FULL error output from the command result
            2. Identify the EXACT file and line number in the **error** (NOT warnings)
            3. Use `ReadFile` on that file to see the current state
            4. Use `EditFile` to fix ONLY the broken part (minimal safe change)
            5. Run the build again immediately
            6. Repeat steps 1-5 until exit code = 0
            7. MAX 5 fix attempts per error — if still failing, try a different approach

            **ERROR PATTERN FIXES:**
            - `CS0246: type not found` → Add missing `using` statement
            - `CS0103: name not found` → Check spelling, add using, or create the method
            - `CS0029: cannot convert type` → Fix type mismatch or add conversion
            - `CS0117: does not contain definition` → Method/property doesn't exist, check API
            - `NU1101: package not found` → Check package name, run `dotnet restore`
            - `Build FAILED` → Read the FIRST error, fix it, rebuild

            **IMPORTANT — WARNINGS ARE OK:**
            - `warning NU1903` = NuGet package vulnerability warning — IGNORE these, they don't break the build
            - `warning CS...` = C# compiler warnings — usually OK, build still succeeds
            - Only fix **errors** (lines containing `: error `), NEVER fix warnings
            - If the build says "Build succeeded" with warnings — the build PASSED, move on
            - If `Exit code: 0` — the command succeeded, even if there are warnings

            **NEVER:**
            - Skip the build after writing code
            - Give up after one build failure
            - Rewrite an entire file when only one line is broken
            - Assume the build passed without running it
            - Try to fix warnings — they are informational only

            ## PHASE 5 — VERIFY
            After a successful build:
            - Optionally start the app and check a key output
            - Confirm the actual files created/modified
            - Confirm the task is truly complete

            ## PHASE 6 — REPORT
            Only after verification:
            - Mark all todo items as `done`
            - Write a clear summary of: what was built, files created/changed, how to run it
            - Include any important commands (e.g., `dotnet run`, `npm start`)

            ## DOCUMENT CREATION — MANDATORY RULE
            When the user asks you to CREATE, BUILD, or GENERATE a document:
            - You MUST use your tools to create an ACTUAL FILE in the workspace — never just output text
            - Word (.docx): use CreateWordDocument with sections (heading + content arrays)
            - Excel (.xlsx): use CreateExcelDocument with sheets (name + headers + rows)
            - PowerPoint (.pptx): use CreatePowerPointDocument with slides (title + content bullets)
            - ALWAYS create the actual document file — NEVER tell the user to convert manually
            - ALWAYS tell the user the exact path to the created file
            """,

            // ── Error recovery patterns ───────────────────────────────────────
            """
            # ERROR RECOVERY PATTERNS

            ## FILE OPERATIONS
            **EditFile "text not found" error:**
            → ReadFile to get exact current content → verify whitespace/indentation → retry EditFile with exact match
            → If still failing, use WriteFile to rewrite the entire file

            **ReadFile/EditFile "Access to path is denied":**
            → You specified a DIRECTORY, not a file. Use ListDirectory to browse → then specify a file path

            **ReadFile/EditFile "File not found":**
            → Use ListDirectory or SearchFiles to locate the file → use the correct path
            → Check for typos in the filename

            **WriteFile fails:**
            → Check if directory exists → use CreateDirectory first → retry WriteFile

            ## BUILD ERRORS (.NET)
            **CS0246 / type or namespace not found:**
            → Add missing `using` statement at the top of the file
            → Example: `using System.Text.Json;`

            **CS0103 / name does not exist:**
            → Check spelling → add using → or create the method/class

            **CS0029 / cannot implicitly convert type:**
            → Fix type mismatch → add explicit cast → or use correct type

            **CS0117 / does not contain a definition for:**
            → Method/property doesn't exist → check API documentation → verify spelling

            **CS0122 / inaccessible due to protection level:**
            → Change access modifier → or add public property/method

            **CS1501 / not all code paths return a value:**
            → Add return statement → or change return type to void

            **CS1061 / does not contain a definition for (interface):**
            → Class doesn't implement interface method → add the missing implementation

            ## BUILD ERRORS (Node.js)
            **npm install fails:**
            → Check package.json with ReadFile → fix dependency versions → retry

            **Module not found:**
            → Run: npm install → check import path → verify package.json

            **TypeScript errors:**
            → Read the error → fix the type issue → check tsconfig.json

            ## COMMAND ERRORS
            **PowerShell "not recognized":**
            → The command may not be installed → use WebSearch to find installation → install → retry

            **"Permission denied":**
            → Try a different approach → do not retry the same denied operation repeatedly

            **"Path not found" / directory access denied:**
            → You specified a directory instead of a file → use ListDirectory → specify a file

            ## GENERAL STRATEGY
            **Any other error:**
            → Read the error message carefully → search the web for the solution → apply the fix → retry
            → If stuck after 3 attempts → try a completely different approach
            → If still stuck → report what you tried and ask for guidance
            """,

            // ── Available tools ───────────────────────────────────────────────
            $"""
            # AVAILABLE TOOLS
            {toolList}
            """,

            // ── Project structure rules ──────────────────────────────────────
            """
            # PROJECT STRUCTURE RULES — CRITICAL

            ## .NET Solution Structure
            When a workspace contains a `.sln` file, the source files go in the **project subfolder**, NOT the root.

            Example:
            ```
            AML_AND_CFT_COMMITTEE/           ← workspace root (contains .sln)
            ├── AML_AND_CFT_COMMITTEE.sln    ← solution file
            └── AML_AND_CFT_COMMITTEE/       ← project subfolder (contains .csproj)
                ├── AML_AND_CFT_COMMITTEE.csproj
                ├── Forms/                    ← source files go HERE
                │   └── MainForm.cs
                ├── Models/
                └── Program.cs
            ```

            **RULE:** Before creating files, ALWAYS:
            1. Use `ListDirectory` on workspace root to find the `.sln` file
            2. Use `ListDirectory` on the project subfolder to find the `.csproj`
            3. Create source files INSIDE the project subfolder (where .csproj is)

            **WRONG:** `AML_AND_CFT_COMMITTEE/Forms/MainForm.cs` (root level)
            **CORRECT:** `AML_AND_CFT_COMMITTEE/AML_AND_CFT_COMMITTEE/Forms/MainForm.cs` (inside project)

            ## Node.js Structure
            Source files go in `src/` folder, not root.

            ## General Rule
            Always check where the `.csproj`, `package.json`, or `pyproject.toml` is located.
            Create source files in the SAME directory as the project file, not at workspace root.
            """,

            // ── Professional coding standards ─────────────────────────────────
            """
            # PROFESSIONAL CODING STANDARDS

            ## C# / .NET
            - Use PascalCase for public members, _camelCase for private fields
            - Include XML documentation for public APIs
            - Use nullable reference types (?) where appropriate
            - Always include using statements at the top
            - Follow dependency injection patterns
            - Use async/await for I/O operations

            ## JavaScript / TypeScript
            - Use camelCase for variables and functions
            - Use PascalCase for classes and constructors
            - Use const by default, let when reassignment needed
            - Always use semicolons
            - Use async/await instead of .then()
            - Use strict equality (===)

            ## Python
            - Use snake_case for functions and variables
            - Use PascalCase for classes
            - Include type hints
            - Follow PEP 8 style guide
            - Use virtual environments

            ## GENERAL
            - Write self-documenting code with clear names
            - Handle all error cases (null, empty, invalid)
            - Avoid magic numbers — use named constants
            - Keep functions small and focused
            - Single responsibility principle
            """,

            // ── Project context ───────────────────────────────────────────────
            $"""
            # PROJECT CONTEXT
            {ctx.ProjectSummary}
            """,

            // ── Current task list ─────────────────────────────────────────────
            $"""
            # CURRENT TASKS
            {todoSection}
            """,
        };

        // Memory
        if (!string.IsNullOrWhiteSpace(ctx.MemorySummary))
            parts.Add($"# RELEVANT MEMORY\n{ctx.MemorySummary}");

        // File-based memory (Phase 5)
        if (!string.IsNullOrWhiteSpace(fileMemoryIndex))
            parts.Add($"# PROJECT MEMORY\n{fileMemoryIndex}");

        // Skills (Phase 7)
        if (!string.IsNullOrWhiteSpace(skillsPrompt))
            parts.Add(skillsPrompt);

        // Project-specific instructions (CLAUDE.md / AGENTS.md / .omnicoderpilot.md)
        if (!string.IsNullOrWhiteSpace(ctx.ProjectInstructions))
            parts.Add($"# PROJECT INSTRUCTIONS\n{ctx.ProjectInstructions}");

        // Compressed prior conversation
        if (!string.IsNullOrWhiteSpace(ctx.CompressedHistory))
            parts.Add($"# PRIOR CONVERSATION (summary)\n{ctx.CompressedHistory}");

        return string.Join("\n\n", parts);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Nudge prompt builders — the heart of self-healing behavior
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard continuation nudge after all tools succeed.
    /// </summary>
    private static string BuildContinuationNudge(
        string originalPrompt,
        IReadOnlyList<ToolResult> results,
        int turn, int maxTurns)
    {
        var successList = results.Where(r => r.Success).Select(r => $"✅ {r.ToolName}").ToList();
        var summary = successList.Count > 0
            ? string.Join(", ", successList)
            : "(no successful tools this turn)";

        return $"""
            Tool results received (turn {turn}/{maxTurns}).
            Completed this turn: {summary}

            Original request: "{Trim(originalPrompt, 200)}"

            Decide your next action:
            - If there are remaining todo steps → execute the next step NOW with tool calls (WriteFile, ExecuteCommand, etc). Do NOT update the task list — just DO the work.
            - If the task is FULLY done AND verified (build passed, output confirmed) → write your final summary to the user WITHOUT tool calls.
            - If a build/test failed → read the error, fix it, run again.
            - NEVER stop without completing all planned steps.
            - NEVER call TodoWrite unless the task is COMPLETELY finished.
            """;
    }

    /// <summary>
    /// Targeted error recovery prompt when some tools failed.
    /// </summary>
    private static string BuildErrorRecoveryPrompt(
        string originalPrompt,
        IReadOnlyList<ToolResult> failed,
        IReadOnlyList<ToolResult> succeeded,
        int consecutiveFailures,
        AgentState state)
    {
        var failDetails = string.Join("\n", failed.Select(r =>
            $"  ❌ {r.ToolName}: {Trim(r.Content, 1_500)}"));

        var succeedDetails = succeeded.Count > 0
            ? $"\nThese tools succeeded: {string.Join(", ", succeeded.Select(r => r.ToolName))}"
            : "";

        var urgency = consecutiveFailures >= 2
            ? $"\n⚠️ WARNING: {consecutiveFailures} consecutive failures. You MUST change your approach."
            : "";

        // EditFile loop detection
        var editFileLoop = state.IsInEditFileLoop
            ? $"\n🔄 EDIT FILE LOOP DETECTED: You've tried to edit {state.StuckOnFile} {state.EditFileRetryCounts.GetValueOrDefault(state.StuckOnFile?.Replace('\\', '/').ToLowerInvariant() ?? "", 0)} times.\n" +
              $"You MUST use a DIFFERENT approach:\n" +
              $"  - Option 1: Use WriteFile to rewrite the entire file with the corrected content\n" +
              $"  - Option 2: Use a completely different edit strategy (different oldText/newText)\n" +
              $"  - Option 3: If the file is large, read only the specific section you need to edit\n" +
              $"Do NOT retry the same edit again."
            : "";

        return $"""
            ❌ TOOL FAILURE — RECOVERY REQUIRED
            Original request: "{Trim(originalPrompt, 200)}"
            {succeedDetails}

            FAILED tools:
            {failDetails}
            {urgency}
            {editFileLoop}

            MANDATORY RECOVERY STEPS:
            1. Read the EXACT error message above carefully
            2. Identify the root cause (wrong path? wrong text? missing file? syntax error?)
            3. Fix the root cause with the appropriate tool(s)
            4. Retry the failed operation with the corrected parameters

            SPECIFIC GUIDANCE:
            - If EditFile failed with "text not found": use ReadFile first to get EXACT current content, then retry EditFile with EXACT match
            - If EditFile failed multiple times on same file: use WriteFile to rewrite the entire file
            - If a build failed: read the error, find the exact file/line, use EditFile to fix it, rebuild
            - If a file was not found: use ListDirectory or SearchFiles to locate the correct path
            - If a command failed: read the error output, fix the issue, run again

            Do NOT give up. Do NOT apologize. Fix and retry NOW.
            """;
    }

    /// <summary>
    /// Deep strategy reset after many consecutive failures.
    /// </summary>
    private static string BuildStrategyResetPrompt(
        string originalPrompt,
        IReadOnlyList<ToolResult> failed,
        int consecutiveFailures)
    {
        var failDetails = string.Join("\n", failed.Select(r =>
            $"  ❌ {r.ToolName}: {Trim(r.Content, 1_500)}"));

        return $"""
            🔄 STRATEGY RESET — {consecutiveFailures} CONSECUTIVE FAILURES DETECTED

            Original request: "{Trim(originalPrompt, 200)}"

            Recent failures:
            {failDetails}

            Your current approach is NOT working. You MUST completely change strategy:

            STEP 1 — DIAGNOSTIC: Use ListDirectory and ReadFile to understand the current state of the workspace.
            STEP 2 — ROOT CAUSE: What is the actual underlying problem causing all these failures?
            STEP 3 — NEW APPROACH: Choose a fundamentally different method to achieve the goal.
            STEP 4 — SIMPLIFY: Can you accomplish this goal with simpler steps?

            Reset your todo list with TodoWrite to reflect your new strategy.
            Start fresh — begin with an exploration step to understand what actually exists.

            You WILL succeed. Change your approach now.
            """;
    }

    /// <summary>
    /// Prompt injected when the agent calls no tools but the task needs them.
    /// </summary>
    private static string BuildNoToolCorrectionPrompt(
        string originalPrompt,
        bool executedAnyTool,
        int attempt)
    {
        var startStep = executedAnyTool
            ? "Continue with the NEXT required tool call."
            : "Start by calling TodoWrite to plan your steps, then use ListDirectory to explore the workspace.";

        var urgency = attempt >= 3
            ? "\n⚠️ CRITICAL: You have not called any tools multiple times. You MUST call a tool NOW."
            : "";

        return $"""
            You responded with text only, but this task REQUIRES tool calls to interact with the workspace.
            Original request: "{Trim(originalPrompt, 200)}"
            {urgency}

            {startStep}

            Rules:
            - Do NOT print JSON for the user to copy — emit actual tool calls
            - Do NOT explain what you WOULD do — DO it with tools
            - Do NOT give up — use the tools available to you
            - If unsure where to start, use ListDirectory on the workspace root

            Call a tool NOW.
            """;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Tool step metadata for inline display (Claude Code style)
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed record ToolStepMeta(
        string HumanName, string FilePath, string Operation,
        int AddedLines, int RemovedLines, int ElapsedMs, bool IsExpandable, string Detail = "");

    private static ToolStepMeta GetToolStepMeta(OllamaToolCall call, ToolResult result)
    {
        var path = GetArg(call.Arguments, "relativePath", "path", "fromRelativePath") ?? "";
        var fileName = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileName(path);

        return call.Name switch
        {
            "ReadFile" or "GetFileInfo" => new(
                HumanName: call.Name == "ReadFile" ? "Analyzed" : "Checked",
                FilePath: fileName,
                Operation: "read",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: true,
                Detail: $"#{CountLines(result.Content)} lines"),

            "ListDirectory" => new(
                HumanName: "Explored",
                FilePath: GetArg(call.Arguments, "relativePath", "path") ?? ".",
                Operation: "explore",
                AddedLines: CountLines(result.Content),
                RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: true,
                Detail: $"{CountLines(result.Content)} entries"),

            "CreateDirectory" => new(
                HumanName: "Created",
                FilePath: fileName,
                Operation: "create",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false),

            "WriteFile" or "AppendFile" => new(
                HumanName: "Wrote",
                FilePath: fileName,
                Operation: "write",
                AddedLines: CountLines(result.Content),
                RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false,
                Detail: $"{CountLines(result.Content)} lines"),

            "EditFile" => new(
                HumanName: "Edited",
                FilePath: fileName,
                Operation: "edit",
                AddedLines: ParseDiffStat(result.Content).Added,
                RemovedLines: ParseDiffStat(result.Content).Removed,
                ElapsedMs: 0, IsExpandable: false),

            "DeleteFile" => new(
                HumanName: "Deleted",
                FilePath: fileName,
                Operation: "delete",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false),

            "RenameFile" => new(
                HumanName: "Renamed",
                FilePath: GetArg(call.Arguments, "fromRelativePath") ?? "",
                Operation: "rename",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false),

            "SearchFiles" or "Glob" or "SearchText" or "GrepText" => new(
                HumanName: "Searched",
                FilePath: call.Name,
                Operation: "search",
                AddedLines: CountSearchResults(result.Content),
                RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: true,
                Detail: $"{CountSearchResults(result.Content)} matches"),

            "ExecuteCommand" => new(
                HumanName: "Ran",
                FilePath: GetArg(call.Arguments, "command", "commandLine") ?? "",
                Operation: "command",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: true),

            "WebFetch" or "WebSearch" => new(
                HumanName: call.Name == "WebFetch" ? "Fetched" : "Searched",
                FilePath: GetArg(call.Arguments, "url") ?? call.Name,
                Operation: "web",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: true),

            "TodoWrite" or "TodoRead" => new(
                HumanName: call.Name == "TodoWrite" ? "Updated" : "Read",
                FilePath: "task list",
                Operation: "task",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false),

            _ => new(
                HumanName: call.Name,
                FilePath: "",
                Operation: "action",
                AddedLines: 0, RemovedLines: 0,
                ElapsedMs: 0, IsExpandable: false),
        };
    }

    private static IReadOnlyList<ToolStepGroup> BuildToolStepGroups(
        IReadOnlyList<OllamaToolCall> calls, IReadOnlyList<ToolResult> results, int turnNumber)
    {
        var groups = new List<ToolStepGroup>();
        var currentReadGroup = new List<ToolStepEntry>();

        foreach (var (call, result) in calls.Zip(results))
        {
            var meta = GetToolStepMeta(call, result);
            var lang = GetLangFromPath(meta.FilePath);
            var detail = meta.Detail;
            if (string.IsNullOrEmpty(detail))
            {
                var parts = new List<string>();
                if (meta.AddedLines > 0) parts.Add($"+{meta.AddedLines}");
                if (meta.RemovedLines > 0) parts.Add($"-{meta.RemovedLines}");
                detail = string.Join(" ", parts);
            }

            // Extract diff content for edit/write operations
            var diffContent = "";
            if (meta.Operation is "edit" or "write" && !string.IsNullOrEmpty(result.Content))
            {
                // The result.Content contains the unified diff for edit operations
                diffContent = result.Content;
            }

            var entry = new ToolStepEntry(
                Label: meta.HumanName,
                FilePath: meta.FilePath,
                Lang: lang,
                Detail: detail ?? "",
                Operation: meta.Operation,
                DiffContent: diffContent);

            if (meta.IsExpandable)
            {
                currentReadGroup.Add(entry);
            }
            else
            {
                // Flush any accumulated read group
                if (currentReadGroup.Count > 0)
                {
                    groups.Add(new ToolStepGroup(
                        Header: $"Explored {currentReadGroup.Count} files",
                        Expandable: true,
                        Steps: currentReadGroup.ToList(),
                        TurnNumber: turnNumber,
                        Phase: AgentLoopPhase.Act));
                    currentReadGroup.Clear();
                }
                // Write tools are standalone
                groups.Add(new ToolStepGroup(
                    Header: "",
                    Expandable: false,
                    Steps: [entry],
                    TurnNumber: turnNumber,
                    Phase: AgentLoopPhase.Act));
            }
        }

        // Flush trailing read group
        if (currentReadGroup.Count > 0)
        {
            groups.Add(new ToolStepGroup(
                Header: $"Explored {currentReadGroup.Count} files",
                Expandable: true,
                Steps: currentReadGroup.ToList(),
                TurnNumber: turnNumber,
                Phase: AgentLoopPhase.Act));
        }

        return groups;
    }

    private static string GetLangFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "C#",
            "js" => "JS",
            "ts" => "TS",
            "tsx" => "TSX",
            "jsx" => "JSX",
            "py" => "Python",
            "go" => "Go",
            "rs" => "Rust",
            "rb" => "Ruby",
            "java" => "Java",
            "kt" => "Kotlin",
            "swift" => "Swift",
            "cpp" or "cc" or "cxx" => "C++",
            "c" => "C",
            "h" => "C/C++",
            "hpp" => "C++",
            "xml" or "html" or "htm" => "HTML",
            "css" or "scss" or "less" => "CSS",
            "json" => "JSON",
            "yaml" or "yml" => "YAML",
            "md" => "Markdown",
            "sql" => "SQL",
            "sh" or "bash" => "Shell",
            "ps1" => "PowerShell",
            "csproj" or "fsproj" or "vbproj" => "MSBuild",
            "sln" => "Solution",
            "razor" => "Razor",
            "xaml" => "XAML",
            _ => ext.ToUpperInvariant(),
        };
    }

    private static int CountLines(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    private static int CountSearchResults(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => !l.StartsWith("Exit code:") && !l.Trim().StartsWith("Found"));
    }

    private static (int Added, int Removed) ParseDiffStat(string resultContent)
    {
        // Try to extract +N / -N from edit result
        var added = 0;
        var removed = 0;
        if (string.IsNullOrEmpty(resultContent)) return (0, 0);

        // Count lines that look like additions/removals
        foreach (var line in resultContent.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('+') && !trimmed.StartsWith("++"))
                added++;
            else if (trimmed.StartsWith('-') && !trimmed.StartsWith("--"))
                removed++;
        }

        // Fallback: if no diff-like lines, estimate from result
        if (added == 0 && removed == 0)
        {
            var lines = resultContent.Split('\n').Length;
            added = lines;
        }
        return (added, removed);
    }

    private static string DescribeToolBatch(IReadOnlyList<OllamaToolCall> calls)
    {
        if (calls.Count == 1)
            return DescribeSingleToolCall(calls[0]);
        var names = string.Join(", ", calls.Take(4).Select(c => DescribeSingleToolCall(c)));
        return $"Running {calls.Count} tools: {names}";
    }

    private static string DescribeSingleToolCall(OllamaToolCall call)
    {
        var path = GetArg(call.Arguments, "relativePath", "path", "fromRelativePath");
        var query = GetArg(call.Arguments, "query", "pattern", "search");
        var cmd = GetArg(call.Arguments, "command", "commandLine");

        return call.Name switch
        {
            "ReadFile"       => $"Reading {path ?? "file"}",
            "WriteFile"      => $"Writing {path ?? "file"}",
            "EditFile"       => $"Editing {path ?? "file"}",
            "AppendFile"     => $"Appending to {path ?? "file"}",
            "DeleteFile"     => $"Deleting {path ?? "file"}",
            "RenameFile"     => $"Renaming {GetArg(call.Arguments, "fromRelativePath") ?? "file"}",
            "ListDirectory"  => $"Listing directory {path ?? "."}",
            "CreateDirectory"=> $"Creating directory {path ?? ""}",
            "GetFileInfo"    => $"Checking {path ?? "file"}",
            "SearchFiles"    => $"Searching files for \"{query ?? ""}\"",
            "Glob"           => $"Matching pattern {GetArg(call.Arguments, "pattern") ?? ""}",
            "SearchText"     => $"Searching text for \"{query ?? ""}\"",
            "GrepText"       => $"Grep for \"{GetArg(call.Arguments, "pattern") ?? ""}\"",
            "ExecuteCommand" => $"Running: {Trim(cmd ?? "command", 80)}",
            "WebFetch"       => $"Fetching {GetArg(call.Arguments, "url") ?? "URL"}",
            "WebSearch"      => $"Searching web for \"{query ?? ""}\"",
            "TodoWrite"      => "Updating task list",
            "TodoRead"       => "Reading task list",
            "EnterPlanMode"  => "Entering plan mode",
            "ExitPlanMode"   => "Exiting plan mode",
            _                => call.Name
        };
    }

    private static string DescribeSingleToolResult(string toolName, JsonObject args, ToolResult result)
    {
        var path = GetArg(args, "relativePath", "path");
        return toolName switch
        {
            "ReadFile"       => $"Read {path} ({result.Content.Split('\n').Length} lines)",
            "WriteFile"      => $"Wrote {path}",
            "EditFile"       => $"Edited {path}",
            "CreateDirectory"=> $"Created directory {path}",
            "ExecuteCommand" => result.Success ? "Command succeeded ✓" : "Command failed",
            "WebSearch"      => $"Got search results ({result.Content.Length} chars)",
            "WebFetch"       => $"Fetched page ({result.Content.Length} chars)",
            "TodoWrite"      => "Task list updated",
            _                => result.Success ? $"{toolName} ✓" : $"{toolName} ✗"
        };
    }

    /// <summary>
    /// Deterministic summary built when agent produces no final text.
    /// </summary>
    private static string BuildDeterministicFinalSummary(IReadOnlyList<ToolResult> results, string prompt)
    {
        if (results.Count == 0)
            return $"I processed your request: \"{Trim(prompt, 200)}\". The task has been completed.";

        var successes = results.Where(r => r.Success).Select(r => r.ToolName).Distinct().ToList();
        var failures  = results.Where(r => !r.Success).Select(r => r.ToolName).Distinct().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Task completed: \"{Trim(prompt, 200)}\"");
        if (successes.Count > 0)
            sb.AppendLine($"\nSuccessful operations: {string.Join(", ", successes)}");
        if (failures.Count > 0)
            sb.AppendLine($"\nFailed operations (may need attention): {string.Join(", ", failures)}");

        return sb.ToString().Trim();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Text-based tool call parsing (fallback for models without native tool support)
    // ═══════════════════════════════════════════════════════════════════════════

    private static IEnumerable<(string Name, JsonObject Args)> ParseToolCallsFromText(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var fencePattern = new Regex(
            @"```(?:tool_call|json)\s*\n([\s\S]*?)\n```",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        foreach (Match m in fencePattern.Matches(text))
        {
            foreach (var call in TryParseJson(m.Groups[1].Value.Trim()))
            {
                var key = $"{call.Name}\n{call.Args.ToJsonString()}";
                if (seen.Add(key)) yield return call;
            }
        }

        foreach (var json in ExtractJsonPayloads(text))
        {
            foreach (var call in TryParseJson(json))
            {
                var key = $"{call.Name}\n{call.Args.ToJsonString()}";
                if (seen.Add(key)) yield return call;
            }
        }
    }

    private static IEnumerable<(string Name, JsonObject Args)> TryParseJson(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { yield break; }

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                foreach (var call in ParseToolCallNode(item))
                    yield return call;
        }
        else foreach (var call in ParseToolCallNode(node))
            yield return call;
    }

    private static IEnumerable<(string Name, JsonObject Args)> ParseToolCallNode(JsonNode? node)
    {
        if (node is not JsonObject obj) yield break;

        var name = GetStringProp(obj, "name") ?? GetStringProp(obj, "tool") ?? GetStringProp(obj, "toolName");
        if (string.IsNullOrWhiteSpace(name)) yield break;

        var argsNode = obj["arguments"] ?? obj["args"] ?? obj["input"] ?? obj["parameters"];
        if (argsNode is null) yield break;

        JsonObject args;
        if (argsNode is JsonObject o) args = JsonNode.Parse(o.ToJsonString())!.AsObject();
        else if (argsNode is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { args = JsonNode.Parse(s)?.AsObject() ?? new JsonObject(); } catch { args = new JsonObject(); }
        }
        else args = new JsonObject();

        yield return (name!, args);
    }

    private static IEnumerable<string> ExtractJsonPayloads(string text)
    {
        var depth = 0; var start = -1; var inStr = false; var esc = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr) { if (esc) { esc = false; continue; } if (ch == '\\') { esc = true; continue; } if (ch == '"') inStr = false; continue; }
            if (ch == '"') { inStr = true; continue; }
            if (ch is '{' or '[') { if (depth++ == 0) start = i; continue; }
            if ((ch is '}' or ']') && depth > 0 && --depth == 0 && start >= 0)
            { yield return text[start..(i + 1)]; start = -1; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════════════════

    private static string StripThinking(string text, out string thinking)
    {
        var visible = new StringBuilder();
        var thought = new StringBuilder();
        var idx = 0;
        while (idx < text.Length)
        {
            var s = text.IndexOf("<think>", idx, StringComparison.OrdinalIgnoreCase);
            if (s < 0) { visible.Append(text[idx..]); break; }
            visible.Append(text[idx..s]);
            var ts = s + 7;
            var e = text.IndexOf("</think>", ts, StringComparison.OrdinalIgnoreCase);
            if (e < 0) { thought.Append(text[ts..]); break; }
            thought.AppendLine(text[ts..e]);
            idx = e + 8;
        }
        thinking = thought.ToString().Trim();
        return visible.ToString().Trim();
    }

    private static bool LooksLikeWorkspaceTask(string prompt)
    {
        var keywords = new[] {
            "build", "create", "make", "generate", "implement", "add", "change", "modify",
            "edit", "fix", "debug", "run", "test", "install", "project", "api", "web", "app",
            "file", "folder", "directory", "database", "sql", "controller", "write", "code",
            "refactor", "update", "deploy", "configure", "setup", "scaffold", "migrate"
        };
        return keywords.Any(k => prompt.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetArg(JsonObject args, params string[] names)
    {
        foreach (var n in names)
            if (args[n] is JsonValue v) { try { return v.GetValue<string>(); } catch { } }
        return null;
    }

    private static string? GetStringProp(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>(); } catch { return null; }
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.User      => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool      => "user",
        _                  => "system"
    };

    private static int EstimateTokens(string s) => Math.Max(1, s.Length / 4);

    /// <summary>
    /// Estimate total tokens across all messages.
    /// Based on reference project's token estimation pattern.
    /// </summary>
    private static int EstimateTokens(IReadOnlyList<OllamaChatMessage> messages)
    {
        var totalChars = 0;
        foreach (var msg in messages)
        {
            totalChars += msg.Content?.Length ?? 0;
            if (msg.ToolCalls is not null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    totalChars += tc.Name?.Length ?? 0;
                    totalChars += tc.Arguments?.ToJsonString()?.Length ?? 0;
                }
            }
        }
        return Math.Max(1, totalChars / 4);
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
