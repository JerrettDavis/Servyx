using System.Text.RegularExpressions;
using Servyx.Domain.Rcon;

namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>control</c> block: the control channels available to the workload
/// and the commands/endpoints exposed over each.
/// </summary>
/// <param name="Channels">The declared control channels, e.g. <c>rcon</c>, <c>rest</c>, <c>query</c>.</param>
/// <param name="Players">Cross-channel player-list configuration, if declared.</param>
public sealed record ControlPlane(IReadOnlyList<ControlChannelDefinition> Channels, PlayersConfig? Players);

/// <summary>
/// One entry of <see cref="ControlPlane.Channels"/>: a single control channel, its reachability strategies,
/// and its command/endpoint catalogue.
/// </summary>
/// <param name="Id">Channel identifier, e.g. <c>rcon</c>.</param>
/// <param name="Protocol">
/// The wire protocol, e.g. <c>source-rcon</c>, <c>palworld-rest</c>, <c>a2s</c>. Kept as an open string,
/// unlike <see cref="DeclaredConfigSurface.Format"/>. The distinguishing principle: a protocol is resolved
/// by adapter key against machinery that lives entirely outside a single surface parse — a readiness probe,
/// an RCON reachability strategy, a control-channel session — so a second game can register a brand new
/// protocol id purely by registering a new adapter, with no change to this codebase's closed types. A
/// format, by contrast, names the parser Servyx itself must ship to read a surface at all; see the remarks
/// on <see cref="DeclaredConfigSurface.Format"/> for why that closes the set instead.
/// </param>
/// <param name="Port">The port this channel listens on.</param>
/// <param name="PasswordRef">A credential reference for this channel, e.g. RCON's <c>passwordRef</c>. Null when the channel uses <see cref="Auth"/> instead, or needs no credential.</param>
/// <param name="Auth">An authentication scheme for this channel, e.g. REST's basic auth. Null when the channel uses <see cref="PasswordRef"/> instead, or needs no credential.</param>
/// <param name="EnabledWhen">A gate on whether this channel is usable, if declared.</param>
/// <param name="Reachability">Ordered strategies tried in sequence until one succeeds.</param>
/// <param name="Commands">This channel's command catalogue (e.g. RCON), keyed by command id. Empty for a channel with none.</param>
/// <param name="Endpoints">This channel's endpoint catalogue (e.g. REST), keyed by endpoint id. Empty for a channel with none.</param>
public sealed record ControlChannelDefinition(
    string Id,
    string Protocol,
    PortRef Port,
    SecretRef? PasswordRef,
    AuthSpec? Auth,
    EnabledWhenPredicate? EnabledWhen,
    IReadOnlyList<ReachabilityStrategy> Reachability,
    IReadOnlyDictionary<string, ControlCommand> Commands,
    IReadOnlyDictionary<string, ControlEndpoint> Endpoints);

/// <summary>An authentication scheme for a <see cref="ControlChannelDefinition"/>.</summary>
public abstract record AuthSpec
{
    private AuthSpec()
    {
    }

    /// <summary>HTTP basic authentication.</summary>
    /// <param name="User">The username.</param>
    /// <param name="PasswordRef">A reference to the password secret.</param>
    public sealed record Basic(string User, SecretRef PasswordRef) : AuthSpec;
}

/// <summary>
/// A closed, evaluable gate on whether a control channel is usable, e.g. <c>env.RCON_ENABLED == 'true'</c>
/// parses to <see cref="SurfaceId"/> <c>"env"</c>, <see cref="Key"/> <c>"RCON_ENABLED"</c>,
/// <see cref="EqualsValue"/> <c>"true"</c>.
/// </summary>
/// <remarks>
/// Deliberately not a free-form expression string, even though <c>docs/schema.md</c> describes
/// <c>enabledWhen</c> as one. Evaluating an arbitrary expression sourced from an untrusted definition file is
/// a code-execution-adjacent surface; a closed predicate over one known surface value is something a
/// validator and an evaluator can both reason about completely, at the cost of not supporting compound
/// conditions. If a future definition genuinely needs one, that is a deliberate addition to this closed
/// shape, not a reason to fall back to a string.
/// </remarks>
/// <param name="SurfaceId">The surface whose value is checked, e.g. <c>env</c>.</param>
/// <param name="Key">The key within that surface, e.g. <c>RCON_ENABLED</c>.</param>
/// <param name="EqualsValue">
/// The value the key must equal for the predicate to hold, e.g. <c>true</c>. Named <c>EqualsValue</c> rather
/// than <c>Equals</c> because a record positional parameter named <c>Equals</c> collides with the
/// compiler-generated <see cref="object.Equals(object?)"/> override.
/// </param>
public sealed record EnabledWhenPredicate(string SurfaceId, string Key, string EqualsValue);

/// <summary>
/// One strategy for reaching a <see cref="ControlChannelDefinition"/>'s endpoint, tried in the declared
/// order until one succeeds. Mirrors the strategy ids used by
/// <see cref="Servyx.Domain.Rcon.IRconReachability.StrategyId"/>.
/// </summary>
public abstract record ReachabilityStrategy
{
    private ReachabilityStrategy()
    {
    }

    /// <summary>Connect directly over TCP — usable only if the port is published to the host network.</summary>
    public sealed record DirectTcp : ReachabilityStrategy;

    /// <summary>Invoke a tool inside the container (e.g. <c>docker exec</c>) to reach the channel.</summary>
    /// <param name="Tool">The tool to invoke, e.g. <c>rcon-cli</c>.</param>
    /// <param name="Argv">The argument vector template, with placeholders like <c>{command}</c>.</param>
    public sealed record DockerExecTool(string Tool, IReadOnlyList<string> Argv) : ReachabilityStrategy;

    /// <summary>Reach the channel from a sibling container on the same Docker network.</summary>
    public sealed record DockerExecNetwork : ReachabilityStrategy;

    /// <summary>Reach the channel through an SSH tunnel.</summary>
    public sealed record SshTunnel : ReachabilityStrategy;
}

/// <summary>One entry of a <see cref="ControlChannelDefinition"/>'s <c>commands</c> catalogue (e.g. RCON).</summary>
/// <param name="Template">The command template, with placeholders, e.g. <c>Shutdown {seconds} "{message}"</c>.</param>
/// <param name="ReadOnly">Whether this command is safe to run when the server's write mode does not permit mutation. Enforced by the write-mode guard.</param>
public sealed record ControlCommand(string Template, bool ReadOnly);

/// <summary>One entry of a <see cref="ControlChannelDefinition"/>'s <c>endpoints</c> catalogue (e.g. REST).</summary>
/// <param name="Method">The HTTP method, e.g. <c>GET</c>.</param>
/// <param name="Path">The endpoint path, e.g. <c>/v1/api/players</c>.</param>
/// <param name="ReadOnly">Whether this endpoint is safe to call when the server's write mode does not permit mutation.</param>
public sealed record ControlEndpoint(string Method, string Path, bool ReadOnly);

/// <summary>
/// A regular expression authored in an untrusted definition file, compiled once during definition
/// validation rather than at match time.
/// </summary>
/// <remarks>
/// <para>
/// Two guarantees follow from compiling here rather than lazily:
/// </para>
/// <list type="number">
/// <item>
/// A pattern that cannot be compiled is a parse-time <see cref="ValidationSeverity.Error"/> against the
/// definition file, not a runtime exception in whatever polled a player list at 3am.
/// </item>
/// <item>
/// Compilation is attempted under <see cref="RegexOptions.NonBacktracking"/> ONLY, which guarantees
/// linear-time matching and refuses (at construction) the constructs that make catastrophic backtracking
/// possible — backreferences, lookaround, atomic groups. A definition therefore cannot express a ReDoS at
/// all; there is no fallback to a backtracking engine to downgrade into. An explicit
/// <see cref="MatchTimeout"/> is set anyway, as a second bound on a pathologically large reply.
/// </item>
/// </list>
/// <para>
/// Equality is deliberately by <see cref="Source"/> alone: <see cref="System.Text.RegularExpressions.Regex"/>
/// has reference equality, so a compiler-generated record equality over it would report two parses of the
/// same file as unequal.
/// </para>
/// </remarks>
public sealed class CompiledPattern : IEquatable<CompiledPattern>
{
    /// <summary>
    /// The options every definition-authored pattern is compiled under. <see cref="RegexOptions.Multiline"/>
    /// so <c>^</c>/<c>$</c> anchor per reply line, which is what a line-oriented control-channel reply needs.
    /// </summary>
    public const RegexOptions Options =
        RegexOptions.NonBacktracking | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    /// <summary>A hard upper bound on a single match, independent of the linear-time guarantee above.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private CompiledPattern(string source, Regex regex)
    {
        Source = source;
        Regex = regex;
    }

    /// <summary>The pattern exactly as the definition file spelled it.</summary>
    public string Source { get; }

    /// <summary>The compiled, non-backtracking, timeout-bounded matcher.</summary>
    public Regex Regex { get; }

    /// <summary>Whether the compiled pattern declares a named group.</summary>
    public bool HasGroup(string name) => Array.IndexOf(Regex.GetGroupNames(), name) >= 0;

    /// <summary>
    /// Compiles <paramref name="source"/>, returning <see langword="null"/> and an explanatory
    /// <paramref name="error"/> instead of throwing when it is not a valid non-backtracking pattern.
    /// </summary>
    public static CompiledPattern? TryCompile(string source, out string? error)
    {
        try
        {
            var pattern = new CompiledPattern(source, new Regex(source, Options, MatchTimeout));
            error = null;
            return pattern;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <inheritdoc />
    public bool Equals(CompiledPattern? other) =>
        other is not null && string.Equals(Source, other.Source, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CompiledPattern);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Source);

    /// <inheritdoc />
    public override string ToString() => Source;
}

/// <summary>
/// The named capture groups a <see cref="PlayerParserSpec"/> pattern may declare. Named here rather than
/// spelled inline so the validator that requires a group and the matcher that reads it cannot drift apart.
/// </summary>
public static class PlayerParserGroups
{
    /// <summary>Required by <see cref="PlayerParserSpec.SummaryLine"/> and <see cref="PlayerParserSpec.Count"/>; optional on a <see cref="PlayerParserSpec.Lines"/> header.</summary>
    public const string Count = "count";

    /// <summary>Optional: the server's configured player limit.</summary>
    public const string Max = "max";

    /// <summary>Optional on <see cref="PlayerParserSpec.SummaryLine"/>: the separator-joined name tail.</summary>
    public const string Names = "names";

    /// <summary>Required on <see cref="PlayerParserSpec.Lines"/>'s entry pattern.</summary>
    public const string Name = "name";

    /// <summary>Optional on <see cref="PlayerParserSpec.Lines"/>'s entry pattern: the player's own identifier.</summary>
    public const string Id = "id";
}

/// <summary>How a raw player-list response from a channel is parsed into structured records.</summary>
/// <remarks>
/// <para>
/// The four cases are named for the SHAPE of the reply, never for a game: a numbered line list and a
/// summary sentence are formats, and more than one title emits each of them. A guard test fails the build
/// if a game name leaks into a source file under <c>src/</c>, and these names are the main place that would
/// otherwise happen.
/// </para>
/// <para>
/// Every case is total by construction on the parsing side: a reply that does not fit the declared shape
/// degrades to <see cref="PlayerListFidelity.Unknown"/> with a diagnostic rather than throwing or guessing.
/// See <see cref="PlayerListSnapshot"/>.
/// </para>
/// </remarks>
public abstract record PlayerParserSpec
{
    private PlayerParserSpec()
    {
    }

    /// <summary>A CSV response with a header row, e.g. <c>name,playerUid,steamId</c> plus one line per player.</summary>
    /// <param name="Columns">The expected column names, in order. The declared count is also the required field count of every data line.</param>
    /// <param name="NameColumn">Which declared column carries the display name. Defaults to the first column when the definition omits it.</param>
    /// <param name="IdColumn">
    /// Which declared column carries the player's own identifier, or <see langword="null"/> when the
    /// definition omits it. Omitting it does not mean "no identifier": the identifier slots are filled from
    /// the remaining declared columns in declaration order, which is exactly the behaviour of the original
    /// three-column implementation this case generalizes.
    /// </param>
    public sealed record CsvWithHeader(IReadOnlyList<string> Columns, string NameColumn, string? IdColumn) : PlayerParserSpec;

    /// <summary>
    /// A single summary sentence covering the whole reply, e.g. a count, an optional limit, and an optional
    /// separator-joined tail of names.
    /// </summary>
    /// <param name="Pattern">
    /// Matched against the entire reply. Must declare <see cref="PlayerParserGroups.Count"/>; may declare
    /// <see cref="PlayerParserGroups.Max"/> and <see cref="PlayerParserGroups.Names"/>.
    /// </param>
    /// <param name="NameSeparator">What separates names inside the <c>names</c> group. Defaults to <see cref="DefaultNameSeparator"/>.</param>
    public sealed record SummaryLine(CompiledPattern Pattern, string NameSeparator) : PlayerParserSpec
    {
        /// <summary>The separator assumed when the definition declares none.</summary>
        public const string DefaultNameSeparator = ", ";
    }

    /// <summary>An optional header line followed by one line per player.</summary>
    /// <param name="HeaderPattern">Optional. May declare <see cref="PlayerParserGroups.Count"/>, which is then cross-checked against the entries actually matched.</param>
    /// <param name="EntryPattern">Matched per line. Must declare <see cref="PlayerParserGroups.Name"/>; may declare <see cref="PlayerParserGroups.Id"/>.</param>
    /// <param name="IgnorePatterns">
    /// Lines matching any of these are skipped rather than treated as unrecognized — this is how an
    /// empty-server sentinel line ("nobody is connected") is distinguished from a reply nobody understood.
    /// </param>
    public sealed record Lines(
        CompiledPattern? HeaderPattern,
        CompiledPattern EntryPattern,
        IReadOnlyList<CompiledPattern> IgnorePatterns) : PlayerParserSpec;

    /// <summary>
    /// A reply that carries a player count and no names at all. A first-class outcome, not a failure: a UI
    /// can render "N players online" perfectly well without a roster.
    /// </summary>
    /// <param name="Pattern">A regex over a text reply declaring <see cref="PlayerParserGroups.Count"/>, or <see langword="null"/> when <paramref name="JsonPointer"/> is used.</param>
    /// <param name="JsonPointer">An RFC 6901 pointer into a JSON reply resolving to a number, or <see langword="null"/> when <paramref name="Pattern"/> is used. Exactly one of the two is set.</param>
    public sealed record Count(CompiledPattern? Pattern, string? JsonPointer) : PlayerParserSpec;
}

/// <summary>How much a <see cref="PlayerListSnapshot"/> actually knows about who is connected.</summary>
public enum PlayerListFidelity
{
    /// <summary>Both the roster and the count are trustworthy.</summary>
    NamesAndCount,

    /// <summary>The count is trustworthy; no roster is available. A legitimate outcome, not an error.</summary>
    CountOnly,

    /// <summary>Nothing could be established from the reply. Never an exception, never an error surfaced to an operator.</summary>
    Unknown,
}

/// <summary>
/// The total result of parsing a raw player-list reply.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type is deliberately quarantined.</strong> Several control-channel reply formats are
/// modelled from unverified community reports, so a mis-parse must be incapable of affecting anything that
/// starts, stops, backs up, or declares a server ready. It is consumed only by the status/query projection
/// in <c>Servyx.Application.Servers</c>; readiness continues to come from log-regex, port, and health
/// probes, never from a player count. An architecture test asserts that nothing under a <c>Lifecycle</c> or
/// <c>Backups</c> directory references this type or its parser.
/// </para>
/// </remarks>
/// <param name="Fidelity">How much of the below is trustworthy.</param>
/// <param name="Players">The roster. Empty unless <paramref name="Fidelity"/> is <see cref="PlayerListFidelity.NamesAndCount"/>.</param>
/// <param name="Count">The connected-player count, when known.</param>
/// <param name="Max">The server's player limit, when the reply carried one.</param>
/// <param name="Diagnostic">Why the result degraded, for display as an explanation — never raised as an error.</param>
public sealed record PlayerListSnapshot(
    PlayerListFidelity Fidelity,
    IReadOnlyList<PlayerInfo> Players,
    int? Count,
    int? Max,
    string? Diagnostic)
{
    /// <summary>A snapshot that established nothing, carrying only the reason.</summary>
    public static PlayerListSnapshot Unresolved(string diagnostic) =>
        new(PlayerListFidelity.Unknown, [], null, null, diagnostic);

    /// <summary>A snapshot carrying a trustworthy count and no roster.</summary>
    public static PlayerListSnapshot CountOnly(int count, int? max = null, string? diagnostic = null) =>
        new(PlayerListFidelity.CountOnly, [], count, max, diagnostic);

    /// <summary>A snapshot carrying a trustworthy roster.</summary>
    public static PlayerListSnapshot Roster(IReadOnlyList<PlayerInfo> players, int? max = null, string? diagnostic = null) =>
        new(PlayerListFidelity.NamesAndCount, players, players.Count, max, diagnostic);
}

/// <summary>The <c>control.players</c> block: cross-channel player-list configuration.</summary>
/// <param name="Preferred">The order in which channel/endpoint combinations are tried for the player list, e.g. <c>rest.players</c>, <c>rcon.players</c>, <c>query</c>.</param>
/// <param name="PollInterval">Delay between successive player-list polls.</param>
/// <param name="Parsers">How to parse each channel/endpoint's raw player-list response, keyed the same way as <see cref="Preferred"/>'s entries.</param>
public sealed record PlayersConfig(
    IReadOnlyList<string> Preferred,
    TimeSpan PollInterval,
    IReadOnlyDictionary<string, PlayerParserSpec> Parsers);
