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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _threadUrls = new();
    private Guid? _activeConversationId;
    private bool _disposed;

    public string ChatUrl      => KnownSites[0].Url;
    public string ProviderName => KnownSites[0].DisplayName;
    public bool   IsLoggedIn   => _sites.Values.Any(s => s.IsLoggedIn);
    public Guid?  ActiveConversationId => _activeConversationId;

    public event Action<Guid, string>? ThreadUrlUpdated;

    public void SetActiveConversation(Guid conversationId, string? existingThreadUrl = null)
    {
        _activeConversationId = conversationId;
        if (!string.IsNullOrWhiteSpace(existingThreadUrl))
        {
            _threadUrls[conversationId] = existingThreadUrl;
        }
    }

    public string? GetThreadUrlForConversation(Guid conversationId)
    {
        return _threadUrls.TryGetValue(conversationId, out var url) ? url : null;
    }

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
    public IAsyncEnumerable<string> SendMessageAsync(string msg, CancellationToken ct)            => SendMessageToSiteAsync(_sites.Values.First(), msg, null, false, ct);
    public bool                     IsLoggedInFor(string model)                                   => GetSite(model).IsLoggedIn;
    public Task<bool>               ShowLoginForModelAsync(string model, CancellationToken ct)    => ShowLoginForSiteAsync(GetSite(model), ct);
    public IAsyncEnumerable<string> SendMessageForModelAsync(string model, string msg, CancellationToken ct, Guid? conversationId = null, bool startNewChat = false) => SendMessageToSiteAsync(GetSite(model), msg, conversationId, startNewChat, ct);
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
                    Width = 1280, Height = 900,
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
                {
                    site.HostWindow.Width = 1280;
                    site.HostWindow.Height = 900;
                    site.HostWindow.Content = site.WebView;
                }
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
        SiteState site, string message, Guid? conversationId, bool startNewChat,
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

        var convId = conversationId ?? _activeConversationId;
        string? savedThreadUrl = null;
        bool hasKnownThread = convId.HasValue && _threadUrls.TryGetValue(convId.Value, out savedThreadUrl) && !string.IsNullOrWhiteSpace(savedThreadUrl);

        if (hasKnownThread)
        {
            // We ALREADY have a web chat thread for this OmniCoderPilot conversation!
            // Ensure WebView2 is on this thread:
            var currentUrl = await ExecScriptStringAsync(site, "window.location.href");
            if (!IsSameThreadUrl(currentUrl, savedThreadUrl!))
            {
                await _dispatcher.InvokeAsync(() => site.WebView!.CoreWebView2.Navigate(savedThreadUrl!));
                await Task.Delay(2000, ct);
            }
            // If already on the same thread, DO NOT TOUCH THE URL! Stay right here!
        }
        else if (startNewChat || (convId.HasValue && _activeConversationId.HasValue && _activeConversationId != convId))
        {
            // Brand new OmniCoderPilot conversation without a known thread: start a fresh chat on web chat
            await NavigateToNewChatAsync(site, ct);
        }

        if (convId.HasValue)
        {
            _activeConversationId = convId.Value;
        }

        bool inputReady = await WaitForInputAsync(site, ct, timeoutSeconds: 15);
        if (!inputReady)
        {
            yield return $"⚠️ Timed out waiting for {site.Config.DisplayName} input box.";
            yield break;
        }

        // Snapshot: count and text snippet of assistant messages BEFORE we send
        var (snapshotCount, lastAsstSnippet) = await GetAssistantSnapshotAsync(site);

        var typed = await TypeMessageAsync(site, message, ct);
        if (typed == "no-input")
        {
            yield return "⚠️ Could not find the chat input. Please try again.";
            yield break;
        }
        if (typed == "empty")
        {
            yield return "⚠️ Could not insert message into chat input. Please check if the browser is responsive.";
            yield break;
        }

        await Task.Delay(300, ct);
        var submitted = await SubmitMessageAsync(site, ct);
        if (!submitted)
        {
            var isGenerating = await ExecScriptBoolAsync(site,
                "(function(){ return !!document.querySelector('button[data-testid=\"stop-button\"], button[aria-label*=\"Stop\"], button[aria-label*=\"stop\"]'); })()");
            if (!isGenerating)
            {
                yield return "⚠️ Could not submit message to Web Chat. Send button was not ready.";
                yield break;
            }
        }

        // Stream the response (only messages appearing AFTER snapshot)
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        _ = Task.Run(() => PollResponseAsync(site, snapshotCount, lastAsstSnippet, convId, channel.Writer, ct), ct);
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
                   + "  try {"
                   + "    var closeBtns = document.querySelectorAll('button[aria-label=\"Close\"], button[data-testid=\"close-button\"], div[role=\"dialog\"] button');"
                   + "    for (var b of closeBtns) {"
                   + "      var txt = (b.innerText || b.textContent || '').toLowerCase();"
                   + "      if (txt.includes('stay logged out') || txt.includes('dismiss') || txt.includes('close') || txt.includes('accept') || txt.includes('not now')) {"
                   + "        b.click();"
                   + "      }"
                   + "    }"
                   + "  } catch(e) {}"
                   + "  var el = document.querySelector('#prompt-textarea')"
                   + "         || document.querySelector('#chat-input')"
                   + "         || document.querySelector('div[id=\"prompt-textarea\"]')"
                   + "         || document.querySelector('div[role=\"textbox\"][contenteditable=\"true\"]')"
                   + "         || document.querySelector('div[contenteditable=\"true\"]')"
                   + "         || document.querySelector('textarea[placeholder]')"
                   + "         || document.querySelector('textarea');"
                   + "  return el ? 'ready' : 'wait';"
                   + "})()";
            if (await ExecScriptStringAsync(site, js) == "ready") return true;
            await Task.Delay(600, ct);
        }
        return false;
    }

    private async Task<string> TypeMessageAsync(SiteState site, string message, CancellationToken ct = default)
    {
        // 1. Give focus to WebView2 natively
        try
        {
            await _dispatcher.InvokeAsync(() => site.WebView?.Focus());
        }
        catch { }

        var msg = JsStr(message);

        // 2. Multi-layer insertion script supporting ProseMirror, synthetic paste, execCommand, and textareas
        var insertJs = "(function(){"
            + "  function findInput() {"
            + "    var el = document.querySelector('#prompt-textarea');"
            + "    if (el) {"
            + "      if (el.getAttribute('contenteditable') === 'true' || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') return el;"
            + "      var inner = el.querySelector('div[contenteditable=\"true\"], [contenteditable=\"true\"], textarea');"
            + "      if (inner) return inner;"
            + "      return el;"
            + "    }"
            + "    return document.querySelector('#chat-input')"
            + "        || document.querySelector('div[id=\"prompt-textarea\"]')"
            + "        || document.querySelector('div[role=\"textbox\"][contenteditable=\"true\"]')"
            + "        || document.querySelector('div[contenteditable=\"true\"]')"
            + "        || document.querySelector('textarea[placeholder]')"
            + "        || document.querySelector('textarea');"
            + "  }"
            + ""
            + "  try {"
            + "    var closeBtns = document.querySelectorAll('button[aria-label=\"Close\"], button[data-testid=\"close-button\"], div[role=\"dialog\"] button');"
            + "    for (var b of closeBtns) {"
            + "      var txt = (b.innerText || b.textContent || '').toLowerCase();"
            + "      if (txt.includes('stay logged out') || txt.includes('dismiss') || txt.includes('close') || txt.includes('accept') || txt.includes('not now')) {"
            + "        b.click();"
            + "      }"
            + "    }"
            + "  } catch(e) {}"
            + ""
            + "  var input = findInput();"
            + "  if (!input) return 'no-input';"
            + ""
            + "  var text = " + msg + ";"
            + "  input.focus();"
            + ""
            + "  // Strategy A: Standard Textarea / Input (DeepSeek, etc.)"
            + "  if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {"
            + "    var proto = input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;"
            + "    var desc = Object.getOwnPropertyDescriptor(proto, 'value');"
            + "    if (desc && desc.set) { desc.set.call(input, text); }"
            + "    else { input.value = text; }"
            + "    input.dispatchEvent(new Event('input',  { bubbles: true, composed: true }));"
            + "    input.dispatchEvent(new Event('change', { bubbles: true, composed: true }));"
            + "    return (input.value || '').trim().length > 0 ? 'ok' : 'empty';"
            + "  }"
            + ""
            + "  // Strategy B: ProseMirror View transaction dispatch"
            + "  try {"
            + "    var curEl = input;"
            + "    var pmView = null;"
            + "    while (curEl && !pmView) {"
            + "      if (curEl.pmViewDesc && curEl.pmViewDesc.view) pmView = curEl.pmViewDesc.view;"
            + "      else if (curEl._pmViewDesc && curEl._pmViewDesc.view) pmView = curEl._pmViewDesc.view;"
            + "      curEl = curEl.parentElement;"
            + "    }"
            + "    if (pmView && pmView.state && pmView.dispatch) {"
            + "      var tr = pmView.state.tr;"
            + "      tr.delete(0, pmView.state.doc.content.size);"
            + "      tr.insertText(text, 0);"
            + "      pmView.dispatch(tr);"
            + "      pmView.focus();"
            + "      var cur = (input.innerText || input.textContent || '').trim();"
            + "      if (cur.length > 0) return 'ok';"
            + "    }"
            + "  } catch(e) {}"
            + ""
            + "  // Strategy C: Synthetic ClipboardEvent / Paste (DataTransfer)"
            + "  try {"
            + "    var sel = window.getSelection();"
            + "    var range = document.createRange();"
            + "    range.selectNodeContents(input);"
            + "    sel.removeAllRanges();"
            + "    sel.addRange(range);"
            + "    var dt = new DataTransfer();"
            + "    dt.setData('text/plain', text);"
            + "    var pasteEv = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });"
            + "    input.dispatchEvent(pasteEv);"
            + "    var cur = (input.innerText || input.textContent || '').trim();"
            + "    if (cur.length > 0) {"
            + "      input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));"
            + "      return 'ok';"
            + "    }"
            + "  } catch(e) {}"
            + ""
            + "  // Strategy D: document.execCommand('insertText')"
            + "  try {"
            + "    input.focus();"
            + "    document.execCommand('selectAll', false, null);"
            + "    document.execCommand('insertText', false, text);"
            + "    var cur = (input.innerText || input.textContent || '').trim();"
            + "    if (cur.length > 0) {"
            + "      input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));"
            + "      return 'ok';"
            + "    }"
            + "  } catch(e) {}"
            + ""
            + "  // Strategy E: InputEvent ('beforeinput' + 'input')"
            + "  try {"
            + "    input.focus();"
            + "    input.dispatchEvent(new InputEvent('beforeinput', { bubbles: true, cancelable: true, inputType: 'insertText', data: text }));"
            + "    input.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: text }));"
            + "    var cur = (input.innerText || input.textContent || '').trim();"
            + "    if (cur.length > 0) return 'ok';"
            + "  } catch(e) {}"
            + ""
            + "  // Strategy F: Safe DOM Paragraph insertion"
            + "  try {"
            + "    var p = input.querySelector('p');"
            + "    if (!p) { p = document.createElement('p'); input.appendChild(p); }"
            + "    p.textContent = text;"
            + "    input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));"
            + "    input.dispatchEvent(new Event('change', { bubbles: true, composed: true }));"
            + "  } catch(e) {}"
            + ""
            + "  var finalCur = (input.innerText || input.textContent || '').trim();"
            + "  return finalCur.length > 0 ? 'ok' : 'empty';"
            + "})()";

        var verifyJs = "(function(){"
            + "  var el = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
            + "  if (!el) return 'no-input';"
            + "  var t = (el.value || el.innerText || el.textContent || '').trim();"
            + "  return t.length > 0 ? 'ok' : 'empty';"
            + "})()";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var res = await ExecScriptStringAsync(site, insertJs);
            if (res == "ok") return "ok";
            if (res == "no-input")
            {
                if (attempt < 2)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                return "no-input";
            }

            await Task.Delay(200, ct);
            if (await ExecScriptStringAsync(site, verifyJs) == "ok")
                return "ok";
        }

        // Fallback: DevTools Protocol Input.insertText
        try
        {
            if (site.WebView?.CoreWebView2 != null)
            {
                await ExecScriptStringAsync(site,
                    "(function(){"
                    + "  var el = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
                    + "  if (el) { el.focus(); try { document.execCommand('selectAll', false, null); } catch(e){} }"
                    + "})()");
                var insertJson = JsonSerializer.Serialize(new { text = message });
                await _dispatcher.InvokeAsync(async () =>
                {
                    await site.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", insertJson);
                });
                await Task.Delay(250, ct);
                if (await ExecScriptStringAsync(site, verifyJs) == "ok")
                    return "ok";
            }
        }
        catch { }

        return "empty";
    }

    private async Task<bool> SubmitMessageAsync(SiteState site, CancellationToken ct)
    {
        // Snapshot user message count BEFORE submission
        var userCountBefore = await ExecScriptIntAsync(site,
            "(function(){ return document.querySelectorAll('[data-message-author-role=\"user\"]').length; })()");

        // 1. Poll for the send button to become enabled and click it (up to 4s, 20 iterations * 200ms)
        for (int i = 0; i < 20; i++)
        {
            var clickJs = "(function(){"
                + "  var inp = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
                + "  if (inp) {"
                + "    inp.dispatchEvent(new Event('input', { bubbles: true, composed: true }));"
                + "    inp.dispatchEvent(new Event('change', { bubbles: true, composed: true }));"
                + "  }"
                + "  var btn = document.querySelector('button[data-testid=\"send-button\"]')"
                + "          || document.querySelector('button[data-testid=\"composer-button-send\"]')"
                + "          || document.querySelector('button[data-testid=\"fruitjuice-send-button\"]')"
                + "          || document.querySelector('button[aria-label=\"Send prompt\"]')"
                + "          || document.querySelector('button[aria-label=\"Send message\"]')"
                + "          || document.querySelector('button[aria-label*=\"Send\"]')"
                + "          || document.querySelector('button[aria-label*=\"send\"]')"
                + "          || document.querySelector('form button[type=\"submit\"]');"
                + "  if (!btn) {"
                + "    var composer = document.querySelector('#composer-background, form, div[class*=\"composer\"]');"
                + "    if (composer) {"
                + "      var btns = Array.from(composer.querySelectorAll('button'));"
                + "      for (var b of btns) {"
                + "        var label = (b.getAttribute('aria-label') || '').toLowerCase();"
                + "        var tid = (b.getAttribute('data-testid') || '').toLowerCase();"
                + "        if (label.includes('attach') || label.includes('voice') || label.includes('speech') ||"
                + "            tid.includes('attach') || tid.includes('speech') || tid.includes('voice')) continue;"
                + "        if (b.querySelector('svg') || b.textContent.trim().length > 0) { btn = b; break; }"
                + "      }"
                + "    }"
                + "  }"
                + "  if (btn && !btn.disabled) { btn.click(); return 'clicked'; }"
                + "  if (btn && btn.disabled) {"
                + "    if (inp) {"
                + "      inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true }));"
                + "      inp.dispatchEvent(new KeyboardEvent('keyup', { key: 'a', bubbles: true }));"
                + "    }"
                + "    return 'disabled';"
                + "  }"
                + "  return 'not-found';"
                + "})()";

            var clickRes = await ExecScriptStringAsync(site, clickJs);
            if (clickRes == "clicked")
            {
                await Task.Delay(300, ct);
                if (await CheckIsSubmittedAsync(site, userCountBefore))
                    return true;
            }
            await Task.Delay(200, ct);
        }

        // 2. Fallback: Dispatch Enter key via JS (keydown, keypress, keyup) and form submit
        var enterJs = "(function(){"
            + "  var inp = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
            + "  if (inp) {"
            + "    inp.focus();"
            + "    var kd = new KeyboardEvent('keydown', { key:'Enter', code:'Enter', keyCode:13, which:13, bubbles:true, cancelable:true });"
            + "    inp.dispatchEvent(kd);"
            + "    var kp = new KeyboardEvent('keypress', { key:'Enter', code:'Enter', keyCode:13, which:13, bubbles:true, cancelable:true });"
            + "    inp.dispatchEvent(kp);"
            + "    var ku = new KeyboardEvent('keyup', { key:'Enter', code:'Enter', keyCode:13, which:13, bubbles:true, cancelable:true });"
            + "    inp.dispatchEvent(ku);"
            + "    var form = inp.closest('form');"
            + "    if (form) { try { form.requestSubmit(); } catch(e){} }"
            + "    return 'enter';"
            + "  }"
            + "  return 'no-input';"
            + "})()";
        await ExecScriptStringAsync(site, enterJs);
        await Task.Delay(400, ct);
        if (await CheckIsSubmittedAsync(site, userCountBefore))
            return true;

        // 3. Fallback: Dispatch native Enter via CDP
        try
        {
            if (site.WebView?.CoreWebView2 != null)
            {
                await _dispatcher.InvokeAsync(async () =>
                {
                    await site.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        "{\"type\":\"rawKeyDown\",\"windowsVirtualKeyCode\":13,\"code\":\"Enter\",\"key\":\"Enter\",\"unmodifiedText\":\"\\r\",\"text\":\"\\r\"}");
                    await site.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        "{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":13,\"code\":\"Enter\",\"key\":\"Enter\"}");
                });
            }
        }
        catch { }

        // 4. Verify whether message was actually submitted (poll for up to 4s)
        for (int i = 0; i < 14; i++)
        {
            await Task.Delay(300, ct);
            if (await CheckIsSubmittedAsync(site, userCountBefore))
                return true;
        }

        return false;
    }

    private async Task<bool> CheckIsSubmittedAsync(SiteState site, int userCountBefore)
    {
        var checkSubmittedJs = "(function(){"
            + "  var stop = document.querySelector('button[data-testid=\"stop-button\"], button[aria-label*=\"Stop\"], button[aria-label*=\"stop\"]');"
            + "  if (stop) return 'submitted-generating';"
            + "  var userNodes = document.querySelectorAll('[data-message-author-role=\"user\"]').length;"
            + "  if (userNodes > " + userCountBefore + ") return 'submitted-user-node';"
            + "  var inp = document.querySelector('#prompt-textarea, #chat-input, div[contenteditable=\"true\"], textarea');"
            + "  var text = (inp ? (inp.value || inp.innerText || inp.textContent || '') : '').trim();"
            + "  if (text.length === 0) return 'submitted-empty';"
            + "  return 'still-filled';"
            + "})()";

        var status = await ExecScriptStringAsync(site, checkSubmittedJs);
        return status is "submitted-generating" or "submitted-user-node" or "submitted-empty";
    }

    private async Task<int> ExecScriptIntAsync(SiteState site, string js)
    {
        var str = await ExecScriptStringAsync(site, js);
        return int.TryParse(str, out var v) ? v : 0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Response polling — snapshot-aware & virtualization-resilient
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(int Count, string Snippet)> GetAssistantSnapshotAsync(SiteState site)
    {
        var js = "(function(){"
               + "  var cg = document.querySelectorAll('[data-message-author-role=\"assistant\"]');"
               + "  if (cg.length > 0) {"
               + "    var last = cg[cg.length - 1];"
               + "    var t = (last.innerText || last.textContent || '').trim();"
               + "    var snip = t.length > 80 ? t.substring(t.length - 80) : t;"
               + "    return JSON.stringify({ count: cg.length, snip: snip });"
               + "  }"
               + "  var ds = document.querySelectorAll('.ds-markdown, [class*=\"ds-markdown\"]');"
               + "  if (ds.length > 0) {"
               + "    var last = ds[ds.length - 1];"
               + "    var t = (last.innerText || last.textContent || '').trim();"
               + "    var snip = t.length > 80 ? t.substring(t.length - 80) : t;"
               + "    return JSON.stringify({ count: ds.length, snip: snip });"
               + "  }"
               + "  var gen = document.querySelectorAll('[class*=\"assistant\"], [class*=\"bot-msg\"], [class*=\"ai-message\"]');"
               + "  if (gen.length > 0) {"
               + "    var last = gen[gen.length - 1];"
               + "    var t = (last.innerText || last.textContent || '').trim();"
               + "    var snip = t.length > 80 ? t.substring(t.length - 80) : t;"
               + "    return JSON.stringify({ count: gen.length, snip: snip });"
               + "  }"
               + "  return JSON.stringify({ count: 0, snip: '' });"
               + "})()";
        var r = await ExecScriptStringAsync(site, js);
        try
        {
            using var doc = JsonDocument.Parse(r);
            var root = doc.RootElement;
            int c = root.GetProperty("count").GetInt32();
            string s = root.GetProperty("snip").GetString() ?? "";
            return (c, s);
        }
        catch
        {
            return (0, "");
        }
    }

    private async Task PollResponseAsync(
        SiteState site, int snapshotCount, string lastAsstSnippet, Guid? convId,
        ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            string lastText    = "";
            int    stableCount = 0;
            const int stableThreshold = 5;
            var deadline = DateTime.UtcNow.AddMinutes(5);

            // Wait up to 75s for new response to appear (extend while Stop button is visible)
            for (int i = 0; i < 150 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                var t = await GetLatestResponseTextAsync(site, snapshotCount, lastAsstSnippet);
                if (!string.IsNullOrWhiteSpace(t)) { lastText = t; break; }

                // If Stop button is visible, ChatGPT is actively generating/thinking: keep waiting!
                var isGenerating = await ExecScriptBoolAsync(site,
                    "(function(){"
                    + "  var stop = document.querySelector('button[data-testid=\"stop-button\"], button[aria-label*=\"Stop\"], button[aria-label*=\"stop\"]');"
                    + "  return stop ? 'true' : 'false';"
                    + "})()");
                if (isGenerating && i > 120)
                {
                    i = 100; // extend wait while actively generating
                }
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
                    $"⚠️ No response from {site.Config.DisplayName} in 75s.\n"
                    + $"Debug: {diag}\n"
                    + "Make sure you are logged in and the message was submitted.", ct);
                return;
            }

            await writer.WriteAsync(lastText, ct);
            await TryCaptureThreadUrlAsync(site, convId);

            // Stream subsequent chunks as text grows
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);
                var current = await GetLatestResponseTextAsync(site, snapshotCount, lastAsstSnippet);

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

            await TryCaptureThreadUrlAsync(site, convId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { await writer.WriteAsync($"\n⚠️ {ex.Message}", ct); } catch { } }
        finally { writer.Complete(); }
    }

    private async Task TryCaptureThreadUrlAsync(SiteState site, Guid? convId)
    {
        if (!convId.HasValue) return;
        try
        {
            var url = await ExecScriptStringAsync(site, "window.location.href");
            if (IsThreadUrl(url, site))
            {
                if (!_threadUrls.TryGetValue(convId.Value, out var existing) || existing != url)
                {
                    _threadUrls[convId.Value] = url;
                    ThreadUrlUpdated?.Invoke(convId.Value, url);
                }
            }
        }
        catch { }
    }

    private static bool IsThreadUrl(string url, SiteState site)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var clean = url.Split('?')[0].TrimEnd('/');
        var baseClean = site.Config.Url.Split('?')[0].TrimEnd('/');

        if (clean.Equals(baseClean, StringComparison.OrdinalIgnoreCase)) return false;

        // ChatGPT thread URL pattern: https://chatgpt.com/c/<uuid>
        if (clean.Contains("/c/", StringComparison.OrdinalIgnoreCase)) return true;

        // DeepSeek thread URL pattern: https://chat.deepseek.com/a/chat/s/<uuid>
        if (clean.Contains("/chat/", StringComparison.OrdinalIgnoreCase)) return true;

        return clean.Length > baseClean.Length + 4;
    }

    private static bool IsSameThreadUrl(string u1, string u2)
    {
        if (string.IsNullOrWhiteSpace(u1) || string.IsNullOrWhiteSpace(u2)) return false;
        var c1 = u1.Split('?')[0].TrimEnd('/');
        var c2 = u2.Split('?')[0].TrimEnd('/');
        return c1.Equals(c2, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetLatestResponseTextAsync(SiteState site, int snapshotCount, string lastSnippet)
    {
        var snapStr = snapshotCount.ToString();
        var snipStr = JsStr(lastSnippet);
        var js = "(function(){"
            + "  var snap = " + snapStr + ";"
            + "  var prevSnip = " + snipStr + ";"
            + "  var isGen = !!document.querySelector('button[data-testid=\"stop-button\"], button[aria-label*=\"Stop\"], button[aria-label*=\"stop\"]');"
            + "  var cg = document.querySelectorAll('[data-message-author-role=\"assistant\"]');"
            + "  if (cg.length > 0) {"
            + "    var last = cg[cg.length - 1];"
            + "    var textEl = last.querySelector('.markdown, .prose, [class*=\"markdown\"], [class*=\"whitespace-pre-wrap\"]') || last;"
            + "    var t = (textEl.innerText || textEl.textContent || '').trim();"
            + "    if (cg.length > snap && t.length > 0) return t;"
            + "    if (isGen && t.length > 0) return t;"
            + "    if (prevSnip.length > 0 && !t.endsWith(prevSnip) && t.length > 0) return t;"
            + "  }"
            + "  var ds = document.querySelectorAll('.ds-markdown, [class*=\"ds-markdown\"]');"
            + "  if (ds.length > 0) {"
            + "    var last = ds[ds.length - 1];"
            + "    var t = (last.innerText || last.textContent || '').trim();"
            + "    if (ds.length > snap && t.length > 0) return t;"
            + "    if (isGen && t.length > 0) return t;"
            + "    if (prevSnip.length > 0 && !t.endsWith(prevSnip) && t.length > 0) return t;"
            + "  }"
            + "  var gen = document.querySelectorAll('[class*=\"assistant\"], [class*=\"bot-msg\"], [class*=\"ai-message\"]');"
            + "  if (gen.length > 0) {"
            + "    var last = gen[gen.length - 1];"
            + "    var t = (last.innerText || last.textContent || '').trim();"
            + "    if (gen.length > snap && t.length > 0) return t;"
            + "    if (isGen && t.length > 0) return t;"
            + "    if (prevSnip.length > 0 && !t.endsWith(prevSnip) && t.length > 0) return t;"
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
