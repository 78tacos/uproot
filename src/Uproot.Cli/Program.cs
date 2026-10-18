using System.Text;
using Uproot.Core.Cli;

// Console entry point. All of the work lives in Uproot.Core.Cli.CliRunner, which the window
// also calls when it is given a command, so the two never drift apart.

Console.OutputEncoding = Encoding.UTF8;

var options = CommandLineParser.Parse(args);

// With no arguments a command-line tool should explain itself rather than sit there, and it
// must not try to open the window: that is what Uproot.exe is for.
if (options.Verb == CliVerb.Gui)
{
    Console.Error.WriteLine("No command given. Run uproot help, or start Uproot.exe for the window.");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLineParser.Usage);
    return ExitCodes.UsageError;
}

try
{
    return await CliRunner.RunAsync(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"uproot failed: {ex.Message}");
    return ExitCodes.Failed;
}
