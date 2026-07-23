using FluentAssertions;
using Servyx.Domain.Connectors;
using Servyx.Infrastructure.Connectors;

namespace Servyx.Infrastructure.Tests.Connectors;

public class FileHostKeyStoreTests
{
    private static string MakeFilePath() =>
        Path.Combine(Path.GetTempPath(), "servyx-hostkeys-" + Guid.NewGuid().ToString("N") + ".json");

    private static HostKeyRecord MakeRecord(string host = "10.0.0.4", int port = 22, string fingerprint = "SHA256:aaaa") =>
        new(host, port, "ssh-ed25519", fingerprint, [1, 2, 3, 4], DateTimeOffset.UtcNow, "alice");

    [Fact]
    public async Task FindAsync_NeverPinned_ReturnsNull()
    {
        var store = new FileHostKeyStore(MakeFilePath());

        var found = await store.FindAsync("10.0.0.4", 22);

        found.Should().BeNull();
    }

    [Fact]
    public async Task PinAsync_ThenFindAsync_ReturnsSameRecord()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        var record = MakeRecord();

        await store.PinAsync(record, "alice");
        var found = await store.FindAsync(record.Host, record.Port);

        found.Should().NotBeNull();
        found!.Host.Should().Be(record.Host);
        found.Port.Should().Be(record.Port);
        found.Algorithm.Should().Be(record.Algorithm);
        found.Sha256Fingerprint.Should().Be(record.Sha256Fingerprint);
        found.PublicKeyBlob.Should().Equal(record.PublicKeyBlob);
        found.PinnedByActor.Should().Be("alice");
    }

    [Fact]
    public async Task PinAsync_Twice_ReplacesPreviousRecord()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        await store.PinAsync(MakeRecord(fingerprint: "SHA256:first"), "alice");
        await store.PinAsync(MakeRecord(fingerprint: "SHA256:second"), "bob");

        var found = await store.FindAsync("10.0.0.4", 22);

        found!.Sha256Fingerprint.Should().Be("SHA256:second");
        found.PinnedByActor.Should().Be("bob");
    }

    [Fact]
    public async Task RevokeAsync_PreviouslyPinnedHost_FindReturnsNullAndIsRevokedTrue()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        await store.PinAsync(MakeRecord(), "alice");

        await store.RevokeAsync("10.0.0.4", 22, "security-team");

        (await store.FindAsync("10.0.0.4", 22)).Should().BeNull();
        (await store.IsRevokedAsync("10.0.0.4", 22)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAsync_HostNeverPinned_StillMarksRevoked()
    {
        var store = new FileHostKeyStore(MakeFilePath());

        await store.RevokeAsync("10.0.0.99", 22, "security-team");

        (await store.IsRevokedAsync("10.0.0.99", 22)).Should().BeTrue();
        (await store.FindAsync("10.0.0.99", 22)).Should().BeNull();
    }

    [Fact]
    public async Task IsRevokedAsync_NeverTouchedHost_ReturnsFalse()
    {
        var store = new FileHostKeyStore(MakeFilePath());

        (await store.IsRevokedAsync("10.0.0.4", 22)).Should().BeFalse();
    }

    [Fact]
    public async Task PinAsync_AfterRevoke_ClearsRevocation()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        await store.PinAsync(MakeRecord(), "alice");
        await store.RevokeAsync("10.0.0.4", 22, "security-team");

        await store.PinAsync(MakeRecord(fingerprint: "SHA256:re-pinned"), "alice");

        (await store.IsRevokedAsync("10.0.0.4", 22)).Should().BeFalse();
        var found = await store.FindAsync("10.0.0.4", 22);
        found!.Sha256Fingerprint.Should().Be("SHA256:re-pinned");
    }

    [Fact]
    public async Task HostLookup_IsCaseInsensitive()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        await store.PinAsync(MakeRecord(host: "MyHost.Example.com"), "alice");

        var found = await store.FindAsync("myhost.example.com", 22);

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task State_PersistsAcrossStoreInstancesOverSameFile()
    {
        var path = MakeFilePath();
        var first = new FileHostKeyStore(path);
        await first.PinAsync(MakeRecord(), "alice");

        var second = new FileHostKeyStore(path);
        var found = await second.FindAsync("10.0.0.4", 22);

        found.Should().NotBeNull();
        found!.PinnedByActor.Should().Be("alice");
    }

    [Fact]
    public async Task ConcurrentPinAsync_ForDifferentHosts_AllPersist()
    {
        var store = new FileHostKeyStore(MakeFilePath());

        var tasks = Enumerable.Range(0, 20)
            .Select(i => store.PinAsync(MakeRecord(host: $"host-{i}", fingerprint: $"SHA256:fp{i}"), "actor"));

        await Task.WhenAll(tasks);

        for (var i = 0; i < 20; i++)
        {
            var found = await store.FindAsync($"host-{i}", 22);
            found.Should().NotBeNull();
        }
    }
}
