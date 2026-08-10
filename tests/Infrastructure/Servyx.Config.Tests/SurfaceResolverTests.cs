using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Config.Tests;

/// <summary>
/// Covers <see cref="SurfaceResolver"/>: locator expansion for every shipped definition, and the four ways
/// a surface is refused rather than resolved onto a path that would be wrong.
/// </summary>
public class SurfaceResolverTests
{
    /// <summary>Where a Docker session's files land: inside the container, at its own root.</summary>
    private const TransportCapabilities DockerCapabilities =
        TransportCapabilities.ExecuteCommand
        | TransportCapabilities.FileRead
        | TransportCapabilities.FileWrite
        | TransportCapabilities.DirectoryList
        | TransportCapabilities.ContainerApi
        | TransportCapabilities.ContainerScopedFiles;

    /// <summary>
    /// The ssh+docker shape: exec is container-addressed, but the file members are SFTP against the SSH
    /// host's own root. Note the deliberate absence of
    /// <see cref="TransportCapabilities.ContainerScopedFiles"/>.
    /// </summary>
    private const TransportCapabilities SshDockerCapabilities =
        TransportCapabilities.ExecuteCommand
        | TransportCapabilities.StreamOutput
        | TransportCapabilities.FileRead
        | TransportCapabilities.FileWrite
        | TransportCapabilities.DirectoryList
        | TransportCapabilities.ContainerApi;

    /// <summary>A session with no file channel at all — an exec-only target.</summary>
    private const TransportCapabilities ExecOnlyCapabilities =
        TransportCapabilities.ExecuteCommand | TransportCapabilities.StreamOutput;

    [Fact]
    public async Task ResolveAsync_ContainerScopedSurfaceOnAHostScopedFileChannel_IsUnresolvable_NotResolvedToAHostPath()
    {
        // definitions/minecraft-itzg.yaml's 'properties' surface, reached over an ssh+docker session whose
        // SFTP file channel is rooted on the SSH host. '/data/server.properties' exists inside the
        // container; on the host it is either nothing or somebody else's file.
        var resolver = Build(new SurfaceResolutionContext(
            SshDockerCapabilities,
            SessionRoot: "/",
            DataDirectory: "/data",
            ComposeDirectory: "/opt/servyx/minecraft",
            DataDirectoryIsContainerScoped: true));

        var result = await resolver.ResolveAsync(
            "mc-1",
            new NoIoExecutionTarget(),
            [Surface("properties", SurfaceRole.Derived, SurfaceFormat.Properties, "${DATA_DIR}/server.properties")]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.SurfaceId.Should().Be("properties");
        failure.Reason.Should().Contain("ContainerScopedFiles");
        failure.Reason.Should().Contain("reach the host filesystem");
        failure.RemediationHint.Should().Contain("rooted in the container");
    }

    [Fact]
    public async Task ResolveAsync_ExecOnlyTarget_ReportsTheUnsatisfiedCapability_RatherThanThrowing()
    {
        var resolver = Build(new SurfaceResolutionContext(
            ExecOnlyCapabilities,
            SessionRoot: "/",
            DataDirectory: "/home/pok/arkserver/ShooterGame/Saved",
            ComposeDirectory: "/opt/servyx/ark",
            DataDirectoryIsContainerScoped: false));

        var result = await resolver.ResolveAsync(
            "ark-1",
            new NoIoExecutionTarget(),
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env")]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.SurfaceId.Should().Be("env");
        failure.Reason.Should().Contain("FileRead");
        failure.Reason.Should().Contain("FileWrite");
        failure.RemediationHint.Should().Contain("exec-only session cannot write");
    }

    [Fact]
    public async Task ResolveAsync_DerivedSurface_ResolvesReadOnly_AndNeverRequiresFileWrite()
    {
        var resolver = Build(new SurfaceResolutionContext(
            DockerCapabilities,
            SessionRoot: "/data",
            DataDirectory: "/data",
            ComposeDirectory: "/opt/servyx/minecraft",
            DataDirectoryIsContainerScoped: true));

        var result = await resolver.ResolveAsync(
            "mc-1",
            new NoIoExecutionTarget(),
            [Surface("properties", SurfaceRole.Derived, SurfaceFormat.Properties, "${DATA_DIR}/server.properties")]);

        result.Unresolvable.Should().BeEmpty();
        var surface = result.Resolved.Should().ContainSingle().Which;
        surface.ServyxMayWrite.Should().BeFalse();
        surface.RequiredCapabilities.Should().Be(TransportCapabilities.FileRead | TransportCapabilities.ContainerScopedFiles);
        surface.RequiredCapabilities.HasFlag(TransportCapabilities.FileWrite).Should().BeFalse();
        surface.ContainerScoped.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_AuthoritativeSurface_RequiresBothFileReadAndFileWrite()
    {
        var resolver = Build(HostSession("/opt/servyx/factorio", "/factorio"));

        var result = await resolver.ResolveAsync(
            "factorio-1",
            new NoIoExecutionTarget(),
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env")]);

        var surface = result.Resolved.Should().ContainSingle().Which;
        surface.ServyxMayWrite.Should().BeTrue();
        surface.RequiredCapabilities.Should().Be(TransportCapabilities.FileRead | TransportCapabilities.FileWrite);
        surface.ContainerScoped.Should().BeFalse();
    }

    /// <summary>
    /// Every host-file surface across the four shipped definitions whose format has a registered adapter.
    /// The <c>compose</c> surfaces are excluded here on purpose and covered by the no-adapter test below.
    /// </summary>
    [Theory]
    // definitions/palworld-docker.yaml, deployment 'docker-thijsvanloef'
    [InlineData("${COMPOSE_DIR}/.env", "opt/servyx/palworld/.env")]
    [InlineData(
        "${DATA_DIR}/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
        "palworld/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini")]
    // definitions/minecraft-itzg.yaml
    [InlineData("${DATA_DIR}/server.properties", "data/server.properties")]
    // definitions/factorio-factoriotools.yaml
    [InlineData("${DATA_DIR}/config/server-settings.json", "factorio/config/server-settings.json")]
    // definitions/ark-asa-pok.yaml
    [InlineData(
        "${DATA_DIR}/Config/WindowsServer/GameUserSettings.ini",
        "home/pok/arkserver/ShooterGame/Saved/Config/WindowsServer/GameUserSettings.ini")]
    [InlineData(
        "${DATA_DIR}/Config/WindowsServer/Game.ini",
        "home/pok/arkserver/ShooterGame/Saved/Config/WindowsServer/Game.ini")]
    public async Task ResolveAsync_ExpandsAShippedDefinitionsLocator_ToTheExpectedTargetPath(
        string declaredPath,
        string expected)
    {
        // One context wide enough to expand all six: each InlineData names the root its own definition uses.
        var resolver = Build(new SurfaceResolutionContext(
            DockerCapabilities,
            SessionRoot: "/",
            DataDirectory: DataRootFor(declaredPath),
            ComposeDirectory: "/opt/servyx/palworld",
            DataDirectoryIsContainerScoped: false));

        var format = declaredPath.EndsWith(".json", StringComparison.Ordinal) ? SurfaceFormat.Json
            : declaredPath.EndsWith(".ini", StringComparison.Ordinal) ? SurfaceFormat.Ini
            : declaredPath.EndsWith(".properties", StringComparison.Ordinal) ? SurfaceFormat.Properties
            : SurfaceFormat.Dotenv;

        var result = await resolver.ResolveAsync(
            "s-1",
            new NoIoExecutionTarget(),
            [Surface("s", SurfaceRole.Authoritative, format, declaredPath)]);

        result.Unresolvable.Should().BeEmpty();
        result.Resolved.Should().ContainSingle().Which.Path!.Value.Value.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_OnADockerSessionRootedAtTheDataDirectory_ProducesAPathRelativeToThatRoot()
    {
        // The SessionRoot is load-bearing: resolving the same locator against "/" would yield
        // 'palworld/Pal/...', which a Docker session already rooted at /palworld would then read as
        // /palworld/palworld/Pal/... — a real file path, and the wrong one.
        var resolver = Build(new SurfaceResolutionContext(
            DockerCapabilities,
            SessionRoot: "/palworld",
            DataDirectory: "/palworld",
            ComposeDirectory: null,
            DataDirectoryIsContainerScoped: true));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface(
                "palworldsettings",
                SurfaceRole.Derived,
                SurfaceFormat.Ini,
                "${DATA_DIR}/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
                codec: "unreal-option-settings",
                codecPath: "[\"/Script/Pal.PalGameWorldSettings\"].OptionSettings")]);

        var surface = result.Resolved.Should().ContainSingle().Which;
        surface.Path!.Value.Value.Should().Be("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        surface.CodecId.Should().Be("unreal-option-settings");
        surface.CodecPath.Should().Be("[\"/Script/Pal.PalGameWorldSettings\"].OptionSettings");
    }

    [Fact]
    public async Task ResolveAsync_YamlSurface_ReportsThatNoAdapterIsRegistered_RatherThanResolvingAgainstANullAdapter()
    {
        // The refusal path for a format with no adapter behind it, exercised through the 'compose' surface
        // all four shipped definitions declare. Build() deliberately injects an adapter set that excludes
        // yaml, so this stays a test of the refusal rather than of whichever adapters happen to be
        // registered today — the resolver looks adapters up dynamically, so a YamlConfigAdapter added to
        // AddServyxConfig starts resolving these surfaces with no change to SurfaceResolver.
        var resolver = Build(HostSession("/opt/servyx/palworld", "/palworld"));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("compose", SurfaceRole.Authoritative, SurfaceFormat.Yaml, "${COMPOSE_DIR}/compose.yaml")]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.SurfaceId.Should().Be("compose");
        failure.Reason.Should().Contain("format 'yaml'");
        failure.Reason.Should().Contain("no IConfigAdapter is");
        // Names what IS registered, so the gap is obvious at a glance.
        failure.Reason.Should().Contain("'dotenv'").And.Contain("'ini'").And.Contain("'json'").And.Contain("'properties'");
        failure.RemediationHint.Should().Contain("FormatId is 'yaml'");
    }

    [Fact]
    public async Task ResolveAsync_ControlChannelSurface_IsReportedAsNotFileBacked_WithNoPath()
    {
        var resolver = Build(HostSession("/opt/servyx/palworld", "/palworld"));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [
                new DeclaredConfigSurface(
                    "live",
                    SurfaceRole.Runtime,
                    SurfaceFormat.Json,
                    Codec: null,
                    CodecPath: null,
                    new SurfaceLocator.ControlChannel("rest", "/v1/api/settings"),
                    ManagedSubtree: null,
                    MergePolicy.PreserveUnknown,
                    DerivedFrom: [],
                    Regeneration: null),
            ]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.SurfaceId.Should().Be("live");
        failure.Reason.Should().Contain("control channel 'rest'");
        failure.RemediationHint.Should().Contain("'rest' control channel");
    }

    [Fact]
    public async Task ResolveAsync_AMixOfResolvableAndUnresolvableSurfaces_ReturnsBothListsCorrectly()
    {
        // definitions/palworld-docker.yaml's whole 'docker-thijsvanloef' surface set, over an ssh+docker
        // session: the two ${COMPOSE_DIR} surfaces are on the SSH host and reachable, the in-container INI
        // is not, the compose file has no adapter, and 'live' is not a file at all.
        var resolver = Build(new SurfaceResolutionContext(
            SshDockerCapabilities,
            SessionRoot: "/",
            DataDirectory: "/palworld",
            ComposeDirectory: "/opt/servyx/palworld",
            DataDirectoryIsContainerScoped: true));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [
                Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env"),
                Surface("compose", SurfaceRole.Authoritative, SurfaceFormat.Yaml, "${COMPOSE_DIR}/compose.yaml"),
                Surface(
                    "palworldsettings",
                    SurfaceRole.Derived,
                    SurfaceFormat.Ini,
                    "${DATA_DIR}/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"),
                new DeclaredConfigSurface(
                    "live",
                    SurfaceRole.Runtime,
                    SurfaceFormat.Json,
                    Codec: null,
                    CodecPath: null,
                    new SurfaceLocator.ControlChannel("rest", "/v1/api/settings"),
                    ManagedSubtree: null,
                    MergePolicy.PreserveUnknown,
                    DerivedFrom: [],
                    Regeneration: null),
            ]);

        result.Resolved.Select(s => s.Id).Should().Equal("env");
        result.Resolved.Single().Path!.Value.Value.Should().Be("opt/servyx/palworld/.env");

        result.Unresolvable.Select(f => f.SurfaceId).Should().Equal("compose", "palworldsettings", "live");
        result.Unresolvable.Should().OnlyContain(f => f.RemediationHint.Length > 0);
    }

    [Fact]
    public async Task ResolveAsync_WithNoContextForTheServer_RefusesEverySurface_AndNamesWhatIsMissing()
    {
        var resolver = new SurfaceResolver(new NullContextSource(), Adapters());

        var result = await resolver.ResolveAsync(
            "unknown-1",
            new NoIoExecutionTarget(),
            [
                Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env"),
                Surface("properties", SurfaceRole.Derived, SurfaceFormat.Properties, "${DATA_DIR}/server.properties"),
            ]);

        result.Resolved.Should().BeEmpty();
        result.Unresolvable.Select(f => f.SurfaceId).Should().Equal("env", "properties");
        result.Unresolvable.Should().OnlyContain(f => f.Reason.Contains("'unknown-1'"));
        result.Unresolvable.Should().OnlyContain(f => f.RemediationHint.Contains("ISurfaceResolutionContextSource"));
    }

    [Fact]
    public async Task ResolveAsync_RootVariableWithNoConfiguredExpansion_IsRefused_NotResolvedRelativeToTheSessionRoot()
    {
        var resolver = Build(new SurfaceResolutionContext(
            SshDockerCapabilities,
            SessionRoot: "/",
            DataDirectory: "/palworld",
            ComposeDirectory: null,
            DataDirectoryIsContainerScoped: false));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env")]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.Reason.Should().Contain("${COMPOSE_DIR}");
        failure.RemediationHint.Should().Contain("ComposeDirectory");
    }

    [Fact]
    public async Task ResolveAsync_LocatorCarryingAnUnknownVariable_IsRefused_RatherThanLeftLiteralInThePath()
    {
        var resolver = Build(HostSession("/opt/servyx/pal", "/palworld"));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/${INSTANCE_ID}/.env")]);

        result.Resolved.Should().BeEmpty();
        result.Unresolvable.Should().ContainSingle().Which.Reason.Should().Contain("${INSTANCE_ID}");
    }

    /// <summary>
    /// Definition YAML is semi-trusted: an operator can import a definition authored outside this project,
    /// so a locator must never be able to name a file outside the root variable it claims to be relative to.
    /// Containment against the session root alone is not enough — on the whole-host SSH/SFTP topology the
    /// session root is <c>"/"</c>, which every absolute path trivially satisfies.
    /// </summary>
    [Theory]
    // Leading traversal, whole-host SSH session: the case that is unguarded if only SessionRoot is checked.
    [InlineData("${COMPOSE_DIR}/../../../etc/passwd", "/")]
    [InlineData("${DATA_DIR}/../..", "/")]
    // Traversal buried after a legitimate-looking segment.
    [InlineData("${DATA_DIR}/config/../../../etc/shadow", "/")]
    // The same locators against a narrow Docker session root, where the outer check would also have caught it.
    [InlineData("${DATA_DIR}/../../../etc/passwd", "/palworld")]
    [InlineData("${DATA_DIR}/config/../../../etc/shadow", "/palworld")]
    public async Task ResolveAsync_LocatorEscapingItsOwnRootVariable_IsRefused_OnEveryTopology(
        string declaredPath,
        string sessionRoot)
    {
        var resolver = Build(new SurfaceResolutionContext(
            TransportCapabilities.ExecuteCommand
                | TransportCapabilities.FileRead
                | TransportCapabilities.FileWrite
                | TransportCapabilities.DirectoryList
                | TransportCapabilities.ContainerScopedFiles,
            sessionRoot,
            DataDirectory: "/palworld",
            ComposeDirectory: "/opt/servyx/palworld",
            DataDirectoryIsContainerScoped: false));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, declaredPath)]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.SurfaceId.Should().Be("env");
        failure.Reason.Should().Contain("escapes");
        failure.RemediationHint.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_TraversalThatStaysInsideItsRoot_StillResolves_AndIsNormalized()
    {
        // Do not over-correct into banning every '..'. This one is redundant, not an escape.
        var resolver = Build(new SurfaceResolutionContext(
            TransportCapabilities.FileRead | TransportCapabilities.FileWrite,
            SessionRoot: "/",
            DataDirectory: "/data",
            ComposeDirectory: "/opt/servyx/minecraft",
            DataDirectoryIsContainerScoped: false));

        var result = await resolver.ResolveAsync(
            "mc-1",
            new NoIoExecutionTarget(),
            [Surface(
                "properties",
                SurfaceRole.Authoritative,
                SurfaceFormat.Properties,
                "${DATA_DIR}/config/../config/server.properties")]);

        result.Unresolvable.Should().BeEmpty();
        result.Resolved.Should().ContainSingle().Which.Path!.Value.Value
            .Should().Be("data/config/server.properties");
    }

    [Fact]
    public async Task ResolveAsync_LocatorNotRootedAtADeclaredVariable_IsRefused()
    {
        var resolver = Build(HostSession("/opt/servyx/pal", "/palworld"));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("stray", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "/etc/passwd")]);

        result.Resolved.Should().BeEmpty();
        result.Unresolvable.Should().ContainSingle().Which.Reason.Should().Contain("does not begin with");
    }

    [Fact]
    public async Task ResolveAsync_RootVariableAppearingAfterAPrefix_IsRefused_BecauseARootMustLead()
    {
        // The root regex is anchored at the start on purpose: '${DATA_DIR}' buried mid-path is not a root,
        // it is a literal directory name that happens to look like one. The refusal says so.
        var resolver = Build(HostSession("/opt/servyx/pal", "/palworld"));

        var result = await resolver.ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("stray", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "/prefix/${DATA_DIR}/x")]);

        result.Resolved.Should().BeEmpty();
        var failure = result.Unresolvable.Should().ContainSingle().Which;
        failure.Reason.Should().Contain("does not begin with");
        failure.Reason.Should().Contain("must be the very first thing in the path");
    }

    [Fact]
    public async Task ResolveAsync_PerformsNoIoOnTheTarget()
    {
        var target = new NoIoExecutionTarget();
        var resolver = Build(HostSession("/opt/servyx/pal", "/palworld"));

        await resolver.ResolveAsync(
            "pal-1",
            target,
            [Surface("env", SurfaceRole.Authoritative, SurfaceFormat.Dotenv, "${COMPOSE_DIR}/.env")]);

        // NoIoExecutionTarget throws on every member; reaching here at all is the assertion, and the flag
        // makes the intent explicit rather than implicit in the absence of an exception.
        target.WasTouched.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_WithANullTarget_Throws_BecauseThatIsACallerBugAndNotADeploymentFact()
    {
        var resolver = Build(HostSession("/opt/servyx/pal", "/palworld"));

        var act = async () => await resolver.ResolveAsync("pal-1", null!, []);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// The first end-to-end proof that a YAML surface resolves. Deliberately built from the adapters
    /// <see cref="ServiceCollectionExtensions.AddServyxConfig"/> actually registers, not a hand-picked set —
    /// that is what makes it a test of the wiring rather than of this file's own <see cref="Adapters"/>
    /// helper. Every one of the four shipped definitions declares this exact surface.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ComposeYamlSurface_ResolvesThroughTheRegisteredAdapterSet()
    {
        var services = new ServiceCollection();
        services.AddServyxConfig();
        services.AddSingleton<ISurfaceResolutionContextSource>(
            new FixedContextSource(HostSession("/opt/servyx/palworld", "/palworld")));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ISurfaceResolver>().ResolveAsync(
            "pal-1",
            new NoIoExecutionTarget(),
            [Surface("compose", SurfaceRole.Authoritative, SurfaceFormat.Yaml, "${COMPOSE_DIR}/compose.yaml")]);

        result.Unresolvable.Should().BeEmpty();
        var surface = result.Resolved.Should().ContainSingle().Which;
        surface.FormatId.Should().Be("yaml");
        surface.Path!.Value.Value.Should().Be("opt/servyx/palworld/compose.yaml");
        surface.ServyxMayWrite.Should().BeTrue();
        surface.RequiredCapabilities.Should().Be(TransportCapabilities.FileRead | TransportCapabilities.FileWrite);
        surface.ContainerScoped.Should().BeFalse();
    }

    [Fact]
    public void AddServyxConfig_RegistersTheSurfaceResolver_AndAPlaceholderContextSource()
    {
        var services = new ServiceCollection();

        services.AddServyxConfig();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<ISurfaceResolver>().Should().BeOfType<SurfaceResolver>();
        provider.GetRequiredService<ISurfaceResolutionContextSource>()
            .Should().BeOfType<UnconfiguredSurfaceResolutionContextSource>();
    }

    [Fact]
    public void AddServyxConfig_LeavesAnAlreadyRegisteredContextSourceInPlace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISurfaceResolutionContextSource>(new NullContextSource());

        services.AddServyxConfig();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISurfaceResolutionContextSource>().Should().BeOfType<NullContextSource>();
    }

    private static SurfaceResolver Build(SurfaceResolutionContext context) =>
        new(new FixedContextSource(context), Adapters());

    private static IConfigAdapter[] Adapters() =>
    [
        new DotEnvConfigAdapter(),
        new IniConfigAdapter(),
        new PropertiesConfigAdapter(),
        new JsonConfigAdapter(),
    ];

    /// <summary>A plain SSH session: whole-host file access, nothing container-scoped.</summary>
    private static SurfaceResolutionContext HostSession(string composeDirectory, string dataDirectory) =>
        new(
            TransportCapabilities.ExecuteCommand
                | TransportCapabilities.FileRead
                | TransportCapabilities.FileWrite
                | TransportCapabilities.DirectoryList,
            SessionRoot: "/",
            dataDirectory,
            composeDirectory,
            DataDirectoryIsContainerScoped: false);

    private static string DataRootFor(string declaredPath) => declaredPath switch
    {
        var p when p.Contains("/Pal/Saved/", StringComparison.Ordinal) => "/palworld",
        var p when p.Contains("server.properties", StringComparison.Ordinal) => "/data",
        var p when p.Contains("server-settings.json", StringComparison.Ordinal) => "/factorio",
        _ => "/home/pok/arkserver/ShooterGame/Saved",
    };

    private static DeclaredConfigSurface Surface(
        string id,
        SurfaceRole role,
        SurfaceFormat format,
        string path,
        string? codec = null,
        string? codecPath = null) =>
        new(
            id,
            role,
            format,
            codec,
            codecPath,
            new SurfaceLocator.HostFile(path),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: [],
            Regeneration: null);

    private sealed class FixedContextSource(SurfaceResolutionContext context) : ISurfaceResolutionContextSource
    {
        public Task<SurfaceResolutionContext?> GetAsync(
            string serverId,
            IExecutionTarget target,
            CancellationToken ct = default) =>
            Task.FromResult<SurfaceResolutionContext?>(context);
    }

    private sealed class NullContextSource : ISurfaceResolutionContextSource
    {
        public Task<SurfaceResolutionContext?> GetAsync(
            string serverId,
            IExecutionTarget target,
            CancellationToken ct = default) =>
            Task.FromResult<SurfaceResolutionContext?>(null);
    }

    /// <summary>
    /// An <see cref="IExecutionTarget"/> that refuses every operation. Surface resolution is a pure
    /// computation over declared facts, so any call at all is the bug this fake is here to catch.
    /// </summary>
    private sealed class NoIoExecutionTarget : IExecutionTarget
    {
        public bool WasTouched { get; private set; }

        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse();

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse();

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) => throw Refuse();

        public Task DeleteAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private InvalidOperationException Refuse()
        {
            WasTouched = true;
            return new InvalidOperationException("Surface resolution must not perform I/O on the target.");
        }
    }
}
