using System.Text;

namespace Servyx.Mcp;

/// <summary>
/// Converts a PascalCase .NET identifier (an enum member name, a record type name) to lower-kebab —
/// the wire form every enum and every <c>Outcome</c> discriminant crosses the MCP boundary as (see
/// <c>ResultMapping</c> and <c>Contracts/Unavailable.cs</c>). One implementation, shared, so
/// <c>ControlCommandCatalogue</c> always becomes <c>control-command-catalogue</c> everywhere it is
/// rendered rather than each call site re-deriving its own splitting rule.
/// </summary>
internal static class KebabCase
{
    /// <summary>Converts <paramref name="pascalCase"/> to lower-kebab, splitting before every interior uppercase letter.</summary>
    public static string From(string pascalCase)
    {
        var sb = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('-');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
