using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class ToolCardsViewModel : ObservableObject
{
    public ObservableCollection<ToolCardViewModel> Cards { get; } = new();

    public ToolCardsViewModel(IEventAggregator events)
    {
        events.Subscribe<ToolEvent>(OnToolEvent);
    }

    private void OnToolEvent(ToolEvent e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = FindCard(e.ToolName);
            if (existing is not null)
            {
                existing.Status = e.Status;
                existing.Content = TruncateContent(e.Content);
                existing.FullContent = e.Content;
                existing.Icon = e.Icon;
            }
            else
            {
                Cards.Insert(0, new ToolCardViewModel
                {
                    ToolName = e.ToolName,
                    Status = e.Status,
                    Content = TruncateContent(e.Content),
                    FullContent = e.Content,
                    Icon = e.Icon
                });
            }
        });
    }

    private ToolCardViewModel? FindCard(string name)
    {
        foreach (var c in Cards)
            if (c.ToolName == name) return c;
        return null;
    }

    private static string TruncateContent(string content) =>
        content.Length > 300 ? content[..300] + "…" : content;
}

public partial class ToolCardViewModel : ObservableObject
{
    [ObservableProperty] private string _toolName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _fullContent = "";
    [ObservableProperty] private string _icon = "🛠️";
    [ObservableProperty] private bool _isExpanded;

    public string StatusBrushKey => Status switch
    {
        "running" => "WarnBrush",
        "completed" => "Accent2Brush",
        "failed" => "DangerBrush",
        "planned" => "Accent3Brush",
        _ => "MutedBrush"
    };

    public string StatusLabel => Status switch
    {
        "running" => "Running",
        "completed" => "Done",
        "failed" => "Failed",
        "planned" => "Planned",
        _ => Status
    };

    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        Content = IsExpanded ? FullContent : (FullContent.Length > 300 ? FullContent[..300] + "…" : FullContent);
    }
}
