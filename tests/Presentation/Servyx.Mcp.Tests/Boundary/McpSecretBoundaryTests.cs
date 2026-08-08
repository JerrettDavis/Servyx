using System.Reflection;
using Servyx.Mcp;
using Servyx.Mcp.Tests.Support;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.Boundary;

/// <summary>
/// Guards the secret boundary: no tool reaches <c>ISecretStore</c> (the live risk — a tool that bypassed
/// <c>IServerQueryService.GetServerDetailAsync</c> and resolved a secret directly would leak an unmasked
/// value), and no response record even has a slot shaped to carry one.
/// </summary>
/// <remarks>
/// Masking already happens upstream, in <c>ServerQueryService.BuildSettings</c>
/// (<c>setting.IsSecret ? "********" : value</c>) — <see cref="ServerSettingValueDto"/> passes
/// <see cref="Servyx.Application.Servers.ServerSettingValue.Authoritative"/> straight through rather than
/// re-masking or re-deriving it, so the property-shape assertion below is belt-and-braces, and the IL-call
/// assertion against <c>ISecretStore</c> is the one that actually matters — see
/// <see cref="No_method_in_the_mcp_assembly_calls_ISecretStore"/>.
/// </remarks>
public sealed class McpSecretBoundaryTests
{
    private static readonly Assembly McpAssembly = typeof(ServyxMcpServer).Assembly;

    private const string SecretStoreTypeName = "Servyx.Domain.Secrets.ISecretStore";
    private const string SecretLeaseTypeName = "Servyx.Domain.Secrets.SecretLease";

    [Fact]
    public void No_method_in_the_mcp_assembly_calls_ISecretStore()
    {
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            foreach (var method in IlScanner.DeclaredMethods(type))
            {
                foreach (var call in IlScanner.MethodCallsMadeBy(method))
                {
                    if (call.DeclaringType?.FullName == SecretStoreTypeName)
                    {
                        offenders.Add($"{type.FullName}.{method.Name} -> {call.DeclaringType.FullName}.{call.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "no tool may resolve a secret directly; every setting value must arrive already masked through " +
            $"IServerQueryService — found: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void No_response_record_declares_a_property_of_type_SecretLease()
    {
        // Near-structurally-impossible on its own (SecretLease.Value is a ReadOnlySpan<byte> and cannot
        // cross an async method boundary at all), kept as belt-and-braces alongside the load-bearing
        // IL-call assertion above.
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            if (type.Namespace is null || !type.Namespace.StartsWith("Servyx.Mcp", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.PropertyType.FullName == SecretLeaseTypeName)
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        offenders.Should().BeEmpty("no response record may declare a SecretLease-typed property — found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ServerSettingValueDto_carries_no_property_besides_the_already_masked_Authoritative_value()
    {
        var properties = typeof(ServerSettingValueDto).GetProperties().Select(p => p.Name).ToList();

        properties.Should().Contain("Authoritative");
        properties.Should().Contain("IsSecret");
        properties.Should().NotContain(
            name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Unmasked", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase),
            "a second, unmasked value property would be exactly the shape a masking bypass takes");
    }
}
