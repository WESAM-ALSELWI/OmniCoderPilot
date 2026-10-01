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
/// hidden WebView2 controls — one browser per site, all hidden from the user.
/// A temporary login window appears only when the user needs to authenticate.
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
            InputSelector:      "textarea#chat-input, textarea[placeholder], div[contenteditable='true']",
            SendSelector:       "button[aria-label='Send message'], button[type='submit']",
            ResponseSelector:   "div[class*='ds-markdown'], div[class*='markdown-body']",
            StopSelector:       "button[aria-label*='Stop'], button[data-testid*='stop'], div[class*='stop-button']",
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
        public WebChatSiteConfig Config    { get; init; } = null!;
        public WebView2?         WebView   { get; set; }
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

    // ── IWebChatService (legacy single-site shims) ───────────────────────────
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
    // Initialization
    // ═════════════════════════════════════════════════════════════════════════
    private async Task EnsureInitializedAsync(SiteState site)
    {
        if (site.IsInitialized) return;

        await _dispatcher.InvokeAsync(async () =>
        {
            var wv = new WebView2 { Width = 0, Height = 0, Visibility = Visibility.Collapsed };
            var env = await CoreWebView2Environment.CreateAsync();
            await wv.EnsureCoreWebView2Async(env);
            wv.CoreWebView2.NavigationCompleted += (_, _) =>
                _ = Task.Run(() => CheckAndUpdateLoginAsync(site));
            wv.CoreWebView2.Navigate(site.Config.Url);
            site.WebView = wv;
        }).Task.Unwrap();

        site.IsInitialized = true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Login
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<bool> ShowLoginForSiteAsync(SiteState site, CancellationToken ct)
    {
        await EnsureInitializedAsync(site);

        if (await CheckLoggedInAsync(site)) { site.IsLoggedIn = true; return true; }

        var loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _dispatcher.InvokeAsync(() =>
        {
            site.WebView!.Width      = 1024;
            site.WebView!.Height     = 720;
            site.WebView!.Visibility = Visibility.Visible;

            var win = new Window
            {
                Title  = $"Log in to {site.Config.DisplayName} — close this window when done",
                Width  = 1060,
                Height = 760,
                Content = site.WebView,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            win.Closed += (_, _) =>
            {
                win.Content = null;
                site.WebView!.Width      = 0;
                site.WebView!.Height     = 0;
                site.WebView!.Visibility = Visibility.Collapsed;
                loginTcs.TrySetResult(site.IsLoggedIn);
            };
            win.Show();
        });

        // Poll for login (up to 5 min)
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
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (site.WebView?.Parent is Window w) w.Close();
                    });
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }

        var final = await CheckLoggedInAsync(site);
        site.IsLoggedIn = final;
        return final;
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
            // Build JS without raw string interpolation to avoid {{ issues
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
        await Task.Delay(400, ct);
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

        await _dispatcher.InvokeAsync(async () =>
        {
            var r = await site.WebView!.ExecuteScriptAsync(js);
            if (r == "\"none\"") site.WebView!.CoreWebView2.Navigate(site.Config.Url);
        }).Task.Unwrap();

        await Task.Delay(1800, ct);
    }

    private async Task TypeMessageAsync(SiteState site, string message)
    {
        var msgEscaped = JsStr(message);
        var selEscaped = JsStr(site.Config.InputSelector);

        // Build JS using string concatenation — no raw string interpolation
        var js = "(function(){"
            + " var input = document.querySelector(" + selEscaped + ");"
            + " if (!input) return \"no-input\";"
            + " input.focus();"
            + " if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {"
            + "   var nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value');"
            + "   if (nativeSetter && nativeSetter.set) { nativeSetter.set.call(input, " + msgEscaped + "); }"
            + "   else { input.value = " + msgEscaped + "; }"
            + " } else if (input.contentEditable === 'true') {"
            + "   input.innerHTML = '';"
            + "   var p = document.createElement('p');"
            + "   p.textContent = " + msgEscaped + ";"
            + "   input.appendChild(p);"
            + " }"
            + " input.dispatchEvent(new Event('input', { bubbles: true }));"
            + " input.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return \"ok\";"
            + "})()";

        await _dispatcher.InvokeAsync(async () =>
            await site.WebView!.ExecuteScriptAsync(js)
        ).Task.Unwrap();
    }

    private async Task SubmitMessageAsync(SiteState site)
    {
        var sendSel  = JsStr(site.Config.SendSelector);
        var inputSel = JsStr(site.Config.InputSelector);

        var js = "(function(){"
            + " var btn = document.querySelector(" + sendSel + ");"
            + " if (btn && !btn.disabled) { btn.click(); return \"clicked\"; }"
            + " var input = document.querySelector(" + inputSel + ");"
            + " if (input) { input.dispatchEvent(new KeyboardEvent('keydown', { key:'Enter', code:'Enter', bubbles:true, which:13 })); return \"enter\"; }"
            + " return \"failed\";"
            + "})()";

        await _dispatcher.InvokeAsync(async () =>
            await site.WebView!.ExecuteScriptAsync(js)
        ).Task.Unwrap();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DOM polling
    // ═════════════════════════════════════════════════════════════════════════
    private async Task PollResponseAsync(SiteState site, ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText    = "";
            int    stableCount = 0;
            const int stableThreshold = 4;
            var deadline = DateTime.UtcNow.AddMinutes(3);

            // Wait up to 30s for response to start
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
            foreach (var s in _sites.Values) s.WebView?.Dispose();
        });
    }
}
