using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Process;
using Servyx.Web.Services;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Regression coverage for the write-guard bypass a security review found in the host-rooted (compose)
/// backup source: the compose transport used to carry its own, unconditional <c>WriteMode.Enabled</c> grant
/// scoped only to the compose directory, independent of the specific server's own write-mode grant. A
/// server explicitly marked <see cref="WriteMode.ReadOnly"/> could still have a restore overwrite its host
/// <c>.env</c>/<c>compose.yaml</c>, because nothing checked <em>that server's</em> posture before writing to
/// its compose directory.
/// </summary>
/// <remarks>
/// The fix: the compose transport is still built as a <see cref="WriteGuardedTransport"/> at its
/// construction site (required by <c>ProvisionerCompositionWriteGuardTests</c>' sibling architecture check
/// for this file), but the resolver it carries is a <see cref="ComposeWriteModeResolver"/> — it re-asks the
/// same <see cref="IWriteModeResolver"/> backing the shared Docker <see cref="ITransport"/> for the specific
/// server named on the compose session's descriptor, rather than granting the compose directory
/// unconditionally.
/// </remarks>
public class ServyxBackupContextSourceWriteGuardTests : IDisposable
{
    private const string Container = "palworld-server";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("servyx-write-guard-data-").FullName;
    private readonly string _composeRoot = Directory.CreateTempSubdirectory("servyx-write-guard-compose-").FullName;

    public ServyxBackupContextSourceWriteGuardTests()
    {
        // The data root is deliberately left otherwise empty (no Pal/Saved tree): the real definition's
        // data-rooted include globs then match nothing, so the archive this test creates contains ONLY the
        // compose entry — isolating the write-guard check to the exact path the bypass affected, rather
        // than conflating it with the (already correctly guarded) data source. The store directory itself
        // still has to exist — LocalExecutionTarget.WriteFileAsync does not create intermediate directories.
        Directory.CreateDirectory(Path.Combine(_dataRoot, BackupWiringOptions.DefaultStoreDirectory));
        File.WriteAllText(Path.Combine(_composeRoot, ".env"), "ADMIN_PASSWORD=original\n");
    }

    public void Dispose()
    {
        Directory.Delete(_dataRoot, recursive: true);
        Directory.Delete(_composeRoot, recursive: true);
    }

    private static GameDefinition RealDefinition()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));
        return new GameDefinitionYamlParser().Parse(yaml).Definition!;
    }

    private IServerQueryService Query()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(Container, Container, "palworld", ServerState.Running, ServerHealthStatus.Healthy, null, null, "localhost", []),
                "thijsvanloef/palworld-server-docker:latest",
                _dataRoot,
                _dataRoot,
                null, null, null, null, [])));

        return query;
    }

    private static IWriteModeResolver ResolverFor(WriteMode mode) =>
        mode == WriteMode.ReadOnly
            ? new GrantedWriteModeResolver(null)
            : new GrantedWriteModeResolver(
            [
                new WriteModeGrant(mode, "docker", endpoint: null, requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containerName"] = Container,
                }),
            ]);

    private ServyxBackupContextSource BuildSource(WriteMode serverMode)
    {
        var resolver = ResolverFor(serverMode);

        // The data transport stands in for the Docker Engine one, so it must declare
        // ContainerScopedFiles — ServyxBackupContextSource refuses to open a container-rooted session on a
        // transport that does not (see its RequireContainerScopedFiles). The compose transport below is
        // deliberately left undecorated: that source IS host-rooted, by design.
        return new ServyxBackupContextSource(
            Query(),
            new WriteGuardedTransport(new ContainerScopedFilesTransport(new LocalProcessTransport()), resolver),
            new BackupWiringOptions(include: ["NoSuchDirectory/**"], composeDirectory: _composeRoot),
            rcon: null,
            RealDefinition(),
            composeTransport: new WriteGuardedTransport(new LocalProcessTransport(), new ComposeWriteModeResolver(resolver)));
    }

    [Fact]
    public async Task A_read_only_server_cannot_have_its_compose_directory_written_by_a_restore()
    {
        // Arrange: create a real backup while this server IS writable, so the archive genuinely contains a
        // compose-rooted '.env' entry read from disk.
        string backupId;
        await using (var writableSource = BuildSource(WriteMode.Enabled))
        {
            var provider = new DockerBackupProvider(writableSource);
            var artifact = await provider.CreateAsync(Container);
            backupId = artifact.Id;
        }

        var originalContent = File.ReadAllText(Path.Combine(_composeRoot, ".env"));

        // Act: the SAME server is now ReadOnly. Preview and attempt to restore the backup just created.
        await using var readOnlySource = BuildSource(WriteMode.ReadOnly);
        var readOnlyProvider = new DockerBackupProvider(readOnlySource);

        var plan = await readOnlyProvider.PlanRestoreAsync(backupId);
        plan.AffectedPaths.Should().Contain(p => p.EndsWith(".env", StringComparison.Ordinal),
            "the plan must still show what a restore WOULD touch — refusing happens at RestoreAsync, not at planning");

        var act = async () => await readOnlyProvider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<WritesDisabledException>(
            "a ReadOnly server's compose directory must be refused exactly like its container write would be");

        // Assert: not merely "an exception happened" — the file on disk is byte-for-byte unchanged.
        File.ReadAllText(Path.Combine(_composeRoot, ".env")).Should().Be(originalContent);
    }

    [Fact]
    public async Task A_writable_server_can_restore_into_its_own_compose_directory()
    {
        // Control case: the same flow with the server Enabled must actually succeed, proving the ReadOnly
        // test above fails for the right reason (a write-mode refusal) rather than some unrelated defect.
        string backupId;
        await using (var writableSource = BuildSource(WriteMode.Enabled))
        {
            var provider = new DockerBackupProvider(writableSource);
            var artifact = await provider.CreateAsync(Container);
            backupId = artifact.Id;
        }

        File.WriteAllText(Path.Combine(_composeRoot, ".env"), "ADMIN_PASSWORD=tampered\n");

        await using var writableSource2 = BuildSource(WriteMode.Enabled);
        var writableProvider = new DockerBackupProvider(writableSource2);

        var plan = await writableProvider.PlanRestoreAsync(backupId);
        await writableProvider.RestoreAsync(plan.Id);

        File.ReadAllText(Path.Combine(_composeRoot, ".env")).Should().Be("ADMIN_PASSWORD=original\n");
    }
}
