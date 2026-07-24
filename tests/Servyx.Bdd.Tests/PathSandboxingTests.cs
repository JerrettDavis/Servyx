using Servyx.Domain.Transport;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// Every path a caller supplies must be proven to stay within the server's sandbox root before Servyx
/// will touch a filesystem with it. <see cref="SandboxedPathResolver"/> is the sole gate: these scenarios
/// drive it directly against the family of escape techniques it must reject.
/// </summary>
[Feature("Path sandboxing", "As an operator I trust that Servyx can never be tricked into reading or writing outside a server's own data root")]
public class PathSandboxingTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private static string MakeRoot() => Path.Combine(Path.GetTempPath(), "servyx-bdd-sandbox-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string> EscapeAttempts()
    {
        var root = MakeRoot();
        var data = new TheoryData<string, string>
        {
            { "a parent-directory traversal", "../etc/passwd" },
            { "a backslash parent-directory traversal", @"..\windows\system32" },
            { "an absolute path outside the root", Path.Combine(Path.GetTempPath(), "servyx-bdd-outside-" + Guid.NewGuid().ToString("N"), "secret.txt") },
            { "a UNC path", @"\\server\share\file.txt" },
            { "a reserved device name", "CON" },
        };

        if (OperatingSystem.IsWindows())
        {
            data.Add("an NTFS alternate data stream", "file.txt:stream");
        }

        return data;
    }

    [Scenario("An escape attempt is rejected before it ever reaches the filesystem", "unit")]
    [Theory]
    [MemberData(nameof(EscapeAttempts))]
    [DisableOptimization]
    public async Task EscapeAttempt_IsRejected(string _, string attemptedPath)
        => await Given("a sandboxed path resolver scoped to a server's data root", () => new SandboxedPathResolver(MakeRoot()))
            .When("the escape attempt is resolved", async Task<Exception?> (resolver) =>
            {
                try
                {
                    resolver.Resolve(attemptedPath);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then("it is rejected with PathEscapesSandboxException", ex => Task.FromResult(ex is PathEscapesSandboxException))
            .AssertPassed();

    [Scenario("A sibling-directory prefix attack is rejected", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task SiblingDirectoryPrefixAttack_IsRejected()
        => await Given("a sandbox rooted at a \"data\" directory", () =>
            {
                var root = MakeRoot();
                return (Resolver: new SandboxedPathResolver(root), Root: root);
            })
            .When("a path under the sibling \"data-evil\" directory is resolved", async Task<Exception?> (state) =>
            {
                try
                {
                    state.Resolver.Resolve(state.Root + "-evil" + Path.DirectorySeparatorChar + "secret.txt");
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then(
                "it is rejected, because a naive string-prefix check would wrongly treat \"data-evil\" as inside \"data\"",
                ex => Task.FromResult(ex is PathEscapesSandboxException))
            .AssertPassed();

    [Scenario("A legitimate relative path is accepted and normalized", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task LegitimateRelativePath_IsAcceptedAndNormalized()
        => await Given("a sandboxed path resolver", () => new SandboxedPathResolver(MakeRoot()))
            .When("a nested relative save path is resolved", async Task<TargetPath> (resolver) => await Task.FromResult(resolver.Resolve(Path.Combine("saves", "world1", "level.sav"))))
            .Then("it succeeds and is normalized to forward slashes", path => Task.FromResult(path.Value == "saves/world1/level.sav"))
            .AssertPassed();

    [Scenario("A name that merely starts with a reserved device prefix is accepted", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task NameStartingWithReservedPrefix_IsAccepted()
        => await Given("a sandboxed path resolver", () => new SandboxedPathResolver(MakeRoot()))
            .When("\"CONFIG\" is resolved", async Task<TargetPath> (resolver) => await Task.FromResult(resolver.Resolve("CONFIG")))
            .Then("it is accepted, because CONFIG is not the reserved device name CON", path => Task.FromResult(path.Value == "CONFIG"))
            .AssertPassed();
}
