namespace Sightline.Protocol.GpSock;

/// <summary>What a GPSOCKET frame is: a command from us, or the camera's answer.</summary>
public enum GpSockType : ushort
{
    /// <summary>A request. Every frame this app sends carries this.</summary>
    Command = 1,

    /// <summary>The camera did it. The payload is the answer, if there is one.</summary>
    Ack = 2,

    /// <summary>The camera refused. The payload's first two bytes are a <see cref="NakCode"/>.</summary>
    Nak = 3,
}

/// <summary>
/// The commands the camera understands.
/// </summary>
/// <remarks>
/// The numbering is the vendor's: the high byte selects a group and the low byte a command within
/// it, and the two travel as separate bytes on the wire. Every value here was read out of the
/// camera's own firmware headers and the reachable ones were confirmed against the device.
/// </remarks>
public enum GpSockCommand : ushort
{
    /// <summary>Switch between record, capture and browse. Payload: one <see cref="CameraMode"/> byte.</summary>
    SetMode = 0x0000,

    /// <summary>Mode, battery, card and storage. See <see cref="DeviceStatus"/>.</summary>
    GetDeviceStatus = 0x0001,

    /// <summary>The menu XML: every setting this camera supports, with its allowed values.</summary>
    GetParameterFile = 0x0002,

    /// <summary>Turn the camera off.</summary>
    PowerOff = 0x0003,

    /// <summary>
    /// Start the media flow.
    /// </summary>
    /// <remarks>
    /// Not optional. RTSP SETUP and PLAY can both succeed and still deliver no packets until this
    /// has been sent, which is the most common "connects but no picture" failure in this family.
    /// </remarks>
    RestartStreaming = 0x0004,

    /// <summary>
    /// A client-genuineness check, not a login.
    /// </summary>
    /// <remarks>
    /// The firmware's dispatcher treats it like any other command and gates nothing behind it, so
    /// Sightline does not send it. Kept here so the number is not mistaken for something else.
    /// </remarks>
    AuthDevice = 0x0005,

    /// <summary>Start or stop recording to the camera's card. The camera toggles; it takes no argument.</summary>
    RecordToggle = 0x0100,

    /// <summary>Turn the camera's own microphone on or off for its recordings.</summary>
    RecordAudio = 0x0101,

    /// <summary>Take a photograph.</summary>
    CapturePicture = 0x0200,

    /// <summary>Begin playing a file from the card on the camera itself.</summary>
    PlaybackStart = 0x0300,

    /// <summary>Pause camera-side playback.</summary>
    PlaybackPause = 0x0301,

    /// <summary>How many files are on the card. Payload: one little-endian 16-bit count.</summary>
    PlaybackGetFileCount = 0x0302,

    /// <summary>A page of the file list. Payload: a first-page flag, then a 16-bit index.</summary>
    PlaybackGetFileList = 0x0303,

    /// <summary>A file's thumbnail. Payload: a 16-bit index.</summary>
    PlaybackGetThumbnail = 0x0304,

    /// <summary>A file's bytes, chunked. Payload: a 16-bit index.</summary>
    PlaybackGetRawData = 0x0305,

    /// <summary>Stop camera-side playback.</summary>
    PlaybackStop = 0x0306,

    /// <summary>A file's name. Payload: a 16-bit index.</summary>
    PlaybackGetSpecificName = 0x0307,

    /// <summary>Delete a file from the card. Payload: a 16-bit index.</summary>
    PlaybackDeleteFile = 0x0308,

    /// <summary>Read one setting. Payload: a 32-bit menu id.</summary>
    MenuGetParameter = 0x0400,

    /// <summary>Write one setting. Payload: a 32-bit menu id, then the value.</summary>
    MenuSetParameter = 0x0401,
}

/// <summary>
/// Which mode the camera is in, which decides what it will accept.
/// </summary>
/// <remarks>
/// Browsing the card is only possible in <see cref="Browse"/> with streaming stopped; asking for a
/// file list in any other mode is answered with <see cref="NakCode.ServerBusy"/>.
/// </remarks>
public enum CameraMode : byte
{
    /// <summary>Recording video, and the mode the camera starts in.</summary>
    Record = 0,

    /// <summary>Taking photographs.</summary>
    Capture = 1,

    /// <summary>Reading the card. Required before any playback command.</summary>
    Browse = 2,
}

/// <summary>Why the camera refused a command.</summary>
public enum NakCode : short
{
    /// <summary>No error.</summary>
    Ok = 0,

    /// <summary>Busy. Usually the wrong mode, or streaming still running during a browse.</summary>
    ServerBusy = -1,

    /// <summary>The camera does not know this command.</summary>
    InvalidCommand = -2,

    /// <summary>The camera gave up waiting.</summary>
    RequestTimeout = -3,

    /// <summary>The command does not apply in the current mode.</summary>
    ModeError = -4,

    /// <summary>No card.</summary>
    NoStorage = -5,

    /// <summary>The card could not be written.</summary>
    WriteFail = -6,

    /// <summary>The file list could not be read.</summary>
    GetFileListFail = -7,

    /// <summary>The thumbnail could not be read.</summary>
    GetThumbnailFail = -8,

    /// <summary>The card is full.</summary>
    FullStorage = -9,

    /// <summary>The battery is too low to do this.</summary>
    BatteryLow = -10,

    /// <summary>The camera ran out of memory.</summary>
    MemoryError = -11,

    /// <summary>A checksum did not match.</summary>
    ChecksumError = -12,

    /// <summary>Setting the clock failed.</summary>
    SyncTimeError = -13,
}
