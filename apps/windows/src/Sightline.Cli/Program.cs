using System.Globalization;
using System.Reflection;
using Sightline.Core;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.Cli;

/// <summary>The <c>sightline</c> command.</summary>
internal static class Program
{
    private const string DefaultSsidPrefix = "ActionCam_";
    private const string DefaultPassword = "12345678";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // A redirected or unusual console can refuse; a mangled separator is not worth failing over.
        }

        var options = Options.Parse(args);
        if (options.Command is null or "help" or "--help" or "-h" || options.Has("--help"))
        {
            PrintHelp();
            return ExitCode.Success;
        }

        if (options.Command == "--version" || options.Command == "version")
        {
            Console.WriteLine($"sightline {Version()}");
            return ExitCode.Success;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            return options.Command switch
            {
                "adapters" => Adapters(),
                "connect" => Connect(options),
                "disconnect" => Disconnect(options),
                "status" => await WithCamera(Status, cancel.Token),
                "settings" => await WithCamera(Settings, cancel.Token),
                "files" => await WithCamera(Files, cancel.Token),
                "photo" => await WithCamera(Photo, cancel.Token),
                "record" => await WithCamera(Record, cancel.Token),
                "snapshot" => await WithCamera((s, t) => Snapshot(s, options, t), cancel.Token),
                _ => Unknown(options.Command),
            };
        }
        catch (CameraNotReachableException exception)
        {
            Error(exception.Message);
            return ExitCode.NoCamera;
        }
        catch (GpSockRefusedException exception)
        {
            Error(exception.Message);
            return ExitCode.Refused;
        }
        catch (TimeoutException exception)
        {
            Error(exception.Message);
            return ExitCode.TimedOut;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException
                                              or GpSockProtocolException or RtspException)
        {
            Error($"Lost the camera: {exception.Message}");
            Console.Error.WriteLine("  Its Wi-Fi switches itself off after about a minute. Press its Wi-Fi button and try again.");
            return ExitCode.NoCamera;
        }
        catch (OperationCanceledException)
        {
            Error("Stopped.");
            return ExitCode.TimedOut;
        }
    }

    private static async Task<int> WithCamera(
        Func<CameraSession, CancellationToken, Task<int>> action, CancellationToken cancellationToken)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connecting.CancelAfter(TimeSpan.FromSeconds(8));
        await using var session = await CameraSession.OpenAsync(null, connecting.Token);
        return await action(session, cancellationToken);
    }

    private static async Task<int> Status(CameraSession session, CancellationToken cancellationToken)
    {
        var status = await session.Control.GetStatusAsync(cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"  Mode       {status.Mode}");
        Console.WriteLine($"  Recording  {(status.IsBusy ? "yes" : "no")}");
        Console.WriteLine($"  Audio      {(status.RecordsAudio ? "on" : "off")}");
        Console.WriteLine($"  Power      {(status.IsCharging ? "external" : "battery")}");
        Console.WriteLine($"  Battery    {(status.BatteryPercent is { } b ? $"{b}%" : "not reported by this firmware")}");
        Console.WriteLine($"  Raw        {Convert.ToHexString(status.Raw)} ({status.Length} bytes)");
        Console.WriteLine();
        return ExitCode.Success;
    }

    private static async Task<int> Settings(CameraSession session, CancellationToken cancellationToken)
    {
        var menu = await session.Control.GetMenuAsync(cancellationToken);
        foreach (var category in menu.Categories)
        {
            Console.WriteLine();
            Console.WriteLine($"  {category.ToUpperInvariant()}");
            foreach (var setting in menu.Settings.Where(s => s.Category == category))
            {
                var detail = setting.Kind switch
                {
                    MenuSettingKind.Choice => string.Join(", ", setting.Choices.Select(c => c.Label)),
                    MenuSettingKind.Action => "(action)",
                    MenuSettingKind.Text => "(text)",
                    _ => setting.Choices.Count > 0 ? setting.Choices[0].Label : "(read-only)",
                };
                Console.WriteLine($"    {setting.Name,-18} 0x{setting.Id:X4}  {detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {menu.Settings.Count} settings.");
        return ExitCode.Success;
    }

    private static async Task<int> Files(CameraSession session, CancellationToken cancellationToken)
    {
        // Browsing is only allowed in browse mode; the camera refuses with "busy" otherwise.
        await session.Control.SetModeAsync(CameraMode.Browse, cancellationToken);
        var count = await session.Control.GetFileCountAsync(cancellationToken);
        Console.WriteLine($"  {count.ToString(CultureInfo.InvariantCulture)} file(s) on the card.");
        await session.Control.SetModeAsync(CameraMode.Record, cancellationToken);
        return ExitCode.Success;
    }

    private static async Task<int> Photo(CameraSession session, CancellationToken cancellationToken)
    {
        await session.Control.SetModeAsync(CameraMode.Capture, cancellationToken);
        await session.Control.CapturePictureAsync(cancellationToken);
        Console.WriteLine("  Photo taken. It is on the camera's card.");
        return ExitCode.Success;
    }

    private static async Task<int> Record(CameraSession session, CancellationToken cancellationToken)
    {
        await session.Control.SetModeAsync(CameraMode.Record, cancellationToken);
        await session.Control.ToggleRecordingAsync(cancellationToken);
        var status = await session.Control.GetStatusAsync(cancellationToken);
        Console.WriteLine(status.IsBusy
            ? "  Recording to the camera's card. Run `sightline record` again to stop."
            : "  Recording stopped.");
        return ExitCode.Success;
    }

    private static async Task<int> Snapshot(CameraSession session, Options options, CancellationToken cancellationToken)
    {
        var path = options.Value("--out") ?? $"sightline-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        if (options.Has("--verbose"))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            session.Trace = line => Console.Error.WriteLine($"  [{clock.Elapsed.TotalSeconds,5:0.00}s] {line}");
        }

        var frame = await session.GrabFrameAsync(TimeSpan.FromSeconds(15), cancellationToken);
        await File.WriteAllBytesAsync(path, frame.Jpeg, cancellationToken);
        Console.WriteLine($"  {frame.Width}x{frame.Height} picture saved to {Path.GetFullPath(path)} ({frame.Jpeg.Length:N0} bytes).");
        return ExitCode.Success;
    }

    private static int Adapters()
    {
        if (!OperatingSystem.IsWindows())
        {
            Error("Listing Wi-Fi adapters is only supported on Windows.");
            return ExitCode.Unavailable;
        }

        var adapters = WindowsWifi.Adapters();
        if (adapters.Count == 0)
        {
            Console.WriteLine("  No Wi-Fi adapters.");
            return ExitCode.Unavailable;
        }

        foreach (var adapter in adapters)
        {
            Console.WriteLine($"  {adapter.Name,-12} {(adapter.IsIdle ? "idle" : $"on {adapter.ConnectedTo}"),-30} {adapter.Description}");
        }

        var choice = WindowsWifi.Choose(adapters);
        if (choice is { } chosen)
        {
            Console.WriteLine();
            Console.WriteLine(chosen.InterruptsInternet
                ? $"  The camera would use {chosen.Adapter.Name}, which is this PC's Wi-Fi internet; it would pause."
                : $"  The camera would use {chosen.Adapter.Name}. Nothing else changes.");
        }

        return ExitCode.Success;
    }

    private static int Connect(Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Error("Joining Wi-Fi is only supported on Windows. Join the camera's network yourself, then use the other commands.");
            return ExitCode.Unavailable;
        }

        var adapters = WindowsWifi.Adapters();
        var named = options.Value("--adapter");
        WifiAdapter? adapter;
        if (named is not null)
        {
            adapter = adapters.FirstOrDefault(a => a.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var choice = WindowsWifi.Choose(adapters);
            if (choice is { InterruptsInternet: true } && !options.Has("--yes"))
            {
                Error($"The only Wi-Fi adapter, {choice.Value.Adapter.Name}, carries this PC's internet, which would pause.");
                Console.Error.WriteLine("  Run again with --yes to go ahead, or plug in a second Wi-Fi adapter.");
                return ExitCode.Usage;
            }

            adapter = choice?.Adapter;
        }

        if (adapter is null)
        {
            Error(named is null ? "No Wi-Fi adapter was found." : $"No Wi-Fi adapter is called '{named}'.");
            return ExitCode.Unavailable;
        }

        var ssid = options.Value("--ssid");
        if (ssid is null)
        {
            Error($"Name the camera's network with --ssid. Its Wi-Fi name starts with '{DefaultSsidPrefix}' and is on the camera's screen.");
            return ExitCode.Usage;
        }

        var password = options.Value("--password") ?? DefaultPassword;
        Console.WriteLine($"  Joining {ssid} on {adapter.Name}...");
        Console.WriteLine(WindowsWifi.Join(adapter.Name, ssid, password));

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(750);
            if (CameraAddress.LocalAddressFor(CameraAddress.Default) is { } local)
            {
                Console.WriteLine($"  On the camera's network as {local}.");
                return ExitCode.Success;
            }
        }

        Error("Joined, but no address arrived from the camera. Is its Wi-Fi still on?");
        return ExitCode.NoCamera;
    }

    private static int Disconnect(Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Error("Leaving Wi-Fi is only supported on Windows.");
            return ExitCode.Unavailable;
        }

        var ssid = options.Value("--ssid");
        var adapter = options.Value("--adapter")
            ?? WindowsWifi.Adapters().FirstOrDefault(a => a.ConnectedTo?.StartsWith(DefaultSsidPrefix, StringComparison.Ordinal) == true)?.Name;
        if (adapter is null || ssid is null && (ssid = WindowsWifi.Adapters().FirstOrDefault(a => a.Name == adapter)?.ConnectedTo) is null)
        {
            Console.WriteLine("  Not on a camera's network.");
            return ExitCode.Success;
        }

        WindowsWifi.Leave(adapter, ssid);
        Console.WriteLine($"  Left {ssid} and removed its profile from {adapter}.");
        return ExitCode.Success;
    }

    private static int Unknown(string command)
    {
        Error($"Unknown command '{command}'.");
        PrintHelp();
        return ExitCode.Usage;
    }

    private static void Error(string message) => Console.Error.WriteLine($"  {message}");

    private static string Version() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static void PrintHelp()
    {
        Console.WriteLine("""

              REX TECHNOLOGIES · SIGHTLINE

              sightline <command> [options]

              Getting on the camera's Wi-Fi (Windows)
                adapters                          Which Wi-Fi adapter the camera would use, and why
                connect --ssid NAME [--password P] [--adapter A] [--yes]
                disconnect [--ssid NAME] [--adapter A]

              Talking to the camera
                status                            Mode, recording, power
                settings                          Every setting this camera supports
                files                             How many files are on the card
                photo                             Take a photo onto the camera's card
                record                            Start or stop recording to the card
                snapshot [--out FILE]             Save one picture from the live view

                --version  --help

              Exit codes: 0 ok, 2 timed out, 3 no camera, 4 camera refused, 64 bad command line,
              69 not available here.

            """);
    }
}

/// <summary>What <c>sightline</c> returns, which scripts branch on.</summary>
internal static class ExitCode
{
    public const int Success = 0;
    public const int TimedOut = 2;
    public const int NoCamera = 3;
    public const int Refused = 4;
    public const int Usage = 64;
    public const int Unavailable = 69;
}

/// <summary>A deliberately small argument reader: a command, then <c>--name value</c> pairs and flags.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }

    public static Options Parse(string[] args)
    {
        var parsed = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && parsed.Command is not null)
            {
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                parsed.values[arg] = hasValue ? args[++i] : null;
            }
            else if (parsed.Command is null)
            {
                parsed.Command = arg.ToLowerInvariant();
            }
        }

        return parsed;
    }

    public bool Has(string name) => values.ContainsKey(name);

    public string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;
}
