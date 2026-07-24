using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// A container's raw environment commonly carries real secrets (<c>ADMIN_PASSWORD</c>,
/// <c>SERVER_PASSWORD</c>). <see cref="ServerQueryService"/> is the one place that ever reads that raw
/// dictionary; these scenarios prove its read-model output is safe by construction — an allowlist of
/// known keys, secret values always replaced by a fixed mask — rather than trusting every downstream
/// caller to remember to redact.
/// </summary>
[Feature("Secret protection", "As an operator I trust that a real secret value never leaves the boundary where it was read, in any form")]
public class SecretProtectionTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private const string RealSecret = "supersecret123";
    private const string ServerId = "container-1";

    private static ServerQueryService CreateQueryService(IReadOnlyDictionary<string, string> environmentVariables)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(environmentVariables)]));

        return new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            Substitute.For<ITransport>(),
            AdoptionCriteria.PalworldDefault);
    }

    private static DiscoveredServer BuildDiscoveredServer(IReadOnlyDictionary<string, string> environmentVariables) => new(
        ServerId: ServerId,
        Name: "palworld-server",
        Image: "thijsvanloef/palworld-server-docker:latest",
        ImageDigest: null,
        State: "running",
        HealthStatus: "unhealthy",
        CreatedAt: DateTimeOffset.UtcNow,
        StartedAt: DateTimeOffset.UtcNow,
        Ports: [],
        Mounts: [new DiscoveredMount(@"D:\Games\Palworld\data", "/palworld", true)],
        NetworkName: "palworld_default",
        ContainerIp: "172.19.0.2",
        MemoryLimitBytes: 8_000_000_000,
        CpuLimit: 4.0,
        RestartPolicy: "unless-stopped",
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: environmentVariables);

    [Scenario("A real admin password never appears in any field of the resolved server detail", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task RealAdminPassword_NeverAppearsInAnyFieldOfServerDetail()
        => await Given(
                "a container environment containing a real admin password",
                () => CreateQueryService(new Dictionary<string, string> { ["ADMIN_PASSWORD"] = RealSecret, ["SERVER_NAME"] = "Palygondwanaland" }))
            .When("server detail is resolved", async Task<ServerDetail?> (queryService) => await queryService.GetServerDetailAsync(ServerId))
            .Then("the password value appears in no field of the result", detail => Task.FromResult(detail is not null && !detail.ToString()!.Contains(RealSecret)))
            .And(
                "the setting is present but masked",
                detail => Task.FromResult(detail!.Settings.Single(s => s.Key == "ADMIN_PASSWORD").Authoritative == "********"))
            .AssertPassed();

    [Scenario("An unknown environment variable does not appear at all — an allowlist, not a blocklist", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task UnknownEnvironmentVariable_DoesNotAppearAtAll()
        => await Given(
                "a container environment containing a variable Servyx has no knowledge of",
                () => CreateQueryService(new Dictionary<string, string> { ["SOME_UNDOCUMENTED_INTERNAL_TOKEN"] = "leaked-if-blocklisted" }))
            .When("server detail is resolved", async Task<ServerDetail?> (queryService) => await queryService.GetServerDetailAsync(ServerId))
            .Then(
                "the unknown key never appears in the settings read-model at all",
                detail => Task.FromResult(detail is not null && detail.Settings.All(s => s.Key != "SOME_UNDOCUMENTED_INTERNAL_TOKEN")))
            .And(
                "its value never appears anywhere in the resolved detail either",
                detail => Task.FromResult(!detail!.ToString()!.Contains("leaked-if-blocklisted")))
            .AssertPassed();
}
