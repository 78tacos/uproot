using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// Uproot elevated with an administrator's credentials reads two accounts' per-user programs:
/// the administrator's own, from HKCU, and the signed-in person's, from their hive. Both used to be
/// "Just me". The administrator's are now labelled with its name, run as it, and have their
/// leftovers looked for in its profile; the signed-in person's are refused, as before.
/// </summary>
public class OtherAccountProgramTests
{
    private static readonly (string Name, string? Sid) Admin = (@"LFND\LFND_Admin", "S-1-5-21-1-2-3-500");

    private static InstalledProgram Program(RegistryLocation key, ProgramScope scope) => new()
    {
        Key = key,
        DisplayName = "Example",
        Scope = scope
    };

    private static InstalledProgram InHkcu => Program(RegistryLocation.CurrentUser(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example"), ProgramScope.User);
    private static InstalledProgram InYourHive => Program(RegistryLocation.Users(@"S-1-5-21-1-2-3-1001\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example"), ProgramScope.User);
    private static InstalledProgram ForEveryone => Program(RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Example"), ProgramScope.Machine);

    [Fact]
    public void Elevated_as_another_account_its_own_programs_carry_its_name()
    {
        var tagged = ProgramOwner.Tag(InHkcu, Admin);

        Assert.Equal(@"LFND\LFND_Admin", tagged.Owner);
        Assert.Equal("S-1-5-21-1-2-3-500", tagged.OwnerSid);
        Assert.Equal("LFND_Admin only", tagged.InstalledFor);
    }

    [Fact]
    public void Yours_and_everyones_are_left_as_they_are()
    {
        Assert.Equal("Just me", ProgramOwner.Tag(InYourHive, Admin).InstalledFor);
        Assert.Equal("Everyone", ProgramOwner.Tag(ForEveryone, Admin).InstalledFor);
    }

    [Fact]
    public void Running_as_yourself_nothing_is_tagged()
    {
        Assert.Null(ProgramOwner.Tag(InHkcu, null).Owner);
        Assert.Equal("Just me", ProgramOwner.Tag(InHkcu, null).InstalledFor);
    }

    [Theory]
    [InlineData(@"LFND\LFND_Admin", "LFND_Admin")]
    [InlineData("LFND_Admin", "LFND_Admin")]
    [InlineData(@"PC\", @"PC\")]
    public void Labels_use_the_account_without_its_domain(string account, string expected)
    {
        Assert.Equal(expected, InstalledProgram.ShortAccount(account));
    }

    [Fact]
    public void Only_programs_of_the_account_running_can_be_uninstalled_here()
    {
        Assert.True(ProgramOwner.RunsAsOwner(ForEveryone, asAnotherAccount: true));
        Assert.True(ProgramOwner.RunsAsOwner(ProgramOwner.Tag(InHkcu, Admin), asAnotherAccount: true));

        // Yours, from the administrator's session: its uninstaller would run as the administrator.
        Assert.False(ProgramOwner.RunsAsOwner(InYourHive, asAnotherAccount: true));

        // Running as yourself, everything is yours to remove.
        Assert.True(ProgramOwner.RunsAsOwner(InHkcu, asAnotherAccount: false));
    }

    private static UserProfile Profile(string sid, string name, bool current = false) =>
        new() { Sid = sid, ProfilePath = $@"C:\Users\{name}", AccountName = name, IsCurrent = current };

    private static readonly IReadOnlyList<UserProfile> Profiles =
    [
        Profile("S-1-5-21-1-2-3-1001", "joseph.ogle", current: true),
        Profile("S-1-5-21-1-2-3-500", "LFND_Admin"),
        Profile("S-1-5-21-1-2-3-1002", "someone.else")
    ];

    [Fact]
    public void Another_accounts_program_is_searched_for_in_that_profile_alone()
    {
        var chosen = LeftoverScanContext.ChooseProfiles(Profiles, everyProfile: false, ownerSid: "S-1-5-21-1-2-3-500");

        // Not the signed-in person's: they may have a copy of their own, whose data is not a leftover.
        Assert.Equal(["LFND_Admin"], chosen.Select(p => p.AccountName));
    }

    [Fact]
    public void Every_profile_still_puts_the_owner_first()
    {
        var chosen = LeftoverScanContext.ChooseProfiles(Profiles, everyProfile: true, ownerSid: "S-1-5-21-1-2-3-500");

        Assert.Equal(3, chosen.Count);
        Assert.Equal("LFND_Admin", chosen[0].AccountName);
    }

    [Fact]
    public void Without_an_owner_it_is_the_signed_in_person()
    {
        Assert.Equal(["joseph.ogle"], LeftoverScanContext.ChooseProfiles(Profiles, false, null).Select(p => p.AccountName));
        Assert.Equal(["joseph.ogle"], LeftoverScanContext.ChooseProfiles(Profiles, false, "S-1-5-21-9-9-9-9").Select(p => p.AccountName));
    }
}
