using System.Globalization;
using System.Reflection;
using Sightline.Core;
using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Network;
using Sightline.Platform.Windows.Wlan;
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
                "connect" => await Connect(options, cancel.Token),
                "disconnect" => await Disconnect(options),
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
        catch (LivePictureUnavailableException exception)
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
        // Send from the adapter `sightline connect` put on the camera when there is one; Windows keeps
        // a disconnected adapter's old address on record, so "any address on the subnet" can be dead.
        var held = LinkStateStore.Default.Load();
        var local = held is null
            ? null
            : CameraAddress.LocalAddressFor(CameraAddress.Default, new SystemNetworkState(CameraAddress.Default).AddressesOn(held.AdapterId));
        await using var session = local is null
            ? await CameraSession.OpenAsync(CameraAddress.Default, CameraAddress.RequireLocalAddressFor(CameraAddress.Default), connecting.Token)
            : await CameraSession.OpenAsync(CameraAddress.Default, local, connecting.Token);
        return await action(session, cancellationToken);
    }

    private static async Task<int> Status(CameraSession session, CancellationToken cancellationToken)
    {
        var status = await session.Control.GetStatusAsync(cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"  Mode       {status.Mode}");
        Console.WriteLine($"  Recording  {(status.IsRecording ? "yes" : "no")}");
        Console.WriteLine($"  Audio      {(status.RecordsAudio ? "on" : "off")}");
        Console.WriteLine($"  Power      {(status.OnExternalPower ? "external" : "battery")}");
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
        Console.WriteLine(status.IsRecording
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
        var wlan = new WindowsWlanClient();
        var network = new SystemNetworkState(CameraAddress.Default);
        var adapters = wlan.Adapters();
        if (adapters.Count == 0)
        {
            Console.WriteLine("  No Wi-Fi adapters.");
            return ExitCode.Unavailable;
        }

        var choices = AdapterAdvisor.Assess(adapters, "the camera", a => network.InternetPathOtherThan(a.Id));
        foreach (var choice in choices)
        {
            var adapter = choice.Adapter;
            Console.WriteLine($"  {adapter.Name,-12} {(adapter.IsExternal ? "plug-in" : "built-in"),-9} "
                + $"{(adapter.Connection is { } on ? $"on {on.Ssid}" : "free"),-28} {adapter.Description}");
            Console.WriteLine($"  {"",-12} {choice.Explanation}");
        }

        Console.WriteLine();
        Console.WriteLine(AdapterAdvisor.Recommend(choices, null) is { } best
            ? $"  sightline connect would use {best.Adapter.Name}."
            : "  Every adapter is on a network. Choose one with --adapter and confirm with --yes.");
        return ExitCode.Success;
    }

    private static async Task<int> Connect(Options options, CancellationToken cancellationToken)
    {
        var wlan = new WindowsWlanClient();
        var network = new SystemNetworkState(CameraAddress.Default);
        var store = LinkStateStore.Default;
        if (store.Load() is { } held)
        {
            Error($"Already connected through {held.AdapterName}. Run `sightline disconnect` first.");
            return ExitCode.Usage;
        }

        var adapters = wlan.Adapters();
        // The shared finder: it asks only adapters on no network to scan, so looking never disturbs a connection.
        var ssid = options.Value("--ssid")
            ?? (await new CameraFinder(wlan, network).FindAsync(null, cancellationToken)).FirstOrDefault()?.Ssid;
        if (ssid is null)
        {
            Error($"No camera network in range. Press the camera's Wi-Fi button, or name it with --ssid (it starts '{DefaultSsidPrefix}').");
            return ExitCode.NoCamera;
        }

        var choices = AdapterAdvisor.Assess(adapters, ssid, a => network.InternetPathOtherThan(a.Id));
        var named = options.Value("--adapter");
        var choice = named is null
            ? AdapterAdvisor.Recommend(choices, null)
            : choices.FirstOrDefault(c => c.Adapter.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            Error(named is not null
                ? $"No Wi-Fi adapter is called '{named}'. `sightline adapters` lists them."
                : adapters.Count == 0
                    ? "This PC has no Wi-Fi adapter."
                    : "Every Wi-Fi adapter is on a network. Choose one with --adapter and confirm with --yes; `sightline adapters` says what each would cost.");
            return adapters.Count == 0 ? ExitCode.Unavailable : ExitCode.Usage;
        }

        if (choice.NeedsConsent && !options.Has("--yes"))
        {
            Error(choice.Explanation);
            Console.Error.WriteLine("  Run again with --yes to go ahead.");
            return ExitCode.Usage;
        }

        var link = new CameraLink(wlan, network) { Trace = TraceFor(options) };
        Console.WriteLine($"  Joining {ssid} on {choice.Adapter.Name}...");
        try
        {
            var local = await link.JoinAsync(choice, ssid, options.Value("--password") ?? DefaultPassword,
                choice.NeedsConsent, CameraAddress.Default, cancellationToken);
            store.Save(link.State!);
            Console.WriteLine($"  On the camera's network as {local}.");
            return ExitCode.Success;
        }
        catch (CameraLinkException exception)
        {
            Error(exception.Message);
            return ExitCode.NoCamera;
        }
    }

    private static async Task<int> Disconnect(Options options)
    {
        var store = LinkStateStore.Default;
        if (store.Load() is not { } state)
        {
            Console.WriteLine("  sightline is not connected to a camera.");
            return ExitCode.Success;
        }

        var link = new CameraLink(new WindowsWlanClient(), new SystemNetworkState(CameraAddress.Default))
        {
            Trace = TraceFor(options),
        };
        link.Resume(state);
        var restored = await link.LeaveAsync();
        store.Clear();
        Console.WriteLine(restored is null
            ? $"  Left the camera on {state.AdapterName}."
            : $"  Left the camera; {state.AdapterName} is back on {restored}.");
        if (link.ProfileLeftBehind is { } profile)
        {
            // It holds the camera's password; say so, and how to remove it by hand.
            Error($"Windows would not delete the saved network '{profile}'. To remove it:");
            Console.Error.WriteLine($"    netsh wlan delete profile name=\"{profile}\" interface=\"{state.AdapterName}\"");
        }

        return ExitCode.Success;
    }

    private static int Unknown(string command)
    {
        Error($"Unknown command '{command}'.");
        PrintHelp();
        return ExitCode.Usage;
    }

    /// <summary>With --verbose, each Wi-Fi step goes to standard error as it happens.</summary>
    private static Action<string>? TraceFor(Options options) =>
        options.Has("--verbose") ? line => Console.Error.WriteLine($"  · {line}") : null;

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
                connect [--ssid NAME] [--password P] [--adapter A] [--yes] [--verbose]
                disconnect [--verbose]            Leave the camera and put the adapter back
                                                  --verbose prints each Wi-Fi step as it happens

              Talking to the camera
                status                            Mode, recording, power
                settings                          Every setting this camera supports
                files                             How many files are on the card
                photo                             Take a photo onto the camera's card
                record                            Start or stop recording to the card
                snapshot [--out FILE]             Save one picture from the live view. The camera gives its
                                                  live picture once each time it is switched on, and reading
                                                  its card ends it.

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
