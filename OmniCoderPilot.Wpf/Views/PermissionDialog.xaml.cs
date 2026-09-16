using System.Windows;
using OmniCoderPilot.Application;

namespace OmniCoderPilot.Wpf.Views;

public partial class PermissionDialog : Window
{
    public PermissionDialog(PermissionRequest request)
    {
        InitializeComponent();
        ToolNameText.Text = request.ToolName;
        DescText.Text = request.Description;
        ArgsText.Text = request.Arguments;
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
