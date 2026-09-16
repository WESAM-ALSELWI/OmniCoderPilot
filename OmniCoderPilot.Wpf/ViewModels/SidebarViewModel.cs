using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Infrastructure;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class SidebarViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IEventAggregator _events;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FileTreeViewModel _fileTree;

    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private Guid? _workspaceId;
    [ObservableProperty] private ObservableCollection<Conversation> _conversations = new();
    [ObservableProperty] private Conversation? _selectedConversation;
    [ObservableProperty] private ObservableCollection<string> _providers = new()
    {
        "All Providers",
        "💻 Local Ollama",
        "🌐 OpenRouter",
        "⚡ Groq (Free Cloud)",
        "🧠 DeepSeek Direct",
        "🤖 OpenAI Direct",
        "⚙️ Custom Endpoint"
    };
    [ObservableProperty] private string _selectedProvider = "All Providers";
    [ObservableProperty] private ObservableCollection<ModelItemViewModel> _allModels = new();
    [ObservableProperty] private ObservableCollection<ModelItemViewModel> _models = new();
    [ObservableProperty] private ModelItemViewModel? _selectedModel;
    [ObservableProperty] private string _currentProviderLabel = "💻 Local";
    [ObservableProperty] private bool _isFolderBrowserOpen;
    [ObservableProperty] private FolderBrowserViewModel? _folderBrowser;

    public string WorkspaceName => string.IsNullOrWhiteSpace(WorkspacePath)
        ? "No Workspace Selected"
        : System.IO.Path.GetFileName(WorkspacePath);

    partial void OnWorkspacePathChanged(string value) => OnPropertyChanged(nameof(WorkspaceName));

    partial void OnSelectedProviderChanged(string value)
    {
        FilterModels();
    }

    partial void OnSelectedModelChanged(ModelItemViewModel? value)
    {
        if (value is not null)
        {
            _events.Publish(new ModelSelectedEvent(value.Name));
            CurrentProviderLabel = $"{value.Icon} {value.Provider} ({value.DisplayName})";
        }
    }

    partial void OnSelectedConversationChanged(Conversation? value)
    {
        if (value is not null)
            _events.Publish(new ConversationSelectedEvent(value.Id));
    }

    public event Action? WorkspaceOpened;
    public event Action? ConversationCreated;

    public SidebarViewModel(IServiceProvider services, IEventAggregator events, IDbContextFactory<AppDbContext> dbFactory, FileTreeViewModel fileTree)
    {
        _services = services;
        _events = events;
        _dbFactory = dbFactory;
        _fileTree = fileTree;
        FolderBrowser = new FolderBrowserViewModel();
    }

    public async Task InitializeAsync()
    {
        await LoadModels();
        if (!string.IsNullOrWhiteSpace(WorkspacePath))
            await OpenWorkspace(WorkspacePath);
    }

    [RelayCommand]
    public void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Workspace Folder",
            InitialDirectory = string.IsNullOrEmpty(WorkspacePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : WorkspacePath
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            WorkspacePath = dlg.FolderName;
            _ = OpenWorkspace(WorkspacePath);
        }
    }

    [RelayCommand]
    private async Task OpenWorkspaceFromTextAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath)) return;
        await OpenWorkspace(WorkspacePath);
    }

    public async Task OpenWorkspace(string path)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.RootPath == path);
            if (existing is null)
            {
                existing = new Workspace
                {
                    Id = Guid.NewGuid(),
                    Name = System.IO.Path.GetFileName(path),
                    RootPath = path,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Workspaces.Add(existing);
                await db.SaveChangesAsync();
            }
            WorkspaceId = existing.Id;
            await LoadConversations();
            if (_fileTree is not null)
                await _fileTree.LoadTreeAsync(existing.Id, path);
            WorkspaceOpened?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenWorkspace error: {ex}");
        }
    }

    [RelayCommand]
    private async Task NewChatAsync()
    {
        if (WorkspaceId is null)
        {
            BrowseFolder();
            if (WorkspaceId is null) return;
        }
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conv = new Conversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId.Value,
            Title = "New Chat",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        await LoadConversations();
        SelectedConversation = Conversations.FirstOrDefault(c => c.Id == conv.Id);
        ConversationCreated?.Invoke();
    }

    private async Task LoadConversations()
    {
        if (WorkspaceId is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var convs = db.Conversations
            .Where(c => c.WorkspaceId == WorkspaceId)
            .AsEnumerable()
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();
        Conversations = new ObservableCollection<Conversation>(convs);
    }

    private async Task LoadModels()
    {
        try
        {
            using var scope = _services.CreateScope();
            var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClient>();
            var rawModels = await ollama.ListModelsAsync(CancellationToken.None);

            var items = new List<ModelItemViewModel>();

            foreach (var m in rawModels)
            {
                bool isRemote = IsRemoteModel(m.Name);
                items.Add(new ModelItemViewModel
                {
                    Name = m.Name,
                    DisplayName = m.Name,
                    Provider = isRemote ? "OpenRouter" : "Local Ollama",
                    IsRemote = isRemote
                });
            }

            // Ensure standard presets are present
            EnsureDefaultModels(items);

            AllModels = new ObservableCollection<ModelItemViewModel>(items);
            FilterModels();
        }
        catch
        {
            var fallback = new List<ModelItemViewModel>();
            EnsureDefaultModels(fallback);
            AllModels = new ObservableCollection<ModelItemViewModel>(fallback);
            FilterModels();
        }
    }

    private void FilterModels()
    {
        IEnumerable<ModelItemViewModel> list = AllModels;
        if (SelectedProvider == "💻 Local Ollama")
        {
            list = AllModels.Where(m => !m.IsRemote);
        }
        else if (SelectedProvider == "🌐 OpenRouter")
        {
            list = AllModels.Where(m => m.Provider == "OpenRouter");
        }
        else if (SelectedProvider == "⚡ Groq (Free Cloud)")
        {
            list = AllModels.Where(m => m.Provider == "Groq");
        }
        else if (SelectedProvider == "🧠 DeepSeek Direct")
        {
            list = AllModels.Where(m => m.Provider == "DeepSeek");
        }
        else if (SelectedProvider == "🤖 OpenAI Direct")
        {
            list = AllModels.Where(m => m.Provider == "OpenAI");
        }
        else if (SelectedProvider == "⚙️ Custom Endpoint")
        {
            list = AllModels.Where(m => m.Provider == "Custom");
        }

        var filtered = list.ToList();
        Models = new ObservableCollection<ModelItemViewModel>(filtered);
        if (SelectedModel == null || !filtered.Contains(SelectedModel))
        {
            SelectedModel = filtered.FirstOrDefault();
        }
    }

    private static bool IsRemoteModel(string name) =>
        name.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("meta-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("mistral", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("google/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("groq/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("openai-direct/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("custom/", StringComparison.OrdinalIgnoreCase);

    private static void EnsureDefaultModels(List<ModelItemViewModel> items)
    {
        void AddIfMissing(string name, bool isRemote, string provider)
        {
            if (!items.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new ModelItemViewModel
                {
                    Name = name,
                    DisplayName = name,
                    Provider = provider,
                    IsRemote = isRemote
                });
            }
        }

        // Local Ollama presets
        AddIfMissing("qwen3:8b", false, "Local Ollama");
        AddIfMissing("qwen3:4b", false, "Local Ollama");
        AddIfMissing("qwen2.5-coder:7b", false, "Local Ollama");

        // Cloud OpenRouter presets
        AddIfMissing("anthropic/claude-3.5-sonnet", true, "OpenRouter");
        AddIfMissing("openai/gpt-4o", true, "OpenRouter");
        AddIfMissing("google/gemini-2.5-flash", true, "OpenRouter");
        AddIfMissing("deepseek/deepseek-coder", true, "OpenRouter");
        AddIfMissing("deepseek/deepseek-r1:free", true, "OpenRouter");

        // Groq Free presets
        AddIfMissing("groq/llama-3.3-70b-versatile", true, "Groq");
        AddIfMissing("groq/deepseek-r1-distill-llama-70b", true, "Groq");
        AddIfMissing("groq/qwen-2.5-coder-32b", true, "Groq");

        // DeepSeek Direct presets
        AddIfMissing("deepseek-chat", true, "DeepSeek");
        AddIfMissing("deepseek-reasoner", true, "DeepSeek");

        // OpenAI Direct presets
        AddIfMissing("openai-direct/gpt-4o", true, "OpenAI");
        AddIfMissing("openai-direct/gpt-4o-mini", true, "OpenAI");
        AddIfMissing("openai-direct/o3-mini", true, "OpenAI");
    }

    [RelayCommand]
    private void RefreshModels() => _ = LoadModels();

    [RelayCommand]
    private async Task DeleteConversationAsync(Conversation? conv)
    {
        if (conv is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Delete all messages in this conversation
        var messages = await db.Messages.Where(m => m.ConversationId == conv.Id).ToListAsync();
        db.Messages.RemoveRange(messages);

        // Delete the conversation
        var entity = await db.Conversations.FindAsync(conv.Id);
        if (entity is not null)
            db.Conversations.Remove(entity);

        await db.SaveChangesAsync();

        // Remove from UI
        Conversations.Remove(conv);

        // If deleted conversation was selected, clear selection
        if (SelectedConversation?.Id == conv.Id)
        {
            SelectedConversation = Conversations.FirstOrDefault();
        }
    }
}

public sealed class ModelItemViewModel : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Provider { get; init; } = "Local Ollama";
    public bool IsRemote { get; init; }
    public string Icon => IsRemote ? "🌐" : "💻";
    public string Tag => IsRemote ? "OpenRouter" : "Ollama";
}

public sealed record ConversationSelectedEvent(Guid ConversationId);
