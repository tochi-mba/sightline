namespace Sightline.Protocol.GpSock;

/// <summary>
/// The menu ids whose meaning this project knows.
/// </summary>
/// <remarks>
/// <para>
/// The camera's own menu (<see cref="GpSockCommand.GetParameterFile"/>) is the authority on which
/// settings exist, what they are called and what values they take; an app renders whatever it
/// says. These names exist for the few ids an app must treat specially — an action that erases
/// the card, text that changes how the camera is reached, the clock.
/// </para>
/// <para>
/// Two ids are not in the menu at all. <see cref="SyncTime"/> and <see cref="FirmwareVersion"/> are
/// hidden ids the firmware accepts but does not advertise.
/// </para>
/// </remarks>
public static class MenuIds
{
    /// <summary>Video resolution: 4K down to VGA on the reference camera.</summary>
    public const int RecordResolution = 0x0000;

    /// <summary>Video exposure compensation.</summary>
    public const int RecordExposure = 0x0001;

    /// <summary>Loop recording: off, or the length of each looped clip.</summary>
    public const int LoopRecording = 0x0003;

    /// <summary>Whether the camera records its own microphone.</summary>
    public const int RecordAudio = 0x0005;

    /// <summary>Whether the date and time are burned into the video.</summary>
    public const int RecordDateStamp = 0x0006;

    /// <summary>Photo resolution: 16 MP down to VGA on the reference camera.</summary>
    public const int CaptureResolution = 0x0100;

    /// <summary>Photo JPEG quality.</summary>
    public const int CaptureQuality = 0x0102;

    /// <summary>Burst photos.</summary>
    public const int CaptureSequence = 0x0103;

    /// <summary>Photo sharpening.</summary>
    public const int CaptureSharpness = 0x0104;

    /// <summary>White balance.</summary>
    public const int CaptureWhiteBalance = 0x0108;

    /// <summary>Mains frequency, against flicker under artificial light.</summary>
    public const int Frequency = 0x0200;

    /// <summary>When the camera's screen turns itself off.</summary>
    public const int ScreenSaver = 0x0201;

    /// <summary>When the camera turns itself off.</summary>
    public const int AutoPowerOff = 0x0202;

    /// <summary>The language of the camera's own screen.</summary>
    public const int Language = 0x0203;

    /// <summary>Whether the camera beeps.</summary>
    public const int BeepSound = 0x0204;

    /// <summary>How the camera writes dates: year, month or day first.</summary>
    public const int DateFormat = 0x0205;

    /// <summary>An action: erase the card. Destructive and irreversible.</summary>
    public const int FormatCard = 0x0207;

    /// <summary>An action: put every setting back to the factory value.</summary>
    public const int DefaultSettings = 0x0208;

    /// <summary>Read-only: the firmware build, as the menu shows it.</summary>
    public const int Version = 0x0209;

    /// <summary>Hidden: sets the camera's clock.</summary>
    public const int SyncTime = 0x020A;

    /// <summary>Hidden: the firmware version string.</summary>
    public const int FirmwareVersion = 0x020B;

    /// <summary>Text: the camera's Wi-Fi network name.</summary>
    public const int WifiName = 0x0300;

    /// <summary>Text: the camera's Wi-Fi password.</summary>
    public const int WifiPassword = 0x0301;
}
