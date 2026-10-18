using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Uproot.Core.Elevation;

/// <summary>
/// Who Uproot is running as, and whether that is the person at the keyboard.
///
/// Elevating keeps the account when the signed-in user is an administrator. When they are a
/// standard user, the administrator prompt asks for another account's credentials, and the
/// elevated process then runs as that account: its HKCU, its %LOCALAPPDATA%, its per-user
/// programs, its Store apps and its Recycle Bin, none of which are the signed-in
/// person's. Everything that would act on "your" profile has to know that, or it acts on the
/// administrator's without saying so.
///
/// The check fails closed: elevated, and unable to confirm the signed-in account is this one,
/// counts as another account.
/// </summary>
public sealed record AccountContext
{
    /// <summary>This process has administrator rights.</summary>
    public required bool Elevated { get; init; }

    /// <summary>Elevated as an account other than the one signed in to this session, or not known to be the same.</summary>
    public required bool AsAnotherAccount { get; init; }

    /// <summary>"DOMAIN\user" this process runs as.</summary>
    public string? ProcessAccount { get; init; }

    /// <summary>"DOMAIN\user" signed in to this session, when it could be read.</summary>
    public string? SignedInAccount { get; init; }

    /// <summary>
    /// Whether elevating would keep this account: true for an administrator (who gets a split
    /// token), false for a standard user, whose administrator prompt switches account.
    /// </summary>
    public required bool CanElevateAsSelf { get; init; }

    private static readonly Lazy<AccountContext> Detected = new(Detect);

    /// <summary>This process, worked out once: the token does not change while it runs.</summary>
    public static AccountContext Current => Detected.Value;

    /// <summary>
    /// Decides <see cref="AsAnotherAccount"/> from what could be read. Pure, so every case is tested.
    /// </summary>
    public static AccountContext Evaluate(
        bool elevated, string? processAccount, string? processSid,
        string? signedInAccount, string? signedInSid, TokenElevation elevation, bool isAdministrator)
    {
        bool another;
        if (!elevated)
        {
            another = false;
        }
        else if (processSid is not null && signedInSid is not null)
        {
            another = !processSid.Equals(signedInSid, StringComparison.OrdinalIgnoreCase);
        }
        else if (processAccount is not null && signedInAccount is not null)
        {
            another = !processAccount.Equals(signedInAccount, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            another = true;   // could not tell: assume it is not you
        }

        var canElevateAsSelf = elevation switch
        {
            TokenElevation.Limited => true,           // an administrator with UAC: elevating keeps the account
            TokenElevation.Full => !another,          // already elevated; as yourself unless it switched
            _ => isAdministrator && !another          // UAC off, or the built-in Administrator
        };

        return new AccountContext
        {
            Elevated = elevated,
            AsAnotherAccount = another,
            ProcessAccount = processAccount,
            SignedInAccount = signedInAccount,
            CanElevateAsSelf = canElevateAsSelf
        };
    }

    /// <summary>
    /// One sentence for a banner: "Uproot is running as PC\admin, not as you (PC\joseph)."
    /// Null when it is running as you.
    /// </summary>
    public string? DescribeOtherAccount()
    {
        if (!AsAnotherAccount) return null;

        var who = ProcessAccount ?? "an administrator account";
        return SignedInAccount is { } you
            ? $"Uproot is running as {who}, not as you ({you})."
            : $"Uproot is running as {who}, and could not confirm that is the account signed in.";
    }

    private static AccountContext Detect()
    {
        string? processAccount = null;
        string? processSid = null;
        var elevation = TokenElevation.Default;
        var isAdministrator = false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            processAccount = identity.Name;
            processSid = identity.User?.Value;
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            elevation = ReadElevation(identity.Token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return Evaluate(
            ElevationState.IsElevated,
            processAccount,
            processSid,
            ElevationValidation.InteractiveAccountName(),
            ElevationValidation.InteractiveUserSid(),
            elevation,
            isAdministrator);
    }

    private const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    private static TokenElevation ReadElevation(IntPtr token) =>
        GetTokenInformation(token, TokenElevationTypeClass, out var type, sizeof(int), out _)
            ? type switch { 2 => TokenElevation.Full, 3 => TokenElevation.Limited, _ => TokenElevation.Default }
            : TokenElevation.Default;
}

/// <summary>TOKEN_ELEVATION_TYPE: how this process's token relates to UAC.</summary>
public enum TokenElevation
{
    /// <summary>No split token: a standard user, UAC off, or the built-in Administrator.</summary>
    Default,

    /// <summary>The elevated half of an administrator's split token.</summary>
    Full,

    /// <summary>The filtered half of an administrator's split token: elevating keeps the account.</summary>
    Limited
}
