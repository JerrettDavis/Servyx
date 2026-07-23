using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>
/// Decodes and re-encodes Palworld's single-line Unreal <c>OptionSettings=(Key=Value,Key2=Value2,...)</c>
/// scalar. Every member's raw text — including its quote characters, if any, and its exact numeric
/// formatting — is carried through untouched unless a caller explicitly replaces it, which is what makes
/// <c>1.000000</c> survive a decode/encode cycle without being renormalized to <c>1</c>.
/// </summary>
public sealed class UnrealOptionSettingsCodec : IConfigValueCodec
{
    /// <inheritdoc />
    public string CodecId => "unreal-option-settings";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Decode(string scalar)
    {
        ArgumentNullException.ThrowIfNull(scalar);

        var trimmed = scalar.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            throw new FormatException($"Expected a parenthesized Unreal struct value, got: '{scalar}'.");
        }

        var inner = trimmed[1..^1];
        var result = new OrderedDictionary<string, string>();

        foreach (var member in SplitMembers(inner))
        {
            var equals = member.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = member[..equals];
            var value = member[(equals + 1)..];
            result[key] = value;
        }

        return result;
    }

    /// <inheritdoc />
    public string Encode(IReadOnlyDictionary<string, string> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        return "(" + string.Join(",", members.Select(kv => $"{kv.Key}={kv.Value}")) + ")";
    }

    /// <summary>
    /// Splits the struct's inner text on top-level commas — commas nested inside <c>(...)</c> (e.g. a
    /// <c>CrossplayPlatforms=(Steam,Xbox,...)</c> member) or inside a quoted value are not member
    /// separators.
    /// </summary>
    private static List<string> SplitMembers(string inner)
    {
        var members = new List<string>();
        if (inner.Length == 0)
        {
            return members;
        }

        var depth = 0;
        char? quote = null;
        var start = 0;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];

            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    members.Add(inner[start..i]);
                    start = i + 1;
                    break;
            }
        }

        members.Add(inner[start..]);
        return members;
    }
}
