using System.Runtime.InteropServices;
using Sightline.Core.Playback;
using Sightline.Protocol.Media;

namespace Sightline.Platform.Windows.Audio;

/// <summary>waveOutOpen.</summary>
internal delegate uint WaveOutOpenCall(out IntPtr device, uint deviceId, in WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

/// <summary>waveOutPrepareHeader, waveOutUnprepareHeader and waveOutWrite, which all take a buffer's header.</summary>
internal delegate uint WaveOutHeaderCall(IntPtr device, IntPtr header, uint headerSize);

/// <summary>waveOutReset and waveOutClose.</summary>
internal delegate uint WaveOutCall(IntPtr device);

/// <summary>WAVEFORMATEX, as mmreg.h lays it out.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal readonly record struct WaveFormat(
    ushort FormatTag,
    ushort Channels,
    uint SamplesPerSecond,
    uint AverageBytesPerSecond,
    ushort BlockAlign,
    ushort BitsPerSample,
    ushort ExtraSize);

/// <summary>
/// The waveOut entry points the device uses.
/// </summary>
/// <remarks>
/// A seam at the very edge, as for Wi-Fi: tests hand in functions that play nothing, so every path is exercised
/// on a machine with no sound card, as the build machines are. <see cref="Native"/> is the operating system's own.
/// </remarks>
internal sealed record WaveOutFunctions(
    WaveOutOpenCall Open,
    WaveOutHeaderCall Prepare,
    WaveOutHeaderCall Unprepare,
    WaveOutHeaderCall Write,
    WaveOutCall Reset,
    WaveOutCall Close)
{
    /// <summary>The operating system's functions.</summary>
    public static WaveOutFunctions Native { get; } = new(
        NativeWaveOut.Open,
        NativeWaveOut.Prepare,
        NativeWaveOut.Unprepare,
        NativeWaveOut.Write,
        NativeWaveOut.Reset,
        NativeWaveOut.Close);
}

/// <summary>
/// A clip's sound on this PC's default sound device, through waveOut.
/// </summary>
/// <remarks>
/// <para>
/// waveOut is the oldest of Windows' sound interfaces and still the plainest: hand it a buffer and it plays it after
/// the last one. A buffer must stay where it is in memory while it plays, so each lives in unmanaged memory with its
/// header in front, and is let go once Windows marks it done. Nothing is called back; finished buffers are let go
/// whenever the device is asked how much it holds, which its feed does on every step.
/// </para>
/// <para>
/// A device that fails part-way, as one unplugged during a clip does, goes quiet rather than stopping the picture: a
/// buffer it will not take is let go and forgotten. One thread uses it, the one that steps the player.
/// </para>
/// </remarks>
public sealed class WaveOutDevice : ISoundDevice
{
    private const uint Success = 0;
    private const uint WaveMapper = uint.MaxValue;
    private const uint NoCallback = 0;
    private const ushort Pcm = 1;
    private const int Done = 1;

    // WAVEHDR: the data's address, its length, bytes recorded, a user value, flags, loops, the next header, a reserved value.
    private static readonly int HeaderSize = (4 * IntPtr.Size) + 16;
    private static readonly int LengthAt = IntPtr.Size;
    private static readonly int FlagsAt = (2 * IntPtr.Size) + 8;
    private static readonly byte[] BlankHeader = new byte[HeaderSize];

    private readonly WaveOutFunctions functions;
    private readonly IntPtr device;
    private readonly List<(IntPtr Header, int Length)> held = [];
    private bool disposed;

    private WaveOutDevice(WaveOutFunctions functions, IntPtr device)
    {
        this.functions = functions;
        this.device = device;
    }

    /// <inheritdoc />
    public long Holding
    {
        get
        {
            LetGoOfFinished();
            return held.Sum(buffer => (long)buffer.Length);
        }
    }

    /// <summary>This PC's default sound device, open for <paramref name="sound"/>; null when there is none, or it cannot play that.</summary>
    public static ISoundDevice? Open(AviSound sound) => Open(sound, WaveOutFunctions.Native);

    /// <inheritdoc />
    public void Play(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ObjectDisposedException.ThrowIf(disposed, this);
        LetGoOfFinished();
        var header = Marshal.AllocHGlobal(HeaderSize + pcm.Length);
        Marshal.Copy(BlankHeader, 0, header, HeaderSize);
        Marshal.WriteIntPtr(header, header + HeaderSize);
        Marshal.WriteInt32(header, LengthAt, pcm.Length);
        Marshal.Copy(pcm, 0, header + HeaderSize, pcm.Length);
        if (functions.Prepare(device, header, (uint)HeaderSize) != Success)
        {
            Marshal.FreeHGlobal(header);
            return;
        }

        if (functions.Write(device, header, (uint)HeaderSize) != Success)
        {
            functions.Unprepare(device, header, (uint)HeaderSize);
            Marshal.FreeHGlobal(header);
            return;
        }

        held.Add((header, pcm.Length));
    }

    /// <inheritdoc />
    public void Stop()
    {
        // Every buffer comes back marked done, played or not.
        functions.Reset(device);
        LetGoOfFinished();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        functions.Close(device);
    }

    internal static WaveOutDevice? Open(AviSound sound, WaveOutFunctions functions)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var blockAlign = (ushort)(sound.Channels * (sound.BitsPerSample / 8));
        var format = new WaveFormat(
            Pcm,
            (ushort)sound.Channels,
            (uint)sound.SampleRate,
            (uint)(sound.SampleRate * blockAlign),
            blockAlign,
            (ushort)sound.BitsPerSample,
            0);
        return functions.Open(out var device, WaveMapper, in format, IntPtr.Zero, IntPtr.Zero, NoCallback) == Success
            ? new WaveOutDevice(functions, device)
            : null;
    }

    private void LetGoOfFinished() => held.RemoveAll(buffer =>
    {
        if ((Marshal.ReadInt32(buffer.Header, FlagsAt) & Done) == 0)
        {
            return false;
        }

        functions.Unprepare(device, buffer.Header, (uint)HeaderSize);
        Marshal.FreeHGlobal(buffer.Header);
        return true;
    });
}

/// <summary>The waveOut functions in winmm.dll.</summary>
internal static partial class NativeWaveOut
{
    private const string Library = "winmm.dll";

    [LibraryImport(Library, EntryPoint = "waveOutOpen")]
    internal static partial uint Open(out IntPtr device, uint deviceId, in WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [LibraryImport(Library, EntryPoint = "waveOutPrepareHeader")]
    internal static partial uint Prepare(IntPtr device, IntPtr header, uint headerSize);

    [LibraryImport(Library, EntryPoint = "waveOutUnprepareHeader")]
    internal static partial uint Unprepare(IntPtr device, IntPtr header, uint headerSize);

    [LibraryImport(Library, EntryPoint = "waveOutWrite")]
    internal static partial uint Write(IntPtr device, IntPtr header, uint headerSize);

    [LibraryImport(Library, EntryPoint = "waveOutReset")]
    internal static partial uint Reset(IntPtr device);

    [LibraryImport(Library, EntryPoint = "waveOutClose")]
    internal static partial uint Close(IntPtr device);
}
