using Avalonia.Controls;
using Sightline.App.ViewModels;

namespace Sightline.App.Views;

/// <summary>The main window.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as MainViewModel)?.RefreshCamerasCommand.Execute(null);
    }
}
