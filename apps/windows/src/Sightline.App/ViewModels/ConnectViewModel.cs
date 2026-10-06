using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Settings;

namespace Sightline.App.ViewModels;

/// <summary>
/// Choosing a camera and the Wi-Fi adapter to reach it through, and joining it: what using each adapter
/// would do is said before anything happens, and an adapter on another network is used only once the
/// person has agreed.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly AppParts parts;
    private readonly TimeSpan searchTime;

    /// <summary>Creates the panel.</summary>
    /// <param name="parts">What the window is built from.</param>
    /// <param name="searchTime">How long looking for cameras may take; fifteen seconds when omitted.</param>
    public ConnectViewModel(AppParts parts, TimeSpan? searchTime = null)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        this.searchTime = searchTime ?? TimeSpan.FromSeconds(15);
        password = parts.Preferences.Current.CameraPassword;
    }

    /// <summary>Each camera in range, once per adapter that can see it, best first.</summary>
    public ObservableCollection<CameraOption> Cameras { get; } = [];

    /// <summary>The camera and adapter chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsConsent), nameof(Advice))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private CameraOption? selected;

    /// <summary>Whether the person accepted that the chosen adapter leaves the network it is on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool consented;

    /// <summary>The camera's Wi-Fi password, shown on its screen under WPA2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordProblem))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string password;

    /// <summary>Whether cameras are being looked for.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private bool searching;

    /// <summary>Whether a camera is being joined, or is joined.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool busy;

    /// <summary>One sentence on what to do next.</summary>
    [ObservableProperty]
    private string message = "Turn on the camera's Wi-Fi, then look for it.";

    /// <summary>Why the last connection failed, and what to do about it; null when it did not.</summary>
    [ObservableProperty]
    private string? problem;

    /// <summary>Whether the chosen adapter must leave a network, so the person is asked first.</summary>
    public bool NeedsConsent => Selected?.RequiresConsent == true;

    /// <summary>What using the chosen adapter does to this PC's connections.</summary>
    public string Advice => Selected?.Explanation ?? "";

    /// <summary>What is wrong with the password, or null when nothing is.</summary>
    public string? PasswordProblem => Preferences.IsValidPassword(Password) ? null : "8 to 63 letters, numbers or symbols.";

    /// <summary>Follows the camera's connection.</summary>
    internal void Apply(CameraState state)
    {
        Busy = state.Connection is not (Connection.Idle or Connection.Failed);
        Problem = state.Connection is Connection.Failed failed ? failed.Problem.Explanation : null;
    }

    partial void OnSelectedChanged(CameraOption? value) => Consented = false;

    private bool CanFind() => !Searching;

    /// <summary>Looks for cameras with every adapter, changing no connection.</summary>
    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        Searching = true;
        IReadOnlyList<CameraOption> found;
        try
        {
            using var limit = new CancellationTokenSource(searchTime);
            found = await parts.Finder.FindAsync(parts.Preferences.Current.LastAdapter, limit.Token);
        }
        catch (Exception failure) when (failure is WlanException or OperationCanceledException)
        {
            Message = failure is OperationCanceledException
                ? "Windows took too long to look for Wi-Fi. Try again."
                : $"Windows could not look for Wi-Fi: {failure.Message}";
            return;
        }
        finally
        {
            Searching = false;
        }

        var last = parts.Preferences.Current;
        Cameras.Clear();
        foreach (var option in found)
        {
            Cameras.Add(option);
        }

        // The camera used last, through the adapter used last, when it is there; otherwise the best.
        Selected = Cameras.FirstOrDefault(o => o.Ssid == last.LastCamera && o.AdapterId == last.LastAdapter) ?? Cameras.FirstOrDefault();
        Message = Cameras.Count == 0
            ? "No camera in range. Press its Wi-Fi button, keep it close, and look again."
            : "Pick the camera, and the Wi-Fi adapter to reach it through, then connect.";
    }

    private bool CanConnect() =>
        Selected is not null && (!NeedsConsent || Consented) && Preferences.IsValidPassword(Password) && !Busy;

    /// <summary>Joins the chosen camera through the chosen adapter, and remembers both for next time.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        var option = Selected!;
        parts.Preferences.Update(p => p with { CameraPassword = Password, LastAdapter = option.AdapterId, LastCamera = option.Ssid });
        parts.Choice.Current = option.Target(Password, Consented);
        _ = parts.Controller.Connect();
    }

    /// <summary>Leaves the camera, and puts the adapter back on the network it was on.</summary>
    [RelayCommand]
    private Task DisconnectAsync() => parts.Controller.DisconnectAsync();
}
