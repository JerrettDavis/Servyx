using System.Reflection;
using System.Text;
using Servyx.Domain.Hosts;
using Servyx.Domain.Secrets;
using Servyx.Web.Hosts;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Hosts;

/// <summary>
/// What actually reaches storage when a private key (and optional passphrase) is imported at runtime, that
/// it lands at exactly the URNs <c>SshCredentialResolver</c> already knows how to read back, and that no
/// path through this store can ever put key material into a log or an exception message.
/// </summary>
/// <remarks>
/// Servyx.Web does not reference Servyx.Infrastructure.Ssh (see Servyx.Web.csproj), and
/// <c>SshCredentialResolver</c> is <c>internal</c> to that assembly with no <c>InternalsVisibleTo</c> for
/// this test project — so the round-trip tests below cannot call the resolver directly. Instead they assert
/// the exact contract the resolver is written against (see <c>SshCredentialResolver.ResolveAsync</c>'s
/// <c>switch</c> on <c>urn.Name</c>, and its later <c>credentials.Passphrase.ToUtf8String()</c> in
/// <c>SshConnector</c>): a URN whose <c>Name</c> is <c>"private-key"</c> or <c>"passphrase"</c>, resolved
/// through the same <see cref="ISecretStore.GetAsync"/> the resolver itself calls, with the passphrase
/// recoverable as UTF-8 text. Proving that contract through the real <see cref="ISecretStore"/> interface is
/// what makes this a genuine round-trip test rather than a check of this store's internals alone.
/// </remarks>
public sealed class SshHostCredentialStoreTests
{
    private const string HostKey = "my-remote-box";
    private const string Actor = "servyx.web/operator";

    [Fact]
    public void PrivateKeyUrn_And_PassphraseUrn_FollowTheDocumentedConvention()
    {
        // Exactly the shape docs/user-guide/adopting-a-remote-host.md shows for CredentialUrn.
        SshHostCredentialStore.PrivateKeyUrn(HostKey).Value
            .Should().Be("secret://connector/my-remote-box/ssh/private-key");
        SshHostCredentialStore.PassphraseUrn(HostKey).Value
            .Should().Be("secret://connector/my-remote-box/ssh/passphrase");

        var keyUrn = SshHostCredentialStore.PrivateKeyUrn(HostKey);
        keyUrn.Scope.Should().Be("connector");
        keyUrn.ScopeId.Should().Be(HostKey);
        keyUrn.Category.Should().Be("ssh");
        // SshCredentialResolver.ResolveAsync switches on exactly this string.
        keyUrn.Name.Should().Be("private-key");

        var passphraseUrn = SshHostCredentialStore.PassphraseUrn(HostKey);
        passphraseUrn.Name.Should().Be("passphrase");
    }

    [Fact]
    public async Task ImportPrivateKeyAsync_WritesTheExactKeyBytes_RetrievableAtTheResolverExpectedUrn()
    {
        var keyBytes = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\n-----END OPENSSH PRIVATE KEY-----\n"u8.ToArray();
        var secrets = new RecordingSecretStore();
        var store = new SshHostCredentialStore(secrets);

        var result = await store.ImportPrivateKeyAsync(HostKey, keyBytes, passphrase: null, Actor);

        result.PrivateKeyUrn.Should().Be(SshHostCredentialStore.PrivateKeyUrn(HostKey));
        result.PassphraseUrn.Should().BeNull();

        // The same GetAsync call SshCredentialResolver.ResolveAsync makes for the "private-key" case.
        using var lease = await secrets.GetAsync(result.PrivateKeyUrn);
        lease.Should().NotBeNull();
        lease!.Value.ToArray().Should().Equal(keyBytes, "a private key is whitespace- and newline-sensitive");

        (await secrets.ExistsAsync(SshHostCredentialStore.PassphraseUrn(HostKey))).Should().BeFalse();
    }

    [Fact]
    public async Task ImportPrivateKeyAsync_WithPassphrase_WritesBothAtSiblingUrns_RecoverableAsUtf8()
    {
        const string passphrase = "correct-horse-battery-staple";
        var keyBytes = "key-bytes-here"u8.ToArray();
        var secrets = new RecordingSecretStore();
        var store = new SshHostCredentialStore(secrets);

        var result = await store.ImportPrivateKeyAsync(HostKey, keyBytes, passphrase, Actor);

        result.PassphraseUrn.Should().Be(SshHostCredentialStore.PassphraseUrn(HostKey));

        // SshConnector recovers the passphrase via SecretLease.ToUtf8String() — prove that round-trips.
        using var lease = await secrets.GetAsync(result.PassphraseUrn!.Value);
        lease.Should().NotBeNull();
        lease!.ToUtf8String().Should().Be(passphrase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ImportPrivateKeyAsync_WithNoPassphrase_WritesNothingAtThePassphraseUrn(string? passphrase)
    {
        var secrets = new RecordingSecretStore();
        var store = new SshHostCredentialStore(secrets);

        var result = await store.ImportPrivateKeyAsync(HostKey, "key-bytes"u8.ToArray(), passphrase, Actor);

        result.PassphraseUrn.Should().BeNull();
        secrets.SetCalls.Should().Be(1, "only the private key should have been written");
    }

    [Fact]
    public async Task ImportPrivateKeyAsync_RecordsTheGivenActor_NotAHardcodedConstant()
    {
        var secrets = new RecordingSecretStore();
        var store = new SshHostCredentialStore(secrets);

        await store.ImportPrivateKeyAsync(HostKey, "key-bytes"u8.ToArray(), passphrase: null, "operator-alice");
        secrets.LastActor.Should().Be("operator-alice");

        await store.ImportPrivateKeyAsync("second-host", "key-bytes"u8.ToArray(), passphrase: null, "operator-bob");
        secrets.LastActor.Should().Be("operator-bob",
            "the actor must be whatever authenticated identity called this, not a fixed constant like SecretImport's startup actor");
    }

    [Fact]
    public async Task ImportPrivateKeyAsync_OverwritesAnExistingKey_UnlikeTheStartupImportPath()
    {
        var secrets = new RecordingSecretStore();
        var store = new SshHostCredentialStore(secrets);

        await store.ImportPrivateKeyAsync(HostKey, "original-key-bytes"u8.ToArray(), passphrase: null, Actor);
        var result = await store.ImportPrivateKeyAsync(HostKey, "rotated-key-bytes"u8.ToArray(), passphrase: null, Actor);

        secrets.SetCalls.Should().Be(2, "a runtime, operator-attributed re-submission is expected to replace what was there");

        using var lease = await secrets.GetAsync(result.PrivateKeyUrn);
        lease!.ToUtf8String().Should().Be("rotated-key-bytes");
    }

    [Fact]
    public async Task ImportPrivateKeyAsync_RejectsEmptyKeyBytes()
    {
        var store = new SshHostCredentialStore(new RecordingSecretStore());

        var act = async () => await store.ImportPrivateKeyAsync(HostKey, ReadOnlyMemory<byte>.Empty, passphrase: null, Actor);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ImportPrivateKeyAsync_RejectsAMissingActor(string? actor)
    {
        var store = new SshHostCredentialStore(new RecordingSecretStore());

        var act = async () => await store.ImportPrivateKeyAsync(HostKey, "key-bytes"u8.ToArray(), passphrase: null, actor!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsANullSecretStore()
    {
        var act = () => new SshHostCredentialStore(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Security discipline: no logging, no read-back ───────────────────────────────────────────────

    [Fact]
    public void TheStoreTakesNoLoggerDependency_SoNoCodePathInItCanLogAnything()
    {
        // The strongest possible guarantee against "a log call receives the raw key": there is no logger
        // anywhere in this type for such a call to go through, exactly like OperatorCredentialStore. Checked
        // across constructors, fields, and properties so a future edit that quietly wires one in fails this
        // test immediately.
        var type = typeof(SshHostCredentialStore);

        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                IsLoggingType(parameter.ParameterType).Should().BeFalse(
                    $"constructor parameter '{parameter.Name}' of type {parameter.ParameterType} would be a place a logger could be wired in");
            }
        }

        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            IsLoggingType(field.FieldType).Should().BeFalse(
                $"field '{field.Name}' of type {field.FieldType} would let this store log");
        }

        foreach (var property in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            IsLoggingType(property.PropertyType).Should().BeFalse(
                $"property '{property.Name}' of type {property.PropertyType} would let this store log");
        }
    }

    [Fact]
    public async Task WhenTheUnderlyingStoreThrows_TheExceptionMessageNeverContainsTheKeyOrPassphrase()
    {
        const string secretKeyText = "super-secret-private-key-material-that-must-never-appear-anywhere";
        const string secretPassphraseText = "super-secret-passphrase-material";
        var throwing = new ThrowingSecretStore();
        var store = new SshHostCredentialStore(throwing);

        var act = async () => await store.ImportPrivateKeyAsync(
            HostKey, Encoding.UTF8.GetBytes(secretKeyText), secretPassphraseText, Actor);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain(secretKeyText);
        thrown.Which.Message.Should().NotContain(secretPassphraseText);
        (thrown.Which.ToString()).Should().NotContain(secretKeyText);
        (thrown.Which.ToString()).Should().NotContain(secretPassphraseText);
    }

    [Fact]
    public void NoPublicMethodOnTheStore_ReturnsRawKeyOrPassphraseMaterial()
    {
        // A read-back path would be any public method returning byte[], ReadOnlyMemory<byte>, string
        // (other than a URN's own Value), or SecretLease. ImportPrivateKeyAsync's own return type
        // (SshHostCredentialImportResult) carries only SecretUrn values — locators, never secret bytes.
        var type = typeof(SshHostCredentialStore);
        var disallowedReturnShapes = new[]
        {
            typeof(byte[]), typeof(ReadOnlyMemory<byte>), typeof(SecretLease),
            typeof(Task<byte[]>), typeof(Task<SecretLease?>), typeof(Task<SecretLease>),
        };

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            disallowedReturnShapes.Should().NotContain(method.ReturnType,
                $"method '{method.Name}' returns {method.ReturnType}, which is a read-back-of-a-secret shape");
        }

        // And the result record itself: only SecretUrn-typed properties.
        foreach (var property in typeof(SshHostCredentialImportResult).GetProperties())
        {
            (property.PropertyType == typeof(SecretUrn) || property.PropertyType == typeof(SecretUrn?))
                .Should().BeTrue($"'{property.Name}' on the import result must be a locator, not secret material");
        }
    }

    /// <summary>
    /// The layering seam Increment 5 added: <c>Servyx.Application</c>'s host-registration use case reaches this
    /// Presentation-layer store through <see cref="IHostCredentialImporter"/>, a Domain-declared interface,
    /// because the dependency graph runs Web → Application → Domain and must never run back. This asserts the
    /// interface path is the same write path, landing at the same URNs with the same bytes — not a second
    /// implementation that could drift from the one <c>SshCredentialResolver</c> reads back.
    /// </summary>
    [Fact]
    public async Task TheDomainDeclaredImporterInterface_WritesTheSameBytesToTheSameUrns()
    {
        const string passphrase = "correct-horse-battery-staple";
        var keyBytes = "-----BEGIN OPENSSH PRIVATE KEY-----\nseam\n"u8.ToArray();
        var secrets = new RecordingSecretStore();
        IHostCredentialImporter importer = new SshHostCredentialStore(secrets);

        var result = await importer.ImportPrivateKeyAsync(HostKey, keyBytes, passphrase, Actor);

        result.PrivateKeyUrn.Should().Be(SshHostCredentialStore.PrivateKeyUrn(HostKey));
        result.PassphraseUrn.Should().Be(SshHostCredentialStore.PassphraseUrn(HostKey));

        using var lease = await secrets.GetAsync(result.PrivateKeyUrn);
        lease!.Value.ToArray().Should().Equal(keyBytes);

        using var passphraseLease = await secrets.GetAsync(result.PassphraseUrn!.Value);
        Encoding.UTF8.GetString(passphraseLease!.Value).Should().Be(passphrase);
    }

    /// <summary>The interface must reject exactly what the concrete method rejects — no widened surface.</summary>
    [Fact]
    public async Task TheDomainDeclaredImporterInterface_RejectsAnEmptyKeyAndABlankActorJustLikeTheConcreteMethod()
    {
        IHostCredentialImporter importer = new SshHostCredentialStore(new RecordingSecretStore());

        var emptyKey = () => importer.ImportPrivateKeyAsync(HostKey, ReadOnlyMemory<byte>.Empty, null, Actor);
        await emptyKey.Should().ThrowAsync<ArgumentException>();

        var blankActor = () => importer.ImportPrivateKeyAsync(HostKey, "key"u8.ToArray(), null, "   ");
        await blankActor.Should().ThrowAsync<ArgumentException>();
    }

    private static bool IsLoggingType(Type type)
        => type.FullName is not null
            && (type.FullName.StartsWith("Microsoft.Extensions.Logging.", StringComparison.Ordinal)
                || type.FullName.Contains("ILogger", StringComparison.Ordinal));

    /// <summary>An <see cref="ISecretStore"/> whose every write throws, to prove no exception message this
    /// store constructs can ever carry secret bytes even when the underlying store fails.</summary>
    private sealed class ThrowingSecretStore : ISecretStore
    {
        public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default) => Task.FromResult(false);

        public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
            => Task.FromResult<SecretLease?>(null);

        public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
            => throw new InvalidOperationException($"Simulated storage failure writing '{urn}'.");

        public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SecretUrn>>([]);
    }
}
