using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

public sealed class DiffService(AppDbContext db, IWorkspaceFileService files) : IDiffService
{
    public string CreateUnifiedDiff(string relativePath, string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var diff = new List<string> { $"--- a/{relativePath}", $"+++ b/{relativePath}", $"@@ -1,{oldLines.Length} +1,{newLines.Length} @@" };
        for (var i = 0; i < Math.Max(oldLines.Length, newLines.Length); i++)
        {
            var o = i < oldLines.Length ? oldLines[i] : null;
            var n = i < newLines.Length ? newLines[i] : null;
            if (o == n && o is not null) diff.Add(" " + o);
            else { if (o is not null) diff.Add("-" + o); if (n is not null) diff.Add("+" + n); }
        }
        return string.Join('\n', diff);
    }

    public async Task<ChangeSet> CreatePreviewAsync(Guid workspaceId, Guid conversationId, string description, IReadOnlyDictionary<string, string> proposedFiles, CancellationToken ct)
    {
        var cs = new ChangeSet { WorkspaceId = workspaceId, ConversationId = conversationId, Description = description };
        foreach (var pair in proposedFiles)
        {
            var oldText = "";
            try { oldText = await files.ReadFileAsync(workspaceId, pair.Key, ct); } catch (FileNotFoundException) { }
            cs.Files.Add(new FileChange { RelativePath = pair.Key, OriginalText = oldText, NewText = pair.Value, UnifiedDiff = CreateUnifiedDiff(pair.Key, oldText, pair.Value) });
        }
        db.ChangeSets.Add(cs);
        await db.SaveChangesAsync(ct);
        return cs;
    }

    public async Task ApplyAsync(Guid changeSetId, CancellationToken ct)
    {
        var cs = await db.ChangeSets.Include(x => x.Files).SingleAsync(x => x.Id == changeSetId, ct);
        foreach (var f in cs.Files) await files.WriteFileAsync(cs.WorkspaceId, f.RelativePath, f.NewText, ct);
        cs.Status = ChangeStatus.Applied;
        await db.SaveChangesAsync(ct);
    }
}
