using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

public sealed class ProjectUnderstandingService(AppDbContext db) : IProjectUnderstandingService
{
    public async Task<string> BuildProjectSummaryAsync(Guid workspaceId, CancellationToken ct)
    {
        var workspace = await db.Workspaces.FindAsync([workspaceId], ct) ?? throw new InvalidOperationException("Workspace not found.");
        var indexed = await db.FileIndex
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.RelativePath)
            .Take(80)
            .ToListAsync(ct);

        var topLevel = Directory.EnumerateFileSystemEntries(workspace.RootPath)
            .Where(x => Path.GetFileName(x) is not "bin" and not "obj" and not ".git" and not ".vs")
            .OrderBy(x => x)
            .Take(80)
            .Select(x => $"{(Directory.Exists(x) ? "dir" : "file")} {Path.GetFileName(x)}");

        return string.Join('\n',
            $"Workspace: {workspace.Name}",
            $"Root: {workspace.RootPath}",
            "Top-level entries:",
            string.Join('\n', topLevel),
            "Indexed files and symbols:",
            string.Join('\n', indexed.Select(x => $"{x.RelativePath}: {Trim(x.Summary, 240)}")));
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

public sealed class MemoryService(AppDbContext db) : IMemoryService
{
    public async Task<string> GetRelevantMemoriesAsync(Guid workspaceId, string prompt, CancellationToken ct)
    {
        var terms = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        var query = db.Memories.Where(x => x.WorkspaceId == workspaceId);
        var memories = (await query.Take(80).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt)
            .Take(40)
            .ToList();
        var ranked = memories
            .Select(x => new { Memory = x, Score = terms.Count(t => x.Content.Contains(t, StringComparison.OrdinalIgnoreCase)) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.CreatedAt)
            .Take(8)
            .Select(x => $"- [{x.Memory.Kind}] {x.Memory.Content}");

        return string.Join('\n', ranked);
    }

    public async Task CaptureTurnAsync(Guid workspaceId, string userPrompt, string assistantResponse, CancellationToken ct)
    {
        if (userPrompt.Contains("remember", StringComparison.OrdinalIgnoreCase))
        {
            db.Memories.Add(new MemoryItem
            {
                WorkspaceId = workspaceId,
                Kind = "user",
                Content = userPrompt
            });
        }

        var compact = $"{Trim(userPrompt, 280)} => {Trim(assistantResponse, 420)}";
        db.Memories.Add(new MemoryItem
        {
            WorkspaceId = workspaceId,
            Kind = "turn",
            Content = compact
        });

        await db.SaveChangesAsync(ct);
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

public sealed class ContextCompressionService : IContextCompressionService
{
    public string Compress(IReadOnlyList<ChatMessage> messages, int maxCharacters)
    {
        if (messages.Count == 0) return "";

        var rows = messages
            .OrderBy(x => x.CreatedAt)
            .Select(x => $"{x.Role}: {x.Content.ReplaceLineEndings(" ").Trim()}")
            .ToList();

        var selected = new Stack<string>();
        var total = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (total + row.Length > maxCharacters) break;
            selected.Push(row);
            total += row.Length;
        }

        return string.Join('\n', selected);
    }
}
