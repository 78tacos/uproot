using Microsoft.Win32;
using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>
/// Whose a per-user program is, when that is not the person signed in.
///
/// Elevated with an administrator's credentials, HKCU and the Store both answer for that
/// administrator's account. Its per-user programs used to be listed as "Just me" beside the
/// signed-in person's own, read from their hive: two accounts' programs under one label. They are
/// tagged with the account's name instead, so the list says whose each one is, and an uninstall
/// looks for leftovers in that account's profile.
/// </summary>
public static class ProgramOwner
{
    private static readonly Lazy<(string Name, string? Sid)?> ProcessAccountIfOther = new(() =>
    {
        var account = AccountContext.Current;
        return account.AsAnotherAccount && account.ProcessAccount is { } name
            ? (name, ElevationValidation.CurrentUserSid())
            : null;
    });

    /// <summary>The account this process runs as, when that is not the person signed in.</summary>
    public static string? OtherAccount => ProcessAccountIfOther.Value?.Name;

    /// <summary>Tags a program read from this process's own hive, when that hive is someone else's.</summary>
    public static InstalledProgram Tag(InstalledProgram program) => Tag(program, ProcessAccountIfOther.Value);

    internal static InstalledProgram Tag(InstalledProgram program, (string Name, string? Sid)? processAccountIfOther) =>
        processAccountIfOther is { } account
        && program.Owner is null
        && program.Scope == ProgramScope.User
        && program.Key.Hive == RegistryHive.CurrentUser
            ? program with { Owner = account.Name, OwnerSid = account.Sid }
            : program;

    /// <summary>
    /// True when the program's uninstaller can run in this process: everything machine-wide, and
    /// per-user programs of the account this process runs as. Elevated as someone else, the
    /// signed-in person's own per-user programs are not: their uninstaller would run as the
    /// administrator, writing to that account's registry while deleting from theirs.
    /// </summary>
    public static bool RunsAsOwner(InstalledProgram program, bool asAnotherAccount) =>
        program.Scope == ProgramScope.Machine || !asAnotherAccount || program.Owner is not null;

    /// <summary>Why the program cannot be uninstalled from here, or null when it can.</summary>
    public static string? WhyNotHere(InstalledProgram program)
    {
        // Its own uninstaller is this program, and a scan for its leftovers would be looking at
        // the files it is running from.
        if (Installation.IsThisProgram(program))
            return "This is Uproot itself. Remove it from Apps & Features, or run uproot self-uninstall.";

        var account = AccountContext.Current;
        if (RunsAsOwner(program, account.AsAnotherAccount)) return null;

        var runningAs = account.ProcessAccount ?? "an administrator account";

        return program.IsStoreApp
            ? $"{account.DescribeOtherAccount()} {program.DisplayName} is a Store app of yours, and Store apps are removed per account. "
              + "Run Uproot without elevating to remove it."
            : $"{account.DescribeOtherAccount()} {program.DisplayName} is installed just for you, so its uninstaller has to run as you; "
              + $"here it would run as {runningAs}. Run Uproot without elevating to uninstall it.";
    }
}
