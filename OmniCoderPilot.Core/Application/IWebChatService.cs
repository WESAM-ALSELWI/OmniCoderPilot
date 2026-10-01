namespace OmniCoderPilot.Application;

/// <summary>
/// Chat2API-style service: drives a real web-chat UI (DeepSeek, ChatGPT, etc.)
/// inside an embedded browser instead of calling the official REST API.
/// Implementations live in OmniCoderPilot.Wpf (WebView2-based).
/// </summary>
public interface IWebChatService
{
    /// <summary>
    /// Current login state of the embedded browser session.
    /// </summary>
    bool IsLoggedIn { get; }

    /// <summary>
    /// URL of the web chat page this service targets (e.g. "https://chat.deepseek.com").
    /// </summary>
    string ChatUrl { get; }

    /// <summary>
    /// Provider display name, e.g. "DeepSeek Web".
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Show the embedded browser so the user can log in manually.
    /// Returns true once the user is detected as logged in.
    /// </summary>
    Task<bool> ShowLoginAsync(CancellationToken ct);

    /// <summary>
    /// Send a message to the web chat page and stream back the assistant response tokens.
    /// Throws if not logged in.
    /// </summary>
    IAsyncEnumerable<string> SendMessageAsync(string message, CancellationToken ct);
}
