using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Tests;

/// <summary>
/// Tests for <see cref="LocalProcessTransport"/> — the transport-level surface: its identity, the exact set of
/// capabilities it claims, and the side-effect-free probe <see cref="ITransport.ProbeAsync"/> requires.
/// </summary>
public class LocalProcessTransportTests
{
    private static TargetDescriptor Target(string? rootPath = null, string endpoint = "local://test-machine")
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rootPath is not null)
        {
            options["rootPath"] = rootPath;
        }

        return new TargetDescriptor("local", endpoint, null, null, options);
    }

    [Fact]
    public void TransportId_is_local()
    {
        // "local" is one of the four values TargetDescriptor documents, and the id ITransport's own remarks
        // name for "local process execution".
        new LocalProcessTransport().TransportId.Should().Be("local");
        LocalProcessTransport.Id.Should().Be("local");
    }

    [Fact]
    public void Capabilities_are_exactly_the_ones_the_local_execution_target_implements()
    {
        new LocalProcessTransport().Capabilities.Should().Be(
            TransportCapabilities.ExecuteCommand |
            TransportCapabilities.StreamOutput |
            TransportCapabilities.FileRead |
            TransportCapabilities.FileWrite |
            TransportCapabilities.DirectoryList |
            TransportCapabilities.ProcessApi);
    }

    [Theory]
    [InlineData(TransportCapabilities.ExecuteCommand)]
    [InlineData(TransportCapabilities.StreamOutput)]
    [InlineData(TransportCapabilities.FileRead)]
    [InlineData(TransportCapabilities.FileWrite)]
    [InlineData(TransportCapabilities.DirectoryList)]
    [InlineData(TransportCapabilities.ProcessApi)]
    public void Every_declared_capability_is_claimed(TransportCapabilities capability)
    {
        new LocalProcessTransport().Capabilities.Should().HaveFlag(capability);
    }

    [Theory]
    [InlineData(TransportCapabilities.StreamStdin)]
    [InlineData(TransportCapabilities.ContainerApi)]
    [InlineData(TransportCapabilities.PortForward)]
    public void Every_unimplemented_capability_is_omitted(TransportCapabilities capability)
    {
        // Pinned individually, as DockerTransportTests pins Docker's omissions: StreamStdin because
        // IExecutionTarget exposes no way to write a running command's stdin, ContainerApi because a host
        // process is not a container, and PortForward because a target on this machine has nothing to tunnel
        // through. A caller checks these flags before invoking; claiming one Servyx does not implement is how
        // a caller comes to believe a port was opened when it was not.
        new LocalProcessTransport().Capabilities.Should().NotHaveFlag(capability);
    }

    [Fact]
    public async Task ProbeAsync_reports_an_existing_readable_root_as_reachable()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("world/level.sav", "save data");

        var health = await new LocalProcessTransport().ProbeAsync(Target(temp.Root));

        health.Reachable.Should().BeTrue();
        health.Latency.Should().NotBeNull();
        health.Detail.Should().Contain(temp.Root);
    }

    [Fact]
    public async Task ProbeAsync_changes_nothing_at_all_under_the_root_it_inspects()
    {
        // ITransport.ProbeAsync's contract is explicit: "MUST be side-effect free: no state on the target may
        // change as a result of calling this method." The snapshot compares every path, size, and last-write
        // time under the root, so a created temp file, a rewritten file, or even a touched timestamp fails
        // this. Probing repeatedly is deliberate: a probe that mutated once per call would show up here.
        using var temp = new TempDirectory();
        temp.WriteFile("world/level.sav", "save data");
        temp.WriteFile("server.cfg", "port=8211");
        Directory.CreateDirectory(temp.At("empty"));

        var before = temp.Snapshot();

        var transport = new LocalProcessTransport();
        for (var i = 0; i < 3; i++)
        {
            await transport.ProbeAsync(Target(temp.Root));
        }

        temp.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task ProbeAsync_reports_a_missing_root_as_unreachable_and_does_not_create_it()
    {
        // The other half of "side-effect free": a probe is not allowed to helpfully bring the root into
        // existence. That is what makes a probe safe to run against a target nobody has approved yet.
        using var temp = new TempDirectory();
        var missing = temp.At("not-created-yet");

        var health = await new LocalProcessTransport().ProbeAsync(Target(missing));

        health.Reachable.Should().BeFalse();
        health.Latency.Should().BeNull();
        health.Detail.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(missing).Should().BeFalse();
        temp.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeAsync_reports_unreachable_rather_than_throwing_when_the_root_is_unusable()
    {
        // A file is not a directory. The probe answers "unreachable" instead of letting an IOException escape,
        // because a probe is the call a caller makes precisely to find out whether something is usable.
        using var temp = new TempDirectory();
        var file = temp.WriteFile("not-a-directory.txt", "x");

        var health = await new LocalProcessTransport().ProbeAsync(Target(file));

        health.Reachable.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_returns_a_session_sandboxed_to_the_descriptors_root_path()
    {
        using var temp = new TempDirectory();

        await using var session = await new LocalProcessTransport().ConnectAsync(Target(temp.Root));

        session.Should().BeOfType<LocalExecutionTarget>()
            .Which.RootPath.Should().Be(Path.GetFullPath(temp.Root));
    }

    [Fact]
    public async Task ConnectAsync_does_not_require_the_root_to_exist_yet()
    {
        // There is no connection to establish, so refusing here would only mean a provisioner could not open a
        // session against the directory it is about to create. Whether the root exists is ProbeAsync's answer
        // to give; see the remarks on LocalProcessTransport.
        using var temp = new TempDirectory();
        var pending = temp.At("about-to-be-created");

        await using var session = await new LocalProcessTransport().ConnectAsync(Target(pending));

        session.Should().NotBeNull();
        Directory.Exists(pending).Should().BeFalse("connecting must not create anything either");
    }

    [Fact]
    public void ResolveRootPath_prefers_the_root_path_option()
    {
        using var temp = new TempDirectory();

        LocalProcessTransport.ResolveRootPath(Target(temp.Root)).Should().Be(temp.Root);
    }

    [Fact]
    public void ResolveRootPath_falls_back_to_an_endpoint_that_is_itself_a_fully_qualified_path()
    {
        using var temp = new TempDirectory();

        LocalProcessTransport.ResolveRootPath(Target(rootPath: null, endpoint: temp.Root)).Should().Be(temp.Root);
    }

    [Fact]
    public void ResolveRootPath_refuses_a_descriptor_that_names_no_root_rather_than_defaulting_to_the_current_directory()
    {
        var act = () => LocalProcessTransport.ResolveRootPath(Target());

        act.Should().Throw<ArgumentException>().WithMessage("*rootPath*");
    }
}
