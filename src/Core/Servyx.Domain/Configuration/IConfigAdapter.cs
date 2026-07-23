namespace Servyx.Domain.Configuration;

/// <summary>
/// Parses and renders a single configuration format. Round-trip fidelity is a hard contract:
/// <c>Render(Parse(x)) == x</c> byte-for-byte for unmodified input, including comments, blank lines, key
/// order, and quoting style.
/// </summary>
public interface IConfigAdapter
{
    /// <summary>Format identifier, e.g. "dotenv", "yaml", "ini", "json".</summary>
    string FormatId { get; }

    /// <summary>Whether this adapter preserves comments through a round-trip.</summary>
    bool PreservesComments { get; }

    /// <summary>Parses raw text into a <see cref="ConfigDocument"/>.</summary>
    ConfigDocument Parse(string raw);

    /// <summary>Renders a <see cref="ConfigDocument"/> back to text.</summary>
    string Render(ConfigDocument document);
}

/// <summary>
/// Decodes a structured payload embedded inside a single scalar value. The motivating example is
/// <c>unreal-option-settings</c>, which decodes Palworld's single-line
/// <c>OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,…)</c> blob into named members.
/// </summary>
public interface IConfigValueCodec
{
    /// <summary>Codec identifier, e.g. "unreal-option-settings".</summary>
    string CodecId { get; }

    /// <summary>Decodes a scalar into its structured member values, preserving member order for re-encoding.</summary>
    IReadOnlyDictionary<string, string> Decode(string scalar);

    /// <summary>
    /// Re-encodes structured member values back into scalar form, preserving member order and the
    /// source's numeric formatting — Unreal expects <c>1.000000</c>, not <c>1</c>.
    /// </summary>
    string Encode(IReadOnlyDictionary<string, string> members);
}
