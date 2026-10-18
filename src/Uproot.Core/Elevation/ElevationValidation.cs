using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Uproot.Core.Elevation;

/// <summary>
/// Who this process is and who is at the keyboard, which are not always the same account.
/// </summary>
public static class ElevationValidation
{
    /// <summary>Strips control characters before an untrusted value reaches a message.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";

        var clean = new string(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
        return clean.Length == 0 ? "(empty)" : clean;
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The account signed in to this session — the person at the keyboard — which is not always
    /// the account this process runs as. On a machine where the signed-in user is not an
    /// administrator, elevating runs Uproot as a different account entirely.
    /// </summary>
    public static string? InteractiveUserSid()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;

            // Session 0 is services; there is nobody at a keyboard to ask about.
            if (session == 0) return null;

            var user = QuerySession(session, WtsUserName);
            if (string.IsNullOrWhiteSpace(user)) return null;

            var domain = QuerySession(session, WtsDomainName);

            var account = string.IsNullOrWhiteSpace(domain)
                ? new NTAccount(user)
                : new NTAccount(domain, user);

            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        // Translate throws a bare SystemException — not a subclass — when the domain controller
        // cannot be reached ("the trust relationship ... failed"), so that exact type is matched
        // rather than SystemException and everything derived from it.
        catch (Exception ex) when (ex is IdentityNotMappedException
                                       or System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                   || ex.GetType() == typeof(SystemException))
        {
            // The caller falls back to the process's own account, which is correct whenever
            // elevation did not switch account.
            return null;
        }
    }

    /// <summary>
    /// "DOMAIN\user" for the account signed in to this session, read from the session itself, so
    /// it needs no domain controller (unlike turning it into a SID). Null in session 0 or when the
    /// session cannot be asked.
    /// </summary>
    public static string? InteractiveAccountName()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            if (session == 0) return null;

            var user = QuerySession(session, WtsUserName);
            if (string.IsNullOrWhiteSpace(user)) return null;

            var domain = QuerySession(session, WtsDomainName);
            return string.IsNullOrWhiteSpace(domain) ? user : domain + @"\" + user;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? QuerySession(int sessionId, int infoClass)
    {
        // IntPtr.Zero is WTS_CURRENT_SERVER_HANDLE.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return null;

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public static string? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
