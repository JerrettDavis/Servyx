using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Tests.Documentation;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Proves the fix for a purely local, non-SSH Docker-adopted server's statically configured RCON channel
/// (<c>Servyx:Servers:&lt;container&gt;:Rcon:*</c>): before this fix, <c>ServyxCoreCompositionExtensions</c>'s
/// <c>chainFactory</c> closure only ever supplied a <c>docker-exec-tool</c> execution target when an
/// ssh+docker host was statically declared (<c>SshDockerWiringOptions.Any</c>). With no such host — the common
/// "just adopted a local Docker container" install — the composed chain silently omitted
/// <c>docker-exec-tool</c> entirely, leaving only <c>direct-tcp</c> (refused; the adopted
/// <c>thijsvanloef/palworld-server-docker</c> image never publishes RCON) and the permanent
/// <c>docker-exec-network</c> stub, so the channel could never actually be reached — see
/// <c>ServyxCoreCompositionExtensions</c>'s RCON block for the fix (falling back to
/// <c>IServerExecutionTargetResolver</c>'s local branch, the same local-Docker-per-container resolution
/// <c>ServyxServerLifecycles</c>/<c>ServyxBackupContextSource</c> already use).
/// </summary>
public class RconLocalDockerExecTargetCompositionTests
{
    private const string Container = "palworld-local";

    private static HostApplicationBuilder BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-rcon-local-defs-");
        var repoDefinitionsDir = Path.Combine(RepoRootLocator.Find().FullName, "definitions");
        var source = Path.Combine(repoDefinitionsDir, "palworld-docker.yaml");
        File.Copy(source, Path.Combine(definitionsDir.FullName, Path.GetFileName(source)));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;

        // Provisioning must be open for RconWiringOptions to read any Servyx:Servers:<key>:Rcon:* section at
        // all (see RconWiringOptions.FromConfiguration).
        builder.Configuration["Servyx:Provisioning:Enabled"] = "true";
        builder.Configuration[$"Servyx:Servers:{Container}:Rcon:Enabled"] = "true";

        // AddServyxCore's RCON block degrades to ServyxRconChannels.None with no ISecretStore in the
        // container — not something AddServyxCore itself registers (see its own remarks). A real host wires
        // this ahead of AddServyxCore (Servyx.Web's AddServyxOperatorAuthentication); this test does the same.
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());

        // Deliberately no Servyx:Hosts:* section — this is the "purely local, no ssh+docker host configured
        // at all" scenario the bug affected. SshDockerWiringOptions.Any is false for this builder.
        return builder;
    }

    [Fact]
    public async Task A_locally_adopted_static_channel_reaches_for_docker_exec_tool_instead_of_omitting_it()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        var channels = host.Services.GetRequiredService<ServyxRconChannels>();

        var act = async () => await channels.GetSessionAsync(Container);

        // Nothing is actually listening (no real container, no real docker daemon reachable the way the test
        // expects), so every strategy in the chain fails and GetSessionAsync throws RconUnreachableException —
        // but the point of this test is WHICH strategies were tried. Before the fix, docker-exec-tool was
        // never composed into the chain at all for a purely local install, so the message named only
        // direct-tcp and docker-exec-network. After the fix, docker-exec-tool is in the chain — reached
        // through a local per-container Docker exec target, exactly like ServyxServerLifecycles/
        // ServyxBackupContextSource already resolve for other local-container operations — so it shows up in
        // the reachability report too, even though it also fails in this environment.
        var ex = await act.Should().ThrowAsync<RconUnreachableException>();
        ex.Which.Message.Should().Contain("direct-tcp");
        ex.Which.Message.Should().Contain(DockerExecToolRconReachability.Id);
        ex.Which.Message.Should().Contain("docker-exec-network");
    }

    [Fact]
    public void A_locally_adopted_static_channel_composes_without_an_ssh_docker_host_configured()
    {
        // Composition itself (constructing ServyxRconChannels, and therefore validating chainFactory is
        // non-null per its own constructor guard) must not throw just because no ssh+docker host is declared —
        // the whole point of the fix is that a static channel is still usable without one.
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        var act = () => host.Services.GetRequiredService<ServyxRconChannels>();

        act.Should().NotThrow();
    }
}
