using System.Reflection;
using Servyx.Mcp;
using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Inventory;

/// <summary>
/// Proves — by reading IL, not by reading source and trusting nobody changes it — that this build's tool
/// surface never reaches any of the mutating Application-layer members it deliberately withholds. The
/// technique is adapted from
/// <c>tests/Presentation/Servyx.Web.Tests/Services/ProvisionerCompositionWriteGuardTests.cs</c>, which reads
/// <c>newobj</c> operands the same way this file reads <c>call</c>/<c>callvirt</c> operands (see
/// <see cref="Support.IlScanner"/>).
/// </summary>
/// <remarks>
/// <strong>Scope, and one acknowledged weaker spot.</strong> This scans every method body (including
/// compiler-generated async state-machine and lambda-closure types — <see cref="Assembly.GetTypes"/> already
/// flattens those into the assembly's type list, so no per-method lambda-discovery step is needed the way the
/// exemplar file needed one) declared anywhere in the <c>Servyx.Mcp</c> assembly for a direct
/// <c>call</c>/<c>callvirt</c> to a forbidden member. It does NOT prove a tool cannot reach
/// <see cref="Servyx.Application.Provisioning.IProvisioningDashboard"/> through reflection, dynamic
/// dispatch, or a delegate built elsewhere and invoked here — those are outside what an IL opcode scan can
/// see at all. <see cref="No_method_resolves_IProvisioningDashboard_as_a_generic_type_argument"/> narrows
/// that gap by additionally checking generic-method instantiations (e.g. <c>GetService&lt;T&gt;</c>) for the
/// forbidden type as a type argument, but a fully dynamic reach-around (e.g. via
/// <c>Type.GetType("...").GetMethod(...)</c>) would still be invisible to this file. That weaker case is
/// called out here explicitly rather than silently presented as equivalent to the direct-call proof above.
/// </remarks>
public sealed class McpWithheldOperationTests
{
    private static readonly Assembly McpAssembly = typeof(ServyxMcpServer).Assembly;

    private const string ProvisioningDashboardTypeName = "Servyx.Application.Provisioning.IProvisioningDashboard";

    /// <summary>
    /// Every mutating Application-layer member this build's tool surface must never reach. Each entry names
    /// the declaring interface's full name and the member name <c>call</c>/<c>callvirt</c> would resolve to.
    /// </summary>
    private static readonly IReadOnlyList<(string DeclaringType, string MemberName)> ForbiddenMembers =
    [
        ("Servyx.Application.Backups.IBackupDashboard", "CreateAsync"),
        ("Servyx.Application.Backups.IBackupDashboard", "ApplyRestoreAsync"),
        ("Servyx.Application.Backups.IBackupDashboard", "ApplyPruneAsync"),
        ("Servyx.Domain.Rcon.IRconSession", "SendRawAsync"),
        ("Servyx.Domain.Lifecycle.IServerLifecycle", "RecreateAsync"),
    ];

    [Fact]
    public void The_withheld_method_list_names_every_mutating_application_member_this_build_does_not_expose()
    {
        // Anti-vacuity + a documented count, so a future edit that accidentally shrinks the list is loud —
        // see the companion assertion below.
        ForbiddenMembers.Should().NotBeEmpty();
        ForbiddenMembers.Select(m => $"{m.DeclaringType}.{m.MemberName}").Should().BeEquivalentTo(
        [
            "Servyx.Application.Backups.IBackupDashboard.CreateAsync",
            "Servyx.Application.Backups.IBackupDashboard.ApplyRestoreAsync",
            "Servyx.Application.Backups.IBackupDashboard.ApplyPruneAsync",
            "Servyx.Domain.Rcon.IRconSession.SendRawAsync",
            "Servyx.Domain.Lifecycle.IServerLifecycle.RecreateAsync",
        ]);
    }

    [Fact]
    public void The_withheld_method_list_may_not_shrink_without_changing_this_assertion()
    {
        ForbiddenMembers.Count.Should().Be(5,
            "this exact count is pinned so a future edit that silently drops an entry (rather than " +
            "deliberately updating both this assertion and the list) fails loudly");
    }

    [Fact]
    public void No_tool_reaches_a_withheld_mutating_backup_or_lifecycle_or_rcon_member()
    {
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            foreach (var method in IlScanner.DeclaredMethods(type))
            {
                foreach (var call in IlScanner.MethodCallsMadeBy(method))
                {
                    var declaringTypeName = call.DeclaringType?.FullName;
                    if (declaringTypeName is null)
                    {
                        continue;
                    }

                    var isForbidden = ForbiddenMembers.Any(
                        forbidden => declaringTypeName == forbidden.DeclaringType && call.Name == forbidden.MemberName);

                    if (isForbidden)
                    {
                        offenders.Add($"{type.FullName}.{method.Name} -> {declaringTypeName}.{call.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "this build must never call a withheld mutating member — found: " + string.Join("; ", offenders));
    }

    [Fact]
    public void No_tool_reaches_IProvisioningDashboard_at_all()
    {
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            foreach (var method in IlScanner.DeclaredMethods(type))
            {
                foreach (var call in IlScanner.MethodCallsMadeBy(method))
                {
                    if (call.DeclaringType?.FullName == ProvisioningDashboardTypeName)
                    {
                        offenders.Add($"{type.FullName}.{method.Name} -> {call.DeclaringType.FullName}.{call.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            $"this build must never call any member of {ProvisioningDashboardTypeName} — found: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void No_method_resolves_IProvisioningDashboard_as_a_generic_type_argument()
    {
        // Narrows (but does not close — see class remarks) the gap the direct-call scan above cannot see:
        // a call shaped like services.GetService<IProvisioningDashboard>() resolves to a generic method
        // instantiation whose declaring member is ServiceProviderServiceExtensions.GetService, not
        // IProvisioningDashboard itself, so the direct-declaring-type check above would miss it. This is
        // explicitly a WEAKER, supplementary check, not equivalent to the IL-call proof above.
        var offenders = new List<string>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            foreach (var method in IlScanner.DeclaredMethods(type))
            {
                foreach (var call in IlScanner.MethodCallsMadeBy(method))
                {
                    if (call is not MethodInfo { IsGenericMethod: true } generic)
                    {
                        continue;
                    }

                    if (generic.GetGenericArguments().Any(arg => arg.FullName == ProvisioningDashboardTypeName))
                    {
                        offenders.Add($"{type.FullName}.{method.Name} -> {call.Name}<{ProvisioningDashboardTypeName}>");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "this build must never resolve IProvisioningDashboard via a generic service lookup — found: " +
            string.Join("; ", offenders));
    }
}
