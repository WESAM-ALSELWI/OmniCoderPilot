using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Wpf.Services;
using OmniCoderPilot.Wpf.Views;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IEventAggregator _events;
    private readonly IDbContextFactory<OmniCoderPilot.Infrastructure.AppDbContext> _dbFactory;

    [ObservableProperty] private SidebarViewModel _sidebar;
    [ObservableProperty] private ChatViewModel _chat;
    [ObservableProperty] private ActivityViewModel _activity;
    [ObservableProperty] private ToolCardsViewModel _toolCards;
    [ObservableProperty] private TodoViewModel _todo;
    [ObservableProperty] private FileTreeViewModel _fileTree;
    [ObservableProperty] private DiffViewModel _diff;

    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string _ollamaStatus = "Checking…";
    [ObservableProperty] private bool _ollamaOnline;
    [ObservableProperty] private string _activeModel = "";
    [ObservableProperty] private bool _isRightRailOpen = true;
    [ObservableProperty] private bool _isLeftSidebarOpen = true;

    [RelayCommand]
    private void ToggleRightRail() => IsRightRailOpen = !IsRightRailOpen;

    [RelayCommand]
    private void ToggleLeftSidebar() => IsLeftSidebarOpen = !IsLeftSidebarOpen;

    public MainViewModel(IServiceProvider services, IEventAggregator events, IDbContextFactory<OmniCoderPilot.Infrastructure.AppDbContext> dbFactory)
    {
        _services = services;
        _events = events;
        _dbFactory = dbFactory;

        FileTree = new FileTreeViewModel(services, events);
        Sidebar = new SidebarViewModel(services, events, dbFactory, FileTree);
        Chat = new ChatViewModel(services, events, dbFactory);
        Activity = new ActivityViewModel(events);
        ToolCards = new ToolCardsViewModel(events);
        Todo = new TodoViewModel(events);
        Diff = new DiffViewModel(events);

        SubscribeEvents();
        _ = InitializeAsync();
    }

    private void SubscribeEvents()
    {
        _events.Subscribe<StatusEvent>(OnStatus);
        _events.Subscribe<ErrorEvent>(OnError);
        _events.Subscribe<TokenReceivedEvent>(OnToken);
        _events.Subscribe<ThinkingStepEvent>(OnThinkingStep);
        _events.Subscribe<ToolStepsBatchEvent>(OnToolStepsBatch);
        _events.Subscribe<AgentActivityEvent>(OnAgentActivity);
        _events.Subscribe<PermissionRequestEvent>(OnPermissionRequest);
        _events.Subscribe<ModelSelectedEvent>(e => ActiveModel = e.ModelName);
    }

    private void OnStatus(StatusEvent e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Status == "completed" || e.Status == "error" || e.Status == "queued")
            {
                Chat.IsRunning = false;
                Chat.IsThinking = false;
                Chat.CanSend = true;
                Chat.CurrentActivity = "";
                Chat.FinalizeAssistant();
            }
        });
    }

    private void OnError(ErrorEvent e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Chat.IsRunning = false;
            Chat.IsThinking = false;
            Chat.CurrentActivity = "";
            Chat.CanSend = true;
        });
    }

    private void OnToken(TokenReceivedEvent e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Chat.AppendToken(e.Token));

    private void OnThinkingStep(ThinkingStepEvent e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Chat.AddThinkingStep(e.Text, e.ElapsedMs, e.TurnNumber, e.Phase));

    private void OnToolStepsBatch(ToolStepsBatchEvent e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Chat.AddToolStepsBatch(e.Groups));

    private void OnAgentActivity(AgentActivityEvent e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Activity.AddEntry(e.Activity, e.ActivityType);
            Chat.CurrentActivity = e.Activity;
        });
    }

    private void OnPermissionRequest(PermissionRequestEvent e)
    {
        // Show approval dialog on UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new Views.PermissionDialog(e.Request);
            bool approved = dialog.ShowDialog() == true;
            e.CompletionSource.TrySetResult(approved);
        });
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(IsDarkTheme ? "Dark" : "Light")}Theme.xaml", UriKind.Relative)
        };
        System.Windows.Application.Current.Resources.MergedDictionaries.Clear();
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);
    }

    private async Task InitializeAsync()
    {
        await CheckOllamaStatus();
        await Sidebar.InitializeAsync();
    }

    private async Task CheckOllamaStatus()
    {
        try
        {
            using var scope = _services.CreateScope();
            var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClient>();
            OllamaOnline = await ollama.IsAvailableAsync(CancellationToken.None);
            OllamaStatus = OllamaOnline ? "Online" : "Offline";
            if (OllamaOnline)
            {
                var models = await ollama.ListModelsAsync(CancellationToken.None);
                ActiveModel = models.FirstOrDefault()?.Name ?? "";
            }
        }
        catch
        {
            OllamaOnline = false;
            OllamaStatus = "Offline";
        }
    }
}
