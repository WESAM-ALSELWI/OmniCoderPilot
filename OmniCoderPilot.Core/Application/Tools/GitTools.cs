using System.Text.Json.Nodes;

namespace OmniCoderPilot.Application.Tools;

// ── Git Tool Suite ────────────────────────────────────────────────────────────
// All tools call git via the existing ITerminalService (PowerShell).
// They share a small helper for running git commands.

internal static class GitRunner
{
    public static async Task<(string Output, bool Success)> RunAsync(
        ITerminalService terminal,
        string workspaceRoot,
        string gitArgs,
        CancellationToken ct,
        int timeoutSeconds = 30)
    {
        var lines = new System.Text.StringBuilder();
        var exitCode = 0;
        await foreach (var line in terminal.RunPowerShellAsync(workspaceRoot, $"git {gitArgs}", ct, timeoutSeconds))
        {
            if (line.StartsWith("\nExit code:"))
            {
                int.TryParse(line.Replace("\nExit code:", "").Trim(), out exitCode);
            }
            else
            {
                lines.Append(line.TrimStart('[', 'O', 'U', 'T', ']', ' ')
                                 .TrimStart('[', 'E', 'R', 'R', ']', ' '));
            }
        }
        var output = lines.ToString().Trim();
        return (output, exitCode == 0);
    }
}

// ── GitStatus ─────────────────────────────────────────────────────────────────

public sealed class GitStatusTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitStatus";
    public string Description => "Show the working tree status: which files are modified, staged, untracked, or deleted. Always run this before committing to understand what changed.";
    public JsonObject Schema => ToolJson.Schema();
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var (output, success) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, "status --short --branch", ct);
        if (!success && output.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return new(Name, false, "This workspace is not a Git repository. Run 'git init' first via ExecuteCommand.");
        return new(Name, true, string.IsNullOrWhiteSpace(output) ? "✅ Working tree is clean — no uncommitted changes." : output);
    }
}

// ── GitDiff ───────────────────────────────────────────────────────────────────

public sealed class GitDiffTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitDiff";
    public string Description => "Show changes in the working tree vs the last commit. Optionally specify a file path to see changes in that file only. Use --staged to see staged changes.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Optional: workspace-relative file path to diff. Leave empty for all changes."),
        ("staged", "boolean", "If true, show staged (--cached) changes instead of unstaged."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var staged = ToolJson.Bool(input, "staged");
        var args = staged ? "diff --staged" : "diff";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- \"{path.Replace("\\", "/")}\"";

        var (output, _) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, args, ct);
        return new(Name, true, string.IsNullOrWhiteSpace(output) ? "No changes found." : output);
    }
}

// ── GitLog ────────────────────────────────────────────────────────────────────

public sealed class GitLogTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitLog";
    public string Description => "Show recent commit history with author, date, and message. Useful for understanding what changed and when.";
    public JsonObject Schema => ToolJson.Schema(
        ("limit", "number", "Maximum number of commits to show (default 20, max 100)."),
        ("relativePath", "string", "Optional: only show commits affecting this file."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 20), 1, 100);
        var path = ToolJson.String(input, "relativePath");
        var args = $"log --oneline --decorate --graph -n {limit}";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- \"{path.Replace("\\", "/")}\"";

        var (output, _) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, args, ct);
        return new(Name, true, string.IsNullOrWhiteSpace(output) ? "No commits yet." : output);
    }
}

// ── GitBlame ──────────────────────────────────────────────────────────────────

public sealed class GitBlameTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitBlame";
    public string Description => "Show who last changed each line in a file and in which commit. Use to understand why code was written a certain way before changing it.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Workspace-relative file path to blame."),
        ("startLine", "number", "Optional: 1-based start line."),
        ("endLine", "number", "Optional: 1-based end line."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        if (string.IsNullOrWhiteSpace(path)) return new(Name, false, "relativePath is required.");
        var start = ToolJson.Int(input, "startLine", 0);
        var end = ToolJson.Int(input, "endLine", 0);
        var args = $"blame \"{path.Replace("\\", "/")}\" --line-porcelain";
        if (start > 0 && end > 0) args += $" -L {start},{end}";

        var (output, success) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, args, ct);
        if (!success) return new(Name, false, $"git blame failed: {output}");
        // Simplify porcelain output to just committer + line content
        var sb = new System.Text.StringBuilder();
        var lines = output.Split('\n');
        string? lastAuthor = null; string? lastSummary = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("author ")) lastAuthor = lines[i][7..];
            else if (lines[i].StartsWith("summary ")) lastSummary = lines[i][8..];
            else if (lines[i].StartsWith("\t")) sb.AppendLine($"{lastAuthor?.PadRight(20)} | {lastSummary?.Substring(0, Math.Min(30, lastSummary.Length)),-30} | {lines[i][1..]}");
        }
        return new(Name, true, sb.Length > 0 ? sb.ToString() : output);
    }
}

// ── GitCommit ─────────────────────────────────────────────────────────────────

public sealed class GitCommitTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitCommit";
    public string Description => "Stage all changes (git add -A) and create a commit with the given message. Use after completing a logical unit of work. Always run GitStatus first.";
    public JsonObject Schema => ToolJson.Schema(
        ("message", "string", "Commit message. Use conventional commits format: 'feat: add login page', 'fix: null ref in parser', etc."),
        ("addAll", "boolean", "If true (default), stage all changed files with 'git add -A' before committing."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var message = ToolJson.String(input, "message");
        if (string.IsNullOrWhiteSpace(message)) return new(Name, false, "Commit message is required.");
        var addAll = ToolJson.Bool(input, "addAll", true);

        if (addAll)
        {
            var (addOut, addOk) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, "add -A", ct);
            if (!addOk) return new(Name, false, $"git add failed: {addOut}");
        }

        var safeMsg = message.Replace("\"", "\\\"");
        var (commitOut, commitOk) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, $"commit -m \"{safeMsg}\"", ct);
        return commitOk
            ? new(Name, true, $"✅ Committed: {commitOut}")
            : new(Name, false, $"git commit failed: {commitOut}");
    }
}

// ── GitBranch ─────────────────────────────────────────────────────────────────

public sealed class GitBranchTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitBranch";
    public string Description => "List branches, create a new branch, or switch to an existing branch. Useful for isolating features or bug fixes.";
    public JsonObject Schema => ToolJson.Schema(
        ("action", "string", "One of: 'list', 'create', 'switch', 'create-and-switch'."),
        ("name", "string", "Branch name (required for create/switch)."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var action = ToolJson.String(input, "action", "list").ToLowerInvariant();
        var name = ToolJson.String(input, "name");

        var args = action switch
        {
            "list" => "branch -a",
            "create" => $"branch \"{name}\"",
            "switch" => $"switch \"{name}\"",
            "create-and-switch" => $"switch -c \"{name}\"",
            _ => null
        };

        if (args is null) return new(Name, false, $"Unknown action '{action}'. Use: list, create, switch, create-and-switch.");
        if (action != "list" && string.IsNullOrWhiteSpace(name)) return new(Name, false, "Branch name is required.");

        var (output, success) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, args, ct);
        return success
            ? new(Name, true, string.IsNullOrWhiteSpace(output) ? $"✅ Branch operation '{action}' succeeded." : output)
            : new(Name, false, $"git branch operation failed: {output}");
    }
}

// ── GitStash ──────────────────────────────────────────────────────────────────

public sealed class GitStashTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "GitStash";
    public string Description => "Stash current uncommitted changes (push) or restore them (pop). Useful for temporarily shelving work to switch context.";
    public JsonObject Schema => ToolJson.Schema(
        ("action", "string", "One of: 'push' (save changes), 'pop' (restore last stash), 'list' (show stashes)."),
        ("message", "string", "Optional description for the stash (only for push)."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var action = ToolJson.String(input, "action", "push").ToLowerInvariant();
        var message = ToolJson.String(input, "message");
        var args = action switch
        {
            "list" => "stash list",
            "pop" => "stash pop",
            "push" when !string.IsNullOrWhiteSpace(message) => $"stash push -m \"{message.Replace("\"", "\\\"")}\"",
            "push" => "stash push",
            _ => null
        };
        if (args is null) return new(Name, false, $"Unknown action '{action}'. Use: push, pop, list.");
        var (output, success) = await GitRunner.RunAsync(terminal, context.WorkspaceRoot, args, ct);
        return success ? new(Name, true, string.IsNullOrWhiteSpace(output) ? "✅ Stash operation succeeded." : output)
                       : new(Name, false, $"git stash failed: {output}");
    }
}
