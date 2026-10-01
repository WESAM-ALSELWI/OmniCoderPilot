using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Wpf.Services;

// ── Site configuration ───────────────────────────────────────────────────────
public sealed record WebChatSiteConfig(
    string ModelName,
    string Url,
    string DisplayName,
    string LoginCheckSelector, // element present only after login
    string LoginUrlContains    // URL fragment that means "not logged in"
);

/// <summary>
/// Chat2API service: drives DeepSeek / ChatGPT web UIs inside a hidden WebView2.
/// Each site has a persistent hidden host Window so WebView2 always has a visual tree.
/// A visible login Window borrows the WebView2 when the user needs to authenticate.
/// </summary>
public sealed class WebChatService : IWebChatService, IDisposable
{
    // ── Known sites ──────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<WebChatSiteConfig> KnownSites =
    [
        new WebChatSiteConfig(
            ModelName:          "webchat/deepseek",
            Url:                "https://chat.deepseek.com",
            DisplayName:        "DeepSeek Web",
            LoginCheckSelector: "#chat-input, textarea[placeholder], div[class*='chat-input']",
            LoginUrlContains:   "login"
        ),
        new WebChatSiteConfig(
            ModelName:          "webchat/chatgpt",
            Url:                "https://chatgpt.com/",
            DisplayName:        "ChatGPT Web",
            LoginCheckSelector: "#prompt-textarea, div[id='prompt-textarea']",
            LoginUrlContains:   "auth/login"
        ),
    ];

    // ── Per-site state ───────────────────────────────────────────────────────
    private sealed class SiteState
    {
        public WebChatSiteConfig Config     { get; init; } = null!;
        public WebView2?         WebView    { get; set; }
        public Window?           HostWindow { get; set; }   // invisible host
        public Window?           LoginWindow { get; set; }  // visible login window (or null)
        public bool              IsLoggedIn    { get; set; }
        public bool              IsInitialized { get; set; }
    }

    private readonly Dictionary<string, SiteState> _sites;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    // IWebChatService
    public string ChatUrl      => KnownSites[0].Url;
    public string ProviderName => KnownSites[0].DisplayName;
    public bool   IsLoggedIn   => _sites.Values.Any(s => s.IsLoggedIn);

    public WebChatService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _sites = KnownSites.ToDictionary(
            s => s.ModelName,
            s => new SiteState { Config = s },
            StringComparer.OrdinalIgnoreCase);
    }

    private SiteState GetSite(string model)
    {
        _sites.TryGetValue(model.Trim(), out var s);
        return s ?? _sites.Values.First();
    }

    // ── IWebChatService ───────────────────────────────────────────────────────
    public Task<bool>            ShowLoginAsync(CancellationToken ct)        => ShowLoginForSiteAsync(_sites.Values.First(), ct);
    public IAsyncEnumerable<string> SendMessageAsync(string msg, CancellationToken ct) => SendMessageToSiteAsync(_sites.Values.First(), msg, ct);
    public bool                  IsLoggedInFor(string model)                 => GetSite(model).IsLoggedIn;
    public Task<bool>            ShowLoginForModelAsync(string model, CancellationToken ct)  => ShowLoginForSiteAsync(GetSite(model), ct);
    public IAsyncEnumerable<string> SendMessageForModelAsync(string model, string msg, CancellationToken ct) => SendMessageToSiteAsync(GetSite(model), msg, ct);
    public string                GetDisplayNameFor(string model)             => GetSite(model).Config.DisplayName;

    // ═════════════════════════════════════════════════════════════════════════
    // Init — WebView2 lives in a tiny hidden off-screen Window
    // ═════════════════════════════════════════════════════════════════════════
    private async Task EnsureInitializedAsync(SiteState site)
    {
        if (site.IsInitialized) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wv = new WebView2();

                var host = new Window
                {
                    Width  = 1, Height = 1,
                    Left = -32000, Top = -32000,
                    ShowInTaskbar = false, ShowActivated = false,
                    WindowStyle   = WindowStyle.None,
                    AllowsTransparency = true, Opacity = 0,
                    Content = wv
                };
                host.Show();   // must be in a live visual tree for WebView2 to init

                await wv.EnsureCoreWebView2Async();
                wv.CoreWebView2.Navigate(site.Config.Url);

                site.WebView    = wv;
                site.HostWindow = host;
                tcs.SetResult();
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        await tcs.Task;
        site.IsInitialized = true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Login — FIXED: awaited show, reliable auto-close, window ref in state
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<bool> ShowLoginForSiteAsync(SiteState site, CancellationToken ct)
    {
        await EnsureInitializedAsync(site);

        // Already logged in?
        if (await CheckLoggedInAsync(site)) { site.IsLoggedIn = true; return true; }

        var loginDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // ── FIXED: await the InvokeAsync so window is shown before we poll ──
        await _dispatcher.InvokeAsync(() =>
        {
            // Move WebView2 from hidden host → visible login window
            site.HostWindow!.Content = null;

            var loginWin = new Window
            {
                Title  = $"Log in to {site.Config.DisplayName}  —  window closes automatically when done",
                Width  = 1100, Height = 780,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Content = site.WebView
            };

            loginWin.Closed += (_, _) =>
            {
                site.LoginWindow = null;
                loginWin.Content = null;
                // Return WebView2 to host
                if (site.HostWindow != null)
                    site.HostWindow.Content = site.WebView;
                loginDone.TrySetResult(site.IsLoggedIn);
            };

            site.LoginWindow = loginWin;
            loginWin.Show();
            loginWin.Activate();
        });

        // ── Poll every 2 s for login (up to 5 min) ───────────────────────────
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            while (!pollCts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, pollCts.Token);

                if (await CheckLoggedInAsync(site))
                {
                    site.IsLoggedIn = true;

                    // ── FIXED: await the close so WebView2 is returned to host BEFORE we proceed ──
                    await _dispatcher.InvokeAsync(() =>
                    {
                        site.LoginWindow?.Close();   // triggers Closed → returns WebView2 to host
                    });

                    // Wait until the Closed handler has finished returning WebView2 to host
                    await loginDone.Task;
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }

        // User closed the window manually — wait for Closed event to complete
        try { await loginDone.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        site.IsLoggedIn = await CheckLoggedInAsync(site);
        return site.IsLoggedIn;
    }

    private async Task<bool> CheckLoggedInAsync(SiteState site)
    {
        if (site.WebView == null) return false;
        try
        {
            var sel     = JsStr(site.Config.LoginCheckSelector);
            var urlFrag = JsStr(site.Config.LoginUrlContains);
            var js = "(function(){ "
                   + "  var el = document.querySelector(" + sel + "); "
                   + "  var onLoginPage = window.location.href.toLowerCase().includes(" + urlFrag + "); "
                   + "  return (el && !onLoginPage) ? 'true' : 'false'; "
                   + "})()";

            return await ExecScriptBoolAsync(site, js);
        }
        catch { return false; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Send + Stream — FIXED response reading with robust multi-site JS
    // ═════════════════════════════════════════════════════════════════════════
    private async IAsyncEnumerable<string> SendMessageToSiteAsync(
        SiteState site, string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureInitializedAsync(site);

        if (!site.IsLoggedIn)
        {
            var ok = await ShowLoginForSiteAsync(site, ct);
            if (!ok)
            {
                yield return $"⚠️ Not logged in to {site.Config.DisplayName}. Please log in and try again.";
                yield break;
            }
        }

        // Navigate to fresh chat + wait for input to be ready
        await NavigateToNewChatAsync(site, ct);

        // Wait up to 10 s for the text input to appear
        bool inputReady = await WaitForInputAsync(site, ct, timeoutSeconds: 10);
        if (!inputReady)
        {
            yield return $"⚠️ Timed out waiting for {site.Config.DisplayName} input box.";
            yield break;
        }

        // Type + submit
        var typed = await TypeMessageAsync(site, message);
        if (typed == "no-input")
        {
            yield return "⚠️ Could not find chat input box. Please try again.";
            yield break;
        }

        await Task.Delay(500, ct);
        await SubmitMessageAsync(site);

        // Stream response via DOM polling
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        _ = Task.Run(() => PollResponseAsync(site, channel.Writer, ct), ct);
        await foreach (var token in channel.Reader.ReadAllAsync(ct))
            yield return token;
    }

    // ── Navigate to a fresh chat ──────────────────────────────────────────────
    private async Task NavigateToNewChatAsync(SiteState site, CancellationToken ct)
    {
        // Try clicking the "New Chat" button; fall back to navigating to root URL
        var js = "(function(){ "
               + "  var btn = document.querySelector('button[aria-label=\"New chat\"]') "
               + "          || document.querySelector('a[href=\"/\"]'); "
               + "  if (btn) { btn.click(); return 'clicked'; } "
               + "  return 'none'; "
               + "})()";

        var result = await ExecScriptStringAsync(site, js);
        if (result != "clicked")
        {
            // Navigate directly
            await _dispatcher.InvokeAsync(() =>
                site.WebView!.CoreWebView2.Navigate(site.Config.Url));
        }
        await Task.Delay(2500, ct); // wait for new chat to load
    }

    // ── Wait until the text input box is present ──────────────────────────────
    private async Task<bool> WaitForInputAsync(SiteState site, CancellationToken ct, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var js = "(function(){ "
                   + "  var el = document.querySelector('#chat-input') "
                   + "         || document.querySelector('#prompt-textarea') "
                   + "         || document.querySelector('textarea[placeholder]') "
                   + "         || document.querySelector('div[contenteditable=\"true\"]'); "
                   + "  return el ? 'ready' : 'wait'; "
                   + "})()";
            var r = await ExecScriptStringAsync(site, js);
            if (r == "ready") return true;
            await Task.Delay(700, ct);
        }
        return false;
    }

    // ── Type the message ──────────────────────────────────────────────────────
    private async Task<string> TypeMessageAsync(SiteState site, string message)
    {
        var msg = JsStr(message);
        var js = "(function(){ "
            // Try all known input selectors in order
            + "  var input = document.querySelector('#chat-input') "
            + "           || document.querySelector('#prompt-textarea') "
            + "           || document.querySelector('div[id=\"prompt-textarea\"]') "
            + "           || document.querySelector('textarea[placeholder]') "
            + "           || document.querySelector('div[contenteditable=\"true\"]'); "
            + "  if (!input) return 'no-input'; "
            + "  input.focus(); "
            + "  if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') { "
            + "    var desc = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value') "
            + "            || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value'); "
            + "    if (desc && desc.set) { desc.set.call(input, " + msg + "); } "
            + "    else { input.value = " + msg + "; } "
            + "  } else { "  // contenteditable (ChatGPT uses a div)
            + "    input.innerHTML = ''; "
            + "    var p = document.createElement('p'); "
            + "    p.textContent = " + msg + "; "
            + "    input.appendChild(p); "
            + "  } "
            + "  input.dispatchEvent(new Event('input',  { bubbles: true })); "
            + "  input.dispatchEvent(new Event('change', { bubbles: true })); "
            + "  return 'ok'; "
            + "})()";

        return await ExecScriptStringAsync(site, js);
    }

    // ── Click the Send button ─────────────────────────────────────────────────
    private async Task SubmitMessageAsync(SiteState site)
    {
        var js = "(function(){ "
            + "  var btn = document.querySelector('button[data-testid=\"send-button\"]') "
            + "          || document.querySelector('button[aria-label=\"Send message\"]') "
            + "          || document.querySelector('button[aria-label=\"Send prompt\"]') "
            + "          || document.querySelector('button[type=\"submit\"]'); "
            + "  if (btn && !btn.disabled) { btn.click(); return 'clicked'; } "
            // Fallback: press Enter on the input
            + "  var input = document.querySelector('#chat-input') "
            + "           || document.querySelector('#prompt-textarea') "
            + "           || document.querySelector('div[contenteditable=\"true\"]'); "
            + "  if (input) { "
            + "    input.dispatchEvent(new KeyboardEvent('keydown', { key:'Enter', code:'Enter', which:13, bubbles:true })); "
            + "    return 'enter'; "
            + "  } "
            + "  return 'failed'; "
            + "})()";

        await ExecScriptStringAsync(site, js);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DOM polling — stream response text as it grows
    // FIXED: robust selectors, proper streaming, correct stop detection
    // ═════════════════════════════════════════════════════════════════════════
    private async Task PollResponseAsync(SiteState site, ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText    = "";
            int    stableCount = 0;
            const int stableThreshold = 5;          // 5 × 800ms = 4s stable = done
            var deadline = DateTime.UtcNow.AddMinutes(4);

            // ── Wait up to 30s for the response to start ──────────────────────
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                var t = await GetResponseTextAsync(site);
                if (!string.IsNullOrWhiteSpace(t)) { lastText = t; break; }
            }

            if (string.IsNullOrWhiteSpace(lastText))
            {
                await writer.WriteAsync($"⚠️ No response from {site.Config.DisplayName} within 30s.\n"
                    + "Check that you are logged in and the page loaded correctly.", ct);
                return;
            }

            // Emit the first chunk
            await writer.WriteAsync(lastText, ct);

            // ── Keep streaming as the response grows ──────────────────────────
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(800, ct);
                var current = await GetResponseTextAsync(site);

                if (current.Length > lastText.Length)
                {
                    await writer.WriteAsync(current[lastText.Length..], ct);
                    lastText    = current;
                    stableCount = 0;
                }
                else
                {
                    stableCount++;
                    if (stableCount >= stableThreshold)
                        break;  // text stable — generation finished
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await writer.WriteAsync($"\n⚠️ Error: {ex.Message}", ct); } catch { }
        }
        finally { writer.Complete(); }
    }

    // ── FIXED response text extraction — broad selectors for both sites ───────
    private async Task<string> GetResponseTextAsync(SiteState site)
    {
        // Broad JS that works on DeepSeek AND ChatGPT without site-specific config
        var js = "(function(){ "
            // ChatGPT: messages are article elements with data-message-author-role
            + "  var chatgptMsgs = document.querySelectorAll('[data-message-author-role=\"assistant\"]'); "
            + "  if (chatgptMsgs.length > 0) { "
            + "    var last = chatgptMsgs[chatgptMsgs.length - 1]; "
            + "    return last.innerText || last.textContent || ''; "
            + "  } "
            // DeepSeek: messages use ds-markdown class
            + "  var dsMsgs = document.querySelectorAll('.ds-markdown, [class*=\"ds-markdown\"]'); "
            + "  if (dsMsgs.length > 0) { "
            + "    var last = dsMsgs[dsMsgs.length - 1]; "
            + "    return last.innerText || last.textContent || ''; "
            + "  } "
            // Generic fallback: any assistant/bot message container
            + "  var genericMsgs = document.querySelectorAll("
            + "    '[class*=\"assistant-message\"], [class*=\"bot-message\"], "
            + "     [class*=\"response\"] .markdown, [class*=\"reply\"]'); "
            + "  if (genericMsgs.length > 0) { "
            + "    var last = genericMsgs[genericMsgs.length - 1]; "
            + "    return last.innerText || last.textContent || ''; "
            + "  } "
            + "  return ''; "
            + "})()";

        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var raw = await site.WebView!.ExecuteScriptAsync(js);
                if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                return "";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return ""; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helper: execute JS and return the unquoted string result
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<string> ExecScriptStringAsync(SiteState site, string js)
    {
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var raw = await site.WebView!.ExecuteScriptAsync(js);
                // ExecuteScriptAsync returns JSON — unquote a string result
                if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                return raw ?? "";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return ""; }
    }

    private async Task<bool> ExecScriptBoolAsync(SiteState site, string js)
    {
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var r = await site.WebView!.ExecuteScriptAsync(js);
                return r is "\"true\"" or "true";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return false; }
    }

    // ── Escape a C# string for embedding as a JS string literal ──────────────
    private static string JsStr(string v)
        => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"")
                   .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")
          + "\"";

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var s in _sites.Values)
            {
                s.LoginWindow?.Close();
                s.WebView?.Dispose();
                s.HostWindow?.Close();
            }
        });
    }
}
