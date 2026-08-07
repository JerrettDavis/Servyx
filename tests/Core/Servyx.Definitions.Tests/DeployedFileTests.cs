using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Covers <c>deployments[].files[]</c>, the optional list of files Servyx materializes into a deployment's
/// storage before its workload first starts.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture here is the real, shipped <c>definitions/palworld-docker.yaml</c> with a single
/// <c>files</c> block spliced into its first deployment profile — the same
/// <see cref="DefinitionYamlFixture.Mutate"/> approach <see cref="SemanticRuleTests"/> uses — so each test
/// isolates exactly the rule under test against a document that is otherwise entirely valid. In particular
/// the secret key these fixtures reference, <c>admin-password</c>, is a real declared settings item in that
/// file, so a test asserting "an undeclared key is rejected" is genuinely testing the declaration check and
/// not simply that the surrounding document is broken.
/// </para>
/// <para>
/// The path rules get a hostile-input theory rather than one test per shape deliberately: the field names a
/// destination Servyx writes bytes to on behalf of an untrusted definition file, so the interesting
/// question is not whether one representative escape is caught but whether the whole family is.
/// </para>
/// </remarks>
public class DeployedFileTests
{
    /// <summary>The anchor spliced against: the first deployment profile's last scalar field before its <c>config</c> block.</summary>
    private const string Anchor = "    stopTimeout: 60s";

    private static DefinitionParseResult ParseYaml(string yaml) => new GameDefinitionYamlParser().Parse(yaml);

    /// <summary>Splices a single-entry <c>files</c> list, written as one YAML flow mapping, into the first deployment profile.</summary>
    private static string WithFileEntry(string flowMapping) =>
        DefinitionYamlFixture.Mutate(Anchor, $"{Anchor}\n    files:\n      - {flowMapping}");

    private static string WithPath(string path) =>
        WithFileEntry($"{{ path: '{path}', contentFrom: 'secret:admin-password' }}");

    private static IReadOnlyList<ValidationIssue> ErrorsOf(DefinitionParseResult result) =>
        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

    private static void AssertError(DefinitionParseResult result, string messageContains)
    {
        result.Report.IsValid.Should().BeFalse();
        ErrorsOf(result).Should().Contain(i => i.Message.Contains(messageContains, StringComparison.Ordinal));
    }

    // -- The happy path, and the defaults ---------------------------------------------------------------

    [Fact]
    public void FilesEntry_FullyDeclared_ParsesWithNoErrors()
    {
        var result = ParseYaml(WithFileEntry(
            "{ path: '${DATA_DIR}/config/rconpw', mode: '0600', createOnly: true, contentFrom: 'secret:admin-password' }"));

        ErrorsOf(result).Should().BeEmpty();

        var file = result.Definition!.Deployments[0].Files.Should().ContainSingle().Subject;
        file.Path.Should().Be("${DATA_DIR}/config/rconpw");
        file.Mode.Should().Be("0600");
        file.CreateOnly.Should().BeTrue();
        file.ContentFrom.Should().Be("secret:admin-password");
        file.Content.Should().BeNull();
    }

    [Fact]
    public void FilesEntry_OmittingModeAndCreateOnly_DefaultsToOwnerOnlyAndCreateOnly()
    {
        // The two defaults are the security posture of the whole feature: a seeded credential readable only
        // by its owner, and never re-written over live state on a subsequent provision.
        var result = ParseYaml(WithFileEntry("{ path: '${DATA_DIR}/config/rconpw', contentFrom: 'secret:admin-password' }"));

        ErrorsOf(result).Should().BeEmpty();

        var file = result.Definition!.Deployments[0].Files.Should().ContainSingle().Subject;
        file.Mode.Should().Be("0600");
        file.CreateOnly.Should().BeTrue();
    }

    [Fact]
    public void FilesEntry_WithLiteralContent_ParsesWithNoErrors()
    {
        var result = ParseYaml(WithFileEntry("{ path: '${COMPOSE_DIR}/marker.txt', content: 'managed by servyx' }"));

        ErrorsOf(result).Should().BeEmpty();

        var file = result.Definition!.Deployments[0].Files.Should().ContainSingle().Subject;
        file.Content.Should().Be("managed by servyx");
        file.ContentFrom.Should().BeNull();
    }

    [Fact]
    public void Files_IsOptional_SoADefinitionDeclaringNoneStillParses()
    {
        // The regression that matters most: every shipped definition predates this field.
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        ErrorsOf(result).Should().BeEmpty();
        result.Definition!.Deployments.Should().OnlyContain(d => d.Files.Count == 0);
    }

    // -- Path containment ------------------------------------------------------------------------------

    public static TheoryData<string> EscapingPaths() =>
    [
        // Plain traversal, in both separator flavours and in the middle of an otherwise-rooted path.
        "../etc/shadow",
        "${DATA_DIR}/../../etc/shadow",
        @"${DATA_DIR}\..\..\etc\shadow",
        "${DATA_DIR}/config/../../../etc/shadow",

        // Percent-encoded and mixed traversal — the shapes a segment-equality check does not see.
        "${DATA_DIR}/..%2f..%2fetc/shadow",
        "${DATA_DIR}/%2e%2e/%2e%2e/etc/shadow",
        "${DATA_DIR}/..%5c..%5cetc/shadow",

        // OS-absolute, POSIX and Windows shapes.
        "/etc/shadow",
        "/var/run/docker.sock",
        @"C:\Windows\System32\drivers\etc\hosts",
        @"D:/secrets/key.pem",

        // Rooted at nothing, or at a variable that is not a storage root.
        "config/rconpw",
        "${INSTANCE_ID}/config/rconpw",
        "${RCON_PORT}/config/rconpw",
    ];

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public void FilesEntryPath_ThatEscapesTheDeclaredRoots_IsError(string path)
    {
        var result = ParseYaml(WithPath(path));

        result.Report.IsValid.Should().BeFalse(
            "a seeded file path is a write destination taken from an untrusted definition");
        ErrorsOf(result).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("${DATA_DIR}/config/rconpw")]
    [InlineData("${data_dir}/config/rconpw")]
    [InlineData("${COMPOSE_DIR}/secrets/token")]
    [InlineData("${DATA_DIR}/config/${INSTANCE_ID}/rconpw")]
    public void FilesEntryPath_RootedAtAStorageRoot_IsAccepted(string path)
    {
        ErrorsOf(ParseYaml(WithPath(path))).Should().BeEmpty();
    }

    [Fact]
    public void FilesEntryPath_ThatIsEmpty_IsError()
    {
        // Reported by the parser's shared required-scalar helper, in its own wording, rather than by a
        // files-specific restatement of "must be non-empty".
        AssertError(ParseYaml(WithFileEntry("{ path: '', contentFrom: 'secret:admin-password' }")), "must not be blank");
    }

    [Fact]
    public void FilesEntry_WithNoPath_IsError()
    {
        AssertError(ParseYaml(WithFileEntry("{ contentFrom: 'secret:admin-password' }")), "'path'");
    }

    [Fact]
    public void FilesEntryPath_ReferencingAnUndeclaredVariable_IsErrorViaTheSharedDeferredCheck()
    {
        // Routed through the same PendingVariableRefs machinery every other path-like field uses, so the
        // wording and the severity come from ResolveDeferredChecks rather than from anything files-specific.
        AssertError(
            ParseYaml(WithPath("${DATA_DIR}/${NOT_A_THING}/rconpw")),
            "'${NOT_A_THING}' does not name a host-supplied variable");
    }

    // -- contentFrom / content -------------------------------------------------------------------------

    [Fact]
    public void FilesEntry_DeclaringBothContentAndContentFrom_IsError()
    {
        AssertError(
            ParseYaml(WithFileEntry(
                "{ path: '${DATA_DIR}/config/rconpw', content: 'literal', contentFrom: 'secret:admin-password' }")),
            "declares both 'content' and 'contentFrom'");
    }

    [Fact]
    public void FilesEntry_DeclaringNeitherContentNorContentFrom_IsError()
    {
        AssertError(
            ParseYaml(WithFileEntry("{ path: '${DATA_DIR}/config/rconpw' }")),
            "declares neither 'content' nor 'contentFrom'");
    }

    [Fact]
    public void FilesEntryContentFrom_NamingAnUndeclaredSecretKey_IsError()
    {
        AssertError(
            ParseYaml(WithFileEntry("{ path: '${DATA_DIR}/config/rconpw', contentFrom: 'secret:no-such-key' }")),
            "references secret key 'no-such-key'");
    }

    [Theory]
    [InlineData("env:ADMIN_PASSWORD")]
    [InlineData("file:/etc/shadow")]
    [InlineData("vault:kv/data/admin")]
    public void FilesEntryContentFrom_UsingAnySchemeOtherThanSecret_IsError(string reference)
    {
        // Delegated verbatim to ParseSecretRefValue — the same method a control channel's passwordRef uses —
        // so this asserts that reuse, not a second scheme check that happens to agree with it today.
        AssertError(
            ParseYaml(WithFileEntry($"{{ path: '${{DATA_DIR}}/config/rconpw', contentFrom: '{reference}' }}")),
            "only 'secret:' is accepted");
    }

    [Theory]
    [InlineData("admin-password")]
    [InlineData("secret:")]
    [InlineData(":admin-password")]
    public void FilesEntryContentFrom_ThatIsNotShapedSchemeColonKey_IsError(string reference)
    {
        AssertError(
            ParseYaml(WithFileEntry($"{{ path: '${{DATA_DIR}}/config/rconpw', contentFrom: '{reference}' }}")),
            "must be of the form 'scheme:key'");
    }

    // -- mode ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0600")]
    [InlineData("0400")]
    [InlineData("0644")]
    [InlineData("0777")]
    [InlineData("0000")]
    public void FilesEntryMode_ThatIsAFourDigitOctalString_IsAccepted(string mode)
    {
        var result = ParseYaml(WithFileEntry(
            $"{{ path: '${{DATA_DIR}}/config/rconpw', mode: '{mode}', contentFrom: 'secret:admin-password' }}"));

        ErrorsOf(result).Should().BeEmpty();
        result.Definition!.Deployments[0].Files[0].Mode.Should().Be(mode);
    }

    [Theory]
    [InlineData("600")]      // no leading zero
    [InlineData("00600")]    // too long
    [InlineData("0680")]     // '8' is not an octal digit
    [InlineData("0-60")]
    [InlineData("rw-------")]
    [InlineData("0o600")]
    [InlineData("")]
    public void FilesEntryMode_ThatIsNotAFourDigitOctalString_IsError(string mode)
    {
        AssertError(
            ParseYaml(WithFileEntry(
                $"{{ path: '${{DATA_DIR}}/config/rconpw', mode: '{mode}', contentFrom: 'secret:admin-password' }}")),
            "it must be a four-character octal string");
    }

    // -- Unknown keys ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("owner")]
    [InlineData("Path")]
    [InlineData("create_only")]
    [InlineData("contentfrom")]
    public void FilesEntry_WithAnUnknownKey_IsError(string key)
    {
        // The repo-wide closed-key convention has to hold inside this new block too: a misspelled
        // 'createOnly' that parsed as an unknown-but-tolerated key would silently seed with the default.
        AssertError(
            ParseYaml(WithFileEntry(
                $"{{ path: '${{DATA_DIR}}/config/rconpw', contentFrom: 'secret:admin-password', {key}: 'x' }}")),
            key);
    }
}
