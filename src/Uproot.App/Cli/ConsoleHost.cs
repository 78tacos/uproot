using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Uproot.App.Cli;

/// <summary>
/// Gives this WinExe a console to write to.
///
/// Uproot.exe is a windowed application, so it starts with no console attached and anything
/// written to <see cref="Console"/> goes nowhere. AttachConsole borrows the console of whatever
/// shell launched it; when there isn't one (double-clicked from Explorer, say) a new window is
/// allocated so output is still visible rather than silently lost.
/// </summary>
internal static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static bool _allocated;

    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            _allocated = AllocConsole();
        }

        // The standard streams were bound before the console existed, so they have to be
        // rebuilt or every write lands on an invalid handle.
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetOut(stdout);

            var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetError(stderr);

            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a usable console the command still runs; only its output is lost, and the
            // exit code remains meaningful to a caller.
        }
    }

    /// <summary>
    /// Releases the console. When one was allocated for us the window would close instantly on
    /// exit, so the user is given a chance to read it first.
    /// </summary>
    public static void Release(bool waitForKey)
    {
        try
        {
            Console.Out.Flush();

            // Only pause when there is genuinely a person at a keyboard. With redirected
            // streams there is no interactive input, and ReadKey would block forever — which
            // turns a scripted command into a hung process holding a lock on the executable.
            if (_allocated && waitForKey && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.WriteLine();
                Console.Write("Press any key to close…");
                Console.ReadKey(intercept: true);
            }
            else if (!_allocated)
            {
                // A shell has already printed its next prompt by now, so emit a newline to keep
                // the caller's cursor on a clean line.
                Console.WriteLine();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        finally
        {
            FreeConsole();
        }
    }

    public static bool OwnsConsoleWindow => _allocated;
}
