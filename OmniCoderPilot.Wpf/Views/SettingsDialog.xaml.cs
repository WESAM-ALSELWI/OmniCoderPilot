using System.Windows;
using OmniCoderPilot.Wpf.ViewModels;

namespace OmniCoderPilot.Wpf.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += () => DialogResult = true;
    }
}
