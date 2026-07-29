using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// One installed local process, on a temp directory that stands in for the machine, shared by the maintenance
/// and update-execution suites.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <em>single</em> <see cref="LocalProcessProvisioner"/> instance for the whole fixture rather
/// than a new one per call: an update is applied against the same adapter that computed the plan, which is
/// what a caller revalidating a plan does, and is what <c>LocalProcessProvisionerTests</c>'s own fixture does
/// not need.
/// </para>
/// <para>
/// Every path here is composed with <see cref="Path.Combine(string, string)"/> under
/// <see cref="TempDirectory"/>, so nothing assumes a POSIX or a Windows layout and nothing is written outside
/// the machine's temp path.
/// </para>
/// </remarks>
internal sealed class LocalInstallFixture : IDisposable
{
    /// <summary>The instance id every install in these suites uses.</summary>
    internal const string InstanceId = "srv-0001";

    /// <summary>The executable a definition would declare, in the conventional <c>./name</c> spelling.</summary>
    internal const string Executable = "./PalServer.sh";

    internal LocalInstallFixture(string dataDirectoryName = "palworld")
    {
        Temp = new TempDirectory("local-maintenance");
        MarkerRoot = Temp.At("instances");
        DataDirectory = Temp.At(dataDirectoryName);
        Host = new RecordingLocalHost();
        Provisioner = new LocalProcessProvisioner(
            Host,
            machineId: "test-machine",
            credentialUrn: null,
            transportOptions: null,
            markerRoot: MarkerRoot);
    }

    internal TempDirectory Temp { get; }

    internal string MarkerRoot { get; }

    internal string DataDirectory { get; }

    internal RecordingLocalHost Host { get; }

    internal LocalProcessProvisioner Provisioner { get; }

    internal string MarkerPath => Path.Combine(MarkerRoot, InstanceId + ".servyx.json");

    /// <summary>The absolute path the recorded executable resolves to inside the data directory.</summary>
    internal string ExecutablePath => Path.Combine(DataDirectory, "PalServer.sh");

    /// <summary>The directory the fixture's <c>ensure-dir</c> install verb creates.</summary>
    internal string ConfigDirectory => Path.Combine(DataDirectory, "Pal", "Saved", "Config");

    /// <summary>
    /// A realistic request modelled on the <c>native-steamcmd</c> profile of
    /// <c>definitions/palworld-docker.yaml</c>, including its two install verbs.
    /// </summary>
    internal ProvisioningRequest Request(IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = "job-42",
            ["connectorId"] = "local-palworld",
            ["executable"] = Executable,
            ["dataDir"] = DataDirectory,
            ["install:0:verb"] = "steamcmd",
            ["install:0:appId"] = "2394010",
            ["install:0:validate"] = "true",
            ["install:1:verb"] = "ensure-dir",
            ["install:1:path"] = ConfigDirectory,
            ["env:SERVER_NAME"] = "Servyx Test Server",
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        return new ProvisioningRequest("palworld", "native-steamcmd", ConnectorId: null, parameters);
    }

    /// <summary>Convenience for the common "one changed parameter" case.</summary>
    internal static Dictionary<string, string> With(string key, string value) =>
        new(StringComparer.Ordinal) { [key] = value };

    /// <summary>
    /// Installs, then seeds the one artefact provisioning itself cannot produce here: the executable inside the
    /// data directory. <c>steamcmd</c> is a recorded command rather than a real download, so a genuinely intact
    /// install has to be completed by hand — the same thing <c>SshProcessMaintenanceTests.SeedIntactInstall</c>
    /// does, for the same reason. Recordings are cleared afterwards so a test asserts on its own phase only.
    /// </summary>
    internal async Task<ProvisionedResource> InstallAsync(IReadOnlyDictionary<string, string>? extra = null)
    {
        var resource = await Provisioner
            .CreateOperation(LocalProcessProvisioner.BuildSpec(Request(extra)))
            .CreateAsync();

        await File.WriteAllTextAsync(ExecutablePath, "#!/bin/sh\n");
        Host.ClearRecordings();
        return resource;
    }

    /// <summary>Reads the marker file back off disk.</summary>
    internal async Task<IReadOnlyDictionary<string, string>> ReadMarkerAsync() =>
        ServyxProcessMarker.Deserialize(await File.ReadAllBytesAsync(MarkerPath))!;

    public void Dispose() => Temp.Dispose();
}
