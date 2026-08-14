using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Covers the two-key mirrored-write opt-in: <c>mirrorWrites: true</c> on a derived
/// <c>config.surfaces</c> entry, and <c>mirrorWrite: true</c> on an individual settings binding to it.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture is the real, shipped <c>definitions/palworld-docker.yaml</c> with one targeted
/// substitution, following <see cref="DeployedFileTests"/> and <see cref="SemanticRuleTests"/>: the shipped
/// file already declares both halves, so a test asserting "this combination is refused" is genuinely
/// exercising the rule rather than tripping over an otherwise-broken document.
/// </para>
/// <para>
/// <strong>The sensitivity rules are the security-relevant ones here.</strong> They are enforced twice, and
/// both are tested: the parser refuses the declaration at authoring time, and
/// <see cref="SettingDescriptor.MirroredBindings"/> refuses the descriptor at plan time. Neither is
/// sufficient alone — the parser cannot see a definition swapped underneath a running server, and the
/// descriptor cannot tell an author they have written something that will never work.
/// </para>
/// </remarks>
public class MirroredWriteTests
{
    private static DefinitionParseResult ParseYaml(string yaml) => new GameDefinitionYamlParser().Parse(yaml);

    private static IReadOnlyList<ValidationIssue> ErrorsOf(DefinitionParseResult result) =>
        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

    private static void AssertError(DefinitionParseResult result, string messageContains)
    {
        result.Report.IsValid.Should().BeFalse();
        ErrorsOf(result).Should().Contain(i => i.Message.Contains(messageContains, StringComparison.Ordinal));
    }

    private static DeclaredConfigSurface DockerSurface(DefinitionParseResult result, string id) =>
        result.Definition!.Deployments
            .Single(d => d.Id == "docker-thijsvanloef")
            .Surfaces.Single(s => s.Id == id);

    private static SettingDescriptor Setting(DefinitionParseResult result, string key) =>
        result.Definition!.Settings.SelectMany(g => g.Items).Single(s => s.Key == key);

    // -- The shipped definition, as it now stands -------------------------------------------------------

    [Fact]
    public void TheShippedDefinition_DeclaresTheDerivedSurfaceAsMirrorAccepting()
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        ErrorsOf(result).Should().BeEmpty();

        var surface = DockerSurface(result, "palworldsettings");

        // The role is unchanged and is still the truth: the entrypoint really does regenerate this file.
        surface.Role.Should().Be(SurfaceRole.Derived);
        surface.MirrorWrites.Should().BeTrue();
    }

    [Fact]
    public void TheShippedDefinition_LeavesTheAuthoritativeAndRuntimeSurfacesAlone()
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        DockerSurface(result, "env").MirrorWrites.Should().BeFalse();
        DockerSurface(result, "compose").MirrorWrites.Should().BeFalse();
        DockerSurface(result, "live").MirrorWrites.Should().BeFalse();
    }

    [Theory]
    [InlineData("SERVER_NAME")]
    [InlineData("SERVER_DESCRIPTION")]
    [InlineData("PLAYERS")]
    [InlineData("DIFFICULTY")]
    [InlineData("DAY_TIME_SPEEDRATE")]
    [InlineData("ENABLE_PLAYER_TO_PLAYER_DAMAGE")]
    public void TheShippedDefinition_OptsTheseSettingsIn(string key)
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        var setting = Setting(result, key);

        setting.MirroredBindings.Should().ContainSingle()
            .Which.SurfaceId.Should().Be("palworldsettings");
    }

    [Theory]
    [InlineData("admin-password")]
    [InlineData("server-password")]
    public void TheShippedDefinition_NeverOptsASecretIn(string key)
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        var setting = Setting(result, key);

        setting.IsSecret.Should().BeTrue();
        setting.Bindings.Should().NotContain(b => b.MirrorWrite);
        setting.MirroredBindings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PORT")]
    [InlineData("RCON_PORT")]
    public void TheShippedDefinition_DoesNotMirrorASettingThatRequiresARecreate(string key)
    {
        // Nothing forbids it; it would simply be a write with no benefit, because the container is coming
        // down and the file is regenerated from '.env' on the way back up.
        ParseYaml(DefinitionYamlFixture.RealYaml).Definition!.Settings
            .SelectMany(g => g.Items).Single(s => s.Key == key)
            .MirroredBindings.Should().BeEmpty();
    }

    // -- The surface half of the gate -------------------------------------------------------------------

    [Fact]
    public void ASurfaceDeclaringMirrorWrites_MustBeDerived()
    {
        // 'env' is the authoritative surface: it is already written through the ordinary path, and a second,
        // differently-gated route to the same file is not something to acquire by declaring a flag.
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "        - id: env\n          role: authoritative",
            "        - id: env\n          role: authoritative\n          mirrorWrites: true"));

        AssertError(result, "only a 'derived' surface may accept mirrored writes");
    }

    [Fact]
    public void ASurfaceDeclaringMirrorWrites_MustNotBeRuntime()
    {
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "        - id: live\n          role: runtime",
            "        - id: live\n          role: runtime\n          mirrorWrites: true"));

        AssertError(result, "only a 'derived' surface may accept mirrored writes");
    }

    [Fact]
    public void ASurfaceOmittingTheFlag_DefaultsToRefusingMirroredWrites()
    {
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "          role: derived\n          mirrorWrites: true\n",
            "          role: derived\n"));

        // Every binding that opted in now points at a surface that accepts none, and each one says so.
        result.Report.IsValid.Should().BeFalse();
        ErrorsOf(result).Should().Contain(i =>
            i.Message.Contains("no deployment declares with 'mirrorWrites: true'", StringComparison.Ordinal));
    }

    // -- The binding half of the gate -------------------------------------------------------------------

    [Fact]
    public void ABindingDeclaringMirrorWrite_MustNotBeSensitive()
    {
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "{ surface: palworldsettings, direction: read, member: ServerPassword, sensitive: true }",
            "{ surface: palworldsettings, direction: read, member: ServerPassword, sensitive: true, mirrorWrite: true }"));

        AssertError(result, "A sensitive value is never mirrored");
    }

    [Fact]
    public void ASecretTypedSetting_MayNotOptInEvenOnANonSensitiveBinding()
    {
        // The descriptor-level rule, distinct from the per-binding one: 'type: secret' makes the WHOLE
        // setting secret whether or not any single binding says so, and a binding without 'sensitive: true'
        // would slip past the per-binding check alone.
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "{ surface: palworldsettings, direction: read, member: AdminPassword, sensitive: true }",
            "{ surface: palworldsettings, direction: read, member: AdminPassword, mirrorWrite: true }"));

        AssertError(result, "A secret is never mirrored onto a derived surface");
    }

    [Fact]
    public void ABindingDeclaringMirrorWrite_MustBeARead()
    {
        // A 'write' binding already writes its surface directly. Mirroring a write onto itself is either a
        // duplicate or a misunderstanding, and neither should parse.
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "{ surface: env, direction: write, key: DIFFICULTY }",
            "{ surface: env, direction: write, key: DIFFICULTY, mirrorWrite: true }"));

        AssertError(result, "a mirrored write applies only to a 'read' binding");
    }

    [Fact]
    public void ABindingMayOptIn_EvenThoughAnotherProfileDeclaresTheSameSurfaceAuthoritative()
    {
        // Palworld's own shape, and the reason the surface check is "at least one profile accepts mirrored
        // writes" rather than "every one does": 'palworldsettings' is derived under the docker profile and
        // is the AUTHORITATIVE surface under native-steamcmd, where mirroring is meaningless rather than
        // wrong. Settings bindings are not profile-scoped, so requiring every declaration to opt in would
        // make the flag unusable on exactly the kind of definition it exists for.
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        ErrorsOf(result).Should().BeEmpty();

        result.Definition!.Deployments
            .Single(d => d.Id == "native-steamcmd")
            .Surfaces.Single(s => s.Id == "palworldsettings")
            .Role.Should().Be(SurfaceRole.Authoritative);
    }

    [Fact]
    public void AnUnknownKeyOnABinding_IsStillRejected()
    {
        // The allow-list gained one key, not a hole.
        var result = ParseYaml(DefinitionYamlFixture.Mutate(
            "{ surface: palworldsettings, direction: read, mirrorWrite: true, member: Difficulty }",
            "{ surface: palworldsettings, direction: read, mirrorWrites: true, member: Difficulty }"));

        AssertError(result, "mirrorWrites");
    }

    // -- The descriptor-level exclusion, independent of any parser ---------------------------------------

    [Fact]
    public void MirroredBindings_ExcludesEveryBinding_WhenTheDescriptorIsSecretTyped()
    {
        var descriptor = Descriptor(
            SettingType.Secret,
            new SettingBinding.ByMember("s", BindingDirection.Read, false, "AdminPassword", false)
            {
                MirrorWrite = true,
            });

        descriptor.IsSecret.Should().BeTrue();
        descriptor.MirroredBindings.Should().BeEmpty();
    }

    [Fact]
    public void MirroredBindings_ExcludesEveryBinding_WhenAnyBindingIsMarkedSensitive()
    {
        // A setting can be sensitive without being secret-typed, and the exclusion has to follow the value
        // rather than the type — otherwise a masked value could be mirrored simply by typing it 'string'.
        var descriptor = Descriptor(
            SettingType.String,
            new SettingBinding.ByMember("s", BindingDirection.Read, false, "Difficulty", false)
            {
                MirrorWrite = true,
            },
            new SettingBinding.ByMember("s", BindingDirection.Read, true, "AdminPassword", false));

        descriptor.IsSecret.Should().BeTrue();
        descriptor.MirroredBindings.Should().BeEmpty();
    }

    [Fact]
    public void MirroredBindings_IsEmpty_ForASettingThatDeclaresNoFlag()
    {
        Descriptor(
            SettingType.String,
            new SettingBinding.ByMember("s", BindingDirection.Read, false, "Difficulty", false))
            .MirroredBindings.Should().BeEmpty();
    }

    private static SettingDescriptor Descriptor(SettingType type, params SettingBinding[] bindings) => new(
        "k",
        "k",
        "General",
        type,
        Required: false,
        Default: null,
        RenderFormat: null,
        RequiresRecreate: false,
        PublishByDefault: null,
        new SettingConstraints(null, null, null, null, null, null, null, null, null),
        bindings);
}
