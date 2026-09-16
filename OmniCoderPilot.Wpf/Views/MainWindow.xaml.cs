using System;
using System.Windows;
using System.Windows.Input;
using OmniCoderPilot.Domain;
using OmniCoderPilot.Wpf.ViewModels;

namespace OmniCoderPilot.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        Width = Math.Min(1440, screenWidth * 0.85);
        Height = Math.Min(920, screenHeight * 0.85);
        Left = (screenWidth - Width) / 2;
        Top = (screenHeight - Height) / 2;

        if (DataContext is MainViewModel vm)
        {
            vm.Chat.Messages.CollectionChanged += (s, args) =>
            {
                Dispatcher.InvokeAsync(() => ChatScroll.ScrollToBottom());
            };
        }
    }

    private void Prompt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Chat.SendCommand.Execute(null);
                e.Handled = true;
                Dispatcher.InvokeAsync(() => ChatScroll.ScrollToBottom());
            }
        }
    }

    private void Conversation_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border &&
            border.DataContext is Conversation conv &&
            DataContext is MainViewModel vm)
        {
            vm.Sidebar.SelectedConversation = conv;
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Sidebar.BrowseFolder();
        }
    }

    private void WorkspaceHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Sidebar.BrowseFolder();
        }
    }

    private Conversation? _contextConversation;

    private void Conversation_ContextOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border && border.DataContext is Conversation conv)
        {
            _contextConversation = conv;
        }
    }

    private async void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_contextConversation is null || DataContext is not MainViewModel vm) return;

        var result = MessageBox.Show(
            $"Delete \"{_contextConversation.Title}\"?",
            "Delete Conversation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await vm.Sidebar.DeleteConversationCommand.ExecuteAsync(_contextConversation);
        }
        _contextConversation = null;
    }

    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem menuItem &&
            menuItem.DataContext is ActivityEntryViewModel entry)
        {
            try
            {
                System.Windows.Clipboard.SetText(entry.CopyText);
            }
            catch { }
        }
    }

    private void ToolStepEntry_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border &&
            border.DataContext is ToolStepEntryViewModel entry &&
            entry.CanExpand)
        {
            entry.ToggleExpandCommand.Execute(null);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsVm = App.ServiceProvider.GetService(typeof(SettingsViewModel)) as SettingsViewModel;
        if (settingsVm is not null)
        {
            var dlg = new Views.SettingsDialog(settingsVm)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }
    }
}
