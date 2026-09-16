using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace OmniCoderPilot.Application.Tools;

internal static class ToolJson
{
    public static string String(JsonObject input, string name, string fallback = "") =>
        input[name]?.GetValue<string>() ?? fallback;

    public static int Int(JsonObject input, string name, int fallback) =>
        input[name]?.GetValue<int?>() ?? fallback;

    public static bool Bool(JsonObject input, string name, bool fallback = false) =>
        input[name]?.GetValue<bool?>() ?? fallback;

    public static JsonObject Schema(params (string Name, string Type, string Description)[] fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject
            {
                ["type"] = field.Type,
                ["description"] = field.Description
            };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
    }
}

// ── Core File Tools ──────────────────────────────────────────────────────────

public sealed class ReadFileTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "ReadFile";
    public string Description => "Read a UTF-8 text file inside the workspace. Returns file contents with line numbers prepended so you can reference exact lines when editing.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Workspace-relative file path."),
        ("startLine", "number", "Optional 1-based start line (inclusive)."),
        ("endLine", "number", "Optional 1-based end line (inclusive)."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");

        // Check if path is a directory
        var fullPath = Path.Combine(context.WorkspaceRoot, path);
        if (Directory.Exists(fullPath))
        {
            var dirFiles = Directory.GetFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f))
                .Take(10);
            return new(Name, false,
                $"'{path}' is a directory, not a file.\n" +
                $"Files in directory: {string.Join(", ", dirFiles)}\n" +
                $"Use ListDirectory to browse directories, or specify a file path.");
        }

        try
        {
            var content = await files.ReadFileAsync(context.WorkspaceId, path, ct);
            var lines = content.Split('\n');
            var start = Math.Max(1, ToolJson.Int(input, "startLine", 1));
            var end = Math.Min(lines.Length, ToolJson.Int(input, "endLine", lines.Length));
            var sb = new StringBuilder();
            for (var i = start - 1; i < end; i++)
                sb.AppendLine($"{i + 1}: {lines[i]}");
            return new(Name, true, sb.ToString());
        }
        catch (UnauthorizedAccessException)
        {
            return new(Name, false,
                $"Access denied: {path}\n" +
                $"This path may be a directory or you don't have permission.\n" +
                $"Use ListDirectory to browse, or specify a file path.");
        }
        catch (FileNotFoundException)
        {
            var files_in_dir = Directory.Exists(Path.GetDirectoryName(Path.Combine(context.WorkspaceRoot, path)))
                ? Directory.GetFiles(Path.GetDirectoryName(Path.Combine(context.WorkspaceRoot, path))!, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f))
                    .Take(10)
                : [];
            var hint = files_in_dir.Any()
                ? $"\nFiles in directory: {string.Join(", ", files_in_dir)}"
                : "\nDirectory may not exist.";
            return new(Name, false,
                $"File not found: {path}.{hint}\n" +
                $"Use ListDirectory or SearchFiles to find the correct file path.");
        }
    }
}

public sealed class WriteFileTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "WriteFile";
    public string Description => "Create or completely replace a UTF-8 text file inside the workspace. Use EditFile for partial edits to avoid losing unchanged content.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Workspace-relative file path."),
        ("content", "string", "Complete file content."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var content = ToolJson.String(input, "content");
        await files.WriteFileAsync(context.WorkspaceId, path, content, ct);
        var lineCount = content.Split('\n').Length;
        return new(Name, true, $"Wrote {path} ({lineCount} lines)");
    }
}

public sealed class AppendFileTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "AppendFile";
    public string Description => "Append text to the end of a file without overwriting it. Creates the file if it does not exist.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Workspace-relative file path."),
        ("content", "string", "Text to append."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var existing = "";
        try { existing = await files.ReadFileAsync(context.WorkspaceId, path, ct); } catch { }
        var appended = existing + ToolJson.String(input, "content");
        await files.WriteFileAsync(context.WorkspaceId, path, appended, ct);
        return new(Name, true, $"Appended to {path}");
    }
}

public sealed class EditFileTool(IWorkspaceFileService files, IDiffService diff) : IAgentTool
{
    public string Name => "EditFile";
    public string Description =>
        "Edit a file by replacing an exact block of text. " +
        "IMPORTANT: Use ReadFile first to get the exact text including all whitespace and newlines. " +
        "oldText must be an exact character-for-character match. " +
        "The tool validates that oldText appears exactly once — if it appears 0 or 2+ times it will fail with guidance. " +
        "Omit oldText to replace the entire file content.";
    public JsonObject Schema => ToolJson.Schema(
        ("relativePath", "string", "Workspace-relative file path."),
        ("oldText", "string", "Exact text block to find and replace. Must appear exactly once in the file."),
        ("newText", "string", "Replacement text to substitute in place of oldText."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");

        // Check if path is a directory
        var fullPath = Path.Combine(context.WorkspaceRoot, path);
        if (Directory.Exists(fullPath))
        {
            var dirFiles = Directory.GetFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f))
                .Take(10);
            return new(Name, false,
                $"'{path}' is a directory, not a file. Cannot edit a directory.\n" +
                $"Files in directory: {string.Join(", ", dirFiles)}\n" +
                $"Specify a file path to edit.");
        }

        string original;
        try
        {
            original = await files.ReadFileAsync(context.WorkspaceId, path, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return new(Name, false,
                $"Access denied: {path}\n" +
                $"This path may be a directory or you don't have permission.\n" +
                $"Use ListDirectory to browse, or specify a file path.");
        }
        catch (FileNotFoundException)
        {
            var files_in_dir = Directory.Exists(Path.GetDirectoryName(Path.Combine(context.WorkspaceRoot, path)))
                ? Directory.GetFiles(Path.GetDirectoryName(Path.Combine(context.WorkspaceRoot, path))!, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f))
                    .Take(10)
                : [];
            var hint = files_in_dir.Any()
                ? $"\nFiles in directory: {string.Join(", ", files_in_dir)}"
                : "\nDirectory may not exist.";
            return new(Name, false,
                $"File not found: {path}.{hint}\n" +
                $"Use ListDirectory or SearchFiles to find the correct file path.");
        }
        var oldText = ToolJson.String(input, "oldText");
        var newText = ToolJson.String(input, "newText");

        // Whole-file replacement when oldText is omitted
        if (string.IsNullOrEmpty(oldText))
        {
            await files.WriteFileAsync(context.WorkspaceId, path, newText, ct);
            return new(Name, true, diff.CreateUnifiedDiff(path, original, newText));
        }

        // Count occurrences for exact-match validation
        var count = CountOccurrences(original, oldText);
        if (count == 0)
        {
            // Auto-retry: re-read the file in case it changed since the agent last read it
            var freshContent = await files.ReadFileAsync(context.WorkspaceId, path, ct);
            var freshCount = CountOccurrences(freshContent, oldText);
            if (freshCount == 1)
            {
                var updated = freshContent.Replace(oldText, newText, StringComparison.Ordinal);
                await files.WriteFileAsync(context.WorkspaceId, path, updated, ct);
                var unifiedDiff = diff.CreateUnifiedDiff(path, freshContent, updated);
                return new(Name, true, $"Edited {path} successfully (after re-read).\n{unifiedDiff}");
            }

            // Try whitespace-normalised fallback
            var normFile = NormalizeWhitespace(freshContent);
            var normOld  = NormalizeWhitespace(oldText);
            if (normFile.Contains(normOld, StringComparison.Ordinal))
                return new(Name, false,
                    $"No exact match found in {path} — the text exists but with different whitespace/newlines. " +
                    $"Call ReadFile to get the exact content, then retry EditFile with the exact characters shown.");

            // Enhanced error with line count and context snippet
            var fileLines = freshContent.Split('\n');
            var lineCount = fileLines.Length;
            var firstOldLine = oldText.Split('\n')[0].Trim();
            var snippetLines = FindNearestSnippet(fileLines, firstOldLine);
            var snippet = snippetLines.Count > 0
                ? $"\nNearest matching content (around line {snippetLines[0].LineNumber}):\n{snippetLines[0].Content}"
                : "";

            return new(Name, false,
                $"oldText not found in {path}. " +
                $"The file has {lineCount} lines. " +
                snippet +
                $"\nFirst line of oldText: \"{firstOldLine}\" " +
                $"\nCall ReadFile to read the current content before calling EditFile.");
        }
        if (count > 1)
            return new(Name, false,
                $"oldText appears {count} times in {path} — it must be unique. " +
                $"Expand oldText to include more surrounding lines so it is unambiguous.");

        var finalUpdated = original.Replace(oldText, newText, StringComparison.Ordinal);
        await files.WriteFileAsync(context.WorkspaceId, path, finalUpdated, ct);
        var finalDiff = diff.CreateUnifiedDiff(path, original, finalUpdated);
        return new(Name, true, $"Edited {path} successfully.\n{finalDiff}");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0; var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0) { count++; idx += pattern.Length; }
        return count;
    }

    private static string NormalizeWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static List<(int LineNumber, string Content)> FindNearestSnippet(string[] fileLines, string searchLine)
    {
        if (string.IsNullOrWhiteSpace(searchLine)) return [];

        // Try to find the first non-whitespace-only line from searchLine
        var significantPart = searchLine.Split(new[] { '(', ')', '{', '}', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? searchLine;

        if (significantPart.Length < 3) return [];

        // Search for partial match
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (fileLines[i].Contains(significantPart, StringComparison.OrdinalIgnoreCase))
            {
                var start = Math.Max(0, i - 1);
                var end = Math.Min(fileLines.Length - 1, i + 1);
                var result = new List<(int, string)>();
                for (var j = start; j <= end; j++)
                    result.Add((j + 1, $"{j + 1}: {fileLines[j]}"));
                return result;
            }
        }

        return [];
    }
}

public sealed class DeleteFileTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "DeleteFile";
    public string Description => "Delete a file inside the workspace. This is irreversible — use with care.";
    public JsonObject Schema => ToolJson.Schema(("relativePath", "string", "Workspace-relative file path."));
    public bool IsReadOnly => false;
    public bool IsDangerous => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        await files.DeleteFileAsync(context.WorkspaceId, path, ct);
        return new(Name, true, $"Deleted {path}");
    }
}

public sealed class GetFileInfoTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "GetFileInfo";
    public string Description => "Get metadata about a file (size, line count, extension) without reading its contents. Use before ReadFile on unknown files to avoid reading huge binaries.";
    public JsonObject Schema => ToolJson.Schema(("relativePath", "string", "Workspace-relative file path."));
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var relative = ToolJson.String(input, "relativePath");
        var full = files.ResolveInsideWorkspace(context.WorkspaceRoot, relative);
        if (!File.Exists(full))
            return Task.FromResult(new ToolResult(Name, false, $"File not found: {relative}"));
        var info = new FileInfo(full);
        var lineCount = -1;
        try { lineCount = File.ReadAllLines(full).Length; } catch { }
        return Task.FromResult(new ToolResult(Name, true,
            $"Path: {relative}\nSize: {info.Length:N0} bytes\nLines: {(lineCount >= 0 ? lineCount.ToString("N0") : "binary")}\nExtension: {info.Extension}\nModified: {info.LastWriteTimeUtc:u}"));
    }
}

// ── Search Tools ─────────────────────────────────────────────────────────────

public sealed class SearchFilesTool : IAgentTool
{
    public string Name => "SearchFiles";
    public string Description => "Find files by name pattern inside the workspace. Supports wildcards (* and ?). Returns relative paths.";
    public JsonObject Schema => ToolJson.Schema(
        ("query", "string", "Case-insensitive path/name query. Supports wildcards."),
        ("limit", "number", "Maximum results (default 50)."));
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var query = ToolJson.String(input, "query");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 50), 1, 200);
        var rows = Directory.EnumerateFiles(context.WorkspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(path) && Path.GetRelativePath(context.WorkspaceRoot, path).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(path => Path.GetRelativePath(context.WorkspaceRoot, path));
        return Task.FromResult(new ToolResult(Name, true, string.Join('\n', rows)));
    }

    private static bool IsIgnored(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}

public sealed class GlobTool : IAgentTool
{
    public string Name => "Glob";
    public string Description => "Find files matching a glob pattern (e.g. '**/*.cs', 'src/**/*.py', '*.sln'). Faster and more precise than SearchFiles for known patterns.";
    public JsonObject Schema => ToolJson.Schema(
        ("pattern", "string", "Glob pattern relative to workspace root. Examples: '**/*.cs', 'src/**/*.py', '*.json'"),
        ("limit", "number", "Maximum results (default 100)."));
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var pattern = ToolJson.String(input, "pattern");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 100), 1, 500);

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);
        matcher.AddExclude("**/bin/**");
        matcher.AddExclude("**/obj/**");
        matcher.AddExclude("**/.git/**");
        matcher.AddExclude("**/node_modules/**");

        var dir = new DirectoryInfoWrapper(new DirectoryInfo(context.WorkspaceRoot));
        var result = matcher.Execute(dir);
        var files = result.Files.Take(limit).Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar)).ToList();

        return Task.FromResult(new ToolResult(Name, true,
            files.Count == 0
                ? $"No files matched pattern '{pattern}'"
                : $"Found {files.Count} file(s):\n{string.Join('\n', files)}"));
    }
}

public sealed class SearchTextTool : IAgentTool
{
    public string Name => "SearchText";
    public string Description => "Search for literal text in workspace files. Returns file:line matches. Use GrepText for regex patterns.";
    public JsonObject Schema => ToolJson.Schema(
        ("query", "string", "Literal text to search for."),
        ("limit", "number", "Maximum matches (default 50)."),
        ("filePattern", "string", "Optional glob pattern to restrict search, e.g. '*.cs'"));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var query = ToolJson.String(input, "query");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 50), 1, 200);
        var filePattern = ToolJson.String(input, "filePattern", "*");
        var matches = new List<string>();

        IEnumerable<string> files = Directory.EnumerateFiles(context.WorkspaceRoot, filePattern, SearchOption.AllDirectories);
        foreach (var path in files)
        {
            if (matches.Count >= limit || IsIgnored(path) || IsLikelyBinary(path)) continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(path, ct); } catch { continue; }
            for (var i = 0; i < lines.Length && matches.Count < limit; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add($"{Path.GetRelativePath(context.WorkspaceRoot, path)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return new(Name, true, matches.Count == 0 ? $"No matches for '{query}'" : string.Join('\n', matches));
    }

    private static bool IsIgnored(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyBinary(string path) =>
        new[] { ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".db", ".zip", ".nupkg" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed class GrepTool : IAgentTool
{
    public string Name => "GrepText";
    public string Description => "Search workspace files using a regular expression. Returns file:line:match triples with context. Use for finding classes, methods, usages, imports, etc.";
    public JsonObject Schema => ToolJson.Schema(
        ("pattern", "string", "Regular expression pattern (C# Regex syntax)."),
        ("filePattern", "string", "Glob pattern to limit which files are searched, e.g. '*.cs', '*.py'. Default: all text files."),
        ("limit", "number", "Maximum matches (default 50)."),
        ("caseInsensitive", "boolean", "If true, match case-insensitively."));
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var pattern = ToolJson.String(input, "pattern");
        var filePattern = ToolJson.String(input, "filePattern", "*");
        var limit = Math.Clamp(ToolJson.Int(input, "limit", 50), 1, 300);
        var ignoreCase = ToolJson.Bool(input, "caseInsensitive", false);

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, opts, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return new(Name, false, $"Invalid regex: {ex.Message}");
        }

        var matches = new List<string>();
        IEnumerable<string> files = Directory.EnumerateFiles(context.WorkspaceRoot, filePattern, SearchOption.AllDirectories);
        foreach (var path in files)
        {
            if (matches.Count >= limit || IsIgnored(path) || IsLikelyBinary(path)) continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(path, ct); } catch { continue; }
            for (var i = 0; i < lines.Length && matches.Count < limit; i++)
            {
                if (regex.IsMatch(lines[i]))
                    matches.Add($"{Path.GetRelativePath(context.WorkspaceRoot, path)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return new(Name, true, matches.Count == 0 ? $"No matches for pattern '{pattern}'" : string.Join('\n', matches));
    }

    private static bool IsIgnored(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyBinary(string path) =>
        new[] { ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".db", ".zip", ".nupkg" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

// ── Command Execution ─────────────────────────────────────────────────────────

/// <summary>
/// Run a command in a persistent PowerShell session.
/// Environment variables, cd changes, and installed tools persist between calls.
/// Use this for multi-step workflows where you need state to carry over.
/// </summary>
public sealed class PersistentShellTool(IPersistentShellFactory shellFactory) : IAgentTool
{
    public string Name => "RunInSession";
    public string Description =>
        "Run a command in a PERSISTENT shell session for the workspace — environment variables, " +
        "current directory (cd), and installed tools carry over between calls. " +
        "Use when you need: set env vars and then run commands that need them, " +
        "activate a virtualenv and run multiple Python commands, or chain multi-step shell workflows. " +
        "For simple one-off commands, use ExecuteCommand instead.";
    public bool IsDangerous => true;
    public JsonObject Schema => ToolJson.Schema(
        ("command", "string", "PowerShell command to run in the persistent session."),
        ("timeoutSeconds", "number", "Timeout per command in seconds (default 120)."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var cmd = ToolJson.String(input, "command");
        if (string.IsNullOrWhiteSpace(cmd)) return new(Name, false, "command is required.");
        var timeout = Math.Clamp(ToolJson.Int(input, "timeoutSeconds", 120), 5, 600);

        var shell = shellFactory.Create(context.WorkspaceRoot);
        var output = new System.Text.StringBuilder();
        await foreach (var line in shell.RunAsync(cmd, ct, timeout))
            output.Append(line);

        var result = output.ToString();
        var exitCodeMatch = System.Text.RegularExpressions.Regex.Match(result, @"Exit code: (-?\d+)");
        var exitCode = exitCodeMatch.Success ? int.Parse(exitCodeMatch.Groups[1].Value) : 0;
        return new(Name, exitCode == 0, result);
    }
}

public sealed class ExecuteCommandTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "ExecuteCommand";
    public string Description => "Run a PowerShell command in the workspace root and return the combined stdout+stderr output with exit code. Use for: dotnet restore, dotnet build, dotnet run, pip install, python, npm, git, etc. Always check the exit code at the end of output.";
    public bool IsDangerous => true;
    public JsonObject Schema => ToolJson.Schema(
        ("command", "string", "PowerShell command to run from the workspace root."),
        ("timeoutSeconds", "number", "Timeout in seconds. Default 300 (5 min). Use 600+ for long builds."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var cmd = ToolJson.String(input, "command");
        var timeout = Math.Clamp(ToolJson.Int(input, "timeoutSeconds", 300), 10, 1800);
        var output = new List<string>();
        await foreach (var line in terminal.RunPowerShellAsync(context.WorkspaceRoot, cmd, ct, timeout))
            output.Add(line);
        var result = string.Concat(output);

        // Check exit code first (most reliable)
        var exitCodeMatch = System.Text.RegularExpressions.Regex.Match(result, @"Exit code: (-?\d+)");
        var hasExitCode = exitCodeMatch.Success;
        var exitCode = hasExitCode ? int.Parse(exitCodeMatch.Groups[1].Value) : -1;

        // Determine if this is a build/test/restore command
        var cmdLower = cmd.ToLowerInvariant();
        var isBuildCommand = cmdLower.Contains("dotnet build") ||
                             cmdLower.Contains("dotnet restore") ||
                             cmdLower.Contains("dotnet test") ||
                             cmdLower.Contains("dotnet run") ||
                             cmdLower.Contains("npm run build") ||
                             cmdLower.Contains("npm install") ||
                             cmdLower.Contains("cargo build") ||
                             cmdLower.Contains("go build");

        // Count actual errors vs warnings
        var lines = result.Split('\n');
        var errorLines = lines.Count(l =>
            l.Contains(": error ") ||
            l.Contains(" error CS") ||
            l.Contains("BUILD FAILED") ||
            l.Contains("Build FAILED") ||
            l.Contains("error MSB") ||
            l.Contains("error NU"));
        var warningLines = lines.Count(l =>
            l.Contains(": warning ") ||
            l.Contains("warning NU") ||
            l.Contains("warning CS"));

        // Determine success
        bool success;
        if (hasExitCode)
        {
            // Exit code 0 = success (even with warnings)
            // Non-zero exit code = failure
            success = exitCode == 0;
        }
        else
        {
            success = errorLines == 0;
        }

        // Add summary for build commands
        if (isBuildCommand && (errorLines > 0 || warningLines > 0))
        {
            var summary = $"\n--- SUMMARY: {errorLines} error(s), {warningLines} warning(s) ---";
            if (errorLines == 0 && warningLines > 0)
                summary += "\nBuild SUCCEEDED with warnings. Warnings are OK - no fix needed.";
            else if (errorLines > 0)
                summary += "\nBuild FAILED with errors. Fix the errors above.";
            result += summary;
        }

        return new(Name, success, result);
    }
}

// ── Directory Tools ───────────────────────────────────────────────────────────

public sealed class ListDirectoryTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "ListDirectory";
    public string Description => "List immediate children of a workspace directory (dirs and files with sizes). Use to explore the project structure before reading files.";
    public JsonObject Schema => ToolJson.Schema(("relativePath", "string", "Workspace-relative directory path, empty string for root."));
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var relative = ToolJson.String(input, "relativePath");
        var path = files.ResolveInsideWorkspace(context.WorkspaceRoot, relative);
        if (!Directory.Exists(path))
            return Task.FromResult(new ToolResult(Name, false, $"Directory not found: {relative}"));
        var rows = Directory.EnumerateFileSystemEntries(path)
            .OrderBy(x => File.GetAttributes(x).HasFlag(FileAttributes.Directory) ? 0 : 1)
            .ThenBy(Path.GetFileName)
            .Select(x =>
            {
                if (Directory.Exists(x))
                    return $"dir  {Path.GetFileName(x)}/";
                var fi = new FileInfo(x);
                return $"file {Path.GetFileName(x)} ({fi.Length:N0} bytes)";
            });
        return Task.FromResult(new ToolResult(Name, true, string.Join('\n', rows)));
    }
}

public sealed class CreateDirectoryTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "CreateDirectory";
    public string Description => "Create a directory (and any parent directories) inside the workspace.";
    public JsonObject Schema => ToolJson.Schema(("relativePath", "string", "Workspace-relative directory path."));
    public bool IsReadOnly => false;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var relative = ToolJson.String(input, "relativePath");
        Directory.CreateDirectory(files.ResolveInsideWorkspace(context.WorkspaceRoot, relative));
        return Task.FromResult(new ToolResult(Name, true, $"Created directory {relative}"));
    }
}

public sealed class RenameFileTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "RenameFile";
    public string Description => "Rename or move a file or directory inside the workspace.";
    public JsonObject Schema => ToolJson.Schema(
        ("fromRelativePath", "string", "Current workspace-relative path."),
        ("toRelativePath", "string", "New workspace-relative path."));
    public bool IsReadOnly => false;

    public Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var fromRel = ToolJson.String(input, "fromRelativePath");
        var toRel = ToolJson.String(input, "toRelativePath");
        var from = files.ResolveInsideWorkspace(context.WorkspaceRoot, fromRel);
        var to = files.ResolveInsideWorkspace(context.WorkspaceRoot, toRel);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: true);
        return Task.FromResult(new ToolResult(Name, true, $"Renamed {fromRel} → {toRel}"));
    }
}
