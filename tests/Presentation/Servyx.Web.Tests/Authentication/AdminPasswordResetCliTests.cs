using Servyx.Web.Authentication;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// Tests for the parts of <see cref="AdminPasswordResetCli"/> that do not need a real database or console —
/// principally <see cref="AdminPasswordResetCli.IsInvoked"/>, which is the whole reason this break-glass tool
/// can never fire by accident. The end-to-end behaviour (actually resetting a password against a real SQLite
/// file, and proving the login path accepts it afterward) is covered by
/// <c>Servyx.Web.Tests.Integration.AdminPasswordResetCliEndToEndTests</c>, which drives the real published
/// binary as a subprocess.
/// </summary>
public class AdminPasswordResetCliTests
{
    [Fact]
    public void IsInvoked_IsTrueOnlyWhenTheVerbIsTheFirstArgument()
    {
        AdminPasswordResetCli.IsInvoked(["reset-admin-password", "jd"]).Should().BeTrue();
    }

    [Fact]
    public void IsInvoked_IsCaseInsensitive()
    {
        AdminPasswordResetCli.IsInvoked(["Reset-Admin-Password", "jd"]).Should().BeTrue();
        AdminPasswordResetCli.IsInvoked(["RESET-ADMIN-PASSWORD", "jd"]).Should().BeTrue();
    }

    [Fact]
    public void IsInvoked_IsFalse_ForAnOrdinaryWebHostLaunch()
    {
        // The whole safety property: nothing about a normal `dotnet Servyx.Web.dll` launch, with or without
        // Kestrel's own arguments, can accidentally look like this verb.
        AdminPasswordResetCli.IsInvoked([]).Should().BeFalse();
        AdminPasswordResetCli.IsInvoked(["--urls", "http://127.0.0.1:5000"]).Should().BeFalse();
        AdminPasswordResetCli.IsInvoked(["--environment", "Production"]).Should().BeFalse();
    }

    [Fact]
    public void IsInvoked_IsFalse_ForAnUnrelatedFirstArgument()
    {
        AdminPasswordResetCli.IsInvoked(["some-other-command", "reset-admin-password"]).Should().BeFalse(
            "only the FIRST argument selects the verb, so it cannot be smuggled in as a later one");
    }
}
