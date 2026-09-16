using System.Text;
using System.Text.Json.Nodes;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Application.Tools;

public sealed class TodoWriteTool(ITodoService todos, IAgentEventSink sink) : IAgentTool
{
    public string Name => "TodoWrite";
    public string Description => "Write or update the task list for this conversation. Always start a complex task by calling TodoWrite to plan your steps. Update statuses as you progress: pending → in-progress → done. The UI will show this list live to the user.";
    public JsonObject Schema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["todos"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Complete list of TODO items (replaces existing list).",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Task description." },
                        ["status"] = new JsonObject { ["type"] = "string", ["description"] = "pending | in-progress | done | cancelled" },
                        ["order"] = new JsonObject { ["type"] = "number", ["description"] = "Sort order (0-based)." }
                    }
                }
            }
        }
    };
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var todosNode = input["todos"]?.AsArray();
        if (todosNode is null)
            return new(Name, false, "Missing 'todos' array.");

        var items = new List<(string Content, string Status, int Order)>();
        var i = 0;
        foreach (var node in todosNode)
        {
            var content = node?["content"]?.GetValue<string>() ?? "";
            var status = node?["status"]?.GetValue<string>() ?? "pending";
            var order = node?["order"]?.GetValue<int?>() ?? i;
            if (!string.IsNullOrWhiteSpace(content))
                items.Add((content, status, order));
            i++;
        }

        await todos.UpsertAsync(context.ConversationId, items, ct);
        var saved = await todos.GetAsync(context.ConversationId, ct);
        await sink.TodoAsync("", context.ConversationId, saved);

        var sb = new StringBuilder();
        sb.AppendLine($"Updated {saved.Count} todo item(s):");
        foreach (var t in saved)
        {
            var icon = t.Status switch { "done" => "✓", "in-progress" => "▶", "cancelled" => "✗", _ => "○" };
            sb.AppendLine($"  {icon} [{t.Status}] {t.Content}");
        }
        return new(Name, true, sb.ToString());
    }
}

public sealed class TodoReadTool(ITodoService todos) : IAgentTool
{
    public string Name => "TodoRead";
    public string Description => "Read the current task list for this conversation. Use to check which steps are pending/in-progress before continuing a multi-step task.";
    public JsonObject Schema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var saved = await todos.GetAsync(context.ConversationId, ct);
        if (saved.Count == 0)
            return new(Name, true, "No todos set. Use TodoWrite to create a task list.");

        var sb = new StringBuilder();
        sb.AppendLine($"Current task list ({saved.Count} item(s)):");
        foreach (var t in saved)
        {
            var icon = t.Status switch { "done" => "✓", "in-progress" => "▶", "cancelled" => "✗", _ => "○" };
            sb.AppendLine($"  {icon} [{t.Status}] {t.Content}");
        }
        return new(Name, true, sb.ToString());
    }
}
