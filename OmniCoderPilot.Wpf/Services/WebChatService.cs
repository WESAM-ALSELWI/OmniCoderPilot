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
    string InputSelector,
    string SendSelector,
    string ResponseSelector,
    string StopSelector,
    string LoginCheckSelector,
    string LoginUrlContains
);

/// <summary>
/// Chat2API-style service: drives AI web chat UIs (DeepSeek, ChatGPT) inside
/// WebView2 controls hosted in hidden background windows.
///
/// Architecture:
///  • Each site gets a persistent hidden host Window (0×0, not in taskbar)
///    that keeps the WebView2 alive and initialized.
///  • When login is needed a *second* visible window borrows the WebView2 so
///    the user can log in, then returns it to the hidden host when done.
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
            InputSelector:      "textarea#chat-input, textarea[placeholder]",
            SendSelector:       "button[aria-label='Send message'], button[type='submit']",
            ResponseSelector:   "div[class*='ds-markdown'], div[class*='markdown-body']",
            StopSelector:       "button[aria-label*='Stop'], button[data-testid*='stop']",
            LoginCheckSelector: "textarea#chat-input, textarea[placeholder]",
            LoginUrlContains:   "/sign-in"
        ),
        new WebChatSiteConfig(
            ModelName:          "webchat/chatgpt",
            Url:                "https://chatgpt.com/",
            DisplayName:        "ChatGPT Web",
            InputSelector:      "#prompt-textarea, div[id='prompt-textarea']",
            SendSelector:       "button[data-testid='send-button'], button[aria-label='Send prompt']",
            ResponseSelector:   "div[data-message-author-role='assistant'] .markdown, div[data-message-author-role='assistant'] p",
            StopSelector:       "button[aria-label='Stop streaming'], button[data-testid='stop-button']",
            LoginCheckSelector: "#prompt-textarea, button[aria-label='New chat']",
            LoginUrlContains:   "/auth/login"
        ),
    ];

    // ── Per-site state ───────────────────────────────────────────────────────
    private sealed class SiteState
    {
        public WebChatSiteConfig Config     { get; init; } = null!;
        public WebView2?         WebView    { get; set; }
        public Window?           HostWindow { get; set; }  // hidden host window
        public bool              IsLoggedIn    { get; set; }
        public bool              IsInitialized { get; set; }
    }

    private readonly Dictionary<string, SiteState> _sites;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    // IWebChatService legacy props
    public string ChatUrl     => KnownSites[0].Url;
    public string ProviderName => KnownSites[0].DisplayName;
    public bool   IsLoggedIn  => _sites.Values.Any(s => s.IsLoggedIn);

    public WebChatService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _sites = KnownSites.ToDictionary(
            s => s.ModelName,
            s => new SiteState { Config = s },
            StringComparer.OrdinalIgnoreCase);
    }

    private SiteState GetSite(string modelName)
    {
        if (_sites.TryGetValue(modelName.Trim(), out var s)) return s;
        return _sites.Values.First();
    }

    // ── IWebChatService (legacy) ──────────────────────────────────────────────
    public Task<bool> ShowLoginAsync(CancellationToken ct)
        => ShowLoginForSiteAsync(_sites.Values.First(), ct);
    public IAsyncEnumerable<string> SendMessageAsync(string message, CancellationToken ct)
        => SendMessageToSiteAsync(_sites.Values.First(), message, ct);

    // ── IWebChatService (model-aware) ────────────────────────────────────────
    public bool IsLoggedInFor(string modelName)       => GetSite(modelName).IsLoggedIn;
    public Task<bool> ShowLoginForModelAsync(string modelName, CancellationToken ct)
        => ShowLoginForSiteAsync(GetSite(modelName), ct);
    public IAsyncEnumerable<string> SendMessageForModelAsync(string modelName, string message, CancellationToken ct)
        => SendMessageToSiteAsync(GetSite(modelName), message, ct);
    public string GetDisplayNameFor(string modelName) => GetSite(modelName).Config.DisplayName;

    // ═════════════════════════════════════════════════════════════════════════
    // Initialization — must run on UI thread
    // KEY FIX: WebView2 is created inside a hidden host Window so it has
    //          a valid visual tree and EnsureCoreWebView2Async works.
    // ═════════════════════════════════════════════════════════════════════════
    private async Task EnsureInitializedAsync(SiteState site)
    {
        if (site.IsInitialized) return;

        // Create WebView2 + host it in a hidden 1×1 window on the UI thread
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var wv = new WebView2();

                // Hidden host window — keeps WebView2 alive in a visual tree
                var host = new Window
                {
                    Width  = 1,
                    Height = 1,
                    Left   = -10000,   // off-screen
                    Top    = -10000,
                    ShowInTaskbar   = false,
                    ShowActivated   = false,
                    WindowStyle     = WindowStyle.None,
                    AllowsTransparency = true,
                    Opacity         = 0,
                    Content         = wv
                };
                host.Show();    // must Show() so WebView2 is in a live visual tree

                // Now we can initialize CoreWebView2
                await wv.EnsureCoreWebView2Async();
                wv.CoreWebView2.Navigate(site.Config.Url);
                wv.CoreWebView2.NavigationCompleted += (_, _) =>
                    _ = Task.Run(() => CheckAndUpdateLoginAsync(site));

                site.WebView    = wv;
                site.HostWindow = host;
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        await tcs.Task;  // wait for UI-thread init to complete
        site.IsInitialized = true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Login
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<bool> ShowLoginForSiteAsync(SiteState site, CancellationToken ct)
    {
        await EnsureInitializedAsync(site);

        if (await CheckLoggedInAsync(site)) { site.IsLoggedIn = true; return true; }

        var loginDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Show a login window: move WebView2 out of the hidden host into a visible window
        _dispatcher.InvokeAsync(() =>
        {
            // Detach from host window
            site.HostWindow!.Content = null;

            var loginWin = new Window
            {
                Title  = $"Log in to {site.Config.DisplayName} — close this window when done",
                Width  = 1100,
                Height = 780,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Content = site.WebView
            };

            loginWin.Closed += (_, _) =>
            {
                // Move WebView2 back to the hidden host window
                loginWin.Content     = null;
                site.HostWindow!.Content = site.WebView;
                loginDone.TrySetResult(site.IsLoggedIn);
            };

            loginWin.Show();
            loginWin.Activate();
        });

        // Poll every 2s for login detection (up to 5 min)
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
                    // Auto-close the login window
                    _dispatcher.InvokeAsync(() =>
                    {
                        // Find the login window by looking for the window that owns site.WebView
                        foreach (Window w in System.Windows.Application.Current.Windows)
                        {
                            if (w.Content == site.WebView) { w.Close(); break; }
                        }
                    });
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }

        // Wait for window to be closed by the user
        var result = await loginDone.Task.WaitAsync(ct).ConfigureAwait(false);
        site.IsLoggedIn = result || await CheckLoggedInAsync(site);
        return site.IsLoggedIn;
    }

    private async Task CheckAndUpdateLoginAsync(SiteState site)
    {
        if (await CheckLoggedInAsync(site)) site.IsLoggedIn = true;
    }

    private async Task<bool> CheckLoggedInAsync(SiteState site)
    {
        if (site.WebView == null) return false;
        try
        {
            var selEscaped = JsStr(site.Config.LoginCheckSelector);
            var urlEscaped = JsStr(site.Config.LoginUrlContains);
            var js = "(function(){ var el = document.querySelector(" + selEscaped + ");"
                   + " var onLogin = window.location.href.includes(" + urlEscaped + ");"
                   + " return (el && !onLogin) ? \"true\" : \"false\"; })()";

            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var r = await site.WebView!.ExecuteScriptAsync(js);
                return r == "\"true\"";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return false; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Send + Stream
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

        await StartNewChatAsync(site, ct);
        await TypeMessageAsync(site, message);
        await Task.Delay(600, ct);
        await SubmitMessageAsync(site);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        _ = Task.Run(() => PollResponseAsync(site, channel.Writer, ct), ct);
        await foreach (var token in channel.Reader.ReadAllAsync(ct))
            yield return token;
    }

    private async Task StartNewChatAsync(SiteState site, CancellationToken ct)
    {
        var js = "(function(){ var btn = document.querySelector('button[aria-label=\"New chat\"], a[href=\"/\"]');"
               + " if (btn) { btn.click(); return \"clicked\"; } return \"none\"; })()";

        var taskOfTask = _dispatcher.InvokeAsync(async () =>
        {
            var r = await site.WebView!.ExecuteScriptAsync(js);
            if (r == "\"none\"") site.WebView!.CoreWebView2.Navigate(site.Config.Url);
            return r;
        });
        await taskOfTask.Task.Unwrap();
        await Task.Delay(2000, ct);
    }

    private async Task TypeMessageAsync(SiteState site, string message)
    {
        var msgEscaped = JsStr(message);
        var selEscaped = JsStr(site.Config.InputSelector);

        var js = "(function(){"
            + " var input = document.querySelector(" + selEscaped + ");"
            + " if (!input) return \"no-input\";"
            + " input.focus();"
            + " if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {"
            + "   var desc = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')"
            + "           || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');"
            + "   if (desc && desc.set) { desc.set.call(input, " + msgEscaped + "); }"
            + "   else { input.value = " + msgEscaped + "; }"
            + " } else if (input.contentEditable === 'true') {"
            + "   input.innerHTML = '';"
            + "   var p = document.createElement('p'); p.textContent = " + msgEscaped + ";"
            + "   input.appendChild(p);"
            + " }"
            + " input.dispatchEvent(new Event('input',  { bubbles: true }));"
            + " input.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return \"ok\";"
            + "})()";

        var taskOfTask = _dispatcher.InvokeAsync(async () =>
            await site.WebView!.ExecuteScriptAsync(js));
        await taskOfTask.Task.Unwrap();
    }

    private async Task SubmitMessageAsync(SiteState site)
    {
        var sendSel  = JsStr(site.Config.SendSelector);
        var inputSel = JsStr(site.Config.InputSelector);

        var js = "(function(){"
            + " var btn = document.querySelector(" + sendSel + ");"
            + " if (btn && !btn.disabled) { btn.click(); return \"clicked\"; }"
            + " var input = document.querySelector(" + inputSel + ");"
            + " if (input) {"
            + "   input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',bubbles:true,which:13}));"
            + "   return \"enter\";"
            + " }"
            + " return \"failed\";"
            + "})()";

        var taskOfTask = _dispatcher.InvokeAsync(async () =>
            await site.WebView!.ExecuteScriptAsync(js));
        await taskOfTask.Task.Unwrap();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DOM polling
    // ═════════════════════════════════════════════════════════════════════════
    private async Task PollResponseAsync(SiteState site, ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText = "";
            int stableCount = 0;
            const int stableThreshold = 4;
            var deadline = DateTime.UtcNow.AddMinutes(3);

            // Wait up to 30s for response
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                var t = await GetResponseTextAsync(site);
                if (!string.IsNullOrWhiteSpace(t)) { lastText = t; break; }
            }

            if (string.IsNullOrWhiteSpace(lastText))
            {
                await writer.WriteAsync($"⚠️ No response from {site.Config.DisplayName} in 30s.", ct);
                return;
            }

            await writer.WriteAsync(lastText, ct);

            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(700, ct);
                var current = await GetResponseTextAsync(site);

                if (current.Length > lastText.Length)
                {
                    await writer.WriteAsync(current[lastText.Length..], ct);
                    lastText    = current;
                    stableCount = 0;
                }
                else { stableCount++; }

                if (!await IsStillGeneratingAsync(site) && stableCount >= stableThreshold)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { await writer.WriteAsync($"\n⚠️ {ex.Message}", ct); } catch { } }
        finally { writer.Complete(); }
    }

    private async Task<string> GetResponseTextAsync(SiteState site)
    {
        try
        {
            var selEscaped = JsStr(site.Config.ResponseSelector);
            var js = "(function(){"
                   + " var msgs = document.querySelectorAll(" + selEscaped + ");"
                   + " if (!msgs || msgs.length === 0) return \"\";"
                   + " var last = msgs[msgs.length - 1];"
                   + " return last.innerText || last.textContent || \"\";"
                   + "})()";

            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var raw = await site.WebView!.ExecuteScriptAsync(js);
                if (raw != null && raw.StartsWith("\"") && raw.EndsWith("\""))
                    return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                return "";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return ""; }
    }

    private async Task<bool> IsStillGeneratingAsync(SiteState site)
    {
        try
        {
            var selEscaped = JsStr(site.Config.StopSelector);
            var js = "(function(){"
                   + " var stop = document.querySelector(" + selEscaped + ");"
                   + " return stop ? \"true\" : \"false\";"
                   + "})()";

            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var r = await site.WebView!.ExecuteScriptAsync(js);
                return r == "\"true\"";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return false; }
    }

    // ── JS string escaping ───────────────────────────────────────────────────
    private static string JsStr(string value)
        => "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
          + "\"";

    // ── Disposal ─────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var s in _sites.Values)
            {
                s.WebView?.Dispose();
                s.HostWindow?.Close();
            }
        });
    }
}
