using System.Text.Json.Nodes;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Application.Tools;

/// <summary>
/// Enters plan mode: the agent will describe what it plans to do without
/// actually executing file writes or shell commands. This lets the user
/// review the plan before committing.
/// </summary>
public sealed class EnterPlanModeTool(PlanModeState planMode) : IAgentTool
{
    public string Name => "EnterPlanMode";
    public string Description =>
        "Switch to PLAN MODE. In plan mode you describe every file change and command you would run, " +
        "but do NOT actually execute them. The user reviews your plan first. " +
        "Call ExitPlanMode to approve and execute, or the user can cancel. " +
        "Always use plan mode for large refactors or destructive operations.";
    public JsonObject Schema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        planMode.SetActive(context.ConversationId, true);
        return Task.FromResult(new ToolResult(Name, true,
            "✅ Entered PLAN MODE. I will now describe my intended changes step by step without executing them. " +
            "Call ExitPlanMode when you are ready to execute the plan."));
    }
}

/// <summary>
/// Exits plan mode and optionally executes the described plan.
/// </summary>
public sealed class ExitPlanModeTool(PlanModeState planMode) : IAgentTool
{
    public string Name => "ExitPlanMode";
    public string Description =>
        "Exit PLAN MODE and return to normal execution mode. " +
        "After this call, subsequent tool calls will actually execute. " +
        "Use this once you have described your full plan and the user has approved it.";
    public JsonObject Schema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        planMode.SetActive(context.ConversationId, false);
        return Task.FromResult(new ToolResult(Name, true,
            "✅ Exited PLAN MODE. Executing the plan now — all file writes and commands will run for real."));
    }
}
