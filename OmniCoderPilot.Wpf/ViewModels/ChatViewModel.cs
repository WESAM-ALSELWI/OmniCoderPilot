using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Infrastructure;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IEventAggregator _events;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private string _assistantBuffer = "";
    private bool _hasAssistantBubble;
    private CancellationTokenSource? _currentCts;

    [ObservableProperty] private ObservableCollection<ChatMessageViewModel> _messages = new();
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private bool _canSend = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private string _currentActivity = "Working…";
    [ObservableProperty] private Guid? _conversationId;
    [ObservableProperty] private string _chatTitle = "OmniCoderPilot";

    public ChatViewModel(IServiceProvider services, IEventAggregator events, IDbContextFactory<AppDbContext> dbFactory)
    {
        _services = services;
        _events = events;
        _dbFactory = dbFactory;

        _events.Subscribe<ConversationSelectedEvent>(async e =>
        {
            ConversationId = e.ConversationId;
            await LoadHistory(e.ConversationId);
        });
    }

    [RelayCommand]
    private void Stop()
    {
        try
        {
            _currentCts?.Cancel();
        }
        catch { }
        finally
        {
            IsRunning = false;
            IsThinking = false;
            CanSend = true;
            CurrentActivity = "";
            FinalizeAssistant();
        }
    }

    [RelayCommand]
    private void CopyMessage(string? content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            try
            {
                System.Windows.Clipboard.SetText(content);
            }
            catch { }
        }
    }

    [RelayCommand]
    private void EditAndResend(string? content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            Prompt = content;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt) || IsRunning) return;

        var text = Prompt.Trim();
        Prompt = "";

        // Prompt user to select workspace folder if none is selected
        var mainVm = (MainViewModel)System.Windows.Application.Current.MainWindow.DataContext;
        var sidebar = mainVm.Sidebar;
        if (sidebar.WorkspaceId is null || string.IsNullOrWhiteSpace(sidebar.WorkspacePath))
        {
            sidebar.BrowseFolder();
            if (sidebar.WorkspaceId is null)
            {
                // User cancelled folder selection; restore prompt
                Prompt = text;
                return;
            }
        }

        // Create conversation if needed
        if (ConversationId is null)
        {
            await using var newDb = await _dbFactory.CreateDbContextAsync();
            var conv = new Conversation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = sidebar.WorkspaceId.Value,
                Title = text.Length > 50 ? text[..50] + "…" : text,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            newDb.Conversations.Add(conv);
            await newDb.SaveChangesAsync();
            ConversationId = conv.Id;
            sidebar.Conversations.Insert(0, conv);
        }

        // Add user message
        var userMsg = new ChatMessageViewModel { Role = "user", Content = text };
        Messages.Add(userMsg);
        await PersistMessage("user", text);

        // Reset assistant bubble
        _assistantBuffer = "";
        _hasAssistantBubble = false;
        _finalized = false;
        IsRunning = true;
        IsThinking = true;
        CanSend = false;

        // Run agent on background thread
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = _currentCts.Token;

        var wsId = mainVm.Sidebar.WorkspaceId ?? Guid.Empty;
        var activeModel = mainVm.ActiveModel;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();

                await orchestrator.RunTurnAsync(new ChatRequest(
                    wsId,
                    ConversationId.Value,
                    text,
                    "",
                    ct,
                    string.IsNullOrEmpty(activeModel) ? null : activeModel
                ));
            }
            catch (OperationCanceledException)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsRunning = false;
                    IsThinking = false;
                    CanSend = true;
                    CurrentActivity = "";
                    Messages.Add(new ChatMessageViewModel { Role = "assistant", Content = "[Task stopped by user]" });
                    FinalizeAssistant();
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsRunning = false;
                    IsThinking = false;
                    CanSend = true;
                    CurrentActivity = "";
                    Messages.Add(new ChatMessageViewModel { Role = "error", Content = ex.Message });
                    FinalizeAssistant();
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                });
            }
            finally
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsRunning = false;
                    IsThinking = false;
                    CanSend = true;
                    CurrentActivity = "";
                    FinalizeAssistant();
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                });
            }
        }, ct);
    }

    public void AppendToken(string token)
    {
        // Only clear thinking state on the very first token (streaming answer starts)
        if (IsThinking)
        {
            IsThinking = false;
            CurrentActivity = "";
        }
        _assistantBuffer += token;
        if (!_hasAssistantBubble)
        {
            var assistantMsg = new ChatMessageViewModel { Role = "assistant", Content = "", IsStreaming = true };
            Messages.Add(assistantMsg);
            _hasAssistantBubble = true;
        }
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last is not null)
        {
            last.Content = StripMarkdown(_assistantBuffer);
            last.IsStreaming = true;
        }
    }

    /// <summary>
    /// Strips Markdown formatting to display plain text like Claude/Opencode.
    /// </summary>
    private static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Remove <think>...</think> blocks entirely
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);

        // Remove code blocks ```...```
        text = Regex.Replace(text, @"```[\s\S]*?```", m =>
        {
            var code = m.Value.Substring(3, m.Value.Length - 6);
            var lines = code.Split('\n');
            // Skip first line if it's a language identifier (e.g., "csharp", "powershell")
            var startIdx = lines.Length > 1 && !lines[0].Contains(' ') ? 1 : 0;
            return string.Join("\n", lines.Skip(startIdx));
        }, RegexOptions.IgnoreCase);

        // Remove inline code backticks
        text = Regex.Replace(text, @"`([^`]+)`", "$1");

        // Remove bold **text** or __text__
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");

        // Remove italic *text* or _text_ (but not inside words)
        text = Regex.Replace(text, @"(?<!\w)\*([^*]+)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"(?<!\w)_([^_]+)_(?!\w)", "$1");

        // Remove strikethrough ~~text~~
        text = Regex.Replace(text, @"~~([^~]+)~~", "$1");

        // Remove headers ### 
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);

        // Remove horizontal rules ---
        text = Regex.Replace(text, @"^[-*_]{3,}\s*$", "", RegexOptions.Multiline);

        // Remove [text](url) links - keep just the text
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        // Remove images ![alt](url)
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");

        // Remove blockquotes >
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);

        // Collapse multiple blank lines
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    private bool _finalized;

    public void FinalizeAssistant()
    {
        if (_finalized) return;
        _finalized = true;
        IsThinking = false;
        CurrentActivity = "";
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last is not null)
        {
            last.IsStreaming = false;
            last.Content = StripMarkdown(_assistantBuffer);
        }
        _ = PersistMessage("assistant", StripMarkdown(_assistantBuffer));
    }

    public void AddThinkingStep(string text, int elapsedMs, int turnNumber, OmniCoderPilot.Application.AgentLoopPhase phase)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last is null)
        {
            last = new ChatMessageViewModel { Role = "assistant", Content = "" };
            Messages.Add(last);
            _hasAssistantBubble = true;
        }
        var item = new ThinkingStepViewModel(text, elapsedMs, turnNumber, phase);
        last.ThinkingSteps.Add(item);
        last.TraceItems.Add(item);
    }

    public void AddToolStepsBatch(System.Collections.Generic.IReadOnlyList<ToolStepGroup> groups)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last is null)
        {
            last = new ChatMessageViewModel { Role = "assistant", Content = "" };
            Messages.Add(last);
            _hasAssistantBubble = true;
        }
        foreach (var g in groups)
        {
            var item = new ToolStepGroupViewModel(g);
            last.ToolStepGroups.Add(item);
            last.TraceItems.Add(item);
        }
    }

    private async Task LoadHistory(Guid convId)
    {
        Messages.Clear();
        _hasAssistantBubble = false;
        _assistantBuffer = "";
        _finalized = false;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var msgs = db.Messages
            .Where(m => m.ConversationId == convId)
            .AsEnumerable()
            .OrderBy(m => m.CreatedAt)
            .ToList();

        foreach (var m in msgs)
        {
            Messages.Add(new ChatMessageViewModel
            {
                Role = m.Role.ToString().ToLower(),
                Content = m.Content
            });
        }
    }

    private async Task PersistMessage(string role, string content)
    {
        if (ConversationId is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Messages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = ConversationId.Value,
            Role = role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => ChatRole.Assistant
            },
            Content = content,
            TokenEstimate = content.Length / 4,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    partial void OnCanSendChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }
}

public abstract class TraceItemViewModel : ObservableObject
{
}

public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isStreaming;
    public ObservableCollection<TraceItemViewModel> TraceItems { get; } = new();
    public ObservableCollection<ThinkingStepViewModel> ThinkingSteps { get; } = new();
    public ObservableCollection<ToolStepGroupViewModel> ToolStepGroups { get; } = new();

    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsError => Role == "error";
    public bool HasTraceItems => TraceItems.Count > 0;
    public bool HasThinkingSteps => ThinkingSteps.Count > 0;
    public bool HasToolSteps => ToolStepGroups.Count > 0;

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(IsUser));
}

public partial class ThinkingStepViewModel : TraceItemViewModel
{
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _timeLabel;
    [ObservableProperty] private int _turnNumber;
    [ObservableProperty] private string _phaseLabel;

    public string PhaseIcon => PhaseLabel switch
    {
        "Think" => "\U0001F9E0",
        "Observe" => "\U0001F441\uFE0F",
        "Act" => "\u26A1",
        _ => "\U0001F9E0"
    };

    public string PhaseBrushKey => PhaseLabel switch
    {
        "Think" => "AccentBrush",
        "Observe" => "ReadBrush",
        "Act" => "EditBrush",
        _ => "AccentBrush"
    };

    public ThinkingStepViewModel(string text, int elapsedMs, int turnNumber, OmniCoderPilot.Application.AgentLoopPhase phase)
    {
        _text = text;
        _timeLabel = FormatElapsed(elapsedMs);
        _turnNumber = turnNumber;
        _phaseLabel = phase switch
        {
            OmniCoderPilot.Application.AgentLoopPhase.Think => "Think",
            OmniCoderPilot.Application.AgentLoopPhase.Observe => "Observe",
            OmniCoderPilot.Application.AgentLoopPhase.Act => "Act",
            OmniCoderPilot.Application.AgentLoopPhase.Observe_Results => "Results",
            _ => "Think"
        };
    }

    private static string FormatElapsed(int ms)
    {
        if (ms < 1000) return $"{ms}ms";
        var s = ms / 1000.0;
        if (s < 60) return $"{Math.Round(s)}s";
        var m = (int)s / 60;
        var sec = Math.Round(s % 60);
        return $"{m}m {sec}s";
    }
}

public partial class ToolStepGroupViewModel : TraceItemViewModel
{
    [ObservableProperty] private string _header;
    [ObservableProperty] private bool _isExpandable;
    [ObservableProperty] private int _turnNumber;
    [ObservableProperty] private string _phaseLabel;
    public ObservableCollection<ToolStepEntryViewModel> Steps { get; } = new();

    public string HeaderTitle => Header.StartsWith("Explored", StringComparison.OrdinalIgnoreCase)
        ? "Explored"
        : (string.IsNullOrEmpty(Header) ? "Actions" : Header);

    public string HeaderSuffix
    {
        get
        {
            if (Header.StartsWith("Explored ", StringComparison.OrdinalIgnoreCase))
                return Header["Explored ".Length..];
            if (Steps.Count > 0)
                return $"{Steps.Count} reads";
            return "";
        }
    }

    public string PhaseBrushKey => PhaseLabel switch
    {
        "Act" => "EditBrush",
        "Observe" => "ReadBrush",
        _ => "MutedBrush"
    };

    public ToolStepGroupViewModel(ToolStepGroup group)
    {
        _header = group.Header;
        _isExpandable = group.Expandable;
        _turnNumber = group.TurnNumber;
        _phaseLabel = group.Phase switch
        {
            OmniCoderPilot.Application.AgentLoopPhase.Think => "Think",
            OmniCoderPilot.Application.AgentLoopPhase.Observe => "Observe",
            OmniCoderPilot.Application.AgentLoopPhase.Act => "Act",
            OmniCoderPilot.Application.AgentLoopPhase.Observe_Results => "Results",
            _ => "Act"
        };
        foreach (var s in group.Steps)
            Steps.Add(new ToolStepEntryViewModel(s));
    }
}

public partial class ToolStepEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _filePath;
    [ObservableProperty] private string _lang;
    [ObservableProperty] private string _detail;
    [ObservableProperty] private string _operation;
    [ObservableProperty] private string _diffContent;
    [ObservableProperty] private bool _isExpanded;

    public string ActionTitle => Operation switch
    {
        "edit" => "Edit",
        "write" => "Write",
        "append" => "Append",
        "read" or "explore" => "Explored",
        "search" => "Search",
        "command" or "execute" => "Command",
        "delete" => "Delete",
        "web" or "fetch" => "Fetch",
        "task" => "Task",
        _ => !string.IsNullOrEmpty(Label) ? Label : "Action"
    };

    public string OperationBrushKey => Operation switch
    {
        "edit" or "write" or "add" => "EditBrush",
        "read" or "explore" => "ReadBrush",
        "search" => "SearchBrush",
        "command" or "execute" => "CommandBrush",
        "delete" => "DeleteBrush",
        "web" or "fetch" => "WebBrush",
        "task" => "TaskBrush",
        _ => "MutedBrush"
    };

    public string AddedText => ParseAdded(Detail);
    public string RemovedText => ParseRemoved(Detail);

    public bool HasDiff => !string.IsNullOrEmpty(DiffContent);
    public bool CanExpand => HasDiff || Operation is "edit" or "write" or "read" or "explore" or "search" or "command";

    public ToolStepEntryViewModel(ToolStepEntry entry)
    {
        _label = entry.Label;
        _filePath = entry.FilePath;
        _lang = entry.Lang;
        _detail = entry.Detail;
        _operation = entry.Operation;
        _diffContent = entry.DiffContent;
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    private static string ParseAdded(string detail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(detail, @"\+(\d+)");
        return match.Success ? $"+{match.Groups[1].Value}" : "";
    }

    private static string ParseRemoved(string detail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(detail, @"-(\d+)");
        return match.Success ? $"-{match.Groups[1].Value}" : "";
    }
}
