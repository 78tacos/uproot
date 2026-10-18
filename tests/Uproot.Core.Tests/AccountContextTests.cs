using Uproot.Core.Elevation;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// Whether Uproot runs as the person at the keyboard. Elevated as anyone else, everything per-user
/// would be that account's, so the check fails closed: not known to be you counts as not you.
/// </summary>
public class AccountContextTests
{
    private const string You = @"PC\joseph";
    private const string YourSid = "S-1-5-21-1-2-3-1001";
    private const string Admin = @"PC\admin";
    private const string AdminSid = "S-1-5-21-1-2-3-500";

    [Fact]
    public void Not_elevated_is_always_you()
    {
        var account = AccountContext.Evaluate(false, You, YourSid, null, null, TokenElevation.Default, isAdministrator: false);

        Assert.False(account.AsAnotherAccount);
        Assert.Null(account.DescribeOtherAccount());
    }

    [Fact]
    public void An_administrator_elevating_stays_themselves()
    {
        var account = AccountContext.Evaluate(true, You, YourSid, You, YourSid, TokenElevation.Full, isAdministrator: true);

        Assert.False(account.AsAnotherAccount);
        Assert.True(account.CanElevateAsSelf);
    }

    [Fact]
    public void A_standard_user_elevating_becomes_the_administrator()
    {
        var account = AccountContext.Evaluate(true, Admin, AdminSid, You, YourSid, TokenElevation.Default, isAdministrator: true);

        Assert.True(account.AsAnotherAccount);
        Assert.False(account.CanElevateAsSelf);
        Assert.Equal(@"Uproot is running as PC\admin, not as you (PC\joseph).", account.DescribeOtherAccount());
    }

    [Fact]
    public void Names_decide_when_the_signed_in_sid_cannot_be_looked_up()
    {
        // Turning the session's name into a SID needs a domain controller; the name does not.
        Assert.False(AccountContext.Evaluate(true, You, YourSid, @"pc\JOSEPH", null, TokenElevation.Full, true).AsAnotherAccount);
        Assert.True(AccountContext.Evaluate(true, Admin, AdminSid, You, null, TokenElevation.Default, true).AsAnotherAccount);
    }

    [Fact]
    public void Elevated_and_unable_to_tell_counts_as_another_account()
    {
        var account = AccountContext.Evaluate(true, Admin, AdminSid, null, null, TokenElevation.Full, isAdministrator: true);

        Assert.True(account.AsAnotherAccount);
        Assert.Contains("could not confirm", account.DescribeOtherAccount());
    }

    [Theory]
    [InlineData(TokenElevation.Limited, false, true)]    // an administrator with UAC: elevating keeps the account
    [InlineData(TokenElevation.Default, false, false)]   // a standard user: the prompt switches account
    [InlineData(TokenElevation.Default, true, true)]     // UAC off, or the built-in Administrator
    public void Who_can_elevate_as_themselves(TokenElevation elevation, bool isAdministrator, bool expected)
    {
        var account = AccountContext.Evaluate(false, You, YourSid, You, YourSid, elevation, isAdministrator);

        Assert.Equal(expected, account.CanElevateAsSelf);
    }

    [Fact]
    public void This_test_run_is_not_elevated_as_someone_else()
    {
        var account = AccountContext.Current;

        if (!account.Elevated) Assert.False(account.AsAnotherAccount);
        Assert.NotNull(account.ProcessAccount);
    }
}
