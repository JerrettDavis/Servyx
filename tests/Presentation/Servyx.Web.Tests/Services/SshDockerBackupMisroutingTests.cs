using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Process;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Regression coverage for the combination that had none: the <em>Docker</em> backup pipeline
/// (<see cref="DockerBackupProvider"/> over <see cref="ServyxBackupContextSource"/>) running on a deployment
/// wired by <c>AddServyxSshDocker</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What was wrong.</strong> <see cref="ServyxBackupContextSource"/> builds a descriptor naming a
/// container and an in-container root (<c>/palworld</c>) and hands it to the ambient
/// <see cref="ITransport"/>. <c>AddServyxSshDocker</c> replaces that transport with
/// <see cref="SshDockerTransport"/>, which rewrote the descriptor to <c>"ssh"</c> and forwarded every file
/// member to <c>SftpFileChannel</c> — whose <c>ToRemotePath</c> is <c>"/" + path.Value</c> against the
/// <em>SSH host's</em> root. Because <c>SandboxedPathResolver</c> has already made the path relative to the
/// source root, the container root was not merely ignored, it was gone: <c>/palworld/Pal/Saved/x</c> became
/// the host's <c>/Pal/Saved/x</c>. Capture therefore found nothing and wrote an empty archive the operator
/// would believe was a backup; <c>ApplyAsync</c>'s restore wrote the archive's bytes to real paths on the
/// SSH host, outside any container.
/// </para>
/// <para>
/// <strong>Why the existing guards did not catch it.</strong> <c>WriteGuardedExecutionTarget</c> asks only
/// whether writes are permitted <em>for this server</em>, and a restore has already had to answer yes;
/// <c>SandboxedPathResolver</c> keeps a path inside its declared root, and it is the root itself that was
/// lost. Neither is positioned to notice that the filesystem on the other end is the wrong one.
/// </para>
/// <para>
/// <strong>The suite below runs the real types.</strong> The provider, the context source, the real
/// ssh+docker transport and the real write guard are all genuine; only the innermost SSH connection is a
/// double, and it is deliberately shaped like the defect — it ignores the descriptor's <c>rootPath</c> and
/// serves <see cref="HostRoot"/>, exactly as SFTP-on-the-host does. Every "the host directory is unchanged"
/// assertion below is therefore a real assertion about a real directory the misrouted path would have
/// written into.
/// </para>
/// </remarks>
public sealed class SshDockerBackupMisroutingTests : IDisposable
{
    private const string Container = "palworld-server";

    /// <summary>Stands in for the container's own filesystem — where a correct capture and restore belong.</summary>
    private readonly string _containerRoot = Directory.CreateTempSubdirectory("servyx-misroute-container-").FullName;

    /// <summary>
    /// Stands in for the SSH host's filesystem — what SFTP actually reaches, and what a misrouted restore
    /// would write into. Nothing in this suite may ever modify it.
    /// </summary>
    private readonly string _hostRoot = Directory.CreateTempSubdirectory("servyx-misroute-host-").FullName;

    private string HostRoot => _hostRoot;

    public SshDockerBackupMisroutingTests()
    {
        Directory.CreateDirectory(Path.Combine(_containerRoot, "Pal", "Saved", "SaveGames"));
        File.WriteAllText(Path.Combine(_containerRoot, "Pal", "Saved", "SaveGames", "Level.sav"), "container-save\n");

        // LocalExecutionTarget does not create intermediate directories, so the Servyx artifact directory
        // has to exist for a capture to be able to write into it.
        Directory.CreateDirectory(Path.Combine(_containerRoot, BackupWiringOptions.DefaultStoreDirectory));

        // A sentinel on the "SSH host" at the exact place a root-stripped path would land:
        // '/palworld/Pal/Saved/SaveGames/Level.sav' misroutes to the host's
        // '/Pal/Saved/SaveGames/Level.sav'.
        Directory.CreateDirectory(Path.Combine(_hostRoot, "Pal", "Saved", "SaveGames"));
        File.WriteAllText(Path.Combine(_hostRoot, "Pal", "Saved", "SaveGames", "Level.sav"), "HOST DATA - DO NOT TOUCH\n");
    }

    public void Dispose()
    {
        Directory.Delete(_containerRoot, recursive: true);
        Directory.Delete(_hostRoot, recursive: true);
    }

    // ── reachability: the real production wiring is the unfit one ──────────────────────────────────

    [Fact]
    public void The_production_ssh_docker_wiring_registers_a_transport_without_container_scoped_files()
    {
        // This is the reachability half of the defect: not a hypothetical transport, but the one
        // AddServyxSshDocker actually registers over AddServyxDocker's, in the order Program.cs calls them.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Servyx:Hosts:testhost:Enabled"] = "true",
                ["Servyx:Hosts:testhost:Transport"] = "ssh+docker",
                ["Servyx:Hosts:testhost:Endpoint"] = "ssh:user@10.0.0.9:22",
                ["Servyx:Hosts:testhost:Container"] = Container,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddServyxDocker();
        services.AddServyxSshDocker(
            SshDockerWiringOptions.FromConfiguration(configuration, NullLogger.Instance), NullLogger.Instance);

        using var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<ITransport>();

        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.ContainerScopedFiles,
            "the Docker backup pipeline resolves this exact registration, and it cannot serve container-rooted "
            + "paths — that mismatch is the defect");

        // The local Docker registration this one replaced could, which is why the swap is what broke backups.
        new DockerTransport().Capabilities
            .Should().HaveFlag(TransportCapabilities.ContainerScopedFiles);
    }

    // ── capture ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_refuses_loudly_instead_of_writing_an_empty_archive()
    {
        await using var source = MisroutedSource();
        var provider = new DockerBackupProvider(source);

        var act = async () => await provider.CreateAsync(Container);

        var assertion = await act.Should().ThrowAsync<ContainerScopedFilesNotSupportedException>(
            "an archive built from host paths that do not exist is an empty file the operator believes is a "
            + "backup — the worst possible outcome, and strictly worse than a failure");

        assertion.Which.ContainerRef.Should().Be(Container);
        assertion.Which.ContainerRootPath.Should().Be(_containerRoot.Replace('\\', '/').TrimEnd('/'));

        StoreDirectoryEntries().Should().BeEmpty("no archive and no manifest may be produced by a refused capture");
        HostRootIsUnchanged();
    }

    [Fact]
    public async Task Every_read_only_entry_point_refuses_too_rather_than_reporting_host_state()
    {
        // Listing, planning and dry-run pruning are not destructive, but each of them answers a question
        // about the wrong filesystem when misrouted — a listing of the HOST's '.servyx-backups' presented as
        // this container's backups. Refusing is the only honest answer.
        await using var source = MisroutedSource();
        var provider = new DockerBackupProvider(source);

        await FluentActions.Awaiting(() => provider.ListAsync(Container))
            .Should().ThrowAsync<ContainerScopedFilesNotSupportedException>();

        await FluentActions.Awaiting(() => provider.PruneAsync(Container, new RetentionPolicy(1, 1, 1), dryRun: true))
            .Should().ThrowAsync<ContainerScopedFilesNotSupportedException>();

        HostRootIsUnchanged();
    }

    // ── restore: the destructive half ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_refuses_and_leaves_the_ssh_hosts_own_filesystem_byte_for_byte_unchanged()
    {
        // Arrange: produce a genuine archive the correct way — through a container-scoped transport — so the
        // restore below has real bytes it would otherwise write. This is the archive an operator would pick
        // in the UI.
        string backupId;
        await using (var correct = ContainerScopedSource())
        {
            var artifact = await new DockerBackupProvider(correct).CreateAsync(Container);
            backupId = artifact.Id;
        }

        // A transport that starts out container-scoped and is switched to the ssh+docker posture partway
        // through. This is the only way to reach RestoreAsync's apply path at all — a plan lives in the
        // provider instance that issued it, so a misrouted source can never hand one out — and it also pins
        // the property that matters most: the refusal is re-checked per call, ahead of the session cache, so
        // a session opened while the transport was fit does not become a bypass afterwards.
        var switchable = new SwitchableTransport(new ContainerScopedFilesTransport(new LocalProcessTransport()));
        await using var source = BuildSource(new WriteGuardedTransport(switchable, WritableResolver()));
        var provider = new DockerBackupProvider(source);

        var plan = await provider.PlanRestoreAsync(backupId);
        plan.AffectedPaths.Should().NotBeEmpty("the plan must genuinely name paths a restore would write");

        // Act: the deployment is now reached over ssh+docker.
        switchable.Inner = new SshDockerTransport(new HostRootedSshTransport(_hostRoot, _sshConnections));

        await FluentActions.Awaiting(() => provider.RestoreAsync(plan.Id))
            .Should().ThrowAsync<ContainerScopedFilesNotSupportedException>(
                "applying a restore is the destructive half — it must be refused at the apply path itself, "
                + "not merely at planning, and not left to a cached session opened under a fitter transport");

        HostRootIsUnchanged();
    }

    [Fact]
    public async Task A_restore_applied_through_the_misrouted_source_never_reaches_the_hosts_filesystem()
    {
        // The end-to-end shape: one provider instance, plans and applies, over the ssh+docker wiring. Absent
        // the guard this is the exact call sequence that wrote archived save data onto the SSH host.
        await using var misrouted = MisroutedSource();
        var provider = new DockerBackupProvider(misrouted);

        await FluentActions.Awaiting(async () =>
        {
            var plan = await provider.PlanRestoreAsync(BackupArtifactId.Format(Container, "/whatever/servyx-x.tar.gz"));
            await provider.RestoreAsync(plan.Id);
        }).Should().ThrowAsync<ContainerScopedFilesNotSupportedException>();

        HostRootIsUnchanged();
        SshHostWasNeverConnected();
    }

    [Fact]
    public async Task The_refusal_happens_before_any_ssh_connection_is_opened()
    {
        await using var source = MisroutedSource();

        await FluentActions.Awaiting(() => new DockerBackupProvider(source).CreateAsync(Container))
            .Should().ThrowAsync<ContainerScopedFilesNotSupportedException>();

        SshHostWasNeverConnected();
    }

    // ── control: the same flow over a container-scoped transport still works ───────────────────────

    [Fact]
    public async Task The_same_pipeline_over_a_container_scoped_transport_still_captures_and_restores()
    {
        // Without this, every assertion above could be satisfied by a provider that simply never works.
        await using var source = ContainerScopedSource();
        var provider = new DockerBackupProvider(source);

        var artifact = await provider.CreateAsync(Container);
        artifact.Ownership.Should().Be(Servyx.Domain.Backups.BackupOwnership.Servyx);

        var entries = await provider.InspectAsync(artifact.Id);
        entries.Should().Contain(e => e.EndsWith("Pal/Saved/SaveGames/Level.sav", StringComparison.Ordinal),
            "the capture must have read the CONTAINER's save, which is the whole point");

        var savePath = Path.Combine(_containerRoot, "Pal", "Saved", "SaveGames", "Level.sav");
        File.WriteAllText(savePath, "tampered\n");

        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        File.ReadAllText(savePath).Should().Be("container-save\n");
        HostRootIsUnchanged();
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private readonly List<TargetDescriptor> _sshConnections = [];

    /// <summary>
    /// A context source over the real <see cref="SshDockerTransport"/>, guarded the way
    /// <c>AddServyxSshDocker</c> guards it, over an inner "ssh" transport that behaves the way SFTP does:
    /// it ignores the descriptor's container <c>rootPath</c> entirely and serves <see cref="HostRoot"/>.
    /// </summary>
    private ServyxBackupContextSource MisroutedSource() =>
        BuildSource(new WriteGuardedTransport(
            new SshDockerTransport(new HostRootedSshTransport(_hostRoot, _sshConnections)),
            WritableResolver()));

    /// <summary>
    /// The control wiring: a transport that genuinely declares container-scoped file access, standing in for
    /// the Docker Engine transport by serving the descriptor's <c>rootPath</c> — which for this server is
    /// <see cref="_containerRoot"/>.
    /// </summary>
    private ServyxBackupContextSource ContainerScopedSource() =>
        BuildSource(new WriteGuardedTransport(
            new ContainerScopedFilesTransport(new LocalProcessTransport()), WritableResolver()));

    private ServyxBackupContextSource BuildSource(ITransport transport) =>
        new(Query(),
            transport,
            new BackupWiringOptions(include: ["Pal/Saved/**"]),
            rcon: null,
            RealDefinition());

    private static IWriteModeResolver WritableResolver() =>
        new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "docker", endpoint: null,
                requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containerName"] = Container,
                }),
        ]);

    private IServerQueryService Query()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(Container, Container, "palworld", ServerState.Running, ServerHealthStatus.Healthy, null, null, "localhost", []),
                "thijsvanloef/palworld-server-docker:latest",
                _containerRoot,
                _containerRoot,
                null, null, null, null, [])));

        return query;
    }

    private static GameDefinition RealDefinition()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));
        return new GameDefinitionYamlParser().Parse(yaml).Definition!;
    }

    private IEnumerable<string> StoreDirectoryEntries() =>
        Directory.EnumerateFileSystemEntries(Path.Combine(_containerRoot, BackupWiringOptions.DefaultStoreDirectory));

    /// <summary>Asserts the SSH host's filesystem is byte-for-byte what the constructor left there.</summary>
    private void HostRootIsUnchanged()
    {
        var files = Directory.GetFiles(_hostRoot, "*", SearchOption.AllDirectories);
        files.Should().ContainSingle("a misrouted capture or restore would have created files on the SSH host");
        File.ReadAllText(files[0]).Should().Be("HOST DATA - DO NOT TOUCH\n");
    }

    private void SshHostWasNeverConnected() =>
        _sshConnections.Should().BeEmpty(
            "the refusal is positioned ahead of the connection, so a misrouted deployment never even opens "
            + "the session it would have written through");

    /// <summary>
    /// An <see cref="ITransport"/> whose delegate can be swapped at runtime, so one test can observe the
    /// same long-lived context source and provider before and after a deployment's transport changes shape.
    /// </summary>
    private sealed class SwitchableTransport(ITransport inner) : ITransport
    {
        public ITransport Inner { get; set; } = inner;

        public string TransportId => Inner.TransportId;

        public TransportCapabilities Capabilities => Inner.Capabilities;

        public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
            Inner.ProbeAsync(target, ct);

        public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default) =>
            Inner.ConnectAsync(target, ct);
    }

    /// <summary>
    /// An inner "ssh" transport shaped like the real one's defect: whatever root the descriptor asks for, the
    /// session it returns is rooted at the SSH host's own directory — the behaviour <c>SftpFileChannel</c>'s
    /// <c>"/" + path.Value</c> produces.
    /// </summary>
    private sealed class HostRootedSshTransport(string hostRoot, List<TargetDescriptor> connections) : ITransport
    {
        public string TransportId => "ssh";

        public TransportCapabilities Capabilities =>
            TransportCapabilities.ExecuteCommand | TransportCapabilities.FileRead |
            TransportCapabilities.FileWrite | TransportCapabilities.DirectoryList;

        public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
            Task.FromResult(new TargetHealth(true, TimeSpan.Zero, "double"));

        public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
        {
            connections.Add(target);
            return Task.FromResult<IExecutionTarget>(new LocalExecutionTarget(hostRoot));
        }
    }
}
