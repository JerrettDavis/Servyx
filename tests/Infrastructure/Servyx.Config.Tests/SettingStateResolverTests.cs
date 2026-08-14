using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Config.Tests;

/// <summary>
/// Covers <see cref="SettingStateResolverFactory"/> and the <see cref="SettingStateResolver"/> it mints:
/// reading a real Palworld-shaped surface set across two sessions, the codec path a naive key/value read
/// would get wrong, drift and its masking, and the refusals that keep an unreadable column from being
/// reported as an agreeing one.
/// </summary>
public class SettingStateResolverTests
{
    private const string ComposeDirectory = "/opt/servyx/pal";
    private const string DataDirectory = "/palworld";

    private const string DockerCapabilities_Env = """
        # The image's source of truth.
        SERVER_NAME=Authoritative Name
        ADMIN_PASSWORD=hunter2
        PORT=8211
        """;

    private const string PalWorldSettingsIni = """
        [/Script/Pal.PalGameWorldSettings]
        OptionSettings=(Difficulty=None,ServerName="Rendered Name",PublicPort=8211)
        """;

    [Fact]
    public async Task ResolveAsync_ReadsTheAuthoritativeDotenvAndTheDerivedIniCodecMember()
    {
        var resolver = await BuildAsync();

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Authoritative.Should().Be("Authoritative Name");

        // Not "(Difficulty=None,ServerName=…)". A surface that declares a codec holds the value inside a
        // single scalar, and a naive key/value read of that surface returns the whole blob.
        state.Rendered.Should().Be("Rendered Name");
        state.Drift.Should().HaveFlag(DriftKind.AuthoritativeVsRendered);
        state.IsWritable.Should().BeTrue();
        state.NotWritableReason.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_WhenTheDerivedSurfaceRegeneratesOnRestart_ReportsPendingRegeneration()
    {
        var resolver = await BuildAsync();

        var state = await resolver.ResolveAsync("SERVER_NAME");

        // The .env has been edited and the container has not restarted yet. That is not drift an operator
        // has to reconcile; it is drift waiting for the entrypoint to regenerate the INI.
        state.PendingRegeneration.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_WhenAuthoritativeAndRenderedAgree_ReportsNoDrift()
    {
        var resolver = await BuildAsync();

        var state = await resolver.ResolveAsync("PORT");

        state.Authoritative.Should().Be("8211");
        state.Rendered.Should().Be("8211");
        state.Drift.Should().Be(DriftKind.None);
        state.PendingRegeneration.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ForASecret_MasksEveryColumn_ButStillComputesDriftFromTheRealValues()
    {
        var resolver = await BuildAsync(desired: ("ADMIN_PASSWORD", "a-different-password"));

        var state = await resolver.ResolveAsync("ADMIN_PASSWORD");

        state.Authoritative.Should().Be(SettingStateResolver.SecretMask);
        state.Desired.Should().Be(SettingStateResolver.SecretMask);
        state.Authoritative.Should().NotContain("hunter2");
        state.Desired.Should().NotContain("a-different-password");

        // The whole point of masking after comparing: masking first would make every secret equal every
        // other secret and report a drifted password as clean.
        state.Drift.Should().HaveFlag(DriftKind.DesiredVsAuthoritative);
    }

    [Fact]
    public async Task ResolveAsync_WithARecordedDesiredValueThatMatches_ReportsNoDesiredDrift()
    {
        var resolver = await BuildAsync(desired: ("SERVER_NAME", "Authoritative Name"));

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Desired.Should().Be("Authoritative Name");
        state.Drift.Should().NotHaveFlag(DriftKind.DesiredVsAuthoritative);
    }

    [Fact]
    public async Task ResolveAsync_WithNoDesiredValueStore_LeavesDesiredNull_RatherThanInventingOne()
    {
        var resolver = await BuildAsync();

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Desired.Should().BeNull();
        state.Drift.Should().NotHaveFlag(DriftKind.DesiredVsAuthoritative);
    }

    [Fact]
    public async Task ResolveAsync_ReadsEachSurfaceOnceNoMatterHowManySettingsLiveOnIt()
    {
        var files = Files();
        var resolver = await BuildAsync(files: files);

        await resolver.ResolveAsync("SERVER_NAME");
        await resolver.ResolveAsync("ADMIN_PASSWORD");
        await resolver.ResolveAsync("PORT");

        // Three settings, two surfaces, two reads. Re-reading per setting would turn a settings page into
        // one round trip per row.
        files.Reads.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_WhenNoAdapterIsRegisteredForTheFormat_ReportsUnreadable_NotAWrongValue()
    {
        // The generic degradation path for any format with no IConfigAdapter — modelled here by building the
        // resolver with the dotenv adapter alone, so the INI surface has none. YAML used to be this case and
        // is not any more; the mechanism is not format-specific and must keep working for whatever is next.
        var resolver = await BuildAsync(adapters: [new DotEnvConfigAdapter()]);

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Authoritative.Should().Be("Authoritative Name");
        state.Rendered.Should().BeNull();
        state.Drift.Should().HaveFlag(DriftKind.Unreadable);

        // An unread column must never be reported as an agreeing one.
        state.Drift.Should().NotHaveFlag(DriftKind.AuthoritativeVsRendered);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheSurfacesFileIsMissing_ReportsUnreadable_AndKeepsTheOtherColumns()
    {
        var files = Files();
        files.Remove("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini");
        var resolver = await BuildAsync(files: files);

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Authoritative.Should().Be("Authoritative Name");
        state.Rendered.Should().BeNull();
        state.Drift.Should().HaveFlag(DriftKind.Unreadable);
    }

    [Fact]
    public async Task ResolveAsync_ForASettingWithNoWritableBinding_SaysSoRatherThanReportingItWritable()
    {
        var resolver = await BuildAsync();

        var state = await resolver.ResolveAsync("READ_ONLY_SETTING");

        state.IsWritable.Should().BeFalse();
        state.NotWritableReason.Should().Contain("no writable binding");
    }

    [Fact]
    public async Task ResolveAsync_ForAnUnknownKey_Throws_BecauseThatIsACallerBugNotADeploymentFact()
    {
        var resolver = await BuildAsync();

        var act = async () => await resolver.ResolveAsync("NOT_A_SETTING");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_WithNoSessionsAtAll_StillResolves_ReportingEveryColumnUnreadable()
    {
        var factory = new SettingStateResolverFactory(
            new StubSessionSource(new ServerConfigSessions([], Surfaces())),
            new SurfaceResolver(new NoContextSource(), Adapters()),
            Adapters(),
            [new UnrealOptionSettingsCodec()]);

        var resolver = await factory.CreateAsync(new SettingStateScope("pal-1", Settings()));
        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Authoritative.Should().BeNull();
        state.Rendered.Should().BeNull();
        state.Drift.Should().Be(DriftKind.Unreadable);
        state.IsWritable.Should().BeFalse();
        state.NotWritableReason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The guard that makes the dual-session split safe. The composition root nulls
    /// <see cref="SurfaceResolutionContext.ComposeDirectory"/> on the container session and
    /// <see cref="SurfaceResolutionContext.DataDirectory"/> on the host one precisely so a surface cannot
    /// resolve on both — which is exactly why the impossible case is checked. Picking a winner silently
    /// would read a real file off the wrong filesystem, and nothing downstream could tell.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenOneSurfaceResolvesOnTwoSessions_Throws_NamingBothPaths()
    {
        var first = new FakeTarget(Files());
        var second = new FakeTarget(Files());

        // Both contexts expand ${COMPOSE_DIR}, which is the regression this guard exists to catch.
        var contexts = new MappedContextSource
        {
            [first] = HostContext(ComposeDirectory),
            [second] = HostContext("/somewhere/else"),
        };

        var factory = new SettingStateResolverFactory(
            new StubSessionSource(new ServerConfigSessions(
                [new ConfigSession(first, "session A"), new ConfigSession(second, "session B")],
                [EnvSurface()])),
            new SurfaceResolver(contexts, Adapters()),
            Adapters(),
            [new UnrealOptionSettingsCodec()]);

        var act = async () => await factory.CreateAsync(new SettingStateScope("pal-1", Settings()));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("env");
        thrown.Which.Message.Should().Contain("session A");
        thrown.Which.Message.Should().Contain("session B");
    }

    [Fact]
    public async Task CreateAsync_WhenASurfaceFailsOnBothSessions_ReportsOneCoherentReason_NotTwo()
    {
        // The INI surface is ${DATA_DIR}-rooted and neither session expands that variable, so both refuse
        // it — with the identical reason. One surface, one problem, one message.
        var resolver = await BuildAsync(dataSessionKnowsItsRoot: false);

        var state = await resolver.ResolveAsync("SERVER_NAME");

        state.Rendered.Should().BeNull();
        state.Drift.Should().HaveFlag(DriftKind.Unreadable);
        state.IsWritable.Should().BeTrue("the ${COMPOSE_DIR}-rooted env surface is still reachable");
    }

    [Fact]
    public void AddServyxConfig_RegistersTheSettingStateFactory_AndAPlaceholderSessionSource()
    {
        var services = new ServiceCollection();

        services.AddServyxConfig();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<ISettingStateResolverFactory>().Should().BeOfType<SettingStateResolverFactory>();
        provider.GetRequiredService<IServerConfigSessionSource>()
            .Should().BeOfType<UnconfiguredServerConfigSessionSource>();
    }

    [Fact]
    public void AddServyxConfig_LeavesAnAlreadyRegisteredSessionSourceInPlace()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServerConfigSessionSource>(new StubSessionSource(null));

        services.AddServyxConfig();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IServerConfigSessionSource>().Should().BeOfType<StubSessionSource>();
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<ISettingStateResolver> BuildAsync(
        FakeFiles? files = null,
        (string Key, string Value)? desired = null,
        IConfigAdapter[]? adapters = null,
        bool dataSessionKnowsItsRoot = true)
    {
        var adapterSet = adapters ?? Adapters();
        var content = files ?? Files();

        var dataTarget = new FakeTarget(content);
        var composeTarget = new FakeTarget(content);

        var contexts = new MappedContextSource
        {
            [dataTarget] = new SurfaceResolutionContext(
                TransportCapabilities.FileRead
                    | TransportCapabilities.FileWrite
                    | TransportCapabilities.ContainerScopedFiles,
                SessionRoot: dataSessionKnowsItsRoot ? DataDirectory : "/",
                DataDirectory: dataSessionKnowsItsRoot ? DataDirectory : null,
                ComposeDirectory: null,
                DataDirectoryIsContainerScoped: true),
            [composeTarget] = HostContext(ComposeDirectory),
        };

        var factory = new SettingStateResolverFactory(
            new StubSessionSource(new ServerConfigSessions(
                [
                    new ConfigSession(dataTarget, "the deployment's data directory"),
                    new ConfigSession(composeTarget, "the host compose directory"),
                ],
                Surfaces())),
            new SurfaceResolver(contexts, adapterSet),
            adapterSet,
            [new UnrealOptionSettingsCodec()],
            desired is null ? null : new StubDesiredValues(desired.Value.Key, desired.Value.Value));

        return await factory.CreateAsync(new SettingStateScope("pal-1", Settings()));
    }

    private static SurfaceResolutionContext HostContext(string composeDirectory) => new(
        TransportCapabilities.FileRead | TransportCapabilities.FileWrite,
        SessionRoot: composeDirectory,
        DataDirectory: null,
        ComposeDirectory: composeDirectory,
        DataDirectoryIsContainerScoped: false);

    private static IConfigAdapter[] Adapters() =>
    [
        new DotEnvConfigAdapter(),
        new IniConfigAdapter(),
        new PropertiesConfigAdapter(),
        new JsonConfigAdapter(),
    ];

    private static FakeFiles Files() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".env"] = DockerCapabilities_Env,
        ["Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"] = PalWorldSettingsIni,
    });

    private static DeclaredConfigSurface EnvSurface() => new(
        "env",
        SurfaceRole.Authoritative,
        SurfaceFormat.Dotenv,
        Codec: null,
        CodecPath: null,
        new SurfaceLocator.HostFile("${COMPOSE_DIR}/.env"),
        ManagedSubtree: null,
        MergePolicy.PreserveUnknown,
        DerivedFrom: [],
        Regeneration: null);

    /// <summary>Palworld's shipped docker profile, trimmed to the two file surfaces this phase can read.</summary>
    private static IReadOnlyList<DeclaredConfigSurface> Surfaces() =>
    [
        EnvSurface(),
        new(
            "palworldsettings",
            SurfaceRole.Derived,
            SurfaceFormat.Ini,
            Codec: "unreal-option-settings",

            // Quoted exactly as definitions/palworld-docker.yaml writes it, while IniConfigAdapter's own
            // pointers carry the section name bare. Both name the same value.
            CodecPath: """["/Script/Pal.PalGameWorldSettings"].OptionSettings""",
            new SurfaceLocator.HostFile("${DATA_DIR}/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini"),
            ManagedSubtree: null,
            MergePolicy.PreserveUnknown,
            DerivedFrom: ["env"],
            new RegenerationTrigger(
                RegenerationKind.ContainerRestart,
                "Regenerated from .env by the image entrypoint on every start.")),
    ];

    private static IReadOnlyList<SettingDescriptor> Settings() =>
    [
        Setting("SERVER_NAME", SettingType.String, "ServerName", unquote: true),
        Setting("ADMIN_PASSWORD", SettingType.Secret, member: null),
        Setting("PORT", SettingType.Port, "PublicPort"),
        new(
            "READ_ONLY_SETTING",
            "Read only",
            "Diagnostics",
            SettingType.String,
            Required: false,
            Default: null,
            RenderFormat: null,
            RequiresRecreate: false,
            PublishByDefault: null,
            NoConstraints,
            [new SettingBinding.ByMember("palworldsettings", BindingDirection.Read, Sensitive: false, "Difficulty", Unquote: false)]),
    ];

    private static SettingDescriptor Setting(string key, SettingType type, string? member, bool unquote = false)
    {
        var bindings = new List<SettingBinding>
        {
            new SettingBinding.ByKey("env", BindingDirection.Write, Sensitive: false, key),
        };

        if (member is not null)
        {
            bindings.Add(new SettingBinding.ByMember("palworldsettings", BindingDirection.Read, Sensitive: false, member, unquote));
        }

        return new SettingDescriptor(
            key,
            key,
            "General",
            type,
            Required: false,
            Default: null,
            RenderFormat: null,
            RequiresRecreate: false,
            PublishByDefault: null,
            NoConstraints,
            bindings);
    }

    private static readonly SettingConstraints NoConstraints =
        new(null, null, null, null, null, null, null, null, null);

    private sealed class StubSessionSource(ServerConfigSessions? sessions) : IServerConfigSessionSource
    {
        public Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(sessions);
    }

    private sealed class NoContextSource : ISurfaceResolutionContextSource
    {
        public Task<SurfaceResolutionContext?> GetAsync(string serverId, IExecutionTarget target, CancellationToken ct = default) =>
            Task.FromResult<SurfaceResolutionContext?>(null);
    }

    /// <summary>Answers a different context per session, which is the whole point of the dual-session split.</summary>
    private sealed class MappedContextSource : ISurfaceResolutionContextSource
    {
        private readonly Dictionary<IExecutionTarget, SurfaceResolutionContext> _byTarget = [];

        public SurfaceResolutionContext this[IExecutionTarget target]
        {
            set => _byTarget[target] = value;
        }

        public Task<SurfaceResolutionContext?> GetAsync(string serverId, IExecutionTarget target, CancellationToken ct = default) =>
            Task.FromResult(_byTarget.TryGetValue(target, out var context) ? context : null);
    }

    private sealed class StubDesiredValues(string key, string value) : IServerSettingsService
    {
        public Task<ServerSettingsSnapshot?> LoadAsync(string containerId, CancellationToken ct = default) =>
            Task.FromResult<ServerSettingsSnapshot?>(new ServerSettingsSnapshot(
                new ServerId(Guid.NewGuid()),
                new Dictionary<string, DesiredSettingValue>(StringComparer.Ordinal)
                {
                    [key] = new(key, value, "operator", DateTimeOffset.UnixEpoch),
                }));

        public Task<SaveDesiredValueResult> SaveDesiredValueAsync(
            ServerId serverId, string key, string? value, string actor, CancellationToken ct = default) =>
            throw new InvalidOperationException("Reading setting state must never write a desired value.");

        public Task<SaveDesiredValueResult> SetMirrorToDerivedAsync(
            ServerId serverId, string key, bool? mirrorToDerived, string actor, CancellationToken ct = default) =>
            throw new InvalidOperationException("Reading setting state must never write a mirror override.");
    }

    private sealed class FakeFiles(Dictionary<string, string> content)
    {
        public int Reads { get; private set; }

        public void Remove(string path) => content.Remove(path);

        public Stream Open(string path)
        {
            if (!content.TryGetValue(path, out var text))
            {
                throw new FileNotFoundException($"No such file on the target: '{path}'.", path);
            }

            Reads++;
            return new MemoryStream(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>
    /// A read-only session. Every mutating member throws: this phase reads configuration and must never be
    /// able to write it, and a test that silently tolerated a write would be no test at all.
    /// </summary>
    private sealed class FakeTarget(FakeFiles files) : IExecutionTarget
    {
        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
            Task.FromResult(files.Open(path.Value));

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) => Task.FromResult(true);

        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse();

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) => throw Refuse();

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) => throw Refuse();

        public Task DeleteAsync(TargetPath path, CancellationToken ct = default) => throw Refuse();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static InvalidOperationException Refuse() =>
            new("Reading setting state must never mutate the target or run a command on it.");
    }
}
