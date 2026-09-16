using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class FolderBrowserViewModel : ObservableObject
{
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private ObservableCollection<FolderItemViewModel> _items = new();
    [ObservableProperty] private ObservableCollection<string> _breadcrumbs = new();
    [ObservableProperty] private string _selectedPath = "";

    public event Action<string>? FolderSelected;

    [RelayCommand]
    private void NavigateTo(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        CurrentPath = path;
        LoadFolder(path);
    }

    [RelayCommand]
    private void NavigateUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
            NavigateTo(parent.FullName);
    }

    [RelayCommand]
    private void SelectFolder()
    {
        FolderSelected?.Invoke(CurrentPath);
    }

    private void LoadFolder(string path)
    {
        Items.Clear();
        Breadcrumbs.Clear();

        // Build breadcrumbs
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var accumulated = parts.Length > 0 && path.StartsWith("/") ? "/" : "";
        foreach (var part in parts)
        {
            accumulated = string.IsNullOrEmpty(accumulated) ? part : accumulated + Path.DirectorySeparatorChar + part;
            Breadcrumbs.Add(accumulated);
        }

        try
        {
            // Add directories
            foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (name is ".git" or "bin" or "obj" or "node_modules" or ".vs") continue;
                Items.Add(new FolderItemViewModel
                {
                    Name = name,
                    FullPath = dir,
                    IsDirectory = true,
                    Icon = "📁"
                });
            }

            // Add files (dimmed)
            foreach (var file in Directory.GetFiles(path).OrderBy(f => f))
            {
                Items.Add(new FolderItemViewModel
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false,
                    Icon = "📄"
                });
            }
        }
        catch { }
    }
}

public partial class FolderItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private string _icon = "📄";
}
