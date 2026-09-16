using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class ActivityViewModel : ObservableObject
{
    private readonly IEventAggregator _events;

    public ObservableCollection<ActivityEntryViewModel> Entries { get; } = new();

    public ActivityViewModel(IEventAggregator events)
    {
        _events = events;
    }

    public void AddEntry(string activity, string type)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Entries.Insert(0, new ActivityEntryViewModel(activity, type, DateTime.Now));
        });
    }
}

public partial class ActivityEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _type;
    [ObservableProperty] private string _time;
    [ObservableProperty] private string _icon;

    public string TypeBrushKey => Type switch
    {
        "thinking" => "AccentBrush",
        "tool-done" => "Accent2Brush",
        "tool-failed" or "error" => "DangerBrush",
        "tool-running" or "tool-dispatch" => "WarnBrush",
        "plan" => "Accent3Brush",
        "completed" => "Accent2Brush",
        "redirect" => "WarnBrush",
        "warning" => "WarnBrush",
        _ => "MutedBrush"
    };

    public string CopyText => $"[{Time}] {Text}";

    public ActivityEntryViewModel(string text, string type, DateTime time)
    {
        _text = text;
        _type = type;
        _time = time.ToString("HH:mm:ss");
        _icon = Type switch
        {
            "thinking" => "💭",
            "tool-done" => "✅",
            "tool-failed" or "error" => "❌",
            "tool-running" or "tool-dispatch" => "⚡",
            "plan" => "🗺️",
            "completed" => "✅",
            "redirect" => "↩️",
            "warning" => "⚠️",
            _ => "📝"
        };
    }
}
