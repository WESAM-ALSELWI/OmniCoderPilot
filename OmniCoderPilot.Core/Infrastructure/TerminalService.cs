using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Infrastructure;

// ── One-shot terminal (original behaviour) ────────────────────────────────────

public sealed class TerminalService : ITerminalService
{
    public async IAsyncEnumerable<string> RunPowerShellAsync(
        string root,
        string command,
        [EnumeratorCancellation] CancellationToken ct,
        int timeoutSeconds = 300)
    {
        var shell = ResolveShell();
        var psi = new ProcessStartInfo(shell,
            $"-NoLogo -NoProfile -ExecutionPolicy Bypass -Command {Quote(command)}")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await p.StandardOutput.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    await lines.Writer.WriteAsync("[OUT] " + line + "\n", cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await p.StandardError.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    await lines.Writer.WriteAsync("[ERR] " + line + "\n", cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        _ = Task.Run(async () =>
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            lines.Writer.Complete();
        });

        await foreach (var line in lines.Reader.ReadAllAsync(cts.Token))
        {
            yield return line;
            if (cts.Token.IsCancellationRequested) break;
        }

        try { await p.WaitForExitAsync(cts.Token); } catch { }
        var exitCode = 0;
        try { exitCode = p.ExitCode; } catch { }
        yield return $"\nExit code: {exitCode}\n";
    }

    internal static string ResolveShell() =>
        File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "powershell.exe";

    internal static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

// ── Persistent shell session ──────────────────────────────────────────────────
// Interfaces IPersistentShell and IPersistentShellFactory are defined in Application/Abstractions.cs

/// <summary>
/// Keeps a single long-lived PowerShell process alive per conversation.
/// Environment variables, $pwd changes, and installed packages persist
/// between successive RunAsync calls — exactly like a real terminal session.
/// </summary>
public sealed class PersistentPowerShellSession : IPersistentShell
{
    private readonly Process _process;
    private readonly Channel<string> _outputChannel;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string Sentinel = "<<<OMNICODERPILOT_CMD_DONE_";
    private bool _disposed;

    public bool IsAlive => !_disposed && !_process.HasExited;

    public PersistentPowerShellSession(string workingDirectory)
    {
        var shell = TerminalService.ResolveShell();
        var psi = new ProcessStartInfo(shell, "-NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start persistent PowerShell.");
        _outputChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });

        // Continuously pump stdout → channel
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && !_process.HasExited)
                {
                    var line = await _process.StandardOutput.ReadLineAsync();
                    if (line is null) break;
                    await _outputChannel.Writer.WriteAsync(line);
                }
            }
            catch { }
            finally { _outputChannel.Writer.TryComplete(); }
        });

        // Pump stderr → channel with [ERR] prefix
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && !_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line is null) break;
                    await _outputChannel.Writer.WriteAsync("[ERR] " + line);
                }
            }
            catch { }
        });
    }

    public async IAsyncEnumerable<string> RunAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct,
        int timeoutSeconds = 120)
    {
        if (!IsAlive)
        {
            yield return "[ERR] Shell session has died. A new session will be created on next call.\n";
            yield return "\nExit code: -1\n";
            yield break;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var sentinelLine = $"{Sentinel}{tag}>>>";

            // Write the command followed by a sentinel echo so we know when it's done
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.WriteLineAsync($"Write-Host '{sentinelLine}'");
            await _process.StandardInput.FlushAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            int exitCode = 0;
            // Collect output until sentinel
            while (!cts.Token.IsCancellationRequested)
            {
                string line;
                bool timedOut = false;
                try { line = await _outputChannel.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) { timedOut = true; line = ""; }
                if (timedOut) { yield return "\nExit code: -1 (timeout)\n"; yield break; }

                if (line.Contains(sentinelLine))
                {
                    // After sentinel, get $LASTEXITCODE
                    await _process.StandardInput.WriteLineAsync("Write-Host \"EXIT:$LASTEXITCODE\"");
                    await _process.StandardInput.FlushAsync();
                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        var exitLine = await _outputChannel.Reader.ReadAsync(cts2.Token);
                        if (exitLine.StartsWith("EXIT:"))
                            int.TryParse(exitLine[5..].Trim(), out exitCode);
                    }
                    catch { }
                    yield return $"\nExit code: {exitCode}\n";
                    yield break;
                }
                yield return line + "\n";
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _process.StandardInput.WriteLineAsync("exit");
            await _process.StandardInput.FlushAsync();
            await Task.Delay(200);
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
    }
}

/// <summary>
/// Factory that creates and caches one persistent shell per working directory.
/// Sessions are automatically recycled if the process dies.
/// </summary>
public sealed class PersistentShellFactory : IPersistentShellFactory, IDisposable
{
    private readonly Dictionary<string, PersistentPowerShellSession> _sessions = new();
    private readonly Lock _lock = new();

    public IPersistentShell Create(string workingDirectory)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(workingDirectory, out var existing) && existing.IsAlive)
                return existing;

            existing?.DisposeAsync().AsTask().Wait(500);
            var session = new PersistentPowerShellSession(workingDirectory);
            _sessions[workingDirectory] = session;
            return session;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                s.DisposeAsync().AsTask().Wait(500);
            _sessions.Clear();
        }
    }
}
