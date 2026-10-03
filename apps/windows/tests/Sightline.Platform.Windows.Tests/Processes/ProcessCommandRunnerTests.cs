using Shouldly;
using Sightline.Platform.Windows.Processes;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Processes;

/// <summary>Running a real, harmless command.</summary>
public sealed class ProcessCommandRunnerTests
{
    [Fact]
    public void Output_and_exit_code_come_back()
    {
        var result = new ProcessCommandRunner().Run("cmd.exe", "/c echo sightline");

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe("sightline");
    }

    [Fact]
    public void A_failing_command_reports_its_code_and_what_it_wrote_to_standard_error()
    {
        var result = new ProcessCommandRunner().Run("cmd.exe", "/c echo broken 1>&2 & exit 3");

        result.ExitCode.ShouldBe(3);
        result.Output.Trim().ShouldBe("broken");
    }
}
