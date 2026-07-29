using System.Globalization;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// One step of a game definition's <c>install:</c> block, in the only forms
/// <see cref="LocalProcessProvisioner"/> will carry out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The allowlist is the type hierarchy.</strong> The constructor is <c>private protected</c>, so the
/// only subtypes that can exist are the ones declared in this file. A definition author cannot introduce a new
/// verb by writing YAML, and Servyx cannot execute one by accident: an unrecognised verb has no
/// <see cref="LocalInstallStep"/> to become, so <see cref="LocalProcessSpec.Parse"/> rejects it while building
/// the spec — that is, at <em>plan</em> time, before anything is reachable.
/// </para>
/// <para>
/// <strong>Not every step is a command, and that is the one real divergence from the SSH shape.</strong> The
/// SSH provisioner renders <c>ensure-dir</c> as <c>mkdir -p -- &lt;path&gt;</c>, which is fine because its
/// target is POSIX by definition. Windows has no <c>mkdir</c> executable — it is a <c>cmd.exe</c> builtin, and
/// <c>cmd.exe</c> does not parse its command line with <c>CommandLineToArgvW</c>, so passing a caller-supplied
/// path through it would reintroduce exactly the quoting hazard argv arrays exist to remove. Rather than ship a
/// verb that is either broken or unsafe on one of the two platforms Servyx builds for,
/// <see cref="EnsureDirectoryInstallStep"/> spawns no process at all and is realised through
/// <see cref="Directory.CreateDirectory(string)"/> by the operation. A step that starts no process is strictly
/// harder to inject into than one that starts a safe one; what is lost is only the symmetry of "every step is a
/// <see cref="CommandSpec"/>", which was never a guarantee anyone depended on.
/// </para>
/// </remarks>
public abstract record LocalInstallStep
{
    private protected LocalInstallStep(string verb) => Verb = verb;

    /// <summary>The definition verb this step was parsed from.</summary>
    public string Verb { get; }

    /// <summary>Every verb Servyx will carry out, in the spelling a definition uses.</summary>
    public static IReadOnlyList<string> AllowedVerbs { get; } = [SteamCmdInstallStep.VerbName, EnsureDirectoryInstallStep.VerbName];

    /// <summary>Human-readable description of what this step will do, shown to the user before approval.</summary>
    public abstract string Describe(LocalProcessSpec spec);

    /// <summary>
    /// The canonical text this step contributes to a plan hash. Kept separate from
    /// <see cref="Describe"/> so that prose can be reworded without invalidating every outstanding plan.
    /// </summary>
    public abstract string HashInput(LocalProcessSpec spec);

    /// <summary>A stable, per-plan identifier for this step at <paramref name="index"/>.</summary>
    public string StageId(int index) => string.Create(CultureInfo.InvariantCulture, $"install-{index}-{Verb}");
}

/// <summary>
/// The <c>steamcmd</c> verb: install or update a Steam application into the profile's data directory.
/// </summary>
/// <param name="AppId">The Steam application id (a definition's <c>appId</c>), kept as text because it is never arithmetic.</param>
/// <param name="Validate">Whether to append <c>validate</c> to the <c>app_update</c> command (a definition's <c>validate: true</c>).</param>
public sealed record SteamCmdInstallStep(string AppId, bool Validate) : LocalInstallStep(VerbName)
{
    /// <summary>The definition spelling of this verb.</summary>
    public const string VerbName = "steamcmd";

    /// <summary>Renders this step as an argv-array command against <paramref name="spec"/>.</summary>
    /// <remarks>
    /// SteamCMD's own argument grammar is <c>+command arg arg +command arg</c>; each token is a separate argv
    /// element here, so neither <see cref="AppId"/> nor the data directory is ever spliced into another token.
    /// <c>+login anonymous</c> is used because Servyx never handles a user's Steam credentials for a
    /// dedicated-server download.
    /// </remarks>
    public CommandSpec ToCommand(LocalProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var arguments = new List<string>
        {
            "+force_install_dir",
            spec.DataDirectory,
            "+login",
            "anonymous",
            "+app_update",
            AppId,
        };

        if (Validate)
        {
            arguments.Add("validate");
        }

        arguments.Add("+quit");

        return new CommandSpec(spec.SteamCmdPath, arguments, WorkingDirectory: null, spec.Environment);
    }

    /// <inheritdoc />
    public override string Describe(LocalProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Install or update Steam app {AppId} into '{spec.DataDirectory}' via '{spec.SteamCmdPath}'{(Validate ? ", validating existing files" : "")}.");
    }

    /// <inheritdoc />
    public override string HashInput(LocalProcessSpec spec)
    {
        var command = ToCommand(spec);
        return $"{Verb} {command.Executable} {string.Join(' ', command.Arguments)}";
    }
}

/// <summary>
/// The <c>ensure-dir</c> verb: create a directory (and any missing parents) if it does not already exist.
/// </summary>
/// <remarks>
/// Spawns no process — see the remarks on <see cref="LocalInstallStep"/> for why the local shape differs from
/// the SSH one here.
/// </remarks>
/// <param name="Path">The fully-qualified directory path (a definition's <c>path</c>, with <c>${DATA_DIR}</c> already expanded).</param>
public sealed record EnsureDirectoryInstallStep(string Path) : LocalInstallStep(VerbName)
{
    /// <summary>The definition spelling of this verb.</summary>
    public const string VerbName = "ensure-dir";

    /// <summary>
    /// The fully-qualified directory to create. Validated at construction, so a relative path — which
    /// <see cref="Directory.CreateDirectory(string)"/> would happily resolve against whatever directory Servyx
    /// was launched from — is rejected at plan time rather than creating a directory somewhere unexpected.
    /// </summary>
    public string Path { get; } = LocalProcessSpec.RequireFullyQualified(Path, nameof(Path));

    /// <inheritdoc />
    public override string Describe(LocalProcessSpec spec) => $"Ensure directory '{Path}' exists.";

    /// <inheritdoc />
    public override string HashInput(LocalProcessSpec spec) => $"{Verb} {Path}";
}

/// <summary>
/// The full description of a local process install <see cref="LocalProcessProvisioner"/> may create.
/// </summary>
/// <remarks>
/// <see cref="Marker"/> is a required positional argument of type <see cref="ServyxProcessMarker"/>, which
/// cannot itself be constructed without an instance id, a job id, and a connector id: an install described by
/// a spec always carries the mandatory Servyx tags, because there is no way to express a spec that does not.
/// </remarks>
/// <param name="DataDirectory">The profile's <c>dataDir</c> on this machine, e.g. <c>/opt/palworld</c> or <c>D:\servers\palworld</c>.</param>
/// <param name="Executable">The profile's <c>executable</c> for this platform, e.g. <c>./PalServer.sh</c>. Recorded, never run by provisioning.</param>
/// <param name="Marker">The mandatory Servyx tags. Cannot be omitted or defaulted.</param>
public sealed record LocalProcessSpec(string DataDirectory, string Executable, ServyxProcessMarker Marker)
{
    /// <summary>The profile's <c>dataDir</c> on this machine. Must be fully qualified.</summary>
    public string DataDirectory { get; } = RequireFullyQualified(DataDirectory, nameof(DataDirectory));

    /// <summary>The profile's <c>executable</c>. Provisioning installs it; starting it is the control plane's job.</summary>
    public string Executable { get; } = RequireNonBlank(Executable, nameof(Executable));

    /// <summary>The mandatory Servyx tags written into the marker file.</summary>
    public ServyxProcessMarker Marker { get; } = Marker ?? throw new ArgumentNullException(nameof(Marker));

    /// <summary>The install steps, in definition order.</summary>
    public IReadOnlyList<LocalInstallStep> InstallSteps { get; init; } = [];

    /// <summary>Environment variables applied to every install command.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Extra tags written into the marker file alongside the mandatory ones. Applied first, so they can never override one.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>steamcmd</c> binary to invoke. Defaults to whatever <c>steamcmd</c> resolves to on this machine's PATH.</summary>
    public string SteamCmdPath { get; init; } = "steamcmd";

    /// <summary>
    /// Parses a single definition install entry, given its verb and that entry's fields.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="verb"/> is not one of <see cref="LocalInstallStep.AllowedVerbs"/>, or a required field
    /// for the verb is missing.
    /// </exception>
    public static LocalInstallStep Parse(string verb, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return verb switch
        {
            SteamCmdInstallStep.VerbName => new SteamCmdInstallStep(
                RequiredField(verb, fields, "appId"),
                fields.TryGetValue("validate", out var validate) && string.Equals(validate, "true", StringComparison.OrdinalIgnoreCase)),
            EnsureDirectoryInstallStep.VerbName => new EnsureDirectoryInstallStep(RequiredField(verb, fields, "path")),
            _ => throw new ArgumentException(
                $"Install verb '{verb}' is not allowed by the '{LocalProcessProvisioner.Id}' provisioner. " +
                $"Permitted verbs: {string.Join(", ", LocalInstallStep.AllowedVerbs)}.",
                nameof(verb)),
        };
    }

    /// <summary>
    /// The single gate every caller-supplied absolute path in this file passes through.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="System.IO.Path.IsPathFullyQualified(string)"/> rather than the SSH adapter's
    /// "starts with <c>/</c> and contains no backslash or colon" rule. That rule is correct for a remote POSIX
    /// host and wrong for the machine Servyx is running on, where <c>C:\ProgramData\Servyx</c> is a perfectly
    /// good absolute path and <c>/var/lib/servyx</c> is <em>not</em> fully qualified (it is drive-relative).
    /// This is the sharpest place the local and SSH shapes genuinely part company.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank or not fully qualified.</exception>
    internal static string RequireFullyQualified(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

        if (!System.IO.Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException(
                $"'{value}' must be a fully-qualified path on this machine (it is not, so where it would land depends on the current directory).",
                name);
        }

        return value;
    }

    private static string RequiredField(string verb, IReadOnlyDictionary<string, string> fields, string field)
    {
        if (!fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Install verb '{verb}' requires a '{field}' field.", nameof(fields));
        }

        return value;
    }

    private static string RequireNonBlank(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
