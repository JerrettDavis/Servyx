using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The write-guard promise, asserted against the composition root that actually ships.
/// </summary>
/// <remarks>
/// <para>
/// <c>TransportWriteGuardArchitectureTests</c> already asserts that every <c>AddServyx*</c> extension hands
/// out write-guarded sessions. It cannot see this file: <see cref="ProvisionerComposition"/> registers no
/// <see cref="ITransport"/> at all — deliberately, see its remarks — and builds its transports inline, inside
/// private methods, as constructor arguments to the provisioners it registers. Every registration that theory
/// covered was clean while <c>AddProcess</c> handed <see cref="LocalProcessProvisioner"/> a bare
/// <c>LocalProcessTransport</c>, and its own remarks asserted that gap as permanent. The defect is fixed; this
/// file is what stops the next provisioner reintroducing it.
/// </para>
/// <para>
/// <b>Nothing here is hand-listed.</b> The methods under test are discovered by reflecting over
/// <see cref="ProvisionerComposition"/>'s private <c>Add*</c> surface, and the options each one needs come out
/// of <see cref="ProvisionerWiringOptions"/> by matching parameter type to property type. A provisioner added
/// next month is covered the moment it is written, and a provisioner this file cannot construct options for
/// fails <see cref="OptionsFor"/> rather than being quietly skipped.
/// </para>
/// <para>
/// <b>And nothing here passes by omission.</b> The object graph of a registered provisioner is walked for
/// transports, which is a technique that could silently find none — so it is cross-checked against the IL of
/// the composition method itself: every transport type the method's <c>newobj</c> instructions build must be
/// reachable from the provisioner it registered. A transport constructed into somewhere the walk cannot see
/// fails <see cref="Every_transport_a_composition_method_constructs_is_reachable_from_what_it_registers"/>
/// instead of leaving the guard assertion vacuously true.
/// </para>
/// </remarks>
public class ProvisionerCompositionWriteGuardTests
{
    /// <summary>How deep the object walk descends before giving up. Far past any real provisioner.</summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// Every private composition method on <see cref="ProvisionerComposition"/>: static, named <c>Add*</c>,
    /// taking a service collection and one options record. Discovered, never listed.
    /// </summary>
    private static readonly IReadOnlyList<MethodInfo> CompositionMethods = DiscoverCompositionMethods();

    /// <summary>The IL opcode table, read off <see cref="OpCodes"/> rather than transcribed.</summary>
    private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodeTable();

    /// <summary>Every provisioner switched on at once, as the wiring options read them.</summary>
    private static readonly ProvisionerWiringOptions EveryProvisionerEnabled = ReadEveryProvisioner();

    /// <summary>The name of each discovered composition method, for the theories below.</summary>
    public static TheoryData<string> EveryCompositionMethod()
    {
        var data = new TheoryData<string>();

        foreach (var method in CompositionMethods)
        {
            data.Add(method.Name);
        }

        return data;
    }

    [Fact]
    public void Every_provisioner_the_wiring_options_carry_has_a_composition_method_this_file_reaches()
    {
        // The discovery above is the whole mechanism, so it is asserted rather than assumed: if it silently
        // matched nothing, every theory below would pass with no case at all.
        CompositionMethods.Should().NotBeEmpty(
            "the Add* methods are discovered by reflection; finding none means the shape this file looks for "
            + "has changed and it is now asserting nothing");

        CompositionMethods.Count.Should().Be(
            EveryProvisionerEnabled.ProvisionerIds.Count,
            "every provisioner ProvisionerWiringOptions can switch on is composed by exactly one Add* method "
            + "and vice versa — a mismatch means a provisioner is composed that this file cannot supply "
            + "options for, or configured that nothing composes");
    }

    [Theory]
    [MemberData(nameof(EveryCompositionMethod))]
    public void Every_transport_a_composition_method_hands_a_provisioner_is_write_guarded(string methodName)
    {
        var method = CompositionMethods.Single(m => m.Name == methodName);
        using var provider = Compose(method);

        var provisioners = provider.GetServices<IProvisioner>().ToList();
        provisioners.Should().ContainSingle(
            $"ProvisionerComposition.{methodName} must register exactly one IProvisioner for this test to "
            + "have something to walk");

        var provisioner = provisioners[0];
        var unguarded = string.Join(
            ", ",
            TransportsReachableFrom(provisioner)
                .Where(sighting => !sighting.Guarded)
                .Select(sighting => $"{sighting.Path} ({sighting.Transport.GetType().Name})"));

        unguarded.Should().BeEmpty(
            $"ProvisionerComposition.{methodName} must hand {provisioner.GetType().Name} only transports "
            + "wrapped in a WriteGuardedTransport, so every execution target it opens comes out of "
            + "WriteGuardedExecutionTarget — an unguarded one here is a write path that no "
            + "Servyx:Servers:<name>:WriteMode ever authorised");
    }

    [Theory]
    [MemberData(nameof(EveryCompositionMethod))]
    public void Every_transport_a_composition_method_constructs_is_reachable_from_what_it_registers(
        string methodName)
    {
        var method = CompositionMethods.Single(m => m.Name == methodName);

        var constructed = TransportTypesConstructedBy(method);

        using var provider = Compose(method);
        var provisioner = provider.GetServices<IProvisioner>().Single();
        var reachable = TransportsReachableFrom(provisioner)
            .Select(sighting => sighting.Transport.GetType())
            .Distinct()
            .ToList();

        var unreachable = string.Join(", ", constructed.Except(reachable).Select(type => type.Name));

        unreachable.Should().BeEmpty(
            $"ProvisionerComposition.{methodName} builds those transports, but none of them can be reached "
            + $"from the {provisioner.GetType().Name} it registers, so "
            + $"{nameof(Every_transport_a_composition_method_hands_a_provisioner_is_write_guarded)} cannot "
            + "prove they are guarded. A transport this file cannot see is a blind spot, and a blind spot "
            + "fails here rather than passing by omission");
    }

    [Fact]
    public void The_entry_point_builds_no_transport_of_its_own()
    {
        var entry = typeof(ProvisionerComposition).GetMethod(
            nameof(ProvisionerComposition.AddServyxConfiguredProvisioners),
            BindingFlags.Public | BindingFlags.Static);

        entry.Should().NotBeNull("the public entry point is what Program.cs calls");

        var constructed = string.Join(", ", TransportTypesConstructedBy(entry!).Select(type => type.Name));

        constructed.Should().BeEmpty(
            "every transport must be built inside one of the private Add* helpers this file enumerates. One "
            + "built inline in AddServyxConfiguredProvisioners itself would belong to no discovered method "
            + "and so would be covered by no theory above");
    }

    [Fact]
    public void No_other_path_in_the_web_assembly_builds_a_transport_without_a_guard()
    {
        // Weaker than the walk above — it proves only that a guard is constructed alongside, not that it
        // wraps the right thing — but it is the only assertion that covers composition written anywhere
        // else in Servyx.Web, including Program.cs's SSH backup block.
        var offenders = new List<string>();

        foreach (var type in LoadableTypes(typeof(ProvisionerComposition).Assembly))
        {
            foreach (var method in DeclaredMethods(type))
            {
                var constructed = TransportTypesBuiltDirectlyBy(method);

                if (constructed.Count == 0 || constructed.Contains(typeof(WriteGuardedTransport)))
                {
                    continue;
                }

                offenders.Add(
                    $"{type.FullName}.{method.Name} -> {string.Join("/", constructed.Select(t => t.Name))}");
            }
        }

        string.Join("; ", offenders).Should().BeEmpty(
            "a method in Servyx.Web that constructs a transport must construct the WriteGuardedTransport over "
            + "it in the same place; the composition root registers no ITransport, so nothing downstream can "
            + "add the guard later");
    }

    private static IReadOnlyList<MethodInfo> DiscoverCompositionMethods() =>
        [.. typeof(ProvisionerComposition)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(IsCompositionMethod)
            .OrderBy(method => method.Name, StringComparer.Ordinal)];

    private static bool IsCompositionMethod(MethodInfo method)
    {
        if (!method.Name.StartsWith("Add", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 2 && parameters[0].ParameterType == typeof(IServiceCollection);
    }

    private static ProvisionerWiringOptions ReadEveryProvisioner()
    {
        var configuration = ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled());
        return ProvisionerWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));
    }

    /// <summary>
    /// The options record a composition method needs, taken from <see cref="ProvisionerWiringOptions"/> by
    /// type. Throws rather than skipping: a provisioner whose options this file cannot supply is exactly the
    /// provisioner nobody is checking.
    /// </summary>
    private static object OptionsFor(MethodInfo method)
    {
        var optionsType = method.GetParameters()[1].ParameterType;

        var property = typeof(ProvisionerWiringOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate => candidate.PropertyType == optionsType);

        if (property is null)
        {
            throw new InvalidOperationException(
                $"ProvisionerComposition.{method.Name} takes {optionsType.Name}, but ProvisionerWiringOptions "
                + "exposes no single property of that type, so this test cannot construct a case for it. Give "
                + "the provisioner a wiring property rather than leaving its composition unchecked.");
        }

        return property.GetValue(EveryProvisionerEnabled)
            ?? throw new InvalidOperationException(
                $"ProvisionerWiringTests.AllEnabled() leaves '{property.Name}' null, so the case for "
                + $"ProvisionerComposition.{method.Name} would cover nothing. Switch the provisioner on there.");
    }

    /// <summary>
    /// Runs one composition method against the container it would see in <c>Program.cs</c>: the cross-cutting
    /// services and nothing else, so whatever comes back is this method's own work.
    /// </summary>
    private static ServiceProvider Compose(MethodInfo method)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());

        method.Invoke(null, [services, OptionsFor(method)]);

        return services.BuildServiceProvider();
    }

    /// <summary>One transport found somewhere in a provisioner's object graph.</summary>
    /// <param name="Transport">The transport itself.</param>
    /// <param name="Path">Where it was found, as a field path from the provisioner.</param>
    /// <param name="Guarded">
    /// Whether it is a <see cref="WriteGuardedTransport"/> or sits behind one.
    /// </param>
    private sealed record Sighting(ITransport Transport, string Path, bool Guarded);

    private static IReadOnlyList<Sighting> TransportsReachableFrom(object root)
    {
        var found = new List<Sighting>();
        Walk(root, root.GetType().Name, false, new HashSet<object?>(ReferenceEqualityComparer.Instance), found, 0);
        return found;
    }

    private static void Walk(
        object? node,
        string path,
        bool behindGuard,
        HashSet<object?> seen,
        List<Sighting> found,
        int depth)
    {
        if (node is null || depth > MaxDepth || node.GetType().IsValueType || !seen.Add(node))
        {
            return;
        }

        if (node is ITransport transport)
        {
            var guard = transport as WriteGuardedTransport;
            found.Add(new Sighting(transport, path, behindGuard || guard is not null));

            if (guard is not null)
            {
                Walk(guard.Inner, $"{path}.Inner", true, seen, found, depth + 1);
                return;
            }
        }

        foreach (var (child, childPath) in Children(node, path))
        {
            Walk(child, childPath, false, seen, found, depth + 1);
        }
    }

    /// <summary>
    /// What the walk descends into: the elements of a framework collection, and the instance fields of a
    /// Servyx type. It deliberately does not walk into third-party graphs — a transport parked inside one
    /// would be unreachable here, which is what the IL cross-check exists to turn into a failure.
    /// </summary>
    private static IEnumerable<(object? Child, string Path)> Children(object node, string path)
    {
        var type = node.GetType();

        if (type == typeof(string))
        {
            yield break;
        }

        if (node is IEnumerable sequence && IsFrameworkCollection(type))
        {
            var index = 0;

            foreach (var item in sequence)
            {
                yield return (item, $"{path}[{index++}]");
            }

            yield break;
        }

        if (!IsServyxType(type))
        {
            yield break;
        }

        foreach (var field in InstanceFields(type))
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string))
            {
                continue;
            }

            yield return (field.GetValue(node), $"{path}.{field.Name}");
        }
    }

    private static bool IsFrameworkCollection(Type type) =>
        type.IsArray || type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true;

    private static bool IsServyxType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Servyx", StringComparison.Ordinal) == true;

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
        for (var current = type; current is not null && IsServyxType(current); current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    /// <summary>
    /// The transport types a method's <c>newobj</c> instructions build, including those in the lambdas the
    /// compiler lifted out of it — which, for this file, is where every transport is actually constructed.
    /// </summary>
    private static IReadOnlyList<Type> TransportTypesConstructedBy(MethodInfo method) =>
        [.. MethodAndItsLambdas(method)
            .SelectMany(TypesConstructedBy)
            .Where(type => typeof(ITransport).IsAssignableFrom(type))
            .Distinct()];

    private static IReadOnlyList<Type> TransportTypesBuiltDirectlyBy(MethodBase method) =>
        [.. TypesConstructedBy(method)
            .Where(type => typeof(ITransport).IsAssignableFrom(type))
            .Distinct()];

    private static IEnumerable<MethodBase> MethodAndItsLambdas(MethodInfo method)
    {
        yield return method;

        var marker = $"<{method.Name}>";

        foreach (var type in SelfAndNested(method.DeclaringType!))
        {
            foreach (var candidate in DeclaredMethods(type))
            {
                if (candidate.Name.Contains(marker, StringComparison.Ordinal))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<Type> SelfAndNested(Type type)
    {
        yield return type;

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var descendant in SelfAndNested(nested))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        return type.GetMethods(Flags).Cast<MethodBase>().Concat(type.GetConstructors(Flags));
    }

    /// <summary>
    /// Every type a method constructs with <c>newobj</c>, read straight out of its IL. An opcode this reader
    /// does not understand throws, because a method it cannot finish reading is a method it cannot vouch for.
    /// </summary>
    private static IReadOnlyList<Type> TypesConstructedBy(MethodBase method)
    {
        var il = SafeBody(method);

        if (il is null)
        {
            return [];
        }

        var typeArguments = method.DeclaringType is { IsGenericTypeDefinition: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        var constructed = new List<Type>();
        var offset = 0;

        while (offset < il.Length)
        {
            var code = (short)il[offset++];

            if (code == 0xFE)
            {
                code = unchecked((short)(0xFE00 | il[offset++]));
            }

            if (!Opcodes.TryGetValue(code, out var opcode))
            {
                throw new InvalidOperationException(
                    $"Unrecognised IL opcode 0x{code:X4} at offset {offset - 1} of "
                    + $"{method.DeclaringType?.FullName}.{method.Name}. This test refuses to report a method "
                    + "it could not finish reading as clean.");
            }

            if (opcode == OpCodes.Newobj)
            {
                var resolved = method.Module
                    .ResolveMethod(BitConverter.ToInt32(il, offset), typeArguments, methodArguments)?
                    .DeclaringType;

                if (resolved is not null)
                {
                    constructed.Add(resolved);
                }
            }

            offset += OperandSize(opcode, il, offset);
        }

        return constructed;
    }

    /// <summary>
    /// The IL of a method, or null when it has none to read — abstract, extern, or an interface declaration.
    /// </summary>
    private static byte[]? SafeBody(MethodBase method) =>
        method.IsAbstract ? null : method.GetMethodBody()?.GetILAsByteArray();

    private static int OperandSize(OpCode opcode, byte[] il, int offset) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget
            or OperandType.InlineField
            or OperandType.InlineI
            or OperandType.InlineMethod
            or OperandType.InlineSig
            or OperandType.InlineString
            or OperandType.InlineTok
            or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
        _ => throw new InvalidOperationException(
            $"Unhandled IL operand type {opcode.OperandType}; this reader cannot advance past it and will not "
            + "guess."),
    };

    private static Dictionary<short, OpCode> BuildOpcodeTable()
    {
        var table = new Dictionary<short, OpCode>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
            {
                table[opcode.Value] = opcode;
            }
        }

        return table;
    }

    /// <summary>
    /// The types an assembly can surface, for the same reason <c>TransportWriteGuardArchitectureTests</c>
    /// needs it: a type that fails to load must not take the whole scan with it.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
