using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniCoderPilot.Application;
using OmniCoderPilot.Wpf.Services;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class FileTreeViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IEventAggregator _events;
    private string _rootPath = "";

    [ObservableProperty] private ObservableCollection<FileTreeNodeViewModel> _nodes = new();
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewPath = "";
    [ObservableProperty] private string _previewContent = "";

    public FileTreeViewModel(IServiceProvider services, IEventAggregator events)
    {
        _services = services;
        _events = events;
    }

    public async Task LoadTreeAsync(Guid workspaceId, string rootPath)
    {
        _rootPath = rootPath;
        Nodes.Clear();
        var rootNodes = new ObservableCollection<FileTreeNodeViewModel>();
        await Task.Run(() => LoadDirectory(rootPath, rootNodes, 0));
        foreach (var n in rootNodes)
            Nodes.Add(n);
    }

    private void LoadDirectory(string path, ObservableCollection<FileTreeNodeViewModel> collection, int depth)
    {
        if (depth > 2) return;
        try
        {
            var dirs = Directory.GetDirectories(path)
                .Where(d => !IsIgnored(Path.GetFileName(d)))
                .OrderBy(d => d)
                .Take(50);

            foreach (var dir in dirs)
            {
                var node = new FileTreeNodeViewModel
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    IsDirectory = true
                };
                LoadDirectory(dir, node.Children, depth + 1);
                collection.Add(node);
            }

            var files = Directory.GetFiles(path)
                .Where(f => !IsIgnored(Path.GetFileName(f)))
                .OrderBy(f => f)
                .Take(50);

            foreach (var file in files)
            {
                collection.Add(new FileTreeNodeViewModel
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false,
                    Icon = GetFileIcon(Path.GetExtension(file))
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task PreviewFileAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            PreviewPath = path;
            PreviewContent = await File.ReadAllTextAsync(path);
            if (PreviewContent.Length > 8000)
                PreviewContent = PreviewContent[..8000] + "\n… (truncated)";
            HasPreview = true;
        }
        catch (Exception ex)
        {
            PreviewContent = $"Error reading file: {ex.Message}";
            HasPreview = true;
        }
    }

    [RelayCommand]
    private void ClosePreview() => HasPreview = false;

    private static bool IsIgnored(string name) =>
        name is ".git" or "bin" or "obj" or "node_modules" or ".vs" or ".idea" or ".vscode";

    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" => "🟣",
        ".js" or ".ts" or ".tsx" or ".jsx" => "🟡",
        ".py" => "🟢",
        ".json" or ".yaml" or ".yml" => "🟠",
        ".xml" or ".html" or ".htm" => "🔴",
        ".css" or ".scss" or ".less" => "🔵",
        ".md" => "📝",
        ".sql" => "🗃️",
        ".csproj" or ".sln" or ".slnx" => "⚙️",
        _ => "📄"
    };
}

public partial class FileTreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _icon = "📄";

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}
