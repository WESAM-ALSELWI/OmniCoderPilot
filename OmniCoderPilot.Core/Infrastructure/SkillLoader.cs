using System.Text;
using System.Text.RegularExpressions;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Loads skills from markdown files in .mycoder/skills/ directories.
/// Skills are prompt-based commands with frontmatter metadata.
/// </summary>
public sealed partial class SkillLoader : ISkillLoader
{
    private readonly List<string> _skillDirs;

    public SkillLoader(string workspaceRoot)
    {
        _skillDirs =
        [
            Path.Combine(workspaceRoot, ".mycoder", "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mycoder", "skills"),
        ];
    }

    /// <summary>
    /// Load all available skills from all directories.
    /// </summary>
    public IReadOnlyList<SkillDefinition> LoadAll()
    {
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in _skillDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                try
                {
                    var skill = LoadSkillFile(file);
                    if (skill is not null && !skills.ContainsKey(skill.Name))
                        skills[skill.Name] = skill;
                }
                catch { }
            }
        }

        return skills.Values.ToList();
    }

    /// <summary>
    /// Find a skill by name (case-insensitive).
    /// </summary>
    public SkillDefinition? FindByName(string name)
    {
        return LoadAll().FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Search skills by query (matches name, description, and when_to_use).
    /// </summary>
    public IReadOnlyList<SkillDefinition> Search(string query)
    {
        var all = LoadAll();
        var queryLower = query.ToLowerInvariant();
        return all.Where(s =>
            s.Name.Contains(queryLower) ||
            s.Description.Contains(queryLower) ||
            s.WhenToUse.Contains(queryLower))
            .ToList();
    }

    /// <summary>
    /// Get skills relevant to the current task context (file paths being worked on).
    /// </summary>
    public IReadOnlyList<SkillDefinition> GetRelevant(IReadOnlyList<string> filePaths)
    {
        var all = LoadAll();
        return all.Where(s =>
            s.FilePatterns.Count == 0 || // No filter = always relevant
            s.FilePatterns.Any(pattern =>
                filePaths.Any(fp => MatchesGlob(pattern, fp))))
            .ToList();
    }

    /// <summary>
    /// Get the skill list formatted for the system prompt.
    /// </summary>
    public string FormatForSystemPrompt()
    {
        var skills = LoadAll();
        if (skills.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("# AVAILABLE SKILLS");
        sb.AppendLine("Invoke with /skillname or the model can use SkillTool.");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"## {skill.Name}");
            sb.AppendLine(skill.Description);
            if (!string.IsNullOrWhiteSpace(skill.WhenToUse))
                sb.AppendLine($"Use when: {skill.WhenToUse}");
            if (skill.AllowedTools.Count > 0)
                sb.AppendLine($"Tools allowed: {string.Join(", ", skill.AllowedTools)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static SkillDefinition? LoadSkillFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        if (!content.StartsWith("---")) return null;

        var endIdx = content.IndexOf("---", 3);
        if (endIdx < 0) return null;

        var frontmatter = content[3..endIdx].Trim();
        var body = content[(endIdx + 3)..].Trim();

        var name = ExtractFrontmatterValue(frontmatter, "name")
                   ?? Path.GetFileNameWithoutExtension(filePath);
        var description = ExtractFrontmatterValue(frontmatter, "description") ?? "";
        var whenToUse = ExtractFrontmatterValue(frontmatter, "when_to_use") ?? "";
        var allowedTools = ExtractFrontmatterList(frontmatter, "allowed-tools");
        var filePatterns = ExtractFrontmatterList(frontmatter, "paths");
        var model = ExtractFrontmatterValue(frontmatter, "model");
        var argumentHint = ExtractFrontmatterValue(frontmatter, "argument-hint");

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            WhenToUse = whenToUse,
            PromptTemplate = body,
            AllowedTools = allowedTools,
            FilePatterns = filePatterns,
            Model = model,
            ArgumentHint = argumentHint,
            SourcePath = filePath
        };
    }

    private static string? ExtractFrontmatterValue(string frontmatter, string key)
    {
        var match = Regex.Match(frontmatter, $"^{key}:\\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static IReadOnlyList<string> ExtractFrontmatterList(string frontmatter, string key)
    {
        var match = Regex.Match(frontmatter, $"^{key}:\\s*\\n((?:\\s*-\\s*.+\\n)+)", RegexOptions.Multiline);
        if (!match.Success) return [];

        return match.Groups[1].Value
            .Split('\n')
            .Select(l => l.TrimStart().TrimStart('-').Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    private static bool MatchesGlob(string pattern, string path)
    {
        // Simple glob matching: *.cs matches foo.cs, **/*.cs matches foo/bar.cs
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(path.Replace('\\', '/'), regexPattern, RegexOptions.IgnoreCase);
    }
}
