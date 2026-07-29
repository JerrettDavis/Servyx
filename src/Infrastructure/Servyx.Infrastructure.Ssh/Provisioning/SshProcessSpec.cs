using System.Globalization;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Provisioning;

/// <summary>
/// One step of a game definition's <c>install:</c> block, in the only forms Servyx will execute.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The allowlist is the type hierarchy.</strong> The constructor is <c>private protected</c>, so the
/// only subtypes that can exist are the ones declared in this file. A definition author cannot introduce a
/// new verb by writing YAML, and Servyx cannot execute one by accident: an unrecognised verb has no
/// <see cref="SshInstallStep"/> to become, so <see cref="SshProcessSpec.Parse"/> rejects it while building
/// the spec — that is, at <em>plan</em> time, before any connection is opened.
/// </para>
/// <para>
/// <strong>Every step becomes a <see cref="CommandSpec"/>, never a shell string.</strong>
/// <see cref="ToCommand"/> returns an executable plus an argv array. No implementation concatenates a
/// caller-supplied value into a larger string, so a path or app id containing <c>; rm -rf /</c> is an inert
/// argv element with nowhere to be parsed as shell syntax. (The one place a string is unavoidable is the SSH
/// <c>exec</c> wire format itself, where <see cref="PosixArgv"/> quotes each element individually — see its
/// remarks.)
/// </para>
/// </remarks>
public abstract record SshInstallStep
{
    private protected SshInstallStep(string verb) => Verb = verb;

    /// <summary>The definition verb this step was parsed from.</summary>
    public string Verb { get; }

    /// <summary>Every verb Servyx will execute, in the spelling a definition uses.</summary>
    public static IReadOnlyList<string> AllowedVerbs { get; } = [SteamCmdInstallStep.VerbName, EnsureDirectoryInstallStep.VerbName];

    /// <summary>Renders this step as an argv-array command against <paramref name="spec"/>.</summary>
    public abstract CommandSpec ToCommand(SshProcessSpec spec);

    /// <summary>Human-readable description of what this step will do, shown to the user before approval.</summary>
    public abstract string Describe(SshProcessSpec spec);

    /// <summary>A stable, per-plan identifier for this step at <paramref name="index"/>.</summary>
    public string StageId(int index) => string.Create(CultureInfo.InvariantCulture, $"install-{index}-{Verb}");
}

/// <summary>
/// The <c>steamcmd</c> verb: install or update a Steam application into the profile's data directory.
/// </summary>
/// <param name="AppId">The Steam application id (a definition's <c>appId</c>), kept as text because it is never arithmetic.</param>
/// <param name="Validate">Whether to append <c>validate</c> to the <c>app_update</c> command (a definition's <c>validate: true</c>).</param>
public sealed record SteamCmdInstallStep(string AppId, bool Validate) : SshInstallStep(VerbName)
{
    /// <summary>The definition spelling of this verb.</summary>
    public const string VerbName = "steamcmd";

    /// <inheritdoc />
    /// <remarks>
    /// SteamCMD's own argument grammar is <c>+command arg arg +command arg</c>; each token is a separate
    /// argv element here, so neither <see cref="AppId"/> nor the data directory is ever spliced into another
    /// token. <c>+login anonymous</c> is used because Servyx never handles a user's Steam credentials for a
    /// dedicated-server download.
    /// </remarks>
    public override CommandSpec ToCommand(SshProcessSpec spec)
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
    public override string Describe(SshProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Install or update Steam app {AppId} into '{spec.DataDirectory}' via '{spec.SteamCmdPath}'{(Validate ? ", validating existing files" : "")}.");
    }
}

/// <summary>
/// The <c>ensure-dir</c> verb: create a directory (and any missing parents) if it does not already exist.
/// </summary>
/// <param name="Path">The absolute directory path (a definition's <c>path</c>, with <c>${DATA_DIR}</c> already expanded).</param>
public sealed record EnsureDirectoryInstallStep(string Path) : SshInstallStep(VerbName)
{
    /// <summary>The definition spelling of this verb.</summary>
    public const string VerbName = "ensure-dir";

    /// <inheritdoc />
    public override CommandSpec ToCommand(SshProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new CommandSpec("mkdir", ["-p", "--", Path], WorkingDirectory: null, spec.Environment);
    }

    /// <inheritdoc />
    public override string Describe(SshProcessSpec spec) => $"Ensure directory '{Path}' exists.";
}

/// <summary>
/// The full description of a process install <c>SshProcessProvisioner</c> may create.
/// </summary>
/// <remarks>
/// <see cref="Marker"/> is a required positional argument of type <see cref="ServyxProcessMarker"/>, which
/// cannot itself be constructed without an instance id, a job id, and a connector id. That is the same
/// structural guarantee <c>DockerContainerSpec</c> gets from <c>ServyxResourceTags</c>: an install described
/// by a spec always carries the mandatory Servyx tags, because there is no way to express a spec that does
/// not.
/// </remarks>
/// <param name="DataDirectory">The profile's <c>dataDir</c> on the target host, e.g. <c>/opt/palworld</c>.</param>
/// <param name="Executable">The profile's <c>executable</c> for this platform, e.g. <c>./PalServer.sh</c>. Recorded, never run by provisioning.</param>
/// <param name="Marker">The mandatory Servyx tags. Cannot be omitted or defaulted.</param>
public sealed record SshProcessSpec(string DataDirectory, string Executable, ServyxProcessMarker Marker)
{
    /// <summary>The profile's <c>dataDir</c> on the target host.</summary>
    public string DataDirectory { get; } = Validate(DataDirectory, nameof(DataDirectory));

    /// <summary>The profile's <c>executable</c>. Provisioning installs it; starting it is the control plane's job.</summary>
    public string Executable { get; } = Validate(Executable, nameof(Executable));

    /// <summary>The mandatory Servyx tags written into the marker file.</summary>
    public ServyxProcessMarker Marker { get; } = Marker ?? throw new ArgumentNullException(nameof(Marker));

    /// <summary>The install steps, in definition order.</summary>
    public IReadOnlyList<SshInstallStep> InstallSteps { get; init; } = [];

    /// <summary>Environment variables applied to every install command.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Extra tags written into the marker file alongside the mandatory ones. Applied first, so they can never override one.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>steamcmd</c> binary to invoke. Defaults to whatever <c>steamcmd</c> resolves to on the host's PATH.</summary>
    public string SteamCmdPath { get; init; } = "steamcmd";

    /// <summary>
    /// Parses a single definition install entry, given its verb and that entry's fields.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="verb"/> is not one of <see cref="SshInstallStep.AllowedVerbs"/>, or a required field
    /// for the verb is missing.
    /// </exception>
    public static SshInstallStep Parse(string verb, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return verb switch
        {
            SteamCmdInstallStep.VerbName => new SteamCmdInstallStep(
                RequiredField(verb, fields, "appId"),
                fields.TryGetValue("validate", out var validate) && string.Equals(validate, "true", StringComparison.OrdinalIgnoreCase)),
            EnsureDirectoryInstallStep.VerbName => new EnsureDirectoryInstallStep(RequiredField(verb, fields, "path")),
            _ => throw new ArgumentException(
                $"Install verb '{verb}' is not allowed by the 'ssh-process' provisioner. Permitted verbs: {string.Join(", ", SshInstallStep.AllowedVerbs)}.",
                nameof(verb)),
        };
    }

    private static string RequiredField(string verb, IReadOnlyDictionary<string, string> fields, string field)
    {
        if (!fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Install verb '{verb}' requires a '{field}' field.", nameof(fields));
        }

        return value;
    }

    private static string Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
