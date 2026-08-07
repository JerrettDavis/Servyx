using System.Collections.Concurrent;
using System.Reflection;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// Hermetic unit tests of <see cref="MutationTargetGuard"/>'s own logic — no Docker daemon, no
/// Testcontainers, no network I/O. These MUST run in every CI build (they carry no
/// <c>[Trait("Category","Integration")]</c> and are not filtered out by this project's default
/// <c>Category!=Integration</c> run), because the guard's refusal logic is exactly what stands between a
/// mutation test and a real, running production server; it cannot be allowed to only get exercised on
/// machines that happen to have Docker installed.
/// </summary>
public sealed class MutationTargetGuardTests
{
    private const string ProductionContainerName = "palworld-server";
    private const string ProductionEndpoint = "ssh:operator@203.0.113.10:22";

    private static string NewGeneratedName() => $"{MutationTargetGuard.RequiredPrefix}{Guid.NewGuid():N}";

    private static TargetDescriptor MakeDescriptor(string containerName, string endpoint) => new(
        TransportId: "ssh+docker",
        Endpoint: endpoint,
        CredentialUrn: null,
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = containerName });

    /// <summary>Registers a fresh generated name and returns both the descriptor and the disposable handle.</summary>
    private static (TargetDescriptor Target, IDisposable Registration, string Name, int Port) RegisterValidTarget()
    {
        var name = NewGeneratedName();
        var port = Random.Shared.Next(20000, 60000);
        var registration = MutationTargetGuard.Register(name, port);
        var target = MakeDescriptor(name, $"127.0.0.1:{port}");
        return (target, registration, name, port);
    }

    [Fact]
    public void A_production_container_name_is_refused()
    {
        var target = MakeDescriptor(ProductionContainerName, "127.0.0.1:12345");

        var act = () => MutationTargetGuard.Approve(target);

        act.Should().Throw<MutationTargetRefusedException>()
            .Which.Message.Should().Contain(MutationTargetGuard.RequiredPrefix);
    }

    [Fact]
    public void An_unregistered_but_correctly_prefixed_name_is_refused()
    {
        var name = NewGeneratedName();
        var target = MakeDescriptor(name, "127.0.0.1:12345");

        var act = () => MutationTargetGuard.Approve(target);

        act.Should().Throw<MutationTargetRefusedException>()
            .Which.Message.Should().Contain("not currently registered");
    }

    [Fact]
    public void A_registered_generated_name_is_approved()
    {
        var (target, registration, _, _) = RegisterValidTarget();
        using var _ = registration;

        var approved = MutationTargetGuard.Approve(target);

        approved.Should().BeSameAs(target);
    }

    [Fact]
    public void A_name_is_refused_after_its_container_is_disposed()
    {
        var (target, registration, _, _) = RegisterValidTarget();
        registration.Dispose();

        var act = () => MutationTargetGuard.Approve(target);

        act.Should().Throw<MutationTargetRefusedException>()
            .Which.Message.Should().Contain("not currently registered");
    }

    [Fact]
    public void A_non_loopback_endpoint_is_refused()
    {
        var (validTarget, registration, name, _) = RegisterValidTarget();
        using var _ = registration;
        var target = validTarget with { Endpoint = ProductionEndpoint };

        var act = () => MutationTargetGuard.Approve(target);

        act.Should().Throw<MutationTargetRefusedException>()
            .Which.Message.Should().Contain("not loopback/localhost");
        target.Options["containerName"].Should().Be(name); // sanity: same registered name, only the endpoint differs
    }

    [Fact]
    public void A_loopback_endpoint_on_an_unknown_port_is_refused()
    {
        var (validTarget, registration, _, port) = RegisterValidTarget();
        using var _ = registration;
        var target = validTarget with { Endpoint = $"127.0.0.1:{port + 1}" };

        var act = () => MutationTargetGuard.Approve(target);

        act.Should().Throw<MutationTargetRefusedException>()
            .Which.Message.Should().Contain("not the Testcontainers-mapped port");
    }

    [Fact]
    public void Any_servyx_remote_env_var_refuses_everything()
    {
        var (target, registration, _, _) = RegisterValidTarget();
        using var _ = registration;

        const string variable = "SERVYX_REMOTE_E2E";
        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            var act = () => MutationTargetGuard.Approve(target);

            act.Should().Throw<MutationTargetRefusedException>()
                .Which.Message.Should().Contain(variable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        // Restored: the same, otherwise-valid target is approved again once the variable is gone.
        MutationTargetGuard.Approve(target).Should().BeSameAs(target);
    }

    [Fact]
    public void Every_refusal_names_the_layer_that_refused()
    {
        var (validTarget, registration, name, port) = RegisterValidTarget();
        using var _ = registration;

        var scenarios = new (string Description, TargetDescriptor Target, string ExpectedLayerFragment)[]
        {
            ("bad name", MakeDescriptor(ProductionContainerName, $"127.0.0.1:{port}"), "layer 1"),
            ("unregistered", MakeDescriptor(NewGeneratedName(), $"127.0.0.1:{port}"), "layer 2"),
            ("non-loopback", validTarget with { Endpoint = ProductionEndpoint }, "layer 3"),
            ("unknown port", validTarget with { Endpoint = $"127.0.0.1:{port + 1}" }, "layer 3"),
        };

        foreach (var scenario in scenarios)
        {
            var act = () => MutationTargetGuard.Approve(scenario.Target);

            act.Should().Throw<MutationTargetRefusedException>(scenario.Description)
                .Which.Layer.Should().ContainEquivalentOf(scenario.ExpectedLayerFragment, scenario.Description);
        }

        name.Should().StartWith(MutationTargetGuard.RequiredPrefix); // the valid registration itself never refused
    }

    [Fact]
    public void The_guard_is_thread_safe_under_concurrent_registration()
    {
        const int workers = 64;
        var exceptions = new ConcurrentBag<Exception>();
        var names = new ConcurrentBag<string>();

        Parallel.For(0, workers, i =>
        {
            try
            {
                var name = NewGeneratedName();
                var port = 20000 + i;
                names.Add(name);

                using var registration = MutationTargetGuard.Register(name, port);
                var target = MakeDescriptor(name, $"127.0.0.1:{port}");

                MutationTargetGuard.Approve(target).Should().BeSameAs(target);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty();

        // Every worker's `using` disposed its own registration; none should still be approvable.
        Parallel.ForEach(names, name =>
        {
            var target = MakeDescriptor(name, "127.0.0.1:1");
            var act = () => MutationTargetGuard.Approve(target);
            act.Should().Throw<MutationTargetRefusedException>();
        });
    }

    [Fact]
    public void DisposableWorkloadContainer_exposes_no_member_that_accepts_a_container_name()
    {
        var type = typeof(DisposableWorkloadContainer);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var ctor in type.GetConstructors(flags))
        {
            if (IsPublicOrInternal(ctor))
            {
                CollectStringParameters(ctor, "constructor", offenders);
            }
        }

        foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
        {
            if (IsPublicOrInternal(method))
            {
                CollectStringParameters(method, method.Name, offenders);
            }
        }

        foreach (var property in type.GetProperties(flags).Where(p => p.PropertyType == typeof(string)))
        {
            var setter = property.GetSetMethod(nonPublic: true);
            if (setter is not null && IsPublicOrInternal(setter))
            {
                offenders.Add($"settable string property '{property.Name}'");
            }
        }

        offenders.Should().BeEmpty(
            "DisposableWorkloadContainer must generate its own name internally; no public or internal " +
            "member may accept a string a caller could use to supply one");
    }

    private static bool IsPublicOrInternal(MethodBase member) =>
        member.IsPublic || member.IsAssembly || member.IsFamilyOrAssembly;

    private static void CollectStringParameters(MethodBase member, string label, List<string> offenders)
    {
        foreach (var parameter in member.GetParameters().Where(p => p.ParameterType == typeof(string)))
        {
            offenders.Add($"{label}(..., string {parameter.Name}, ...)");
        }
    }
}
