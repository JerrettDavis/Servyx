using Servyx.Domain.Connectors;
using Servyx.Infrastructure.Connectors;

namespace Servyx.Infrastructure.Tests.Connectors;

public class HostKeyVerifierTests
{
    // A real ed25519 key pair generated with `ssh-keygen -t ed25519 -N "" -f id_test`. The blob below is the
    // base64 field from the resulting id_test.pub ("ssh-ed25519 <blob> comment"). The expected fingerprint
    // is the exact output of `ssh-keygen -lf id_test.pub` (equivalently `ssh-keygen -E sha256 -lf id_test.pub`,
    // since SHA256 is OpenSSH's default fingerprint hash), confirming this codebase's fingerprint format is
    // byte-for-byte what OpenSSH itself prints and a user could eyeball-compare.
    private const string KnownVectorBlobBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIDuoMVMc6NUIqgSNWEFM/iXtcYNEDVEIN8fvTa2KJ7Sq";
    private const string KnownVectorExpectedFingerprint = "SHA256:0FSHZRVpj1KN4haa0Dnpy1LjZuMt9o+nYk2GpXbF5oo";

    private static string MakeFilePath() =>
        Path.Combine(Path.GetTempPath(), "servyx-hostkeys-" + Guid.NewGuid().ToString("N") + ".json");

    private static (HostKeyVerifier Verifier, IHostKeyStore Store) CreateVerifier()
    {
        var store = new FileHostKeyStore(MakeFilePath());
        return (new HostKeyVerifier(store), store);
    }

    [Fact]
    public void FingerprintFormat_MatchesKnownOpenSshVector()
    {
        var blob = Convert.FromBase64String(KnownVectorBlobBase64);

        var fingerprint = HostKeyFingerprint.ComputeSha256(blob);

        fingerprint.Should().Be(KnownVectorExpectedFingerprint);
    }

    [Fact]
    public async Task VerifyAsync_RequirePinned_UnknownHost_ReturnsUnknown()
    {
        var (verifier, _) = CreateVerifier();

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", [1, 2, 3], new TrustPolicy.RequirePinned());

        verdict.Should().Be(HostKeyVerdict.Unknown);
    }

    [Fact]
    public async Task VerifyAsync_TrustOnFirstUse_UnknownHost_ReturnsUnknown_AndDoesNotAutoPin()
    {
        var (verifier, store) = CreateVerifier();
        var blob = new byte[] { 1, 2, 3 };

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, new TrustPolicy.TrustOnFirstUse());

        verdict.Should().Be(HostKeyVerdict.Unknown);
        (await store.FindAsync("10.0.0.4", 22)).Should().BeNull("TOFU must never auto-pin on first sight");
    }

    [Fact]
    public async Task VerifyAsync_KnownHost_MatchingFingerprint_ReturnsTrusted()
    {
        var (verifier, store) = CreateVerifier();
        var blob = new byte[] { 10, 20, 30 };
        var fingerprint = HostKeyFingerprint.ComputeSha256(blob);
        await store.PinAsync(new HostKeyRecord("10.0.0.4", 22, "ssh-ed25519", fingerprint, blob, DateTimeOffset.UtcNow, "alice"), "alice");

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, new TrustPolicy.RequirePinned());

        verdict.Should().Be(HostKeyVerdict.Trusted);
    }

    [Fact]
    public async Task VerifyAsync_KnownHost_DifferentFingerprint_ReturnsChanged_AndDoesNotRePin()
    {
        var (verifier, store) = CreateVerifier();
        var originalBlob = new byte[] { 10, 20, 30 };
        var originalFingerprint = HostKeyFingerprint.ComputeSha256(originalBlob);
        await store.PinAsync(new HostKeyRecord("10.0.0.4", 22, "ssh-ed25519", originalFingerprint, originalBlob, DateTimeOffset.UtcNow, "alice"), "alice");

        var newBlob = new byte[] { 99, 98, 97 };

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", newBlob, new TrustPolicy.RequirePinned());

        verdict.Should().Be(HostKeyVerdict.Changed);

        // A changed key must never silently re-pin: the store still reports the original fingerprint.
        var stillPinned = await store.FindAsync("10.0.0.4", 22);
        stillPinned!.Sha256Fingerprint.Should().Be(originalFingerprint);
    }

    [Fact]
    public async Task VerifyAsync_RevokedHost_ReturnsRevoked_RegardlessOfPolicy()
    {
        var (verifier, store) = CreateVerifier();
        var blob = new byte[] { 10, 20, 30 };
        var fingerprint = HostKeyFingerprint.ComputeSha256(blob);
        await store.PinAsync(new HostKeyRecord("10.0.0.4", 22, "ssh-ed25519", fingerprint, blob, DateTimeOffset.UtcNow, "alice"), "alice");
        await store.RevokeAsync("10.0.0.4", 22, "security-team");

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, new TrustPolicy.RequirePinned());

        verdict.Should().Be(HostKeyVerdict.Revoked);
    }

    [Fact]
    public async Task VerifyAsync_PinnedFingerprints_Matching_ReturnsTrusted()
    {
        var (verifier, _) = CreateVerifier();
        var blob = new byte[] { 5, 6, 7 };
        var fingerprint = HostKeyFingerprint.ComputeSha256(blob);
        var policy = new TrustPolicy.PinnedFingerprints([fingerprint]);

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, policy);

        verdict.Should().Be(HostKeyVerdict.Trusted);
    }

    [Fact]
    public async Task VerifyAsync_PinnedFingerprints_NotMatching_ReturnsUnknown()
    {
        var (verifier, _) = CreateVerifier();
        var blob = new byte[] { 5, 6, 7 };
        var policy = new TrustPolicy.PinnedFingerprints(["SHA256:not-a-real-match"]);

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, policy);

        verdict.Should().Be(HostKeyVerdict.Unknown);
    }

    [Fact]
    public async Task VerifyAsync_RevokedHost_WinsOverPinnedFingerprintsPolicy()
    {
        var (verifier, store) = CreateVerifier();
        var blob = new byte[] { 5, 6, 7 };
        var fingerprint = HostKeyFingerprint.ComputeSha256(blob);
        await store.RevokeAsync("10.0.0.4", 22, "security-team");

        var verdict = await verifier.VerifyAsync("10.0.0.4", 22, "ssh-ed25519", blob, new TrustPolicy.PinnedFingerprints([fingerprint]));

        verdict.Should().Be(HostKeyVerdict.Revoked);
    }
}
