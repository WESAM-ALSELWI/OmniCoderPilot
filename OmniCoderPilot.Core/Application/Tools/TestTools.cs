using System.Text.Json.Nodes;

namespace OmniCoderPilot.Application.Tools;

// ── Test Runner Tools ─────────────────────────────────────────────────────────

/// <summary>
/// Detects the test framework and runs all tests in the workspace.
/// Parses output into structured pass/fail/error results.
/// </summary>
public sealed class RunTestsTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "RunTests";
    public string Description =>
        "Discover and run all tests in the workspace. Auto-detects the framework (xUnit/NUnit/MSTest for .NET, pytest for Python, Jest/Vitest for JS/TS). " +
        "Returns a structured pass/fail summary. Always run this after editing code to verify correctness. " +
        "If tests fail, fix the failing tests before declaring the task complete.";
    public JsonObject Schema => ToolJson.Schema(
        ("filter", "string", "Optional: filter by test name, class, or namespace (e.g. 'MyClass' or 'AuthTests')."),
        ("timeoutSeconds", "number", "Timeout in seconds (default 120, max 600)."));
    public bool IsReadOnly => false; // runs code

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var filter = ToolJson.String(input, "filter");
        var timeout = Math.Clamp(ToolJson.Int(input, "timeoutSeconds", 120), 10, 600);

        // Auto-detect framework
        var command = await DetectTestCommand(context.WorkspaceRoot, filter);
        if (command is null)
            return new(Name, false,
                "No test framework detected in this workspace.\n" +
                "Supported: .NET (xUnit/NUnit/MSTest via 'dotnet test'), Python (pytest), JavaScript (jest/vitest via npm test).\n" +
                "Make sure test files exist and the project is built.");

        var output = new System.Text.StringBuilder();
        await foreach (var line in terminal.RunPowerShellAsync(context.WorkspaceRoot, command, ct, timeout))
            output.Append(line);

        var result = output.ToString();
        var summary = ParseTestSummary(result);
        var success = summary.Failed == 0 && summary.Errors == 0;

        var report = new System.Text.StringBuilder();
        report.AppendLine($"Test run: {summary.Passed} passed, {summary.Failed} failed, {summary.Skipped} skipped");
        if (!success)
        {
            report.AppendLine("\n--- FAILURES ---");
            report.AppendLine(summary.FailureDetails);
        }
        report.AppendLine("\n--- FULL OUTPUT ---");
        // Limit output to last 3000 chars to avoid context flooding
        var trimmed = result.Length > 3000 ? "...\n" + result[^3000..] : result;
        report.Append(trimmed);

        return new(Name, success, report.ToString());
    }

    private static async Task<string?> DetectTestCommand(string root, string filter)
    {
        // .NET: look for *.Tests.csproj or any .csproj with test framework references
        var testProjects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f =>
            {
                try
                {
                    var content = File.ReadAllText(f);
                    return content.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("mstest", StringComparison.OrdinalIgnoreCase) ||
                           content.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            })
            .ToList();

        if (testProjects.Count > 0)
        {
            var filterPart = !string.IsNullOrWhiteSpace(filter) ? $" --filter \"{filter}\"" : "";
            return $"dotnet test{filterPart} --no-build --logger \"console;verbosity=normal\" 2>&1";
        }

        // Python: look for pytest
        if (File.Exists(Path.Combine(root, "pytest.ini")) ||
            File.Exists(Path.Combine(root, "setup.cfg")) ||
            Directory.EnumerateFiles(root, "test_*.py", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(root, "*_test.py", SearchOption.AllDirectories).Any())
        {
            var filterPart = !string.IsNullOrWhiteSpace(filter) ? $" -k \"{filter}\"" : "";
            return $"python -m pytest{filterPart} -v 2>&1";
        }

        // JavaScript/TypeScript
        if (File.Exists(Path.Combine(root, "package.json")))
        {
            try
            {
                var pkg = await File.ReadAllTextAsync(Path.Combine(root, "package.json"));
                if (pkg.Contains("jest", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("vitest", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("mocha", StringComparison.OrdinalIgnoreCase))
                {
                    var filterPart = !string.IsNullOrWhiteSpace(filter) ? $" -- --testNamePattern=\"{filter}\"" : "";
                    return $"npm test{filterPart} 2>&1";
                }
            }
            catch { }
        }

        return null;
    }

    private static (int Passed, int Failed, int Skipped, int Errors, string FailureDetails) ParseTestSummary(string output)
    {
        int passed = 0, failed = 0, skipped = 0, errors = 0;
        var failureLines = new System.Text.StringBuilder();

        // .NET xUnit/NUnit patterns
        var summaryMatch = System.Text.RegularExpressions.Regex.Match(output,
            @"(?:Passed|passed)\s*:\s*(\d+).*?(?:Failed|failed)\s*:\s*(\d+).*?(?:Skipped|skipped)\s*:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (summaryMatch.Success)
        {
            int.TryParse(summaryMatch.Groups[1].Value, out passed);
            int.TryParse(summaryMatch.Groups[2].Value, out failed);
            int.TryParse(summaryMatch.Groups[3].Value, out skipped);
        }

        // pytest patterns
        var pytestMatch = System.Text.RegularExpressions.Regex.Match(output,
            @"(\d+) passed(?:,\s*(\d+) failed)?(?:,\s*(\d+) error)?");
        if (pytestMatch.Success)
        {
            int.TryParse(pytestMatch.Groups[1].Value, out passed);
            if (pytestMatch.Groups[2].Success) int.TryParse(pytestMatch.Groups[2].Value, out failed);
            if (pytestMatch.Groups[3].Success) int.TryParse(pytestMatch.Groups[3].Value, out errors);
        }

        // Extract failure details — lines containing FAIL or Error
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Assert", StringComparison.OrdinalIgnoreCase))
            {
                failureLines.AppendLine(line.Trim());
            }
        }

        return (passed, failed, skipped, errors, failureLines.ToString());
    }
}

/// <summary>Runs a single specific test by name — faster than running all tests.</summary>
public sealed class RunSpecificTestTool(ITerminalService terminal) : IAgentTool
{
    public string Name => "RunSpecificTest";
    public string Description =>
        "Run a single specific test or test class by name. Faster than RunTests when you only need to verify one thing. " +
        "Use the exact test method name or class name.";
    public JsonObject Schema => ToolJson.Schema(
        ("testName", "string", "Exact test method name, class name, or namespace to run."),
        ("timeoutSeconds", "number", "Timeout in seconds (default 60)."));
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var testName = ToolJson.String(input, "testName");
        if (string.IsNullOrWhiteSpace(testName))
            return new(Name, false, "testName is required.");

        var timeout = Math.Clamp(ToolJson.Int(input, "timeoutSeconds", 60), 10, 300);
        var command = $"dotnet test --filter \"{testName}\" --logger \"console;verbosity=detailed\" 2>&1";

        var output = new System.Text.StringBuilder();
        await foreach (var line in terminal.RunPowerShellAsync(context.WorkspaceRoot, command, ct, timeout))
            output.Append(line);

        var result = output.ToString();
        var hasFailure = result.Contains("Failed!", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase);
        return new(Name, !hasFailure, result.Length > 4000 ? result[^4000..] : result);
    }
}
