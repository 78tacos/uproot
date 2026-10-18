using Uproot.Core.Cli;
using Xunit;

namespace Uproot.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("programs", CliVerb.Programs)]
    [InlineData("list", CliVerb.Programs)]
    [InlineData("analyze", CliVerb.Analyze)]
    [InlineData("analyse", CliVerb.Analyze)]
    [InlineData("leftovers", CliVerb.Leftovers)]
    [InlineData("uninstall", CliVerb.Uninstall)]
    [InlineData("backups", CliVerb.Backups)]
    [InlineData("restore", CliVerb.Restore)]
    [InlineData("help", CliVerb.Help)]
    [InlineData("version", CliVerb.Version)]
    public void Commands_are_plain_words(string command, CliVerb expected)
    {
        var options = CommandLineParser.Parse(Args(command));

        Assert.Equal(expected, options.Verb);
        Assert.Null(options.Error);
    }

    [Theory]
    [InlineData("--programs", CliVerb.Programs)]
    [InlineData("--analyze", CliVerb.Analyze)]
    [InlineData("--leftovers", CliVerb.Leftovers)]
    [InlineData("--uninstall-program", CliVerb.Uninstall)]
    [InlineData("--backups", CliVerb.Backups)]
    [InlineData("--restore", CliVerb.Restore)]
    [InlineData("--help", CliVerb.Help)]
    [InlineData("--version", CliVerb.Version)]
    public void The_forms_wgu_used_still_work(string command, CliVerb expected)
    {
        var options = CommandLineParser.Parse(Args(command));

        Assert.Equal(expected, options.Verb);
        Assert.Null(options.Error);
    }

    // Commands that take a name get one, so each is tested in the form someone would type.
    private static string[] Args(string command) => command.TrimStart('-') switch
    {
        "analyze" or "analyse" or "leftovers" or "uninstall" or "uninstall-program" => [command, "Adobe Acrobat"],
        "restore" => [command, "latest"],
        _ => [command]
    };

    [Theory]
    [InlineData("analyze")]
    [InlineData("leftovers")]
    [InlineData("uninstall")]
    public void A_program_name_is_required(string command)
    {
        var options = CommandLineParser.Parse([command]);

        Assert.Equal(CliVerb.Help, options.Verb);
        Assert.Contains("program name", options.Error);
    }

    [Fact]
    public void Restore_needs_a_backup()
    {
        Assert.NotNull(CommandLineParser.Parse(["restore"]).Error);
        Assert.Equal("2026-10-01 Contoso", CommandLineParser.Parse(["restore", "2026-10-01 Contoso"]).RestoreQuery);
    }

    [Fact]
    public void Leftover_options_parse()
    {
        var options = CommandLineParser.Parse(["leftovers", "Creative Cloud", "--publisher", "Adobe", "--mode", "EXPERT", "--all-users", "--clean"]);

        Assert.Equal("Creative Cloud", options.ProgramQuery);
        Assert.Equal("Adobe", options.Publisher);
        Assert.Equal(ScanMode.Expert, options.ScanMode);
        Assert.True(options.AllUsers);
        Assert.True(options.Clean);
    }

    [Fact]
    public void A_program_can_be_named_like_a_command()
    {
        // Only the first word is a command; a program called "Backups" is still a name.
        var options = CommandLineParser.Parse(["leftovers", "backups"]);

        Assert.Equal(CliVerb.Leftovers, options.Verb);
        Assert.Equal("backups", options.ProgramQuery);
    }

    [Fact]
    public void The_default_mode_is_moderate_and_nothing_is_removed_unless_asked()
    {
        var options = CommandLineParser.Parse(["uninstall", "Contoso"]);

        Assert.Equal(ScanMode.Moderate, options.ScanMode);
        Assert.False(options.Clean);
        Assert.False(options.Unattended);
    }

    [Theory]
    [InlineData("--mode", "reckless")]
    [InlineData("--mode", "7")]
    public void An_unknown_mode_is_refused(string flag, string value)
    {
        Assert.NotNull(CommandLineParser.Parse(["leftovers", "Contoso", flag, value]).Error);
    }

    [Fact]
    public void Options_only_apply_where_they_mean_something()
    {
        Assert.NotNull(CommandLineParser.Parse(["programs", "--clean"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["uninstall", "Contoso", "--publisher", "X"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["uninstall", "Contoso", "--folder", @"C:\Tools\Contoso"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["leftovers", "Contoso", "--unattended"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["backups", "--mode", "expert"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["analyze", "Contoso", "--all-users"]).Error);
    }

    [Fact]
    public void Force_only_goes_with_a_leftover_clean_up()
    {
        Assert.True(CommandLineParser.Parse(["leftovers", "Contoso", "--clean", "--force"]).Force);
        Assert.NotNull(CommandLineParser.Parse(["leftovers", "Contoso", "--force"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["uninstall", "Contoso", "--clean", "--force"]).Error);
    }

    [Fact]
    public void Nothing_unknown_is_ignored()
    {
        Assert.Contains("Unknown command", CommandLineParser.Parse(["scan"]).Error);
        Assert.Contains("Unrecognised option", CommandLineParser.Parse(["programs", "--fast"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["Adobe Acrobat"]).Error);
    }

    [Fact]
    public void No_arguments_or_a_restart_opens_the_window()
    {
        Assert.Equal(CliVerb.Gui, CommandLineParser.Parse([]).Verb);
        Assert.Equal(CliVerb.Gui, CommandLineParser.Parse(["--restarted"]).Verb);
        Assert.Null(CommandLineParser.Parse(["--restarted"]).Error);
    }
}
