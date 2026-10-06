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
        Opened += (_, _) => Begin(DataContext as ShellViewModel);
    }

    /// <summary>
    /// Once the window is up: joins the last camera when allowed, and looks for cameras either way, which
    /// changes no connection and fills the connect panel in case the last camera is not there.
    /// </summary>
    internal static void Begin(ShellViewModel? shell)
    {
        if (shell is null)
        {
            return;
        }

        shell.Start();
        shell.Connect.FindCommand.Execute(null);
    }
}
