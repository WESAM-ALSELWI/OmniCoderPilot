using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Reads project-level instruction files from the workspace root:
/// CLAUDE.md, AGENTS.md, .mycoder.md — just like Claude Code reads CLAUDE.md.
/// These files let developers teach OmniCoderPilot project-specific rules, conventions,
/// and context without having to re-explain them every session.
/// </summary>
public sealed class ProjectMemoryService : IProjectMemoryService
{
    private static readonly string[] CandidateFiles =
    [
        ".mycoder.md",
        "CLAUDE.md",
        "AGENTS.md",
        ".agents.md",
        "MYCODER.md",
    ];

    private static readonly string[] CandidateDirs =
    [
        ".mycoder",
        ".agents",
        ".github",
    ];

    public async Task<string> LoadProjectInstructionsAsync(string workspaceRoot, CancellationToken ct)
    {
        var sections = new List<string>();

        // 1. Root-level instruction files
        foreach (var name in CandidateFiles)
        {
            var path = Path.Combine(workspaceRoot, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add($"## {name}\n{text.Trim()}");
            }
            catch { /* ignore unreadable files */ }
        }

        // 2. Subdirectory instruction files (.mycoder/instructions.md, etc.)
        foreach (var dir in CandidateDirs)
        {
            var dirPath = Path.Combine(workspaceRoot, dir);
            if (!Directory.Exists(dirPath)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath, "*.md", SearchOption.TopDirectoryOnly).Take(5))
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    if (!string.IsNullOrWhiteSpace(text))
                        sections.Add($"## {Path.GetRelativePath(workspaceRoot, file)}\n{text.Trim()}");
                }
            }
            catch { /* ignore */ }
        }

        if (sections.Count == 0)
            return "";

        return "# Project Instructions (from workspace files)\n\n" +
               string.Join("\n\n---\n\n", sections);
    }
}
