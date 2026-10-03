using System.Net.Sockets;
using Shouldly;
using Sightline.App.Services;
using Sightline.App.ViewModels;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.App.Tests;

public sealed class MainViewModelTests
{
    private static readonly CameraConnectionOption Option = new(
        "ActionCam_test", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Wi-Fi 2", 91,
        "Your other connection stays online.", false, true);

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        Should.Throw<ArgumentNullException>(() => new MainViewModel(null!, _ => null, action => action()));
        var camera = new FakeCameraService();
        Should.Throw<ArgumentNullException>(() => new MainViewModel(camera, null!, action => action()));
        Should.Throw<ArgumentNullException>(() => new MainViewModel(camera, _ => null, null!));
    }

    [Fact]
    public async Task Refresh_ranks_the_result_and_explains_the_selected_adapter()
    {
        var other = Option with { AdapterId = Guid.NewGuid(), AdapterName = "Built-in", RequiresConsent = true };
        var camera = new FakeCameraService { Found = [Option, other] };
        using var viewModel = ViewModel(camera);

        await viewModel.RefreshCamerasAsync();
        viewModel.Cameras.ShouldBe([Option, other]);
        viewModel.SelectedCamera.ShouldBe(Option);
        viewModel.NetworkAdvice.ShouldBe(Option.Explanation);
        viewModel.NetworkChangeConfirmed = true;
        viewModel.SelectedCamera = other;
        viewModel.NetworkChangeConfirmed.ShouldBeFalse();
        viewModel.NeedsNetworkChangeConsent.ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_starts_preview_before_loading_camera_details_and_uses_recording_not_busy()
    {
        var camera = new FakeCameraService
        {
            // Browse mode can be busy without recording.
            Status = new DeviceStatus([2, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        };
        using var viewModel = ViewModel(camera);
        viewModel.SelectedCamera = Option;

        await viewModel.ConnectAsync();
        await camera.LiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.State.ShouldBe(ConnectionState.Live);
        viewModel.IsRecording.ShouldBeFalse();
        viewModel.Settings.Count.ShouldBe(1);
        viewModel.CameraSummary.ShouldContain("firmware test firmware");
        viewModel.IsViewingCamera.ShouldBeTrue();
    }

    [Fact]
    public async Task Camera_actions_update_state_and_disconnect_clears_everything()
    {
        var camera = new FakeCameraService();
        using var viewModel = await Connected(camera);

        await viewModel.PhotoAsync();
        camera.Photos.ShouldBe(1);
        await viewModel.RecordAsync();
        viewModel.IsRecording.ShouldBeTrue();

        var setting = viewModel.Settings.Single();
        setting.Selected = setting.Choices[1];
        await WaitUntil(() => camera.WrittenSetting is not null);
        camera.WrittenSetting.ShouldBe((0x0200, 1));

        await viewModel.DisconnectAsync();
        viewModel.State.ShouldBe(ConnectionState.Disconnected);
        viewModel.Settings.ShouldBeEmpty();
        viewModel.Files.ShouldBeEmpty();
        camera.Disconnects.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Card_library_downloads_and_requires_two_presses_to_delete()
    {
        var file = new CameraFile('J', 7, new DateTime(2026, 10, 3, 12, 30, 0), 2048);
        var camera = new FakeCameraService { CardFiles = [file] };
        using var viewModel = await Connected(camera);

        await viewModel.BrowseFilesAsync();
        viewModel.IsBrowsingFiles.ShouldBeTrue();
        viewModel.Files.Single().Size.ShouldBe("2.0 MB");

        await viewModel.DownloadFileAsync();
        camera.Downloaded.ShouldBe(file);
        viewModel.Message.ShouldContain("Downloads\\Sightline");

        await viewModel.DeleteFileAsync();
        camera.Deleted.ShouldBeNull();
        viewModel.DeleteArmed.ShouldBeTrue();
        await viewModel.DeleteFileAsync();
        camera.Deleted.ShouldBe(file);
        viewModel.Files.ShouldBeEmpty();

        viewModel.ReturnToLive();
        viewModel.IsViewingCamera.ShouldBeTrue();
    }

    [Fact]
    public async Task Failures_become_actionable_messages_instead_of_escaping_commands()
    {
        var camera = new FakeCameraService { Failure = new TimeoutException("raw") };
        using var viewModel = ViewModel(camera);

        await viewModel.RefreshCamerasAsync();
        viewModel.Message.ShouldContain("did not answer in time");

        viewModel.SelectedCamera = Option;
        await viewModel.ConnectAsync();
        viewModel.State.ShouldBe(ConnectionState.Failed);
        camera.Disconnects.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("timeout", "did not answer in time")]
    [InlineData("cancel", "did not answer in time")]
    [InlineData("socket", "Could not reach")]
    [InlineData("io", "plain")]
    public void Explain_turns_known_failures_into_plain_language(string kind, string expected)
    {
        Exception exception = kind switch
        {
            "timeout" => new TimeoutException("plain"),
            "cancel" => new OperationCanceledException("plain"),
            "socket" => new SocketException(),
            _ => new IOException("plain"),
        };
        MainViewModel.Explain(exception).ShouldContain(expected);
    }

    private static MainViewModel ViewModel(FakeCameraService camera) =>
        new(camera, _ => null, action => action(), () => "downloads");

    private static async Task<MainViewModel> Connected(FakeCameraService camera)
    {
        var viewModel = ViewModel(camera);
        viewModel.SelectedCamera = Option;
        await viewModel.ConnectAsync();
        await camera.LiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return viewModel;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
