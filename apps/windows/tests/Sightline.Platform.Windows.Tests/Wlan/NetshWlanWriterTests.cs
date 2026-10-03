using Shouldly;
using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Processes;
using Sightline.Platform.Windows.Wlan;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Wlan;

/// <summary>The netsh commands Sightline runs, against a runner that runs nothing.</summary>
public sealed class NetshWlanWriterTests
{
    private readonly RecordingRunner runner = new();

    [Fact]
    public void A_profile_is_added_from_a_file_that_is_deleted_straight_afterwards()
    {
        // The XML carries the Wi-Fi password, so the file it passes through must not outlive the call.
        new NetshWlanWriter(runner).AddProfile("WiFi 2", "<WLANProfile>secret</WLANProfile>");

        var (file, arguments) = runner.Calls.Single();
        file.ShouldBe("netsh");
        arguments.ShouldStartWith("wlan add profile filename=\"");
        arguments.ShouldEndWith("\" interface=\"WiFi 2\" user=current");
        runner.FileContents.Single().ShouldBe("<WLANProfile>secret</WLANProfile>");
        File.Exists(runner.ProfileFiles.Single()).ShouldBeFalse();
    }

    [Fact]
    public void The_profile_file_is_deleted_even_when_windows_refuses_it()
    {
        runner.ExitCode = 1;

        Should.Throw<WlanException>(() => new NetshWlanWriter(runner).AddProfile("WiFi 2", "<x/>"));

        File.Exists(runner.ProfileFiles.Single()).ShouldBeFalse();
    }

    [Fact]
    public void Joining_names_the_profile_the_network_and_the_adapter()
    {
        new NetshWlanWriter(runner).Connect("WiFi 2", "Sightline ActionCam_x", "ActionCam_x");

        runner.Calls.Single().Arguments.ShouldBe("wlan connect name=\"Sightline ActionCam_x\" ssid=\"ActionCam_x\" interface=\"WiFi 2\"");
    }

    [Fact]
    public void Leaving_and_deleting_name_only_the_adapter_and_the_profile()
    {
        var writer = new NetshWlanWriter(runner);

        writer.Disconnect("WiFi 2");
        writer.DeleteProfile("WiFi 2", "Sightline ActionCam_x");

        runner.Calls.Select(c => c.Arguments).ShouldBe([
            "wlan disconnect interface=\"WiFi 2\"",
            "wlan delete profile name=\"Sightline ActionCam_x\" interface=\"WiFi 2\"",
        ]);
    }

    [Fact]
    public void A_refusal_carries_what_windows_said()
    {
        runner.ExitCode = 1;
        runner.Output = "  There is no such wireless interface on the system.\r\n";

        var refused = Should.Throw<WlanException>(() => new NetshWlanWriter(runner).Disconnect("WiFi 9"));

        refused.Message.ShouldBe("Windows refused: There is no such wireless interface on the system.");
    }

    [Fact]
    public void A_name_with_a_quote_in_it_is_refused_before_anything_runs()
    {
        // netsh cannot express it, and passing it through would let a network name add arguments.
        Should.Throw<WlanException>(() => new NetshWlanWriter(runner).Connect("WiFi 2", "p", "evil\" interface=\"WiFi"));

        runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Records each command and, for a profile file, what it held while the command ran.</summary>
    internal sealed class RecordingRunner : ICommandRunner
    {
        public List<(string File, string Arguments)> Calls { get; } = [];

        public List<string> ProfileFiles { get; } = [];

        public List<string> FileContents { get; } = [];

        public int ExitCode { get; set; }

        public string Output { get; set; } = "";

        public CommandResult Run(string file, string arguments)
        {
            Calls.Add((file, arguments));
            const string marker = "filename=\"";
            var at = arguments.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                var path = arguments[(at + marker.Length)..arguments.IndexOf('"', at + marker.Length)];
                ProfileFiles.Add(path);
                FileContents.Add(File.ReadAllText(path));
            }

            return new CommandResult(ExitCode, Output);
        }
    }
}
