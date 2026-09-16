using System.Text;
using System.Text.RegularExpressions;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// File-based persistent memory stored in .mycoder/memory/ directory.
/// Four memory types: User (preferences), Feedback (corrections),
/// Project (ongoing work), Reference (external links).
/// </summary>
public sealed partial class FileMemoryStore : IMemoryStore
{
    private readonly string _memoryDir;

    public FileMemoryStore(string workspaceRoot)
    {
        _memoryDir = Path.Combine(workspaceRoot, ".mycoder", "memory");
        Directory.CreateDirectory(_memoryDir);
    }

    /// <summary>
    /// Load all memories and build an index string for the system prompt.
    /// </summary>
    public string LoadMemoryIndex()
    {
        var indexFile = Path.Combine(_memoryDir, "MEMORY.md");
        if (!File.Exists(indexFile)) return "";

        var content = File.ReadAllText(indexFile);
        return content.Length > 25_000 ? content[..25_000] : content;
    }

    /// <summary>
    /// Save a memory to a markdown file with frontmatter.
    /// </summary>
    public void SaveMemory(string name, string description, MemoryType type, string content)
    {
        var sanitizedName = SanitizeFileName(name);
        var fileName = $"{sanitizedName}.md";
        var filePath = Path.Combine(_memoryDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {description}");
        sb.AppendLine($"type: {type.ToString().ToLowerInvariant()}");
        sb.AppendLine($"created: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(content);

        File.WriteAllText(filePath, sb.ToString());

        // Update MEMORY.md index
        UpdateIndex(name, fileName, type);
    }

    /// <summary>
    /// Search memories by scanning headers and returning relevant ones.
    /// </summary>
    public IReadOnlyList<MemoryEntry> SearchRelevant(string query, int maxResults = 5)
    {
        var entries = new List<MemoryEntry>();
        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        foreach (var file in Directory.GetFiles(_memoryDir, "*.md"))
        {
            if (Path.GetFileName(file).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var content = File.ReadAllText(file);
                var entry = ParseMemoryFile(file, content);
                if (entry is null) continue;

                // Score by word overlap
                var score = entry.Keywords.Count(kw => queryWords.Contains(kw.ToLowerInvariant()));
                if (score > 0 || entry.Type == MemoryType.Project)
                {
                    entries.Add(entry);
                }
            }
            catch { }
        }

        return entries
            .OrderByDescending(e => e.Type == MemoryType.User ? 10 : 0) // User memories always relevant
            .ThenByDescending(e => e.Keywords.Count(kw => queryWords.Contains(kw.ToLowerInvariant())))
            .ThenByDescending(e => e.Type == MemoryType.Feedback ? 5 : 0)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Extract memories from a conversation turn.
    /// </summary>
    public void ExtractFromConversation(string userPrompt, string assistantResponse)
    {
        // Extract user preferences (e.g., "I prefer...", "I want...", "Always...")
        var userPatterns = new[] { "i prefer", "i want", "always ", "never ", "i like", "i don't like" };
        foreach (var pattern in userPatterns)
        {
            if (userPrompt.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                SaveMemory(
                    $"user-pref-{Guid.NewGuid().ToString()[..8]}",
                    "User preference",
                    MemoryType.User,
                    $"User said: \"{userPrompt}\"");
                break;
            }
        }

        // Extract project context (e.g., file paths, error fixes, decisions)
        if (assistantResponse.Contains("Created ") || assistantResponse.Contains("Edited ") || assistantResponse.Contains("Fixed "))
        {
            var lines = assistantResponse.Split('\n')
                .Where(l => l.Contains("Created ") || l.Contains("Edited ") || l.Contains("Fixed "))
                .Take(3)
                .ToList();

            if (lines.Count > 0)
            {
                SaveMemory(
                    $"project-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    "Recent project changes",
                    MemoryType.Project,
                    string.Join("\n", lines));
            }
        }
    }

    private void UpdateIndex(string name, string fileName, MemoryType type)
    {
        var indexFile = Path.Combine(_memoryDir, "MEMORY.md");
        var entry = $"- [{type}] **{name}** — {fileName}";

        if (File.Exists(indexFile))
        {
            var content = File.ReadAllText(indexFile);
            // Don't add duplicates
            if (!content.Contains(fileName))
            {
                File.WriteAllText(indexFile, content.TrimEnd() + "\n" + entry + "\n");
            }
        }
        else
        {
            File.WriteAllText(indexFile, "# Memory Index\n\n" + entry + "\n");
        }
    }

    private static MemoryEntry? ParseMemoryFile(string filePath, string content)
    {
        if (!content.StartsWith("---")) return null;

        var endIdx = content.IndexOf("---", 3);
        if (endIdx < 0) return null;

        var frontmatter = content[3..endIdx].Trim();
        var body = content[(endIdx + 3)..].Trim();

        var name = ExtractFrontmatterValue(frontmatter, "name") ?? Path.GetFileNameWithoutExtension(filePath);
        var description = ExtractFrontmatterValue(frontmatter, "description") ?? "";
        var typeStr = ExtractFrontmatterValue(frontmatter, "type") ?? "project";
        var type = typeStr.ToLowerInvariant() switch
        {
            "user" => MemoryType.User,
            "feedback" => MemoryType.Feedback,
            "reference" => MemoryType.Reference,
            _ => MemoryType.Project
        };

        var keywords = Regex.Split(name + " " + description + " " + body, @"\W+")
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        return new MemoryEntry(name, description, type, body, keywords);
    }

    private static string? ExtractFrontmatterValue(string frontmatter, string key)
    {
        var match = Regex.Match(frontmatter, $"^{key}:\\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = Regex.Replace(name, @"[^\w\s-]", "");
        sanitized = Regex.Replace(sanitized, @"\s+", "-");
        return sanitized.ToLowerInvariant()[..Math.Min(sanitized.Length, 50)];
    }
}
