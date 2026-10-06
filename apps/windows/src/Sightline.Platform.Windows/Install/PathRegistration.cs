using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sightline.Platform.Windows.Install;

/// <summary>The user's <c>PATH</c>, read and written as stored.</summary>
/// <remarks>
/// An interface because the registry is the one part of <see cref="PathRegistration"/> that cannot be
/// exercised in a test without writing to the machine running it.
/// </remarks>
public interface IUserPathStore
{
    /// <summary>The raw value, or an empty string when the user has no PATH of their own.</summary>
    string Read();

    /// <summary>Replaces the value. An empty string removes the entry entirely.</summary>
    void Write(string value);
}

/// <summary>The real user PATH, under <c>HKEY_CURRENT_USER\Environment</c>.</summary>
/// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>; a test key in tests.</param>
public sealed class RegistryUserPathStore(string keyPath = "Environment") : IUserPathStore
{
    private const string ValueName = "Path";

    /// <inheritdoc />
    public string Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        // Never expanded. A user PATH commonly holds %USERPROFILE%, and writing back an expanded copy
        // would freeze it to this account's current home folder.
        return key?.GetValue(ValueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
    }

    /// <inheritdoc />
    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (value.Length == 0)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        // The kind is kept rather than chosen: rewriting an expandable PATH as a plain string turns every
        // %VARIABLE% in it into a folder that does not exist.
        var expandable = value.Contains('%', StringComparison.Ordinal)
            || (key.GetValue(ValueName) is not null && key.GetValueKind(ValueName) is RegistryValueKind.ExpandString);
        key.SetValue(ValueName, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }
}

/// <summary>Puts the installed folder on the user's <c>PATH</c>, so <c>sightline</c> works in any terminal, and takes it off again.</summary>
/// <remarks>
/// <para>
/// The user's PATH, never the machine's: the install is per person and needs no administrator, and a
/// per-person program has no business in a setting every account shares.
/// </para>
/// <para>
/// Appended, never put first: Sightline's command is no more important than what the person already
/// has, and jumping the queue can hide a tool of the same name they chose on purpose. On removal only the
/// exact folder Sightline added is taken away; the rest of the PATH is left exactly as it was found.
/// </para>
/// </remarks>
public static partial class PathRegistration
{
    /// <summary>Adds <paramref name="directory"/> unless it is already listed.</summary>
    /// <returns>Whether the stored PATH changed.</returns>
    public static bool Add(IUserPathStore store, string directory)
    {
        ArgumentNullException.ThrowIfNull(store);
        var entry = Normalize(directory);
        var current = store.Read();
        if (entry.Length == 0 || Contains(current, entry))
        {
            return false;
        }

        store.Write(current.Length == 0 ? entry : $"{current.TrimEnd(';')};{entry}");
        return true;
    }

    /// <summary>Removes every listing of <paramref name="directory"/>, and nothing else.</summary>
    /// <returns>Whether the stored PATH changed.</returns>
    public static bool Remove(IUserPathStore store, string directory)
    {
        ArgumentNullException.ThrowIfNull(store);
        var entry = Normalize(directory);
        var parts = store.Read().Split(';');
        var kept = parts.Where(part => !SameDirectory(part, entry)).ToArray();
        if (entry.Length == 0 || kept.Length == parts.Length)
        {
            return false;
        }

        // Empty entries are kept as found: to some programs an empty entry means "the current folder".
        store.Write(string.Join(';', kept));
        return true;
    }

    /// <summary>Whether <paramref name="path"/> already lists <paramref name="directory"/>.</summary>
    public static bool Contains(string? path, string? directory)
    {
        var entry = Normalize(directory);
        return entry.Length != 0 && (path ?? string.Empty).Split(';').Any(part => SameDirectory(part, entry));
    }

    /// <summary>Tells the desktop the environment changed, so a terminal opened next sees the new PATH.</summary>
    /// <remarks>
    /// Best effort: the PATH is already stored, and a desktop that does not answer in time is a reason to
    /// open a new terminal, not a reason to fail an install.
    /// </remarks>
    public static void AnnounceChange()
    {
        const int Broadcast = 0xffff;
        const int SettingChange = 0x001a;
        const int AbortIfHung = 0x0002;
        _ = SendMessageTimeout(new IntPtr(Broadcast), SettingChange, IntPtr.Zero, "Environment", AbortIfHung, 1000, out _);
    }

    private static bool SameDirectory(string part, string entry) =>
        string.Equals(Normalize(part), entry, StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims the spacing, quoting and trailing separators a hand-edited PATH collects.</summary>
    private static string Normalize(string? value) => (value ?? string.Empty).Trim().Trim('"').TrimEnd('\\', '/');

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(
        IntPtr window, int message, IntPtr wordParameter, string longParameter, int flags, int timeoutMilliseconds, out IntPtr result);
}
