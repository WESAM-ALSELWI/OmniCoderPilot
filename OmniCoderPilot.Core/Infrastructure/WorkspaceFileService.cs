using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

public sealed class WorkspaceFileService(AppDbContext db) : IWorkspaceFileService
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs" };

    public async Task<FileNodeDto> GetTreeAsync(Guid workspaceId, CancellationToken ct)
    {
        var ws = await db.Workspaces.FindAsync([workspaceId], ct) ?? throw new InvalidOperationException("Workspace not found.");
        return BuildNode(ws.RootPath, ws.RootPath, 0);
    }

    public async Task<string> ReadFileAsync(Guid workspaceId, string relativePath, CancellationToken ct)
    {
        var ws = await db.Workspaces.FindAsync([workspaceId], ct) ?? throw new InvalidOperationException("Workspace not found.");
        return await File.ReadAllTextAsync(ResolveInsideWorkspace(ws.RootPath, relativePath), ct);
    }

    public async Task WriteFileAsync(Guid workspaceId, string relativePath, string content, CancellationToken ct)
    {
        var ws = await db.Workspaces.FindAsync([workspaceId], ct) ?? throw new InvalidOperationException("Workspace not found.");
        var path = ResolveInsideWorkspace(ws.RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }

    public async Task DeleteFileAsync(Guid workspaceId, string relativePath, CancellationToken ct)
    {
        var ws = await db.Workspaces.FindAsync([workspaceId], ct) ?? throw new InvalidOperationException("Workspace not found.");
        var path = ResolveInsideWorkspace(ws.RootPath, relativePath);
        if (File.Exists(path)) File.Delete(path);
        await Task.CompletedTask;
    }

    public string ResolveInsideWorkspace(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithoutSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var canonicalRoot = rootWithoutSlash + Path.DirectorySeparatorChar;
        if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), rootWithoutSlash, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes workspace.");
        return full;
    }

    private static FileNodeDto BuildNode(string root, string path, int depth)
    {
        var info = new DirectoryInfo(path);
        var children = depth > 3 ? [] : info.EnumerateFileSystemInfos()
            .Where(x => !Ignored.Contains(x.Name))
            .OrderByDescending(x => x is DirectoryInfo)
            .ThenBy(x => x.Name)
            .Take(300)
            .Select(x => x is DirectoryInfo ? BuildNode(root, x.FullName, depth + 1) : new FileNodeDto(x.Name, Path.GetRelativePath(root, x.FullName), false, []))
            .ToList();
        return new FileNodeDto(info.Name, Path.GetRelativePath(root, path), true, children);
    }
}
