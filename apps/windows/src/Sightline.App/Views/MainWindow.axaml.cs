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

    /// <summary>Brings the window back from the tray, in front.</summary>
    public void Bring()
    {
        Show();
        WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        Activate();
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

    /// <inheritdoc />
    /// <remarks>
    /// Closed by the person while a camera is connected or Sentry is armed, and they allow it, the window goes
    /// to the tray instead: closing a window should not stop a watch or drop a camera mid-copy. The app
    /// quitting, from the tray or Windows shutting down, always closes it.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && DataContext is ShellViewModel { KeepsRunningWhenClosed: true })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
