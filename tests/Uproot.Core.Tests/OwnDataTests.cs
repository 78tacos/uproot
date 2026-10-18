using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// Uproot keeps its backups where a scan can find them, and reads the ones WGUpdater made when
/// the uninstaller was part of it. Neither may ever be offered as a leftover.
/// </summary>
public class OwnDataTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "uproot-own");
    private static readonly string Own = Path.Combine(Root, "AppData", "Local", "Uproot");
    private static readonly string Legacy = Path.Combine(Root, "AppData", "Local", "WGUpdater", "Uninstall backups");

    private static LeftoverSafety Policy() => new(new SafetyRules
    {
        ProtectedFolders = [Path.Combine(Root, "AppData", "Local")],
        OwnTrees = [Own, Legacy]
    }, []);

    [Fact]
    public void Nothing_in_its_own_folder_is_a_leftover()
    {
        var safety = Policy();
        var backup = Path.Combine(Own, "Backups", "2026-10-18 101500 Contoso", "001 Contoso.reg");

        var verdict = safety.JudgePath(backup);

        Assert.NotNull(verdict);
        Assert.True(verdict!.Absolute);
        Assert.Contains("Uproot's own", verdict.Reason);
        Assert.True(safety.JudgePath(Own)!.Absolute);
    }

    [Fact]
    public void WGUpdaters_backups_are_kept_too_but_the_rest_of_its_folder_is_not()
    {
        var safety = Policy();

        Assert.True(safety.JudgePath(Path.Combine(Legacy, "2026-09-24 171500 Contoso"))!.Absolute);

        // Its folder holds the backups, so it cannot go whole; its other files can.
        var wgupdater = Path.Combine(Root, "AppData", "Local", "WGUpdater");
        Assert.Contains("backups", safety.JudgePath(wgupdater)!.Reason);
        Assert.Null(safety.JudgePath(Path.Combine(wgupdater, "settings.json")));
    }

    [Fact]
    public void A_folder_named_like_its_own_elsewhere_is_judged_normally()
    {
        Assert.Null(Policy().JudgePath(Path.Combine(Root, "AppData", "Local", "Uproot Tools")));
    }

    [Fact]
    public void This_machines_rules_cover_both_programs_folders_in_every_profile()
    {
        var profile = Path.Combine(Root, "Users", "someone");
        var rules = SafetyRules.ForThisMachine([profile]);

        Assert.Contains(RemovalBackup.DataRoot, rules.OwnTrees);
        Assert.Contains(BackupTrust.AdminRoot, rules.OwnTrees);
        Assert.Contains(BackupTrust.LegacyAdminRoot, rules.OwnTrees);
        Assert.Contains(Path.Combine(profile, "AppData", "Local", "Uproot"), rules.OwnTrees);
        Assert.Contains(Path.Combine(profile, "AppData", "Local", "WGUpdater", "Uninstall backups"), rules.OwnTrees);
        Assert.Contains(AppContext.BaseDirectory, rules.ProtectedFolders);
    }

    [Fact]
    public void Backups_go_to_Uprooots_folders_and_WGUpdaters_are_read()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(Path.Combine(local, "Uproot", "Backups"), RemovalBackup.DefaultRoot);
        Assert.Equal(Path.Combine(local, "WGUpdater", "Uninstall backups"), RemovalBackup.LegacyDefaultRoot);
        Assert.Equal(Path.Combine(programData, "Uproot Backups"), BackupTrust.AdminRoot);

        Assert.True(BackupTrust.IsAdminLocation(Path.Combine(BackupTrust.AdminRoot, "S-1-5-21-1", "x")));
        Assert.True(BackupTrust.IsAdminLocation(Path.Combine(BackupTrust.LegacyAdminRoot, "S-1-5-21-1", "x")));
        Assert.False(BackupTrust.IsAdminLocation(Path.Combine(programData, "Uproot Backups Extra")));
    }

    [Fact]
    public void A_backup_outside_both_admin_trees_is_never_trusted()
    {
        Assert.False(BackupTrust.IsTrusted(Path.Combine(RemovalBackup.DefaultRoot, "2026-10-18 101500 Contoso")));
        Assert.False(BackupTrust.IsTrusted(Path.Combine(RemovalBackup.LegacyDefaultRoot, "2026-09-24 171500 Contoso")));
    }

    [Theory]
    [InlineData("Uproot_S-1-5-21-1-2-3-1001", true)]
    [InlineData("WGU_S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-21-1-2-3-1001", false)]
    [InlineData("S-1-5-21-1-2-3-1001_Classes", false)]
    public void Hives_loaded_by_either_program_are_recognised(string name, bool mounted)
    {
        Assert.Equal(mounted, UserProfiles.IsMountName(name));
    }

    [Fact]
    public void Removals_from_both_programs_are_listed_together_newest_first()
    {
        var root = Path.Combine(Root, "history", Guid.NewGuid().ToString("N"));

        try
        {
            Write(root, "2026-09-24 171500 Old", new DateTimeOffset(2026, 9, 24, 17, 15, 0, TimeSpan.Zero));
            Write(root, "2026-10-18 101500 New", new DateTimeOffset(2026, 10, 18, 10, 15, 0, TimeSpan.Zero));

            var records = RemovalHistory.List(root);

            Assert.Equal(["2026-10-18 101500 New", "2026-09-24 171500 Old"], records.Select(r => Path.GetFileName(r.Folder)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string name, DateTimeOffset at)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);

        var manifest = new RemovalManifest
        {
            Format = RemovalManifest.CurrentFormat,
            Program = name,
            RemovedAt = at
        };

        File.WriteAllText(Path.Combine(folder, RemovalBackup.ManifestName),
            System.Text.Json.JsonSerializer.Serialize(manifest, RemovalBackup.Json));
    }
}
