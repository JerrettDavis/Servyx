using Servyx.Domain.Transport;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Unit coverage for <see cref="ComposeWriteModeResolver"/> in isolation, independent of
/// <see cref="ServyxBackupContextSourceWriteGuardTests"/>' end-to-end restore scenario.
/// </summary>
public class ComposeWriteModeResolverTests
{
    private static TargetDescriptor ComposeDescriptor(string? containerName) =>
        new(
            "local",
            "/srv/compose",
            null,
            null,
            containerName is null
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["rootPath"] = "/srv/compose" }
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["rootPath"] = "/srv/compose", ["containerName"] = containerName });

    [Fact]
    public void Delegates_to_the_per_server_grant_for_the_named_container()
    {
        var grants = new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "docker", endpoint: null, requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerName"] = "palworld-server",
            }),
        ]);

        var resolver = new ComposeWriteModeResolver(grants);

        resolver.Resolve(ComposeDescriptor("palworld-server")).Should().Be(WriteMode.Enabled);
        resolver.Resolve(ComposeDescriptor("some-other-server")).Should().Be(WriteMode.ReadOnly,
            "a grant for one server must never widen writes to the compose directory of another");
    }

    [Fact]
    public void A_descriptor_naming_no_container_resolves_read_only()
    {
        var grants = new GrantedWriteModeResolver(
        [
            new WriteModeGrant(WriteMode.Enabled, "docker", endpoint: null, requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerName"] = "palworld-server",
            }),
        ]);

        var resolver = new ComposeWriteModeResolver(grants);

        resolver.Resolve(ComposeDescriptor(containerName: null)).Should().Be(WriteMode.ReadOnly,
            "fail closed — a session this resolver cannot attribute to a server must never be writable");
    }
}
