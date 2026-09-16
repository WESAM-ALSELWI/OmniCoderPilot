using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class TodoViewModel : ObservableObject
{
    public ObservableCollection<TodoItemViewModel> Items { get; } = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _hasItems;

    public TodoViewModel(IEventAggregator events)
    {
        events.Subscribe<TodoEvent>(OnTodoEvent);
    }

    private void OnTodoEvent(TodoEvent e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Items.Clear();
            foreach (var item in e.Todos.OrderBy(t => t.Order))
                Items.Add(new TodoItemViewModel(item));

            TotalCount = Items.Count;
            CompletedCount = Items.Count(i => i.Status == "done");
            ProgressPercent = TotalCount > 0 ? (double)CompletedCount / TotalCount * 100 : 0;
            HasItems = Items.Count > 0;
        });
    }
}

public partial class TodoItemViewModel : ObservableObject
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _icon;
    [ObservableProperty] private string _statusLabel;

    public string StatusBrushKey => Status switch
    {
        "done" => "Accent2Brush",
        "in-progress" => "AccentBrush",
        "cancelled" => "MutedBrush",
        _ => "Muted2Brush"
    };

    public bool IsDone => Status == "done";
    public bool IsInProgress => Status == "in-progress";
    public bool IsCancelled => Status == "cancelled";

    public TodoItemViewModel(TodoItem item)
    {
        _content = item.Content;
        _status = item.Status ?? "pending";
        _icon = Status switch
        {
            "done" => "✅",
            "in-progress" => "🔄",
            "cancelled" => "❌",
            _ => "⬜"
        };
        _statusLabel = Status;
    }
}
