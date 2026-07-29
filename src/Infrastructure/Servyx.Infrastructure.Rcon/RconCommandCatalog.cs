using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// One entry of a definition's <c>control.channels[].commands</c> block.
/// </summary>
/// <param name="Id">The command id callers invoke, e.g. <c>save</c>. Never a raw command string.</param>
/// <param name="Template">
/// The command line to render, with <c>{parameter}</c> placeholders — e.g.
/// <c>Shutdown {seconds} "{message}"</c>.
/// </param>
/// <param name="ReadOnly">
/// The definition's declared intent. <see langword="true"/> means the command observes the server and
/// changes nothing (<c>Info</c>, <c>ShowPlayers</c>); <see langword="false"/> means it mutates
/// (<c>Save</c>, <c>Broadcast</c>, <c>Shutdown</c>). This flag — not the verb, not the transport — is what
/// <see cref="WriteGuardedRconSession"/> gates on, exactly as <c>docs/abstractions.md</c> §8's implementer
/// note requires.
/// </param>
public sealed record RconCommand(string Id, string Template, bool ReadOnly);

/// <summary>
/// The set of control commands a definition declares, and the only vocabulary
/// <see cref="Domain.Rcon.IRconSession.InvokeAsync"/> will render.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An id that is not in here never reaches the wire.</strong> That is the point of taking a command
/// id rather than a command string: every invocation arrives carrying the definition's <c>readOnly</c>
/// classification, so the write guard has something to gate on. An arbitrary string has no classification,
/// which is why the audited <see cref="Domain.Rcon.IRconSession.SendRawAsync"/> escape hatch is a separate,
/// separately-permitted surface rather than a lenient mode of this one.
/// </para>
/// <para>
/// <strong>Templates are validated once, at construction.</strong> An unbalanced brace or a placeholder
/// that is not a plain identifier is a definition bug, and finding it when the catalogue is built is far
/// better than finding it during a quiesce.
/// </para>
/// </remarks>
public sealed class RconCommandCatalog
{
    private readonly Dictionary<string, RconCommand> _commands;

    /// <summary>A catalogue declaring nothing. Every invocation against it is refused.</summary>
    public static RconCommandCatalog Empty { get; } = new([]);

    /// <summary>Creates a catalogue over <paramref name="commands"/>.</summary>
    /// <param name="commands">The definition's declared commands.</param>
    /// <exception cref="ArgumentException">
    /// Two commands share an id, an id or template is blank, or a template's placeholders are malformed.
    /// </exception>
    public RconCommandCatalog(IEnumerable<RconCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        _commands = new Dictionary<string, RconCommand>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in commands)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (string.IsNullOrWhiteSpace(command.Id))
            {
                throw new ArgumentException("A control command must declare a non-empty id.", nameof(commands));
            }

            if (string.IsNullOrWhiteSpace(command.Template))
            {
                throw new ArgumentException(
                    $"Control command '{command.Id}' declares no template, so there is nothing to send.",
                    nameof(commands));
            }

            // Fails now, on a definition nobody is depending on yet, rather than mid-quiesce.
            _ = RconCommandText.ParameterNames(command.Id, command.Template);

            if (!_commands.TryAdd(command.Id, command))
            {
                throw new ArgumentException(
                    $"Control command id '{command.Id}' is declared more than once; a duplicated id has no single "
                    + "readOnly classification, so there is no safe way to gate it.",
                    nameof(commands));
            }
        }
    }

    /// <summary>Every declared command, ordered by id.</summary>
    public IReadOnlyList<RconCommand> Commands => [.. _commands.Values.OrderBy(c => c.Id, StringComparer.Ordinal)];

    /// <summary>Whether <paramref name="commandId"/> is declared.</summary>
    /// <param name="commandId">The id to test.</param>
    public bool Contains(string commandId) =>
        !string.IsNullOrWhiteSpace(commandId) && _commands.ContainsKey(commandId);

    /// <summary>Looks up a declared command.</summary>
    /// <param name="commandId">The id to resolve.</param>
    /// <param name="command">The resolved command, when this returns <see langword="true"/>.</param>
    public bool TryGet(string commandId, [NotNullWhen(true)] out RconCommand? command)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            command = null;
            return false;
        }

        return _commands.TryGetValue(commandId, out command);
    }

    /// <summary>Resolves a declared command, or refuses.</summary>
    /// <param name="commandId">The id to resolve.</param>
    /// <exception cref="RconUnknownCommandException"><paramref name="commandId"/> is not declared.</exception>
    public RconCommand Get(string commandId)
    {
        if (TryGet(commandId, out var command))
        {
            return command;
        }

        var declared = _commands.Count == 0
            ? "none"
            : string.Join(", ", _commands.Keys.OrderBy(k => k, StringComparer.Ordinal));

        throw new RconUnknownCommandException(
            $"'{commandId}' is not a control command this definition declares, so it carries no readOnly "
            + $"classification and will not be sent. Declared commands: {declared}.",
            commandId ?? string.Empty);
    }

    /// <summary>
    /// Renders a declared command's template with <paramref name="args"/>, refusing anything that would
    /// change the command's shape rather than fill a slot in it.
    /// </summary>
    /// <param name="commandId">The declared command id.</param>
    /// <param name="args">The arguments, keyed by placeholder name.</param>
    /// <exception cref="RconUnknownCommandException"><paramref name="commandId"/> is not declared.</exception>
    /// <exception cref="RconArgumentException">An argument is missing, unexpected, or hostile.</exception>
    public string Render(string commandId, IReadOnlyDictionary<string, string>? args) =>
        RconCommandText.Render(Get(commandId), args);
}
