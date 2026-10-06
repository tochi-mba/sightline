using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Core.Updates;

namespace Sightline.App.ViewModels;

/// <summary>The window's pages.</summary>
public enum Page
{
    /// <summary>The live picture and the shutter.</summary>
    Live,

    /// <summary>The camera's card.</summary>
    Library,

    /// <summary>The security camera.</summary>
    Sentry,

    /// <summary>The camera's settings and the app's.</summary>
    Settings,
}

/// <summary>
/// The window: which page shows, the camera's state for every page, the notice the camera last posted,
/// and the introduction or what's new on top.
/// </summary>
/// <remarks>
/// The controller announces changes from whatever thread made them, and two can arrive out of order. So
/// the window never applies the state an announcement carried: it posts one look at the controller's state
/// as it is by then, and announcements that arrive while that look is pending need no look of their own.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly AppParts parts;
    private int applyPending;
    private long noticeId;

    /// <summary>Builds the window from <paramref name="parts"/>.</summary>
    public ShellViewModel(AppParts parts)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        Connect = new ConnectViewModel(parts);
        Live = new LiveViewModel(parts);
        Library = new LibraryViewModel(parts);
        Sentry = new SentryViewModel(parts);
        Updates = new UpdatesViewModel(parts);
        Settings = new SettingsViewModel(parts, this);

        var preferences = parts.Preferences.Current;
        showOnboarding = !preferences.OnboardingDone;
        whatsNew = preferences.OnboardingDone ? WhatsNewCatalog.Since(preferences.LastVersion, parts.Version, parts.Notes) : [];
        if (preferences.OnboardingDone && whatsNew.Count == 0)
        {
            // Nothing to say about this version, so it is the one to compare the next against.
            parts.Preferences.Update(p => p with { LastVersion = parts.Version });
        }

        parts.Controller.StateChanged += OnStateChanged;
        Sentry.PropertyChanged += OnSentryChanged;
        Apply();
        Live.Shown(true);
    }

    /// <summary>Choosing and joining a camera.</summary>
    public ConnectViewModel Connect { get; }

    /// <summary>The live picture and the shutter.</summary>
    public LiveViewModel Live { get; }

    /// <summary>The camera's card.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>The security camera.</summary>
    public SentryViewModel Sentry { get; }

    /// <summary>The camera's settings and the app's.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Newer versions of Sightline.</summary>
    public UpdatesViewModel Updates { get; }

    /// <summary>This build's version.</summary>
    public string Version => parts.Version;

    /// <summary>The page showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsLibrary), nameof(IsSentry), nameof(IsSettings))]
    private Page page = Page.Live;

    /// <summary>The camera as it is now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionWord), nameof(ShowsConnect), nameof(TrayText), nameof(KeepsRunningWhenClosed))]
    private CameraState camera = CameraState.Initial;

    /// <summary>What the camera last had to say, until dismissed or replaced.</summary>
    [ObservableProperty]
    private string? notice;

    /// <summary>Whether the first-run introduction shows.</summary>
    [ObservableProperty]
    private bool showOnboarding;

    /// <summary>What changed since the version that last ran, when an update earned a word.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWhatsNew))]
    private IReadOnlyList<WhatsNewEntry> whatsNew;

    /// <summary>Whether the Live page shows.</summary>
    public bool IsLive => Page == Page.Live;

    /// <summary>Whether the Library page shows.</summary>
    public bool IsLibrary => Page == Page.Library;

    /// <summary>Whether the Sentry page shows.</summary>
    public bool IsSentry => Page == Page.Sentry;

    /// <summary>Whether the Settings page shows.</summary>
    public bool IsSettings => Page == Page.Settings;

    /// <summary>One word on the connection for the header, or null when there is nothing to say.</summary>
    public string? ConnectionWord => CameraWords.Connection(Camera.Connection);

    /// <summary>Whether the connect panel shows in place of the live picture.</summary>
    public bool ShowsConnect => Camera.Connection is not (Connection.Connected or Connection.Reconnecting);

    /// <summary>Whether what's new shows.</summary>
    public bool ShowsWhatsNew => WhatsNew.Count > 0;

    /// <summary>
    /// Whether closing the window leaves Sightline running in the tray: when the person allows it, and a
    /// camera is connected or being connected to, or Sentry is armed. With nothing to keep, it simply closes.
    /// </summary>
    public bool KeepsRunningWhenClosed =>
        parts.Preferences.Current.CloseToTray
        && (Camera.Connection is not (Connection.Idle or Connection.Failed) || Sentry.Armed);

    /// <summary>The tray's tooltip: the last alarm, Sentry armed, or the connection, most pressing first.</summary>
    public string TrayText =>
        Sentry.LastAlarmAt is { } at ? $"Sightline: movement at {at}"
        : Sentry.Armed ? "Sightline: Sentry is armed"
        : ConnectionWord is { } word ? $"Sightline: {word.ToLowerInvariant()}"
        : "Sightline";

    /// <summary>What the tray's Sentry item does now.</summary>
    public string SentryAction => Sentry.Armed ? "Disarm Sentry" : "Arm Sentry";

    /// <summary>Joins the last camera used, when the person allows it. Called once the window is up.</summary>
    public void Start()
    {
        var preferences = parts.Preferences.Current;
        if (preferences.CheckForUpdates && Updates.CheckCommand.CanExecute(null))
        {
            _ = Updates.CheckCommand.ExecuteAsync(null);
        }

        if (!preferences.AutoConnect || preferences.LastAdapter is not { } adapter || preferences.LastCamera is not { } ssid)
        {
            return;
        }

        // Never consent on the person's behalf: an adapter that has since joined another network is left alone.
        parts.Choice.Current = new CameraTarget(adapter, ssid, preferences.CameraPassword, Consented: false);
        _ = parts.Controller.Connect();
    }

    /// <summary>Shows the introduction again, from Settings.</summary>
    public void ShowIntroduction() => ShowOnboarding = true;

    /// <inheritdoc />
    public void Dispose()
    {
        parts.Controller.StateChanged -= OnStateChanged;
        Sentry.PropertyChanged -= OnSentryChanged;
        Live.Dispose();
        Library.Dispose();
        Sentry.Dispose();
        Settings.Dispose();
    }

    partial void OnPageChanged(Page value)
    {
        Live.Shown(value == Page.Live);
        if (value == Page.Library)
        {
            Library.Opened();
        }
    }

    [RelayCommand]
    private void Go(Page to) => Page = to;

    /// <summary>Arms Sentry, or stands it down, from the tray.</summary>
    [RelayCommand]
    private void ToggleSentry()
    {
        if (Sentry.Armed)
        {
            Sentry.DisarmCommand.Execute(null);
        }
        else
        {
            Sentry.ArmCommand.Execute(null);
        }
    }

    private void OnSentryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SentryViewModel.Armed) or nameof(SentryViewModel.LastAlarmAt))
        {
            OnPropertyChanged(nameof(TrayText));
            OnPropertyChanged(nameof(SentryAction));
            OnPropertyChanged(nameof(KeepsRunningWhenClosed));
            HoldUpdates();
        }
    }

    [RelayCommand]
    private void DismissNotice() => parts.Controller.DismissNotice(noticeId);

    [RelayCommand]
    private void FinishOnboarding()
    {
        parts.Preferences.Update(p => p with { OnboardingDone = true, LastVersion = parts.Version });
        ShowOnboarding = false;
    }

    [RelayCommand]
    private void CloseWhatsNew()
    {
        parts.Preferences.Update(p => p with { LastVersion = parts.Version });
        WhatsNew = [];
    }

    private void OnStateChanged(CameraState state)
    {
        if (Interlocked.Exchange(ref applyPending, 1) == 0)
        {
            parts.Post(Apply);
        }
    }

    private void Apply()
    {
        Volatile.Write(ref applyPending, 0);
        var state = parts.Controller.State;
        Camera = state;
        noticeId = state.Notice?.Id ?? 0;
        Notice = state.Notice?.Text;
        Connect.Apply(state);
        Live.Apply(state);
        Library.Apply(state);
        Sentry.Apply(state);
        Settings.Apply(state);
        HoldUpdates();
    }

    /// <summary>An update's restart waits while the camera records or Sentry watches.</summary>
    private void HoldUpdates() => Updates.Hold(Camera.IsRecording || Sentry.Armed);
}
