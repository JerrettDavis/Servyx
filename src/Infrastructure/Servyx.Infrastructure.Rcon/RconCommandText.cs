using System.Text;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Turns a definition's command template plus a caller's arguments into exactly one command line, and
/// refuses every input that could turn it into more than one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the RCON analogue of <see cref="Domain.Transport.CommandSpec"/>'s argv rule.</strong>
/// There, arguments are handed to the target process as a vector so no shell can ever re-parse them. Source
/// RCON has no such luxury: the wire carries a single NUL-terminated <em>line</em> which the game's own
/// console parser splits. So the guarantee has to be re-established here, and it is established the same
/// way it is there — by refusing, never by escaping.
/// </para>
/// <para>
/// <strong>Three independent properties make injection unrepresentable:</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>Single-pass substitution.</em> The template is walked once, left to right, and an argument's value is
/// appended to the output verbatim. Substituted text is never re-scanned, so a value that itself contains
/// <c>{seconds}</c> is the literal eight characters and not a second placeholder.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>A closed argument charset.</em> Control characters — which is where CR, LF, TAB and NUL live — are
/// refused. CR/LF are what would let <c>Broadcast hi\nShutdown 1</c> become two commands; NUL is what would
/// let a value truncate the packet body early and hide the rest of the line from the caller's own audit.
/// The double quote is refused for the same structural reason: the definition's
/// <c>Shutdown {seconds} "{message}"</c> embeds its argument <em>inside</em> quotes, so a quote in the value
/// closes the literal and hands the remainder to the parser as further tokens. Escaping it would require
/// knowing the game's quoting rules, which the definition does not state and Servyx must not guess.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>An exact parameter set.</em> A missing argument is refused rather than rendered as an empty slot,
/// and an argument the template has no placeholder for is refused rather than dropped. An argument that
/// silently goes nowhere is either a caller bug or an attempt to smuggle text past a reviewer's eye.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class RconCommandText
{
    /// <summary>
    /// The longest a single argument value may be. Comfortably above any player name or broadcast message
    /// and far below the packet budget, so no single argument can push a rendered command over the wire
    /// limit on its own.
    /// </summary>
    internal const int MaxArgumentLength = 512;

    /// <summary>Renders <paramref name="command"/>'s template with <paramref name="args"/>.</summary>
    /// <param name="command">The declared command.</param>
    /// <param name="args">The arguments, keyed by placeholder name.</param>
    /// <exception cref="RconArgumentException">An argument is missing, unexpected, or hostile.</exception>
    internal static string Render(RconCommand command, IReadOnlyDictionary<string, string>? args)
    {
        ArgumentNullException.ThrowIfNull(command);

        var template = command.Template;
        var rendered = new StringBuilder(template.Length);
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                rendered.Append(template, index, template.Length - index);
                break;
            }

            rendered.Append(template, index, open - index);

            var close = template.IndexOf('}', open + 1);
            var name = close < 0 ? null : template[(open + 1)..close];

            if (name is null || !IsParameterName(name))
            {
                throw new RconArgumentException(
                    $"Control command '{command.Id}' has a malformed template: '{template}'. Placeholders must be "
                    + "'{name}' where name is a letter or underscore followed by letters, digits or underscores.",
                    command.Id,
                    null);
            }

            var value = Resolve(command, args, name);
            EnsureSafeArgument(command.Id, name, value);

            // Appended verbatim, and the loop continues from *after* the placeholder in the TEMPLATE — never
            // from inside what was just appended. This is what makes a '{' inside a value inert.
            rendered.Append(value);
            consumed.Add(name);

            index = close + 1;
        }

        if (args is not null)
        {
            foreach (var supplied in args.Keys)
            {
                if (!consumed.Contains(supplied))
                {
                    throw new RconArgumentException(
                        $"Control command '{command.Id}' has no '{{{supplied}}}' placeholder, so the supplied argument "
                        + "would go nowhere. Refusing rather than silently discarding it.",
                        command.Id,
                        supplied);
                }
            }
        }

        var line = rendered.ToString();
        EnsureSingleCommandLine(line);
        return line;
    }

    /// <summary>
    /// Returns the placeholder names a template declares, validating the template's shape along the way.
    /// </summary>
    /// <param name="commandId">The command the template belongs to, for the refusal message.</param>
    /// <param name="template">The template to scan.</param>
    /// <exception cref="ArgumentException">The template is malformed.</exception>
    internal static IReadOnlyList<string> ParameterNames(string commandId, string template)
    {
        var names = new List<string>();
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf('}', open + 1);
            var name = close < 0 ? null : template[(open + 1)..close];

            if (name is null || !IsParameterName(name))
            {
                throw new ArgumentException(
                    $"Control command '{commandId}' has a malformed template: '{template}'. Placeholders must be "
                    + "'{name}' where name is a letter or underscore followed by letters, digits or underscores.",
                    nameof(template));
            }

            names.Add(name);
            index = close + 1;
        }

        if (template.IndexOf('}', index) >= 0)
        {
            throw new ArgumentException(
                $"Control command '{commandId}' has an unbalanced '}}' in its template: '{template}'.",
                nameof(template));
        }

        return names;
    }

    /// <summary>
    /// Verifies that <paramref name="line"/> is one command and nothing more — the final gate every string
    /// crosses on its way to a packet body, including one from the audited raw escape hatch.
    /// </summary>
    /// <param name="line">The command line about to be sent.</param>
    /// <exception cref="RconArgumentException"><paramref name="line"/> contains a control character.</exception>
    internal static void EnsureSingleCommandLine(string line)
    {
        foreach (var c in line)
        {
            if (!char.IsControl(c))
            {
                continue;
            }

            throw new RconArgumentException(
                $"An RCON command line contains the control character U+{(int)c:X4}. A carriage return or newline "
                + "would append a second command, and a NUL would truncate the packet body, so the line is refused "
                + "rather than sanitised.");
        }
    }

    private static string Resolve(RconCommand command, IReadOnlyDictionary<string, string>? args, string name)
    {
        if (args is not null && args.TryGetValue(name, out var value) && value is not null)
        {
            return value;
        }

        throw new RconArgumentException(
            $"Control command '{command.Id}' requires an argument for '{{{name}}}'. Rendering it as an empty slot "
            + "would send a differently-shaped command than the definition declares.",
            command.Id,
            name);
    }

    private static void EnsureSafeArgument(string commandId, string name, string value)
    {
        if (value.Length > MaxArgumentLength)
        {
            throw new RconArgumentException(
                $"The '{name}' argument to control command '{commandId}' is {value.Length} characters, beyond the "
                + $"{MaxArgumentLength}-character limit.",
                commandId,
                name);
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new RconArgumentException(
                    $"The '{name}' argument to control command '{commandId}' contains the control character "
                    + $"U+{(int)c:X4}. A carriage return or newline would append a second command and a NUL would "
                    + "truncate the packet body, so the argument is refused rather than escaped.",
                    commandId,
                    name);
            }

            if (c == '"')
            {
                throw new RconArgumentException(
                    $"The '{name}' argument to control command '{commandId}' contains a double quote. Templates such "
                    + "as 'Shutdown {seconds} \"{message}\"' embed arguments inside quotes, so a quote in the value "
                    + "would close the literal and hand the rest to the game's command parser as further tokens. "
                    + "Servyx refuses such a value rather than guessing the game's escaping rules.",
                    commandId,
                    name);
            }
        }
    }

    private static bool IsParameterName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
