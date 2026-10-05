using Shouldly;
using Sightline.App.Services;
using Sightline.Core.Camera;
using Xunit;

namespace Sightline.App.Tests.Services;

public sealed class CameraChoiceTests
{
    [Fact]
    public void It_holds_the_camera_chosen_until_another_is()
    {
        var choice = new CameraChoice();
        choice.Current.ShouldBeNull();

        var camera = new CameraTarget(Guid.NewGuid(), "ActionCam_1", "12345678", false);
        choice.Current = camera;

        choice.Current.ShouldBe(camera);
    }
}
