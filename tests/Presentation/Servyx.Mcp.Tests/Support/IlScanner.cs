using System.Reflection;
using System.Reflection.Emit;

namespace Servyx.Mcp.Tests.Support;

/// <summary>
/// A minimal IL reader shared by every test in this project that needs to prove "nothing in this assembly
/// calls X" structurally rather than by convention. Adapted from
/// <c>tests/Presentation/Servyx.Web.Tests/Services/ProvisionerCompositionWriteGuardTests.cs</c>'s
/// <c>newobj</c>-scanning technique, retargeted at <c>call</c>/<c>callvirt</c> operands.
/// </summary>
/// <remarks>
/// <strong>Fails closed.</strong> An opcode this reader does not recognise, or an operand type it does not
/// know how to skip past, throws rather than letting the scan silently stop early and report a method clean
/// that it never actually finished reading — the same posture the exemplar file above documents and this
/// class exists to preserve, not weaken.
/// </remarks>
internal static class IlScanner
{
    private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodeTable();

    /// <summary>Every method a method's own IL invokes via <c>call</c> or <c>callvirt</c>.</summary>
    public static IReadOnlyList<MethodBase> MethodCallsMadeBy(MethodBase method)
    {
        var il = SafeBody(method);
        if (il is null)
        {
            return [];
        }

        var declaringType = method.DeclaringType;
        var typeArguments = declaringType is { IsGenericType: true } ? declaringType.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;

        var calls = new List<MethodBase>();
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
                    + $"{method.DeclaringType?.FullName}.{method.Name}. This scanner refuses to report a method "
                    + "it could not finish reading as clean.");
            }

            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt)
            {
                MethodBase? resolved;
                try
                {
                    resolved = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), typeArguments, methodArguments);
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Could not resolve a call target referenced by {method.DeclaringType?.FullName}.{method.Name}. "
                        + "This scanner refuses to report a method it could not finish reading as clean.", ex);
                }

                if (resolved is not null)
                {
                    calls.Add(resolved);
                }
            }

            offset += OperandSize(opcode, il, offset);
        }

        return calls;
    }

    /// <summary>Every method (and constructor) directly declared on <paramref name="type"/>.</summary>
    public static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        return type.GetMethods(Flags).Cast<MethodBase>().Concat(type.GetConstructors(Flags));
    }

    /// <summary>Every type an assembly can surface. A type that fails to load must not take the whole scan with it.</summary>
    public static IEnumerable<Type> LoadableTypes(Assembly assembly)
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
            $"Unhandled IL operand type {opcode.OperandType}; this reader cannot advance past it and will not guess."),
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
}
