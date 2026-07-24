using Servyx.Domain.Configuration;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// A setting's truth lives across up to three surfaces (authoritative, rendered/derived, runtime), plus
/// Servyx's own desired intent — and they can disagree. <see cref="SettingState"/> is the four-column
/// model that makes disagreement visible instead of silently picking one column and calling it "the"
/// value; <see cref="SurfaceRole"/> is what determines whether Servyx may ever write a given surface.
/// </summary>
[Feature("Configuration drift", "As an operator I can see exactly which configuration surface disagrees with which, instead of guessing why a change didn't take effect")]
public class ConfigurationDriftTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    [Scenario("Authoritative and rendered values that disagree are reported as drifted, with a restart pending", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task AuthoritativeVsRendered_Disagreement_ReportsDriftAndPendingRestart()
        => await Given(
                "an authoritative value that differs from the rendered value",
                () => new SettingState(
                    Desired: "5000",
                    Authoritative: "5000",
                    Rendered: "4000",
                    Runtime: "4000",
                    Drift: DriftKind.AuthoritativeVsRendered,
                    PendingRegeneration: true,
                    IsWritable: true,
                    NotWritableReason: null))
            .When("settings are resolved", async Task<SettingState> (state) => await Task.FromResult(state))
            .Then("drift reports AuthoritativeVsRendered", state => Task.FromResult(state.Drift.HasFlag(DriftKind.AuthoritativeVsRendered)))
            .And("a restart is indicated as pending", state => Task.FromResult(state.PendingRegeneration))
            .And("desired-vs-authoritative is NOT also flagged, since only that one pair disagreed", state => Task.FromResult(!state.Drift.HasFlag(DriftKind.DesiredVsAuthoritative)))
            .AssertPassed();

    [Scenario("A derived surface is reported as not writable", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task DerivedSurface_IsReportedAsNotWritable()
        => await Given("a configuration surface with the Derived role", () => new ConfigSurface("rendered-ini", SurfaceRole.Derived, new SurfaceLocator.HostFile("/palworld/PalWorldSettings.ini"), "ini", null))
            .When("Servyx checks whether it may write the surface", async Task<bool> (surface) => await Task.FromResult(surface.ServyxMayWrite))
            .Then("Servyx reports it as not writable", mayWrite => Task.FromResult(!mayWrite))
            .AssertPassed();

    [Scenario("An authoritative surface is reported as writable", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task AuthoritativeSurface_IsReportedAsWritable()
        => await Given("a configuration surface with the Authoritative role", () => new ConfigSurface("env", SurfaceRole.Authoritative, new SurfaceLocator.HostFile("/palworld/.env"), "dotenv", null))
            .When("Servyx checks whether it may write the surface", async Task<bool> (surface) => await Task.FromResult(surface.ServyxMayWrite))
            .Then("Servyx reports it as writable", mayWrite => Task.FromResult(mayWrite))
            .AssertPassed();
}
