using Shouldly;
using Sightline.Platform.Windows.Install;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Install;

/// <summary>A PATH in memory, recording each write.</summary>
internal sealed class MemoryPath(string value = "") : IUserPathStore
{
    public string Value { get; private set; } = value;

    public int Writes { get; private set; }

    public string Read() => Value;

    public void Write(string value)
    {
        Value = value;
        Writes++;
    }
}

/// <summary>Putting <c>sightline</c> on the person's PATH, and taking it off again.</summary>
public sealed class PathRegistrationTests
{
    private const string Folder = @"C:\Users\someone\AppData\Local\Sightline\current";

    [Fact]
    public void The_folder_is_appended_once_and_never_put_first()
    {
        var path = new MemoryPath(@"%USERPROFILE%\bin;C:\Tools;");

        PathRegistration.Add(path, Folder).ShouldBeTrue();
        PathRegistration.Add(path, Folder + "\\").ShouldBeFalse();

        path.Value.ShouldBe($@"%USERPROFILE%\bin;C:\Tools;{Folder};");
        path.Writes.ShouldBe(1);
        var empty = new MemoryPath();
        PathRegistration.Add(empty, Folder).ShouldBeTrue();
        empty.Value.ShouldBe(Folder);
        PathRegistration.Add(empty, "  ").ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\Tools;C:\Other")]
    [InlineData(@"C:\Tools;C:\Other;")]
    [InlineData(@"C:\Tools;;C:\Other;")]
    [InlineData(";")]
    public void Installing_and_uninstalling_gives_back_the_path_exactly_as_it_was(string before)
    {
        // Found on 2026-10-06: an install and uninstall dropped the trailing separator of a real PATH.
        var path = new MemoryPath(before);

        PathRegistration.Add(path, Folder).ShouldBeTrue();
        PathRegistration.Remove(path, Folder).ShouldBeTrue();

        path.Value.ShouldBe(before);
    }

    [Fact]
    public void Removing_takes_away_only_that_folder_however_it_was_written()
    {
        var path = new MemoryPath($@"C:\Tools;;""{Folder}"";{Folder.ToUpperInvariant()}\;C:\Other");

        PathRegistration.Remove(path, Folder).ShouldBeTrue();

        path.Value.ShouldBe(@"C:\Tools;;C:\Other");
        PathRegistration.Remove(path, Folder).ShouldBeFalse();
        PathRegistration.Remove(path, "").ShouldBeFalse();
        PathRegistration.Remove(new MemoryPath(), Folder).ShouldBeFalse();
    }

    [Fact]
    public void Whether_a_path_lists_a_folder()
    {
        PathRegistration.Contains($@"C:\Tools;{Folder}", Folder).ShouldBeTrue();
        PathRegistration.Contains(@"C:\Tools", Folder).ShouldBeFalse();
        PathRegistration.Contains(null, Folder).ShouldBeFalse();
        PathRegistration.Contains(Folder, null).ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => PathRegistration.Add(null!, Folder));
        Should.Throw<ArgumentNullException>(() => PathRegistration.Remove(null!, Folder));
        PathRegistration.AnnounceChange();
    }
}

/// <summary>The real user PATH in the registry, exercised under a key of the tests' own.</summary>
public sealed class RegistryUserPathStoreTests : IDisposable
{
    private readonly string key = @"Software\REX Technologies\Sightline Tests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    [Fact]
    public void A_path_is_written_read_back_unexpanded_and_removed()
    {
        var store = new RegistryUserPathStore(key);
        store.Read().ShouldBe("");

        store.Write(@"C:\Tools");
        store.Read().ShouldBe(@"C:\Tools");
        store.Write(@"%USERPROFILE%\bin;C:\Tools");
        store.Read().ShouldBe(@"%USERPROFILE%\bin;C:\Tools");
        // Still expandable after a write with no variables in it, because it was before.
        store.Write(@"C:\Tools");
        using (var stored = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)!)
        {
            stored.GetValueKind("Path").ShouldBe(Microsoft.Win32.RegistryValueKind.ExpandString);
        }

        store.Write("");
        store.Read().ShouldBe("");
        Should.Throw<ArgumentNullException>(() => store.Write(null!));
        new RegistryUserPathStore().ShouldNotBeNull();
    }
}

/// <summary>What installing, updating and removing Sightline do.</summary>
public sealed class InstallLifecycleTests
{
    private const string Folder = @"C:\Users\someone\AppData\Local\Sightline\current";

    [Fact]
    public void Installing_quits_other_copies_and_puts_sightline_on_the_path_once()
    {
        var path = new MemoryPath(@"C:\Tools");
        var quits = 0;
        var announced = 0;
        var lifecycle = new InstallLifecycle(path, Folder, () => quits++, () => announced++);

        lifecycle.AfterInstall();
        lifecycle.AfterInstall();
        lifecycle.AfterUpdate();

        (quits, announced).ShouldBe((3, 1));
        path.Value.ShouldBe($@"C:\Tools;{Folder}");
    }

    [Fact]
    public void Removing_quits_other_copies_and_takes_only_its_own_entry_away()
    {
        var path = new MemoryPath($@"C:\Tools;{Folder}");
        var quits = 0;
        var announced = 0;
        var lifecycle = new InstallLifecycle(path, Folder, () => quits++, () => announced++);

        lifecycle.BeforeUninstall();
        lifecycle.BeforeUninstall();

        (quits, announced).ShouldBe((2, 1));
        path.Value.ShouldBe(@"C:\Tools");
    }

    [Fact]
    public void It_needs_all_its_parts()
    {
        Should.Throw<ArgumentNullException>(() => new InstallLifecycle(null!, Folder, () => { }, () => { }));
        Should.Throw<ArgumentException>(() => new InstallLifecycle(new MemoryPath(), " ", () => { }, () => { }));
        Should.Throw<ArgumentNullException>(() => new InstallLifecycle(new MemoryPath(), Folder, null!, () => { }));
        Should.Throw<ArgumentNullException>(() => new InstallLifecycle(new MemoryPath(), Folder, () => { }, null!));
        InstallLifecycle.ForThisInstall().ShouldNotBeNull();
    }
}

/// <summary>One copy of Sightline at a time, and the installer asking the running one to quit.</summary>
public sealed class SingleInstanceTests
{
    private readonly string name = "SightlineTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_second_start_wakes_the_first_and_does_not_carry_on()
    {
        using var first = new SingleInstance(name);
        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = 0;
        first.OnWake(() => Interlocked.Increment(ref stale));
        first.OnWake(() => woken.TrySetResult());
        first.IsFirst.ShouldBeTrue();

        using (var second = new SingleInstance(name))
        {
            second.IsFirst.ShouldBeFalse();
            second.WakeFirst();
        }

        await woken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref stale).ShouldBe(0);
    }

    [Fact]
    public async Task Asked_to_quit_the_running_copy_is_told_and_the_asker_waits_for_it_to_go()
    {
        var running = new SingleInstance(name);
        running.OnQuit(running.Dispose);

        var gone = await Task.Run(() => SingleInstance.AskToQuit(name, TimeSpan.FromSeconds(5)));

        gone.ShouldBeTrue();
        using var next = new SingleInstance(name);
        next.IsFirst.ShouldBeTrue();
    }

    [Fact]
    public void A_copy_that_will_not_quit_is_waited_for_only_so_long_and_with_none_running_there_is_no_wait()
    {
        SingleInstance.AskToQuit(name, TimeSpan.FromSeconds(5)).ShouldBeTrue();

        using var stubborn = new SingleInstance(name);
        SingleInstance.AskToQuit(name, TimeSpan.FromMilliseconds(250)).ShouldBeFalse();

        Should.Throw<ArgumentException>(() => new SingleInstance(" "));
        Should.Throw<ArgumentException>(() => SingleInstance.AskToQuit(" ", TimeSpan.Zero));
        Should.Throw<ArgumentNullException>(() => stubborn.OnWake(null!));
        Should.Throw<ArgumentNullException>(() => stubborn.OnQuit(null!));
        SingleInstance.SightlineName.ShouldBe("REXTechnologies.Sightline");
    }
}
