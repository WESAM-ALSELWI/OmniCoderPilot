using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Wpf.Services;

public sealed record WebChatSiteConfig(
    string ModelName,
    string Url,
    string DisplayName,
    string LoginCheckSelector,
    string LoginUrlContains
);

/// <summary>
/// Chat2API: drives DeepSeek/ChatGPT web UIs inside a hidden WebView2.
/// Each site has a persistent off-screen host Window so WebView2 always
/// has a valid visual tree. A visible login Window borrows the WebView2
/// temporarily, then returns it when the user is done.
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
        public WebChatSiteConfig Config      { get; init; } = null!;
        public WebView2?         WebView     { get; set; }
        public Window?           HostWindow  { get; set; }
        public Window?           LoginWindow { get; set; }
        public bool              IsLoggedIn    { get; set; }
        public bool              IsInitialized { get; set; }
    }

    private readonly Dictionary<string, SiteState> _sites;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

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
    public Task<bool>               ShowLoginAsync(CancellationToken ct)                          => ShowLoginForSiteAsync(_sites.Values.First(), ct);
    public IAsyncEnumerable<string> SendMessageAsync(string msg, CancellationToken ct)            => SendMessageToSiteAsync(_sites.Values.First(), msg, ct);
    public bool                     IsLoggedInFor(string model)                                   => GetSite(model).IsLoggedIn;
    public Task<bool>               ShowLoginForModelAsync(string model, CancellationToken ct)    => ShowLoginForSiteAsync(GetSite(model), ct);
    public IAsyncEnumerable<string> SendMessageForModelAsync(string model, string msg, CancellationToken ct) => SendMessageToSiteAsync(GetSite(model), msg, ct);
    public string                   GetDisplayNameFor(string model)                               => GetSite(model).Config.DisplayName;
    public Task                     LogoutAsync(string model)                                     => LogoutSiteAsync(GetSite(model));
    public Task<bool>               SwitchAccountAsync(string model, CancellationToken ct)        => SwitchAccountForSiteAsync(GetSite(model), ct);

    private async Task LogoutSiteAsync(SiteState site)
    {
        await EnsureInitializedAsync(site);
        site.IsLoggedIn = false;

        await _dispatcher.InvokeAsync(async () =>
        {
            if (site.WebView?.CoreWebView2 != null)
            {
                // Delete all cookies
                site.WebView.CoreWebView2.CookieManager.DeleteAllCookies();

                // Clear browsing profile data (cookies, storage, cache)
                try
                {
                    await site.WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.Cookies |
                        CoreWebView2BrowsingDataKinds.AllDomStorage |
                        CoreWebView2BrowsingDataKinds.DiskCache);
                }
                catch { }

                try
                {
                    await site.WebView.ExecuteScriptAsync("try { localStorage.clear(); sessionStorage.clear(); } catch(e){}");
                }
                catch { }

                site.WebView.CoreWebView2.Navigate(site.Config.Url);
            }
        });
    }

    private async Task<bool> SwitchAccountForSiteAsync(SiteState site, CancellationToken ct)
    {
        await LogoutSiteAsync(site);
        return await ShowLoginForSiteAsync(site, ct);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Init — WebView2 lives in a tiny off-screen host Window
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
                    Width = 1, Height = 1,
                    Left = -32000, Top = -32000,
                    ShowInTaskbar = false, ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true, Opacity = 0,
                    Content = wv
                };
                host.Show();  // WebView2 requires a live visual tree

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
    // Login
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<bool> ShowLoginForSiteAsync(SiteState site, CancellationToken ct)
    {
        await EnsureInitializedAsync(site);
        if (await CheckLoggedInAsync(site)) { site.IsLoggedIn = true; return true; }

        var loginDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // AWAIT the show so window is visible before polling starts
        await _dispatcher.InvokeAsync(() =>
        {
            site.HostWindow!.Content = null;

            var loginWin = new Window
            {
                Title  = $"Log in to {site.Config.DisplayName}  —  closes automatically when done",
                Width  = 1100, Height = 780,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Content = site.WebView
            };
            loginWin.Closed += (_, _) =>
            {
                site.LoginWindow = null;
                loginWin.Content = null;
                if (site.HostWindow != null)
                    site.HostWindow.Content = site.WebView;
                loginDone.TrySetResult(site.IsLoggedIn);
            };
            site.LoginWindow = loginWin;
            loginWin.Show();
            loginWin.Activate();
        });

        // Poll every 2s for up to 5 min
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
                    // AWAIT close so WebView2 is back in host before we proceed
                    await _dispatcher.InvokeAsync(() => site.LoginWindow?.Close());
                    await loginDone.Task;
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }

        try { await loginDone.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        site.IsLoggedIn = await CheckLoggedInAsync(site);
        return site.IsLoggedIn;
    }

    private async Task<bool> CheckLoggedInAsync(SiteState site)
    {
        if (site.WebView == null) return false;
        try
        {
            var sel  = JsStr(site.Config.LoginCheckSelector);
            var frag = JsStr(site.Config.LoginUrlContains);
            var js = "(function(){"
                   + "  var el = document.querySelector(" + sel + ");"
                   + "  var onLogin = window.location.href.toLowerCase().includes(" + frag + ");"
                   + "  return (el && !onLogin) ? 'true' : 'false';"
                   + "})()";
            return await ExecScriptBoolAsync(site, js);
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

        await NavigateToNewChatAsync(site, ct);

        bool inputReady = await WaitForInputAsync(site, ct, timeoutSeconds: 15);
        if (!inputReady)
        {
            yield return $"⚠️ Timed out waiting for {site.Config.DisplayName} input box.";
            yield break;
        }

        // Snapshot: count assistant messages BEFORE we send
        var snapshotCount = await GetAssistantMessageCountAsync(site);

        var typed = await TypeMessageAsync(site, message);
        if (typed == "no-input")
        {
            yield return "⚠️ Could not find the chat input. Please try again.";
            yield break;
        }

        await Task.Delay(400, ct);
        var submitted = await SubmitMessageAsync(site, ct);
        if (!submitted)
        {
            yield return "⚠️ Could not submit message. Send button was not ready.";
            yield break;
        }

        // Stream the response (only messages appearing AFTER snapshot)
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        _ = Task.Run(() => PollResponseAsync(site, snapshotCount, channel.Writer, ct), ct);
        await foreach (var token in channel.Reader.ReadAllAsync(ct))
            yield return token;
    }

    private async Task NavigateToNewChatAsync(SiteState site, CancellationToken ct)
    {
        var currentUrl = "";
        await _dispatcher.InvokeAsync(() => currentUrl = site.WebView?.Source?.ToString() ?? "");

        // If we are currently inside an existing conversation (e.g. /c/<id>), navigate to base URL
        if (!currentUrl.TrimEnd('/').Equals(site.Config.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            await _dispatcher.InvokeAsync(() => site.WebView!.CoreWebView2.Navigate(site.Config.Url));
            await Task.Delay(2500, ct);
        }
        else
        {
            // Already at base URL, click "New chat" button if present
            var js = "(function(){"
                   + "  var btn = document.querySelector('button[aria-label=\"New chat\"], a[href=\"/\"], a[data-testid=\"new-chat-button\"]');"
                   + "  if (btn) { btn.click(); return 'clicked'; }"
                   + "  return 'none';"
                   + "})()";
            await ExecScriptStringAsync(site, js);
            await Task.Delay(1500, ct);
        }
    }

    private async Task<bool> WaitForInputAsync(SiteState site, CancellationToken ct, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var js = "(function(){"
                   + "  var el = document.querySelector('#prompt-textarea')"
                   + "         || document.querySelector('#chat-input')"
                   + "         || document.querySelector('textarea[placeholder]')"
                   + "         || document.querySelector('div[contenteditable=\"true\"]');"
                   + "  return el ? 'ready' : 'wait';"
                   + "})()";
            if (await ExecScriptStringAsync(site, js) == "ready") return true;
            await Task.Delay(600, ct);
        }
        return false;
    }

    private async Task<string> TypeMessageAsync(SiteState site, string message)
    {
        var msg = JsStr(message);
        var js = "(function(){"
            + "  var input = document.querySelector('#prompt-textarea')"
            + "           || document.querySelector('#chat-input')"
            + "           || document.querySelector('div[id=\"prompt-textarea\"]')"
            + "           || document.querySelector('div[contenteditable=\"true\"]')"
            + "           || document.querySelector('textarea');"
            + "  if (!input) return 'no-input';"
            + "  input.focus();"
            + "  if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {"
            + "    var desc = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value')"
            + "            || Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');"
            + "    if (desc && desc.set) { desc.set.call(input, " + msg + "); }"
            + "    else { input.value = " + msg + "; }"
            + "    input.dispatchEvent(new Event('input',  { bubbles: true }));"
            + "    input.dispatchEvent(new Event('change', { bubbles: true }));"
            + "  } else {"
            // ContentEditable (ChatGPT)
            + "    document.execCommand('selectAll', false, null);"
            + "    document.execCommand('delete',    false, null);"
            + "    var inserted = document.execCommand('insertText', false, " + msg + ");"
            + "    var current = (input.innerText || input.textContent || '').trim();"
            + "    if (!inserted || current.length === 0) {"
            + "      input.innerHTML = '<p>' + " + msg + ".replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;') + '</p>';"
            + "    }"
            + "    try { input.dispatchEvent(new InputEvent('beforeinput', { bubbles: true, cancelable: true, inputType: 'insertText', data: " + msg + " })); } catch(e){}"
            + "    try { input.dispatchEvent(new InputEvent('input',       { bubbles: true, cancelable: true, inputType: 'insertText', data: " + msg + " })); } catch(e){}"
            + "    input.dispatchEvent(new Event('input',  { bubbles: true }));"
            + "    input.dispatchEvent(new Event('change', { bubbles: true }));"
            + "  }"
            + "  return 'ok';"
            + "})()";
        return await ExecScriptStringAsync(site, js);
    }

    private async Task<bool> SubmitMessageAsync(SiteState site, CancellationToken ct)
    {
        // Poll for up to 3 seconds for the send button to become enabled and click it
        for (int i = 0; i < 15; i++)
        {
            var js = "(function(){"
                + "  var btn = document.querySelector('button[data-testid=\"send-button\"]')"
                + "          || document.querySelector('button[data-testid=\"fruitjuice-send-button\"]')"
                + "          || document.querySelector('button[aria-label=\"Send prompt\"]')"
                + "          || document.querySelector('button[aria-label=\"Send message\"]')"
                + "          || document.querySelector('button[aria-label*=\"Send\"]')"
                + "          || document.querySelector('form button[type=\"submit\"]');"
                + "  if (btn && !btn.disabled) { btn.click(); return 'clicked'; }"
                + "  if (btn && btn.disabled)  { return 'disabled'; }"
                + "  return 'not-found';"
                + "})()";
            var r = await ExecScriptStringAsync(site, js);
            if (r == "clicked") return true;
            await Task.Delay(200, ct);
        }

        // Fallback: send Enter key to the input element
        var enterJs = "(function(){"
            + "  var inp = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
            + "  if (inp) {"
            + "    inp.focus();"
            + "    inp.dispatchEvent(new KeyboardEvent('keydown', { key:'Enter', code:'Enter', keyCode:13, which:13, bubbles:true, cancelable:true }));"
            + "    return 'enter';"
            + "  }"
            + "  return 'no-input';"
            + "})()";
        var enterRes = await ExecScriptStringAsync(site, enterJs);
        return enterRes == "enter";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Response polling — snapshot-aware (only reads messages AFTER snapshot)
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<int> GetAssistantMessageCountAsync(SiteState site)
    {
        var js = "(function(){"
               + "  var els = document.querySelectorAll('[data-message-author-role=\"assistant\"], .ds-markdown, [class*=\"ds-markdown\"]');"
               + "  return String(els.length);"
               + "})()";
        var r = await ExecScriptStringAsync(site, js);
        return int.TryParse(r, out var n) ? n : 0;
    }

    private async Task PollResponseAsync(
        SiteState site, int snapshotCount,
        ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText    = "";
            int    stableCount = 0;
            const int stableThreshold = 5;
            var deadline = DateTime.UtcNow.AddMinutes(4);

            // Wait up to 45s for new response to appear
            for (int i = 0; i < 90 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                var t = await GetLatestResponseTextAsync(site, snapshotCount);
                if (!string.IsNullOrWhiteSpace(t)) { lastText = t; break; }
            }

            if (string.IsNullOrWhiteSpace(lastText))
            {
                // Diagnostic dump so we can inspect the exact page state
                var diag = await ExecScriptStringAsync(site,
                    "(function(){"
                    + "  var roles = Array.from(document.querySelectorAll('[data-message-author-role]')).map(e=>e.getAttribute('data-message-author-role')).join(',');"
                    + "  var asstCount = document.querySelectorAll('[data-message-author-role=\"assistant\"], .ds-markdown').length;"
                    + "  var sendBtn = document.querySelector('button[data-testid=\"send-button\"], button[aria-label*=\"Send\"]');"
                    + "  var btnState = sendBtn ? (sendBtn.disabled ? 'disabled' : 'enabled') : 'none';"
                    + "  return 'url=' + window.location.href.split('?')[0] + ' asstCount=' + asstCount + ' snap=' + " + snapshotCount + " + ' btn=' + btnState + ' roles=[' + roles + ']';"
                    + "})()");
                await writer.WriteAsync(
                    $"⚠️ No response from {site.Config.DisplayName} in 45s.\n"
                    + $"Debug: {diag}\n"
                    + "Make sure you are logged in and the message was submitted.", ct);
                return;
            }

            await writer.WriteAsync(lastText, ct);

            // Stream subsequent chunks as text grows
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(600, ct);
                var current = await GetLatestResponseTextAsync(site, snapshotCount);

                if (current.Length > lastText.Length)
                {
                    await writer.WriteAsync(current[lastText.Length..], ct);
                    lastText    = current;
                    stableCount = 0;
                }
                else
                {
                    stableCount++;

                    // Check if generation is still happening (Stop button visible)
                    var isGenerating = await ExecScriptBoolAsync(site,
                        "(function(){"
                        + "  var stop = document.querySelector('button[data-testid=\"stop-button\"], button[aria-label*=\"Stop\"], button[aria-label*=\"stop\"]');"
                        + "  return stop ? 'true' : 'false';"
                        + "})()");

                    if (!isGenerating && stableCount >= stableThreshold)
                    {
                        break;
                    }
                    if (isGenerating)
                    {
                        stableCount = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { await writer.WriteAsync($"\n⚠️ {ex.Message}", ct); } catch { } }
        finally { writer.Complete(); }
    }

    private async Task<string> GetLatestResponseTextAsync(SiteState site, int snapshotCount)
    {
        var snap = snapshotCount.ToString();
        var js = "(function(){"
            + "  var snap = " + snap + ";"

            // ChatGPT: if there is only 1 user message and 1 assistant message, reset snap to 0
            + "  var cg = document.querySelectorAll('[data-message-author-role=\"assistant\"]');"
            + "  var userMsgs = document.querySelectorAll('[data-message-author-role=\"user\"]');"
            + "  if (userMsgs.length === 1 && cg.length === 1) { snap = 0; }"

            + "  if (cg.length > snap) {"
            + "    var el = cg[cg.length - 1];"
            + "    var textEl = el.querySelector('.markdown, .prose, [class*=\"markdown\"], [class*=\"whitespace-pre-wrap\"]') || el;"
            + "    var t = (textEl.innerText || textEl.textContent || '').trim();"
            + "    if (t.length > 0) return t;"
            + "  }"

            // DeepSeek
            + "  var ds = document.querySelectorAll('.ds-markdown, [class*=\"ds-markdown\"]');"
            + "  if (ds.length > snap) {"
            + "    var el = ds[ds.length - 1];"
            + "    var t = (el.innerText || el.textContent || '').trim();"
            + "    if (t.length > 0) return t;"
            + "  }"

            // Generic fallback
            + "  var gen = document.querySelectorAll('[class*=\"assistant\"], [class*=\"bot-msg\"], [class*=\"ai-message\"]');"
            + "  if (gen.length > snap) {"
            + "    var el = gen[gen.length - 1];"
            + "    var t = (el.innerText || el.textContent || '').trim();"
            + "    if (t.length > 0) return t;"
            + "  }"

            + "  return '';"
            + "})()";

        return await ExecScriptStringAsync(site, js);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // JS execution helpers
    // ═════════════════════════════════════════════════════════════════════════
    private async Task<string> ExecScriptStringAsync(SiteState site, string js)
    {
        if (site.WebView == null) return "";
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var raw = await site.WebView.ExecuteScriptAsync(js);
                if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    return JsonSerializer.Deserialize<string>(raw) ?? "";
                return raw ?? "";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return ""; }
    }

    private async Task<bool> ExecScriptBoolAsync(SiteState site, string js)
    {
        if (site.WebView == null) return false;
        try
        {
            var taskOfTask = _dispatcher.InvokeAsync(async () =>
            {
                var r = await site.WebView.ExecuteScriptAsync(js);
                return r is "\"true\"" or "true";
            });
            return await taskOfTask.Task.Unwrap();
        }
        catch { return false; }
    }

    private static string JsStr(string v) => JsonSerializer.Serialize(v);

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
