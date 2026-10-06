using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.App.Services;
using Sightline.Core.Camera;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;

namespace Sightline.App.ViewModels;

/// <summary>One alarm, for the Sentry page's list.</summary>
/// <param name="Number">Its number since Sentry was armed.</param>
/// <param name="Time">When it went up, as a clock shows it.</param>
/// <param name="Picture">The picture that raised it.</param>
public sealed record AlarmItem(int Number, string Time, Bitmap? Picture);

/// <summary>
/// The security camera: arming and disarming, where the watch is, how much is moving, and the alarms.
/// </summary>
public sealed partial class SentryViewModel : ObservableObject, IDisposable
{
    private readonly AppParts parts;
    private readonly TimeZoneInfo zone;
    private SentryStatus status = SentryStatus.Off;
    private bool cameraConnected;

    /// <summary>Creates the page; alarm times are shown in <paramref name="zone"/>, local time when omitted.</summary>
    public SentryViewModel(AppParts parts, TimeZoneInfo? zone = null)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        this.zone = zone ?? TimeZoneInfo.Local;
        parts.Sentry.StatusChanged += OnStatus;
        parts.Alarms.Raised += OnAlarm;
        parts.Preferences.Changed += OnPreferences;
        Show(parts.Sentry.Status);
    }

    /// <summary>The alarms since Sentry was armed, newest first.</summary>
    public ObservableCollection<AlarmItem> Alarms { get; } = [];

    /// <summary>Whether Sentry is armed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ArmCommand), nameof(DisarmCommand))]
    private bool armed;

    /// <summary>Where the watch is, in a word or two.</summary>
    [ObservableProperty]
    private string state = "Not armed";

    /// <summary>How much of the picture moved in the last frame looked at, 0 to 1.</summary>
    [ObservableProperty]
    private double movement;

    /// <summary>The last alarm, in a sentence, until another replaces it.</summary>
    [ObservableProperty]
    private string? lastAlarm;

    /// <summary>How Sentry will run, as the settings have it.</summary>
    public string Summary
    {
        get
        {
            var preferences = parts.Preferences.Current;
            var sensitivity = preferences.SentrySensitivity.ToString().ToLowerInvariant();
            var records = preferences.SentryRecords ? "records on the camera" : "does not record";
            return $"Sensitivity {sensitivity}, starts after {preferences.SentryArmDelaySeconds} seconds, and {records} when it sees movement. Change these in Settings.";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        parts.Sentry.StatusChanged -= OnStatus;
        parts.Alarms.Raised -= OnAlarm;
        parts.Preferences.Changed -= OnPreferences;
        ClearAlarms();
    }

    /// <summary>Follows the camera, which Sentry waits for when armed without one.</summary>
    internal void Apply(CameraState camera)
    {
        cameraConnected = camera.IsConnected;
        State = Words(status, cameraConnected);
    }

    /// <summary>The watch in a word or two, as the page's heading says it.</summary>
    internal static string Words(SentryStatus status, bool cameraConnected)
    {
        if (!status.Armed)
        {
            return "Not armed";
        }

        if (!cameraConnected)
        {
            return "Waiting for the camera";
        }

        return status.Watch switch
        {
            SentryState.Arming => "Arming",
            SentryState.Alarm => "Movement",
            SentryState.Cooldown => "Settling",
            _ => "Watching",
        };
    }

    private void OnStatus(SentryStatus next) => parts.Post(() => Show(next));

    private void Show(SentryStatus next)
    {
        var alarmsChanged = !ReferenceEquals(next.Alarms, status.Alarms);
        status = next;
        Armed = next.Armed;
        State = Words(next, cameraConnected);
        Movement = next.Score;
        if (alarmsChanged)
        {
            ClearAlarms();
            foreach (var alarm in next.Alarms)
            {
                Alarms.Add(new AlarmItem(alarm.Number, Clock(alarm.At), parts.Decode(alarm.Snapshot)));
            }
        }
    }

    private void ClearAlarms()
    {
        foreach (var alarm in Alarms)
        {
            alarm.Picture?.Dispose();
        }

        Alarms.Clear();
    }

    private void OnAlarm(AlarmNotice notice) => parts.Post(() =>
        LastAlarm = notice.SavedTo is null
            ? $"Movement at {Clock(notice.At)}."
            : $"Movement at {Clock(notice.At)}. The picture is in {parts.Alarms.Folder}.");

    private void OnPreferences(Preferences preferences) => parts.Post(() => OnPropertyChanged(nameof(Summary)));

    private string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private bool CanArm() => !Armed;

    [RelayCommand(CanExecute = nameof(CanArm))]
    private void Arm() => parts.Sentry.Arm();

    private bool CanDisarm() => Armed;

    [RelayCommand(CanExecute = nameof(CanDisarm))]
    private void Disarm() => parts.Sentry.Disarm();

    [RelayCommand]
    private void OpenPictures() => parts.Desktop.OpenFolder(parts.Alarms.Folder);
}
