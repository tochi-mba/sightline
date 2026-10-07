using System.Runtime.InteropServices;
using Shouldly;
using Sightline.Platform.Windows.Audio;
using Sightline.Protocol.Media;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Audio;

/// <summary>A clip's sound through waveOut, against functions that keep the buffers as Windows would and play nothing.</summary>
public sealed class WaveOutDeviceTests
{
    // 16 kHz mono 16-bit, as the reference camera records.
    private static readonly AviSound Camera = new(16_000, 1, 16);

    private readonly FakeWaveOut native = new();

    private WaveOutDevice Open() => WaveOutDevice.Open(Camera, native.Functions)!;

    [Fact]
    public void The_default_device_is_opened_for_the_clips_sound_and_plays_each_piece_in_order()
    {
        using var device = Open();

        device.Play([1, 2, 3, 4]);
        device.Play([5, 6]);

        var format = native.Format;
        (format.FormatTag, format.Channels, format.SamplesPerSecond, format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample, format.ExtraSize)
            .ShouldBe(((ushort)1, (ushort)1, 16_000u, 32_000u, (ushort)2, (ushort)16, (ushort)0));
        native.DeviceId.ShouldBe(uint.MaxValue);
        native.Queued.Select(FakeWaveOut.DataOf).ShouldBe([[1, 2, 3, 4], [5, 6]]);
        device.Holding.ShouldBe(6);
    }

    [Fact]
    public void A_piece_that_has_finished_playing_is_let_go()
    {
        using var device = Open();
        device.Play([1, 2, 3, 4]);
        device.Play([5, 6]);

        native.Finish(1);

        device.Holding.ShouldBe(2);
        native.Unprepared.Count.ShouldBe(1);
    }

    [Fact]
    public void Stopping_goes_quiet_at_once_and_lets_go_of_everything()
    {
        using var device = Open();
        device.Play([1, 2, 3, 4]);
        device.Play([5, 6]);

        device.Stop();

        native.Resets.ShouldBe(1);
        device.Holding.ShouldBe(0);
        native.Unprepared.Count.ShouldBe(2);
    }

    [Fact]
    public void A_piece_the_device_will_not_take_is_let_go_quietly()
    {
        using var device = Open();
        native.PrepareResult = 11;
        device.Play([1, 2]);
        native.PrepareResult = 0;
        native.WriteResult = 6;
        device.Play([3, 4]);

        device.Holding.ShouldBe(0);
        native.Unprepared.Count.ShouldBe(1);
        native.Queued.ShouldBeEmpty();
    }

    [Fact]
    public void A_PC_with_no_sound_device_has_none_to_open()
    {
        native.OpenResult = 2;

        WaveOutDevice.Open(Camera, native.Functions).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => WaveOutDevice.Open(null!, native.Functions));
    }

    [Fact]
    public void Closing_stops_it_once_and_it_plays_nothing_after()
    {
        var device = Open();
        device.Play([1, 2]);

        device.Dispose();
        device.Dispose();

        native.Resets.ShouldBe(1);
        native.Closed.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => device.Play([3]));
        Should.Throw<ArgumentNullException>(() => device.Play(null!));
    }

    [Fact]
    public void This_PCs_own_device_opens_or_it_has_none()
    {
        // Opening plays nothing; a build machine with no sound card says it has none.
        using var device = WaveOutDevice.Open(Camera);

        (device?.Holding ?? 0).ShouldBe(0);
    }

    /// <summary>waveOut as Windows keeps it: buffers queued until played or reset, then marked done.</summary>
    private sealed class FakeWaveOut
    {
        private const int Done = 1;
        private const int Prepared = 2;
        private static readonly int FlagsAt = (2 * IntPtr.Size) + 8;

        public FakeWaveOut()
        {
            Functions = new WaveOutFunctions(Open, Prepare, Unprepare, Write, Reset, Close);
        }

        public WaveOutFunctions Functions { get; }

        public uint OpenResult { get; set; }

        public uint PrepareResult { get; set; }

        public uint WriteResult { get; set; }

        public WaveFormat Format { get; private set; }

        public uint DeviceId { get; private set; }

        public List<IntPtr> Queued { get; } = [];

        public List<IntPtr> Unprepared { get; } = [];

        public int Resets { get; private set; }

        public int Closed { get; private set; }

        public static byte[] DataOf(IntPtr header)
        {
            var data = new byte[Marshal.ReadInt32(header, IntPtr.Size)];
            Marshal.Copy(Marshal.ReadIntPtr(header), data, 0, data.Length);
            return data;
        }

        /// <summary>The first <paramref name="count"/> buffers queued finish playing.</summary>
        public void Finish(int count)
        {
            foreach (var header in Queued.Take(count).ToList())
            {
                MarkDone(header);
            }
        }

        private static void MarkDone(IntPtr header) => Marshal.WriteInt32(header, FlagsAt, Marshal.ReadInt32(header, FlagsAt) | Done);

        private uint Open(out IntPtr device, uint deviceId, in WaveFormat format, IntPtr callback, IntPtr instance, uint flags)
        {
            Format = format;
            DeviceId = deviceId;
            device = new IntPtr(0x5150);
            return OpenResult;
        }

        private uint Prepare(IntPtr device, IntPtr header, uint size)
        {
            if (PrepareResult == 0)
            {
                Marshal.WriteInt32(header, FlagsAt, Prepared);
            }

            return PrepareResult;
        }

        private uint Unprepare(IntPtr device, IntPtr header, uint size)
        {
            Unprepared.Add(header);
            Queued.Remove(header);
            return 0;
        }

        private uint Write(IntPtr device, IntPtr header, uint size)
        {
            if (WriteResult == 0)
            {
                Queued.Add(header);
            }

            return WriteResult;
        }

        private uint Reset(IntPtr device)
        {
            Resets++;
            Queued.ForEach(MarkDone);
            return 0;
        }

        private uint Close(IntPtr device)
        {
            Closed++;
            return 0;
        }
    }
}
