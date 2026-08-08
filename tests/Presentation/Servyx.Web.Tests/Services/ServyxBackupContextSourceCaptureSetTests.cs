using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Backups;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Regression coverage for a silent-drop bug a security review found in
/// <c>ServyxBackupContextSource.SplitRoot</c>: the definition parser's <c>ValidateContainedPath</c> accepts
/// <c>${data_dir}</c>/<c>${Data_Dir}</c>/<c>${DATA_DIR}</c> interchangeably (case-insensitive on purpose),
/// but <c>SplitRoot</c> matched only the exact-case <c>"${DATA_DIR}/"</c> literal — so a differently-cased
/// declaration passed validation with no error anywhere, and was then silently excluded from the capture
/// set at backup time. That is the exact failure class this whole effort exists to eliminate: a declaration
/// that looks honored and isn't.
/// </summary>
public class ServyxBackupContextSourceCaptureSetTests
{
    private const string Container = "palworld-server";

    private static GameDefinition RealDefinition()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));
        return new GameDefinitionYamlParser().Parse(yaml).Definition!;
    }

    private static IServerQueryService Query()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(Container, Container, "palworld", ServerState.Running, ServerHealthStatus.Healthy, null, null, "localhost", []),
                "thijsvanloef/palworld-server-docker:latest",
                "/srv/palworld",
                "/palworld",
                null, null, null, null, [])));

        return query;
    }

    private static ITransport FakeTransport()
    {
        var transport = Substitute.For<ITransport>();

        // Stands in for the Docker Engine transport, so it must declare the container-scoped file access
        // ServyxBackupContextSource requires before it will open a container-rooted session — an unflagged
        // transport is refused outright (ContainerScopedFilesNotSupportedException).
        transport.Capabilities.Returns(
            TransportCapabilities.FileRead | TransportCapabilities.DirectoryList |
            TransportCapabilities.ContainerApi | TransportCapabilities.ContainerScopedFiles);
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return transport;
    }

    private static GameDefinition WithBackupInclude(IReadOnlyList<string> include)
    {
        var real = RealDefinition();
        return real with
        {
            Backup = real.Backup with { Include = include },
        };
    }

    [Fact]
    public async Task A_lower_or_mixed_case_root_variable_is_honoured_not_dropped()
    {
        var definition = WithBackupInclude(
        [
            "${data_dir}/Pal/Saved/SaveGames/**",
            "${Compose_Dir}/.env",
        ]);

        await using var source = new ServyxBackupContextSource(
            Query(), FakeTransport(), new BackupWiringOptions(composeDirectory: "/srv/compose"), rcon: null, definition,
            composeTransport: FakeTransport());

        var context = await source.GetAsync(Container);

        var data = context.Sources.Should().ContainSingle(s => s.Id == BackupWiringOptions.DataSourceId).Subject;
        data.Include.Should().Contain("Pal/Saved/SaveGames/**",
            "'${data_dir}' must be recognised exactly like '${DATA_DIR}' — matching ValidateContainedPath's own case-insensitive acceptance");

        var compose = context.Sources.Should().ContainSingle(s => s.Id == BackupWiringOptions.ComposeSourceId).Subject;
        compose.Include.Should().Contain(".env",
            "'${Compose_Dir}' must be recognised exactly like '${COMPOSE_DIR}'");
    }

    [Fact]
    public async Task An_unrecognised_root_variable_fails_loudly_instead_of_being_silently_dropped()
    {
        var definition = WithBackupInclude(["${INSTANCE_ID}/some-file"]);

        await using var source = new ServyxBackupContextSource(
            Query(), FakeTransport(), new BackupWiringOptions(), rcon: null, definition);

        var act = async () => await source.GetAsync(Container);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>(
            "a declared glob with no recognised root must fail loudly rather than vanish from the capture set with no error anywhere");
        assertion.Which.Message.Should().Contain("${INSTANCE_ID}/some-file");
    }
}
