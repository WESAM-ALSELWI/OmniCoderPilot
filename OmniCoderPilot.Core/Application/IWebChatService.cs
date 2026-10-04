namespace OmniCoderPilot.Application;

/// <summary>
/// Chat2API-style service: drives real web-chat UIs (DeepSeek, ChatGPT, etc.)
/// inside an embedded browser instead of calling the official REST API.
/// Implementations live in OmniCoderPilot.Wpf (WebView2-based).
/// </summary>
public interface IWebChatService
{
    // ── Legacy single-site API (kept for compat) ──────────────────────────
    bool   IsLoggedIn  { get; }
    string ChatUrl     { get; }
    string ProviderName { get; }

    Task<bool>                ShowLoginAsync(CancellationToken ct);
    IAsyncEnumerable<string>  SendMessageAsync(string message, CancellationToken ct);

    // ── Multi-site model-aware API ─────────────────────────────────────────

    /// <summary>True if the user is logged in for the given model ("webchat/deepseek" etc.).</summary>
    bool IsLoggedInFor(string modelName);

    /// <summary>
    /// Show the login window for the site that matches <paramref name="modelName"/>.
    /// Returns true once login is detected.
    /// </summary>
    Task<bool> ShowLoginForModelAsync(string modelName, CancellationToken ct);

    /// <summary>
    /// Send a message to the site that matches <paramref name="modelName"/> and stream back tokens.
    /// </summary>
    IAsyncEnumerable<string> SendMessageForModelAsync(string modelName, string message, CancellationToken ct, bool startNewChat = false);

    /// <summary>Display name for a given model name, e.g. "ChatGPT Web" for "webchat/chatgpt".</summary>
    string GetDisplayNameFor(string modelName);

    /// <summary>
    /// Log out the account for the specified model/site (clears cookies, session, localStorage).
    /// </summary>
    Task LogoutAsync(string modelName);

    /// <summary>
    /// Log out the current account, then open the visible login window for the user to log in with a different account.
    /// </summary>
    Task<bool> SwitchAccountAsync(string modelName, CancellationToken ct);
}
