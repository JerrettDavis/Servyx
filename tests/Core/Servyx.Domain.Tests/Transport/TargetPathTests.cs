using System.Reflection;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

public class TargetPathTests
{
    [Fact]
    public void Constructor_IsNotPublic()
    {
        var constructors = typeof(TargetPath).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var valueConstructor = constructors.Single(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(string));

        valueConstructor.IsPublic.Should().BeFalse();
    }

    [Fact]
    public void CannotBeConstructedFromOutsideAssembly_ViaPublicApi()
    {
        // The only public constructors on the record struct itself are the compiler-synthesized
        // parameterless one (unavoidable for a struct) — there is no public way to supply a Value.
        var publicConstructors = typeof(TargetPath).GetConstructors(BindingFlags.Instance | BindingFlags.Public);

        publicConstructors.Should().OnlyContain(c => c.GetParameters().Length == 0);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var resolver = new SandboxedPathResolver(Path.Combine(Path.GetTempPath(), "servyx-sandbox-" + Guid.NewGuid().ToString("N")));
        var path = resolver.Resolve("config.ini");

        path.ToString().Should().Be("config.ini");
    }

    [Fact]
    public void Default_HasNullValue_AndIsNotAValidatedPath()
    {
        // Unavoidable for a struct: default(TargetPath) is always constructible without going through
        // SandboxedPathResolver. Its Value is null, which is itself the signal that it was never
        // resolved/validated and must not be treated as a real, sandboxed path.
        var defaultPath = default(TargetPath);

        defaultPath.Value.Should().BeNull();
    }

    [Fact]
    public void Default_IsNotEqualToAnyResolvedPath()
    {
        var resolver = new SandboxedPathResolver(Path.Combine(Path.GetTempPath(), "servyx-sandbox-" + Guid.NewGuid().ToString("N")));
        var resolved = resolver.Resolve(string.Empty);

        // Both happen to touch "root itself" territory conceptually, but default's null Value must never
        // compare equal to a resolver-produced empty-string TargetPath.
        default(TargetPath).Should().NotBe(resolved);
    }
}
