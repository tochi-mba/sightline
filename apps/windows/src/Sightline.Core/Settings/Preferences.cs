using System.Text.Json;
using System.Text.Json.Serialization;
using Sightline.Core.Sentry;

namespace Sightline.Core.Settings;

/// <summary>How the live picture is laid over the window.</summary>
public enum PictureFit
{
    /// <summary>The whole picture, with bars where the window's shape differs.</summary>
    Fit,

    /// <summary>The window covered, the picture's edges cropped.</summary>
    Fill,
}

/// <summary>Guides drawn over the live picture.</summary>
public enum GridOverlay
{
    /// <summary>None.</summary>
    None,

    /// <summary>The rule of thirds: two lines each way.</summary>
    Thirds,

    /// <summary>A small cross at the centre.</summary>
    Centre,
}

/// <summary>
/// The Windows app's own settings, with the Android app's defaults and ranges wherever both have the
/// setting, so the two behave alike out of the box.
/// </summary>
public sealed record Preferences
{
    /// <summary>The camera's own default Wi-Fi password, shown on its screen under WPA2.</summary>
    public const string DefaultPassword = "12345678";

    /// <summary>Every setting at its default.</summary>
    public static Preferences Default { get; } = new();

    /// <summary>Joins the last camera used when the app opens.</summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Tries to get a lost camera back before saying it is gone.</summary>
    public bool Reconnect { get; init; } = true;

    /// <summary>The camera's Wi-Fi password; see <see cref="IsValidPassword"/>.</summary>
    public string CameraPassword { get; init; } = DefaultPassword;

    /// <summary>The Wi-Fi adapter that last reached the camera, which its DHCP server remembers.</summary>
    public Guid? LastAdapter { get; init; }

    /// <summary>The Wi-Fi name of the camera last connected.</summary>
    public string? LastCamera { get; init; }

    /// <summary>Fit or fill.</summary>
    public PictureFit Fit { get; init; } = PictureFit.Fit;

    /// <summary>The framing guide.</summary>
    public GridOverlay Grid { get; init; } = GridOverlay.None;

    /// <summary>Turns the picture round, for a camera mounted upside down.</summary>
    public bool Flip { get; init; }

    /// <summary>Shows the picture mirrored.</summary>
    public bool Mirror { get; init; }

    /// <summary>Shows how many pictures a second are arriving.</summary>
    public bool ShowFrameRate { get; init; }

    /// <summary>Where copies from the card go; Downloads\Sightline when null.</summary>
    public string? DownloadFolder { get; init; }

    /// <summary>Frees the camera's card once a file is safely on this PC.</summary>
    public bool DeleteAfterCopy { get; init; }

    /// <summary>How much has to move before Sentry raises the alarm.</summary>
    public Sensitivity SentrySensitivity { get; init; } = Sensitivity.Medium;

    /// <summary>Seconds to leave before Sentry starts watching: 0 to 120, in fives.</summary>
    public int SentryArmDelaySeconds { get; init; } = 10;

    /// <summary>Seconds after an alarm before Sentry raises another: 10 to 600, in tens.</summary>
    public int SentryCooldownSeconds { get; init; } = 30;

    /// <summary>Records to the camera's card while the alarm lasts.</summary>
    public bool SentryRecords { get; init; } = true;

    /// <summary>Saves the picture that raised the alarm.</summary>
    public bool SentrySnapshots { get; init; } = true;

    /// <summary>Asks GitHub for a newer version when the app opens. Nothing else is sent.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Closing the window leaves Sightline in the tray while a camera is connected or Sentry armed.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Whether the first-run introduction has been finished.</summary>
    public bool OnboardingDone { get; init; }

    /// <summary>The version that last ran, so an update can say what changed.</summary>
    public string? LastVersion { get; init; }

    /// <summary>Sentry's options, as these preferences set them.</summary>
    [JsonIgnore]
    public SentryOptions Sentry => new(
        SentrySettings.Default with
        {
            Sensitivity = SentrySensitivity,
            ArmDelay = TimeSpan.FromSeconds(SentryArmDelaySeconds),
            Cooldown = TimeSpan.FromSeconds(SentryCooldownSeconds),
        },
        SentrySnapshots,
        SentryRecords);

    /// <summary>Whether <paramref name="password"/> is one a camera's Wi-Fi can have: 8 to 63 plain characters.</summary>
    public static bool IsValidPassword(string? password) =>
        password is { Length: >= 8 and <= 63 } && password.All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// These preferences, with anything out of range put back to its default: a file edited by hand, or
    /// written by a later version, never makes the app misbehave.
    /// </summary>
    public Preferences Validated() => this with
    {
        CameraPassword = IsValidPassword(CameraPassword) ? CameraPassword : DefaultPassword,
        LastCamera = string.IsNullOrWhiteSpace(LastCamera) ? null : LastCamera,
        Fit = Enum.IsDefined(Fit) ? Fit : Default.Fit,
        Grid = Enum.IsDefined(Grid) ? Grid : Default.Grid,
        DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder,
        SentrySensitivity = Enum.IsDefined(SentrySensitivity) ? SentrySensitivity : Default.SentrySensitivity,
        SentryArmDelaySeconds = Stepped(SentryArmDelaySeconds, 0, 120, 5) ? SentryArmDelaySeconds : Default.SentryArmDelaySeconds,
        SentryCooldownSeconds = Stepped(SentryCooldownSeconds, 10, 600, 10) ? SentryCooldownSeconds : Default.SentryCooldownSeconds,
        LastVersion = string.IsNullOrWhiteSpace(LastVersion) ? null : LastVersion,
    };

    private static bool Stepped(int value, int least, int most, int step) =>
        value >= least && value <= most && (value - least) % step == 0;
}

/// <summary>
/// Keeps <see cref="Preferences"/> in one JSON file, in the person's local app data.
/// </summary>
/// <remarks>
/// Read once, written whole on every change. A file that is missing, unreadable or not valid JSON gives
/// the defaults rather than an error: settings are never what stops the app starting. Writing goes
/// through a temporary file and a replace, so a crash mid-write leaves the old file, not half a new one.
/// </remarks>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;
    private readonly Lock gate = new();
    private Preferences current;

    /// <summary>Opens the preferences kept at <paramref name="path"/>.</summary>
    public PreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
        current = Read(path);
    }

    /// <summary>Where Sightline keeps them for this person: preferences.json in <see cref="DataFolder"/>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(DataFolder.Path, "preferences.json");

    /// <summary>Where they are kept.</summary>
    public string Path => path;

    /// <summary>The preferences now.</summary>
    public Preferences Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Raised after every change, with the new preferences.</summary>
    public event Action<Preferences>? Changed;

    /// <summary>Changes the preferences and saves them.</summary>
    /// <exception cref="IOException">The file could not be written; the change still holds for this run.</exception>
    public void Update(Func<Preferences, Preferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Preferences next;
        lock (gate)
        {
            next = change(current).Validated();
            if (next == current)
            {
                return;
            }

            current = next;
        }

        Changed?.Invoke(next);
        Write(next);
    }

    private static Preferences Read(string path)
    {
        try
        {
            return (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path), Json) ?? Preferences.Default).Validated();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return Preferences.Default;
        }
    }

    private void Write(Preferences preferences)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
