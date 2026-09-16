using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniCoderPilot.Application;
using OmniCoderPilot.Infrastructure;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class DiffViewModel : ObservableObject
{
    private readonly IEventAggregator _events;

    public ObservableCollection<DiffCardViewModel> Cards { get; } = new();

    public DiffViewModel(IEventAggregator events)
    {
        _events = events;
        events.Subscribe<DiffEvent>(OnDiffEvent);
    }

    private void OnDiffEvent(DiffEvent e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            await LoadChangeSet(e.ChangeSetId);
        });
    }

    private async Task LoadChangeSet(Guid changeSetId)
    {
        try
        {
            using var scope = App.ServiceProvider.CreateScope();
            var diffService = scope.ServiceProvider.GetRequiredService<IDiffService>();
            Cards.Insert(0, new DiffCardViewModel
            {
                ChangeSetId = changeSetId,
                Description = "Change set ready",
                Status = "Preview"
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task ApplyDiffAsync(Guid changeSetId)
    {
        try
        {
            using var scope = App.ServiceProvider.CreateScope();
            var diffService = scope.ServiceProvider.GetRequiredService<IDiffService>();
            var card = FindCard(changeSetId);
            if (card is not null) card.Status = "Applying…";
            await diffService.ApplyAsync(changeSetId, System.Threading.CancellationToken.None);
            if (card is not null) card.Status = "Applied";
        }
        catch
        {
            var card = FindCard(changeSetId);
            if (card is not null) card.Status = "Failed";
        }
    }

    private DiffCardViewModel? FindCard(Guid id)
    {
        foreach (var c in Cards)
            if (c.ChangeSetId == id) return c;
        return null;
    }
}

public partial class DiffCardViewModel : ObservableObject
{
    [ObservableProperty] private Guid _changeSetId;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private ObservableCollection<DiffLineViewModel> _lines = new();

    public string StatusBrushKey => Status switch
    {
        "Applied" => "Accent2Brush",
        "Failed" => "DangerBrush",
        _ => "AccentBrush"
    };
}

public partial class DiffLineViewModel : ObservableObject
{
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _type = "";

    public string LineBrushKey => Type switch
    {
        "added" => "DiffAddedBrush",
        "removed" => "DiffRemovedBrush",
        "hunk" => "DiffHunkBrush",
        _ => "TextBrush"
    };
}
