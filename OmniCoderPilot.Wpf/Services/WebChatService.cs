using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Wpf.Services;

/// <summary>
/// Chat2API-style service that drives https://chat.deepseek.com inside
/// a hidden WebView2 control.  The browser lives on the WPF UI thread but
/// is controlled from background threads via the Dispatcher.
///
/// Pipeline: Agent (background) → WebChatService → WebView2 (UI thread)
///           → DeepSeek Web → JS polling → Channel → Agent stream
/// </summary>
public sealed class WebChatService : IWebChatService, IDisposable
{
    // ── Configuration ────────────────────────────────────────────────────────
    public string ChatUrl     => "https://chat.deepseek.com";
    public string ProviderName => "DeepSeek Web";

    // ── State ────────────────────────────────────────────────────────────────
    private WebView2? _webView;
    private Window?   _loginWindow;
    private readonly Dispatcher _dispatcher;
    private bool _initialized;
    private bool _disposed;

    // ── Login detection ──────────────────────────────────────────────────────
    private TaskCompletionSource<bool>? _loginTcs;

    public bool IsLoggedIn { get; private set; }

    public WebChatService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Initialization — must run on UI thread
    // ═════════════════════════════════════════════════════════════════════════
    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _dispatcher.InvokeAsync(async () =>
        {
            _webView = new WebView2
            {
                Width  = 0,
                Height = 0,
                Visibility = Visibility.Collapsed
            };

            // Initialize the WebView2 control (requires Edge WebView2 runtime)
            var env = await CoreWebView2Environment.CreateAsync();
            await _webView.EnsureCoreWebView2Async(env);

            // Hook navigation events for login detection
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // Navigate to the chat page
            _webView.CoreWebView2.Navigate(ChatUrl);
        });
        _initialized = true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Login
    // ═════════════════════════════════════════════════════════════════════════
    public async Task<bool> ShowLoginAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync();

        // Check if already logged in
        var alreadyIn = await CheckLoggedInAsync();
        if (alreadyIn)
        {
            IsLoggedIn = true;
            return true;
        }

        _loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Show a login window with the WebView2 visible inside
        await _dispatcher.InvokeAsync(() =>
        {
            // Detach webview from any hidden host and make it visible
            _webView!.Width      = 900;
            _webView!.Height     = 660;
            _webView!.Visibility = Visibility.Visible;

            _loginWindow = new Window
            {
                Title  = "Log in to DeepSeek — close when done",
                Width  = 920,
                Height = 700,
                Content = _webView,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            _loginWindow.Closed += (_, _) =>
            {
                // Detach the WebView2 from the login window so it can keep being used
                _loginWindow.Content = null;
                _webView!.Width      = 0;
                _webView!.Height     = 0;
                _webView!.Visibility = Visibility.Collapsed;
                _loginTcs?.TrySetResult(IsLoggedIn);
            };

            _loginWindow.Show();
        });

        // Poll for login every 2 seconds (up to 5 minutes)
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            while (!pollCts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, pollCts.Token);
                var loggedIn = await CheckLoggedInAsync();
                if (loggedIn)
                {
                    IsLoggedIn = true;
                    await _dispatcher.InvokeAsync(() => _loginWindow?.Close());
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }

        // User may have closed the window manually — do a final check
        var finalCheck = await CheckLoggedInAsync();
        IsLoggedIn = finalCheck;
        return finalCheck;
    }

    /// <summary>
    /// Checks whether DeepSeek is showing the chat UI (i.e. user is logged in).
    /// Looks for elements that only appear after login.
    /// </summary>
    private async Task<bool> CheckLoggedInAsync()
    {
        if (_webView == null) return false;
        try
        {
            // InvokeAsync with async lambda returns Task<Task<bool>> — Unwrap() flattens it
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                // Check for the textarea / new chat button that only appears after login
                var js = """
                    (function() {
                        // DeepSeek shows a textarea with id="chat-input" when logged in
                        var input = document.querySelector('textarea#chat-input, textarea[placeholder], div[contenteditable="true"]');
                        var loginBtn  = document.querySelector('button[class*="login"], a[href*="/sign-in"], a[href*="/login"]');
                        if (input && !loginBtn) return "true";
                        return "false";
                    })()
                    """;
                var r = await _webView!.ExecuteScriptAsync(js);
                return r == "\"true\"";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch
        {
            return false;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Re-check login state after every navigation
        _ = Task.Run(async () =>
        {
            var loggedIn = await CheckLoggedInAsync();
            if (loggedIn && !IsLoggedIn)
            {
                IsLoggedIn = true;
                _loginTcs?.TrySetResult(true);
            }
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Send message + stream response
    // ═════════════════════════════════════════════════════════════════════════
    public async IAsyncEnumerable<string> SendMessageAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureInitializedAsync();

        if (!IsLoggedIn)
            throw new InvalidOperationException("Not logged in to " + ProviderName);

        // Start a new chat before sending
        await StartNewChatAsync();
        await Task.Delay(1500, ct); // let page settle

        // Type the message into the input
        await TypeMessageAsync(message);
        await Task.Delay(500, ct);

        // Submit
        await SubmitMessageAsync();

        // Stream back response by polling DOM
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });

        _ = Task.Run(() => PollResponseAsync(channel.Writer, ct), ct);

        await foreach (var token in channel.Reader.ReadAllAsync(ct))
        {
            yield return token;
        }
    }

    // ── Navigate to a fresh chat ──────────────────────────────────────────────
    private async Task StartNewChatAsync()
    {
        await _dispatcher.InvokeAsync(async () =>
        {
            // Try clicking the "New Chat" button first; fall back to navigating to root
            var js = """
                (function() {
                    var btn = document.querySelector('button[aria-label="New chat"], button[data-testid="new-chat-button"], a[href="/"]');
                    if (btn) { btn.click(); return "clicked"; }
                    return "none";
                })()
                """;
            var r = await _webView!.ExecuteScriptAsync(js);
            if (r == "\"none\"")
                _webView!.CoreWebView2.Navigate(ChatUrl);
        });
        await Task.Delay(1800); // let new chat load
    }

    // ── Type message into textarea ────────────────────────────────────────────
    private async Task TypeMessageAsync(string message)
    {
        // Escape the message for JSON string embedding
        var escaped = System.Text.Json.JsonSerializer.Serialize(message);

        await _dispatcher.InvokeAsync(async () =>
        {
            var js = $$"""
                (function() {
                    var input = document.querySelector('textarea#chat-input')
                             || document.querySelector('textarea[placeholder]')
                             || document.querySelector('div[contenteditable="true"]');
                    if (!input) return "no-input";

                    // Focus and set value
                    input.focus();

                    // Use React's synthetic event system to properly update state
                    var nativeInputValueSetter = Object.getOwnPropertyDescriptor(
                        window.HTMLTextAreaElement.prototype, 'value')?.set
                     || Object.getOwnPropertyDescriptor(
                        window.HTMLInputElement.prototype, 'value')?.set;

                    if (nativeInputValueSetter && input.tagName === 'TEXTAREA') {
                        nativeInputValueSetter.call(input, {{escaped}});
                    } else if (input.contentEditable === 'true') {
                        input.textContent = {{escaped}};
                    } else {
                        input.value = {{escaped}};
                    }

                    // Fire React / Vue change events so the framework sees the value
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                    return "ok";
                })()
                """;
            await _webView!.ExecuteScriptAsync(js);
        });
    }

    // ── Click Send button ────────────────────────────────────────────────────
    private async Task SubmitMessageAsync()
    {
        await _dispatcher.InvokeAsync(async () =>
        {
            var js = """
                (function() {
                    // DeepSeek's send button selector candidates
                    var btn = document.querySelector('button[aria-label="Send message"]')
                           || document.querySelector('button[data-testid="send-button"]')
                           || document.querySelector('button[type="submit"]')
                           || Array.from(document.querySelectorAll('button')).find(b =>
                               b.querySelector('svg') && b.closest('form,div[class*="input"]'));
                    if (btn) { btn.click(); return "sent"; }

                    // Fallback: simulate Enter key on the input
                    var input = document.querySelector('textarea#chat-input')
                             || document.querySelector('textarea[placeholder]');
                    if (input) {
                        input.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', code:'Enter', bubbles:true}));
                        return "enter-pressed";
                    }
                    return "failed";
                })()
                """;
            await _webView!.ExecuteScriptAsync(js);
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Poll DOM for streaming response
    // ═════════════════════════════════════════════════════════════════════════
    private async Task PollResponseAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText = "";
            int stableCount = 0;
            const int stableThreshold = 4;   // 4 × 600 ms = 2.4 s of stability = done
            const int maxWaitMs = 180_000;    // 3 minutes hard timeout
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);

            // Wait for response to start (up to 30 s)
            var started = false;
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                var text = await GetResponseTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    started = true;
                    break;
                }
            }

            if (!started)
            {
                await writer.WriteAsync("⚠️ No response received from " + ProviderName + " within 30 seconds.", ct);
                return;
            }

            // Stream tokens as the response grows
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(600, ct);
                var currentText = await GetResponseTextAsync();

                if (currentText.Length > lastText.Length)
                {
                    var newTokens = currentText[lastText.Length..];
                    await writer.WriteAsync(newTokens, ct);
                    lastText = currentText;
                    stableCount = 0;
                }
                else
                {
                    stableCount++;
                }

                // Check if DeepSeek finished generating
                var isGenerating = await IsStillGeneratingAsync();
                if (!isGenerating && stableCount >= stableThreshold)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await writer.WriteAsync($"\n⚠️ Error reading response: {ex.Message}", ct); } catch { }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Returns the full text content of the last (latest) assistant message.
    /// </summary>
    private async Task<string> GetResponseTextAsync()
    {
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var js = """
                    (function() {
                        // DeepSeek renders messages in markdown blocks
                        // Try multiple selectors in order of specificity
                        var msgs = document.querySelectorAll(
                            'div[class*="markdown"] .markdown-body, ' +
                            'div[class*="response"] .markdown-body, ' +
                            'div[data-message-author-role="assistant"] .markdown-body, ' +
                            'div[class*="ds-markdown"], ' +
                            'div[class*="assistant-message"]'
                        );
                        if (!msgs || msgs.length === 0) return "";
                        // Get the last assistant message
                        return msgs[msgs.length - 1].innerText || "";
                    })()
                    """;
                var raw = await _webView!.ExecuteScriptAsync(js);
                // Unescape JSON string returned by ExecuteScriptAsync
                if (raw != null && raw.StartsWith("\"") && raw.EndsWith("\""))
                    return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                return "";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns true if DeepSeek is still generating (Stop button visible, or spinner present).
    /// </summary>
    private async Task<bool> IsStillGeneratingAsync()
    {
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var js = """
                    (function() {
                        // Check for Stop/pause button or loading spinner
                        var stop = document.querySelector(
                            'button[aria-label*="Stop"], button[aria-label*="stop"], ' +
                            'button[data-testid*="stop"], div[class*="stop-button"], ' +
                            'div[class*="loading"], span[class*="loading"]'
                        );
                        return stop ? "true" : "false";
                    })()
                    """;
                var r = await _webView!.ExecuteScriptAsync(js);
                return r == "\"true\"";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return false; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Disposal
    // ═════════════════════════════════════════════════════════════════════════
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.InvokeAsync(() =>
        {
            _loginWindow?.Close();
            _webView?.Dispose();
        });
    }
}
