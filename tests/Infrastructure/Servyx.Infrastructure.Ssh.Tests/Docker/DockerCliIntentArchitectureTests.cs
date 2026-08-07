using System.Reflection;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// A reflection-driven guard over <see cref="DockerCli"/>'s intent split: every public static factory that
/// returns a <see cref="CommandSpec"/> is discovered, invoked, and checked against a hand-maintained
/// read-only allow-list — in both directions, so a factory added to the allow-list under the wrong intent
/// fails just as loudly as one left off it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is hand-listed except the allow-list itself.</b> The methods under test come from
/// reflecting over <see cref="DockerCli"/>'s public static surface, filtered to those returning
/// <see cref="CommandSpec"/>. A factory added to <see cref="DockerCli"/> next month is covered the moment it
/// is written, and <see cref="Every_docker_cli_factory_is_covered_by_this_theory"/> exists so that a change
/// to the discovery filter that silently matches nothing fails instead of leaving every theory case
/// vacuously true.
/// </para>
/// <para>
/// The allow-list is deliberately not derived from the factories' own declared <see cref="CommandSpec.Intent"/>
/// — doing that would make this file assert that <see cref="DockerCli"/> agrees with itself, which is true by
/// construction and proves nothing. The list is instead named review-list of what a human decided is
/// observation-only, checked against what the code actually declares.
/// </para>
/// </remarks>
public class DockerCliIntentArchitectureTests
{
    /// <summary>
    /// Every <see cref="DockerCli"/> factory a human has reviewed and judged safe to run on a read-only
    /// server. Anything not named here is expected to be <see cref="CommandIntent.Mutating"/>.
    /// </summary>
    private static readonly IReadOnlyList<string> ReadOnlyAllowList =
    [
        nameof(DockerCli.Ps),
        nameof(DockerCli.Inspect),
        nameof(DockerCli.Logs),
        nameof(DockerCli.Stats),
        nameof(DockerCli.Version),
        nameof(DockerCli.ExecReadOnly),
    ];

    /// <summary>
    /// How many public static <see cref="CommandSpec"/>-returning factories <see cref="DockerCli"/> is
    /// expected to expose. Pinned as a number (not just "non-empty") so that adding or removing a factory
    /// without updating <see cref="ReadOnlyAllowList"/> or this constant is caught here rather than by
    /// silently changing how many theory cases run.
    /// </summary>
    private const int ExpectedFactoryCount = 12;

    private static readonly IReadOnlyList<MethodInfo> Factories = DiscoverFactories();

    /// <summary>The name of each discovered <see cref="DockerCli"/> factory, for the theory below.</summary>
    public static TheoryData<string> EveryDockerCliFactory()
    {
        var data = new TheoryData<string>();

        foreach (var method in Factories)
        {
            data.Add(method.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryDockerCliFactory))]
    public void Every_docker_cli_factory_declares_the_intent_its_name_promises(string methodName)
    {
        var method = Factories.Single(candidate => candidate.Name == methodName);
        var spec = Invoke(method);

        spec.Executable.Should().Be("docker", $"DockerCli.{methodName} must invoke the docker CLI");

        var expectedIntent = ReadOnlyAllowList.Contains(methodName)
            ? CommandIntent.ReadOnly
            : CommandIntent.Mutating;

        spec.Intent.Should().Be(
            expectedIntent,
            expectedIntent == CommandIntent.ReadOnly
                ? $"DockerCli.{methodName} is on the read-only allow-list, so it must explicitly declare "
                  + "CommandIntent.ReadOnly rather than relying on the mutating default"
                : $"DockerCli.{methodName} is not on the read-only allow-list, so it must rely on the "
                  + "CommandIntent.Mutating default rather than declaring ReadOnly");
    }

    [Fact]
    public void Every_docker_cli_factory_is_covered_by_this_theory()
    {
        // The discovery above is the whole mechanism, so it is asserted rather than assumed: if it silently
        // matched nothing, the theory above would pass with no case at all.
        Factories.Should().NotBeEmpty(
            "DockerCli factories are discovered by reflection; finding none means the shape this file looks "
            + "for has changed and it is now asserting nothing");

        Factories.Count.Should().Be(
            ExpectedFactoryCount,
            "a factory added to or removed from DockerCli without updating this count (and the read-only "
            + "allow-list, if applicable) would otherwise change what this file covers without failing "
            + "anything");
    }

    [Fact]
    public void The_read_only_allow_list_names_only_real_docker_cli_methods()
    {
        var actualNames = Factories.Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = ReadOnlyAllowList.Where(name => !actualNames.Contains(name)).ToList();

        unknown.Should().BeEmpty(
            "every name in the read-only allow-list must match a real DockerCli factory, or a rename would "
            + "silently drop that factory out of the allow-list instead of failing here");
    }

    private static IReadOnlyList<MethodInfo> DiscoverFactories() =>
        [.. typeof(DockerCli)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(CommandSpec))
            .OrderBy(method => method.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Invokes a discovered factory with placeholder arguments: <see cref="string"/> parameters get
    /// <c>"placeholder"</c>, <see cref="int"/> parameters get <c>1</c>, and <see cref="IReadOnlyList{T}"/> of
    /// <see cref="string"/> parameters get a single-element <c>["noop"]</c> list.
    /// </summary>
    private static CommandSpec Invoke(MethodInfo method)
    {
        var arguments = method.GetParameters().Select(BuildPlaceholder).ToArray();
        return (CommandSpec)method.Invoke(null, arguments)!;
    }

    private static object BuildPlaceholder(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(string))
        {
            return "placeholder";
        }

        if (parameter.ParameterType == typeof(int))
        {
            return 1;
        }

        if (parameter.ParameterType == typeof(IReadOnlyList<string>))
        {
            return new List<string> { "noop" };
        }

        throw new InvalidOperationException(
            $"DockerCliIntentArchitectureTests has no placeholder builder for parameter "
            + $"'{parameter.Name}' of type {parameter.ParameterType}. Add one rather than skipping this "
            + "factory, or it silently drops out of coverage.");
    }
}
