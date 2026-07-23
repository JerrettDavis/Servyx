using Docker.DotNet;
using FluentAssertions;
using NSubstitute;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// Servyx's non-negotiable read-only guarantee: nothing in this milestone may write to a live workload,
/// no matter which surface the call comes through. Each scenario drives a real Infrastructure.Docker
/// type with only its Docker.DotNet client substituted.
/// </summary>
[Feature("Read-only safety", "As an operator I trust that Servyx never mutates a workload it manages")]
public class ReadOnlySafetyTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private static DockerExecutionTarget CreateTarget()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        return new DockerExecutionTarget(client, "palworld-server", "/palworld");
    }

    private static DockerLogStream CreateLogStream()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Docker.DotNet.Models.ContainerInspectResponse { Config = new Docker.DotNet.Models.Config { Tty = false } }));
        return new DockerLogStream(client);
    }

    [Scenario("A config-file write is refused before any I/O occurs", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task ConfigWrite_IsRefused_BeforeAnyIOOccurs()
        => await Given("a Docker execution target for an adopted server", () => CreateTarget())
            .When("a write to a config file is attempted", async Task<Exception?> (target) =>
            {
                var path = new SandboxedPathResolver("/palworld").Resolve("PalWorldSettings.ini");
                try
                {
                    await target.WriteFileAsync(path, new MemoryStream(), new FileWriteOptions(null));
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then("it is refused with WritesDisabledException", ex => Task.FromResult(ex is WritesDisabledException))
            .AssertPassed();

    [Scenario("File deletion on a read-only Docker execution target is refused", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task FileDeletion_OnReadOnlyDockerExecutionTarget_ThrowsWritesDisabledException()
        => await Given("a read-only Docker execution target", () => CreateTarget())
            .When("file deletion is attempted", async Task<Exception?> (target) =>
            {
                var path = new SandboxedPathResolver("/palworld").Resolve("world.sav");
                try
                {
                    await target.DeleteAsync(path);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then("WritesDisabledException is thrown", ex => Task.FromResult(ex is WritesDisabledException))
            .AssertPassed();

    [Scenario("Console input on a read-only log stream is refused", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task ConsoleInput_OnReadOnlyLogStream_IsRefused()
        => await Given("a read-only log stream", () => CreateLogStream())
            .When("console input is attempted", async Task<Exception?> (logStream) =>
            {
                try
                {
                    await logStream.WriteAsync("palworld-server", "/shutdown 60 \"restarting\"");
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then("it is refused with WritesDisabledException", ex => Task.FromResult(ex is WritesDisabledException))
            .And("SupportsInput honestly reports false", _ => Task.FromResult(!CreateLogStream().SupportsInput))
            .AssertPassed();

    [Scenario("Command execution reports exec is not supported at this milestone", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task CommandExecution_ReportsExecNotSupported_AtThisMilestone()
        => await Given("a Docker execution target", () => CreateTarget())
            .When("command execution is attempted", async Task<Exception?> (target) =>
            {
                try
                {
                    await target.ExecuteAsync(new CommandSpec("echo", ["hello"]));
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then("it reports NotSupportedException rather than a fake success", ex => Task.FromResult(ex is NotSupportedException))
            .AssertPassed();
}
