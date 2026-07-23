using System.Text;
using FluentAssertions;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Secrets;

namespace Servyx.Infrastructure.Tests.Secrets;

public class DataProtectionSecretStoreTests
{
    private static DataProtectionSecretStore CreateStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "servyx-secrets-" + Guid.NewGuid().ToString("N"));
        return new DataProtectionSecretStore(new SecretsOptions { SecretsRootDirectory = root });
    }

    [Fact]
    public async Task SetGetExistsDelete_RoundTrips()
    {
        var store = CreateStore(out _);
        var urn = SecretUrn.Create("server", "palworld-01", "rcon", "password");

        (await store.ExistsAsync(urn)).Should().BeFalse();
        (await store.GetAsync(urn)).Should().BeNull();

        await store.SetAsync(urn, Encoding.UTF8.GetBytes("s3cr3t-value"), "alice");

        (await store.ExistsAsync(urn)).Should().BeTrue();

        using (var lease = await store.GetAsync(urn))
        {
            lease.Should().NotBeNull();
            lease!.ToUtf8String().Should().Be("s3cr3t-value");
        }

        await store.DeleteAsync(urn, "alice");

        (await store.ExistsAsync(urn)).Should().BeFalse();
        (await store.GetAsync(urn)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        var store = CreateStore(out _);
        var urn = SecretUrn.Create("server", "palworld-01", "rcon", "password");

        await store.SetAsync(urn, Encoding.UTF8.GetBytes("first"), "alice");
        await store.SetAsync(urn, Encoding.UTF8.GetBytes("second"), "alice");

        using var lease = await store.GetAsync(urn);
        lease!.ToUtf8String().Should().Be("second");
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyUrnsUnderRequestedScope()
    {
        var store = CreateStore(out _);
        var passwordUrn = SecretUrn.Create("server", "srv1", "rcon", "password");
        var keyUrn = SecretUrn.Create("server", "srv1", "ssh", "key");
        var otherServerUrn = SecretUrn.Create("server", "srv2", "rcon", "password");

        await store.SetAsync(passwordUrn, new byte[] { 1 }, "actor");
        await store.SetAsync(keyUrn, new byte[] { 2 }, "actor");
        await store.SetAsync(otherServerUrn, new byte[] { 3 }, "actor");

        var list = await store.ListAsync("server", "srv1");

        list.Should().BeEquivalentTo([passwordUrn, keyUrn]);
    }

    [Fact]
    public async Task ListAsync_UnknownScope_ReturnsEmpty()
    {
        var store = CreateStore(out _);

        var list = await store.ListAsync("server", "does-not-exist");

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_FileOnDisk_DoesNotContainPlaintext()
    {
        var store = CreateStore(out var root);
        var urn = SecretUrn.Create("server", "palworld-01", "rcon", "password");
        const string plaintext = "hunter2-super-secret-value-xyz";

        await store.SetAsync(urn, Encoding.UTF8.GetBytes(plaintext), "actor");

        var file = Directory.EnumerateFiles(root, "*.secret", SearchOption.AllDirectories).Single();
        var rawBytes = await File.ReadAllBytesAsync(file);

        rawBytes.Should().NotBeEmpty();

        var rawText = Encoding.UTF8.GetString(rawBytes);
        rawText.Should().NotContain(plaintext);

        // Also check the raw bytes directly (not just as UTF-8 text), in case the ciphertext's base64
        // happened to decode to something UTF-8 would mangle away.
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        ContainsSubsequence(rawBytes, plaintextBytes).Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_UrnWithTraversalLikeButValidSegment_StaysWithinRoot()
    {
        var store = CreateStore(out var root);
        // ".." itself is rejected by SecretUrn (an all-dots segment), but a segment merely *starting* with
        // dots is a legitimately valid name and must still be safely contained under root.
        var urn = SecretUrn.Create("server", "srv1", "rcon", "..suspicious-name");

        await store.SetAsync(urn, new byte[] { 1, 2, 3 }, "actor");

        var fullRoot = Path.GetFullPath(root);
        var allFiles = Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories);

        allFiles.Should().OnlyContain(f =>
            Path.GetFullPath(f).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase));

        // Nothing should have leaked out to a sibling of the root (e.g. a directory literally named
        // "..suspicious-name" created next to root, which is what a naive Path.Combine could produce).
        var parent = Directory.GetParent(fullRoot)!.FullName;
        var suspiciousSibling = Path.Combine(parent, "suspicious-name");
        Directory.Exists(suspiciousSibling).Should().BeFalse();
        File.Exists(suspiciousSibling).Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_DefaultUrn_ThrowsArgumentException()
    {
        var store = CreateStore(out _);

        var act = async () => await store.SetAsync(default, new byte[] { 1 }, "actor");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetAsync_NullOrWhitespaceActor_Throws()
    {
        var store = CreateStore(out _);
        var urn = SecretUrn.Create("server", "srv1", "rcon", "password");

        var act = async () => await store.SetAsync(urn, new byte[] { 1 }, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
