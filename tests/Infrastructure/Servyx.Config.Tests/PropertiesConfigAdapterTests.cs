using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

/// <summary>
/// Tests for <see cref="PropertiesConfigAdapter"/> — added alongside <see cref="SurfaceFormat.Properties"/>
/// for <c>definitions/minecraft-itzg.yaml</c>'s <c>server.properties</c> surface. Fixtures are inline
/// (rather than files under <c>Fixtures/</c>, like <see cref="DotEnvConfigAdapterTests"/> and
/// <see cref="IniConfigAdapterTests"/> use) since real <c>server.properties</c> content is short enough to
/// read directly in each test.
/// </summary>
public class PropertiesConfigAdapterTests
{
    [Fact]
    public void Parse_ReadsSimpleKeyValues_SkippingHashAndBangComments()
    {
        const string raw = "#Minecraft server properties\n!Another comment style\nmotd=A Minecraft Server\nmax-players=20\n\ndifficulty=easy\n";
        var document = new PropertiesConfigAdapter().Parse(raw);

        var values = ((PropertiesDocument)document.Root).Values;
        values.Should().Contain(new KeyValuePair<string, string>("motd", "A Minecraft Server"));
        values.Should().Contain(new KeyValuePair<string, string>("max-players", "20"));
        values.Should().Contain(new KeyValuePair<string, string>("difficulty", "easy"));
        values.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_SupportsDottedKeys_LikeRconPasswordAndRconPort()
    {
        const string raw = "rcon.password=s3cr3t\nrcon.port=25575\n";
        var document = new PropertiesConfigAdapter().Parse(raw);

        var values = ((PropertiesDocument)document.Root).Values;
        values["rcon.password"].Should().Be("s3cr3t");
        values["rcon.port"].Should().Be("25575");
    }

    [Fact]
    public void Parse_ValuesAreNeverQuoteStripped_UnlikeDotEnv()
    {
        // A '#' after a value is part of the value here — there is no inline-comment syntax in this format,
        // unlike DotEnvConfigAdapter.
        const string raw = "motd=\"quoted looking\" value #not a comment\n";
        var document = new PropertiesConfigAdapter().Parse(raw);

        var values = ((PropertiesDocument)document.Root).Values;
        values["motd"].Should().Be("\"quoted looking\" value #not a comment");
    }

    [Fact]
    public void Parse_DuplicateKeys_LastOccurrenceWinsForReads_BothSpansPreserved()
    {
        const string raw = "level-name=world\nlevel-name=world2\n";
        var document = new PropertiesConfigAdapter().Parse(raw);

        var values = ((PropertiesDocument)document.Root).Values;
        values["level-name"].Should().Be("world2");
        document.Spans.Count(s => s.Pointer == new ConfigPointer("level-name")).Should().Be(2);
    }

    [Fact]
    public void Render_RoundTripsUnmodifiedInput_ByteForByte()
    {
        const string raw = "#Minecraft server properties\nenable-rcon=true\nrcon.port=25575\n";
        var adapter = new PropertiesConfigAdapter();

        var document = adapter.Parse(raw);

        adapter.Render(document).Should().Be(raw);
    }

    [Fact]
    public void WithValue_ChangingOneKey_OnlyChangesThatKeysCharacters()
    {
        const string raw = "motd=old\nmax-players=20\n";
        var adapter = new PropertiesConfigAdapter();
        var document = adapter.Parse(raw);

        var edited = document.WithValue(new ConfigPointer("motd"), "new motd");

        adapter.Render(edited).Should().Be("motd=new motd\nmax-players=20\n");
    }

    [Fact]
    public void FormatId_IsProperties()
    {
        new PropertiesConfigAdapter().FormatId.Should().Be("properties");
    }
}
