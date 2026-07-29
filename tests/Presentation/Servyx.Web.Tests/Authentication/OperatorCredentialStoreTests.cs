using System.Text;
using Servyx.Web.Authentication;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// What actually reaches storage, and what the first-run flow will and will not do a second time.
/// </summary>
public class OperatorCredentialStoreTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public void TheUrnFollowsTheEstablishedSecretConvention()
    {
        OperatorCredentialStore.PasswordUrn.Value
            .Should().Be("secret://global/servyx/auth/operator-password");

        OperatorCredentialStore.PasswordUrn.Scope.Should().Be("global");
        OperatorCredentialStore.PasswordUrn.ScopeId.Should().Be("servyx");
        OperatorCredentialStore.PasswordUrn.Category.Should().Be("auth");
        OperatorCredentialStore.PasswordUrn.Name.Should().Be("operator-password");
    }

    [Fact]
    public async Task AFreshInstall_HasNoPassword_AndAuthenticatesNobody()
    {
        var store = new OperatorCredentialStore(new RecordingSecretStore());

        (await store.IsPasswordSetAsync()).Should().BeFalse();
        (await store.VerifyPasswordAsync(Password)).Should().BeFalse(
            "an install nobody has bootstrapped must not accept any password at all");
        (await store.VerifyPasswordAsync("")).Should().BeFalse();
        (await store.VerifyPasswordAsync(null)).Should().BeFalse();
    }

    [Fact]
    public async Task TheCorrectPasswordAuthenticates_AndAnIncorrectOneDoesNot()
    {
        var store = new OperatorCredentialStore(new RecordingSecretStore());

        (await store.TrySetInitialPasswordAsync(Password)).Should().BeTrue();

        (await store.IsPasswordSetAsync()).Should().BeTrue();
        (await store.VerifyPasswordAsync(Password)).Should().BeTrue();
        (await store.VerifyPasswordAsync("not-the-operator-password")).Should().BeFalse();
        (await store.VerifyPasswordAsync(Password + " ")).Should().BeFalse();
    }

    [Fact]
    public async Task WhatIsPersisted_IsAVerifier_AndContainsNoPlaintextAnywhere()
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        await store.TrySetInitialPasswordAsync(Password);

        secrets.SetCalls.Should().Be(1);
        secrets.Writes.Should().ContainSingle();

        var written = secrets.Writes[0];

        // As text, as raw bytes, and as any six-character run of it: the password is not in there.
        var asText = Encoding.UTF8.GetString(written);
        asText.Should().StartWith("PBKDF2-SHA256$");
        asText.Should().NotContain(Password);

        for (var start = 0; start + 6 <= Password.Length; start++)
        {
            asText.Should().NotContain(Password.Substring(start, 6));
        }

        IndexOf(written, Encoding.UTF8.GetBytes(Password)).Should().Be(
            -1, "the plaintext must not appear in the persisted bytes in any form");

        // And it is genuinely a verifier for that password, not merely something that omits it.
        OperatorPasswordHash.Verify(asText, Password).Should().BeTrue();
        secrets.LastActor.Should().NotBeNullOrWhiteSpace("a secret write is an audit event and names an actor");
    }

    [Fact]
    public async Task TheFirstRunFlow_IsOneTime_AndNeverGrantsAccessAgain()
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        (await store.TrySetInitialPasswordAsync(Password)).Should().BeTrue();

        // The whole point: once a password exists, the bootstrap path is not a way in. It refuses, it writes
        // nothing, and — critically — the original password still works afterwards.
        (await store.TrySetInitialPasswordAsync("attacker-chosen-password")).Should().BeFalse();

        secrets.SetCalls.Should().Be(1, "a refused bootstrap must not touch the store at all");
        (await store.VerifyPasswordAsync("attacker-chosen-password")).Should().BeFalse();
        (await store.VerifyPasswordAsync(Password)).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentFirstRunAttempts_ProduceExactlyOnePassword()
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        var candidates = Enumerable.Range(0, 8).Select(i => $"bootstrap-candidate-{i}").ToArray();
        var results = await Task.WhenAll(candidates.Select(c => store.TrySetInitialPasswordAsync(c)));

        results.Count(won => won).Should().Be(1, "the bootstrap is one-time even under a race");
        secrets.SetCalls.Should().Be(1);

        var accepted = await Task.WhenAll(candidates.Select(c => store.VerifyPasswordAsync(c)));
        accepted.Count(ok => ok).Should().Be(1);
    }

    [Fact]
    public async Task ChangingThePassword_RequiresTheCurrentOne()
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        await store.TrySetInitialPasswordAsync(Password);

        (await store.ChangePasswordAsync("not-the-current-one", "brand-new-password")).Should().BeFalse();
        secrets.SetCalls.Should().Be(1, "a refused change must not write");
        (await store.VerifyPasswordAsync(Password)).Should().BeTrue();

        (await store.ChangePasswordAsync(Password, "brand-new-password")).Should().BeTrue();
        (await store.VerifyPasswordAsync("brand-new-password")).Should().BeTrue();
        (await store.VerifyPasswordAsync(Password)).Should().BeFalse();
    }

    [Fact]
    public async Task ChangingThePasswordBeforeOneExists_IsRefused()
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        (await store.ChangePasswordAsync("anything at all", "brand-new-password")).Should().BeFalse();
        secrets.SetCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task APasswordShorterThanTheMinimum_IsRefused(string candidate)
    {
        var secrets = new RecordingSecretStore();
        var store = new OperatorCredentialStore(secrets);

        var act = async () => await store.TrySetInitialPasswordAsync(candidate);

        await act.Should().ThrowAsync<ArgumentException>();
        secrets.SetCalls.Should().Be(0);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
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
                return i;
            }
        }

        return -1;
    }
}
