using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

public class SandboxedPathResolverTests
{
    private static string MakeRoot() => Path.Combine(Path.GetTempPath(), "servyx-sandbox-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_SimpleRelativePath_Succeeds()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve("config.ini");

        result.Value.Should().Be("config.ini");
    }

    [Fact]
    public void Resolve_NestedRelativePath_NormalizesToForwardSlashes()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve(Path.Combine("saves", "world1", "level.sav"));

        result.Value.Should().Be("saves/world1/level.sav");
    }

    [Fact]
    public void Resolve_ForwardSlashRelativePath_Succeeds()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve("saves/world1/level.sav");

        result.Value.Should().Be("saves/world1/level.sav");
    }

    [Fact]
    public void Resolve_EmptyString_ResolvesToRootItself()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve(string.Empty);

        result.Value.Should().Be(string.Empty);
    }

    [Fact]
    public void Resolve_DotDotEscapingRoot_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("../escape.txt");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    /// <summary>
    /// <c>\</c> is a path separator on Windows but an ordinary file-name character on POSIX, so the same
    /// input is a genuine traversal on one platform and a single literal file name on the other. The
    /// resolver delegates separator semantics to <see cref="Path"/> and is therefore correct on both; this
    /// test asserts the truth of whichever platform it runs on rather than assuming Windows everywhere.
    /// </summary>
    [Fact]
    public void Resolve_BackslashDotDot_EscapesOnWindowsButIsALiteralNameOnPosix()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(@"..\escape.txt");

        if (OperatingSystem.IsWindows())
        {
            act.Should().Throw<PathEscapesSandboxException>();
        }
        else
        {
            // One literal name that cannot leave the root. Rejecting it would wrongly refuse a legal
            // POSIX file name, so the assertion here is that it resolves to exactly that name — not
            // merely that "nothing was thrown".
            act.Should().NotThrow();
            resolver.Resolve(@"..\escape.txt").Value.Should().Be(@"..\escape.txt");
        }
    }

    [Fact]
    public void Resolve_MultipleDotDotSegmentsEscapingRoot_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(Path.Combine("a", "b", "..", "..", "..", "escape.txt"));

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_DotDotThatStaysWithinRoot_Succeeds()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve(Path.Combine("saves", "..", "config.ini"));

        result.Value.Should().Be("config.ini");
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideRoot_Throws()
    {
        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var outside = Path.Combine(Path.GetTempPath(), "servyx-sandbox-outside-" + Guid.NewGuid().ToString("N"), "secret.txt");

        var act = () => resolver.Resolve(outside);

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_AbsolutePathInsideRoot_Succeeds()
    {
        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var inside = Path.Combine(root, "config.ini");

        var result = resolver.Resolve(inside);

        result.Value.Should().Be("config.ini");
    }

    [Fact]
    public void Resolve_UncPath_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(@"\\server\share\file.txt");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_ForwardSlashUncPath_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("//server/share/file.txt");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_DevicePath_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(@"\\?\C:\Windows\System32\config.ini");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_NullByte_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("config\0.ini");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_Null_ThrowsArgumentNullException()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOrWhitespaceRoot_Throws()
    {
        var act = () => new SandboxedPathResolver("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var upperCaseAbsolute = Path.Combine(root.ToUpperInvariant(), "File.txt");

        var act = () => resolver.Resolve(upperCaseAbsolute);

        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_TrailingSlashOnRoot_DoesNotAffectResolution()
    {
        var root = MakeRoot() + Path.DirectorySeparatorChar;
        var resolver = new SandboxedPathResolver(root);

        var result = resolver.Resolve("config.ini");

        result.Value.Should().Be("config.ini");
    }

    [Fact]
    public void Resolve_DeeplyNestedPath_NormalizesAllSeparators()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve(Path.Combine("a", "b", "c", "d", "file.txt"));

        result.Value.Should().Be("a/b/c/d/file.txt");
    }

    [Fact]
    public void Resolve_SameInputTwice_ProducesEqualTargetPaths()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var first = resolver.Resolve("saves/world1");
        var second = resolver.Resolve("saves/world1");

        first.Should().Be(second);
    }

    [Fact]
    public void Resolve_WhitespaceOnlyInput_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("   ");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    // --- Alternate Data Stream rejection (Windows only: colons are ordinary filename characters elsewhere) ---

    [Fact]
    public void Resolve_AlternateDataStreamSuffix_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("file.txt:stream");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_NestedAlternateDataStreamSuffix_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("sub/file.txt:$DATA");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_DriveRelativeColonSegment_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolver = new SandboxedPathResolver(MakeRoot());

        // "a:b" is drive-relative syntax on Windows; it must never resolve inside an unrelated sandbox.
        var act = () => resolver.Resolve("a:b");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    // --- Reserved Windows device names ---

    [Theory]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("NUL")]
    [InlineData("LPT9")]
    [InlineData("con.txt")]
    public void Resolve_ReservedDeviceNameSegment_Throws(string segment)
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve(segment);

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Theory]
    [InlineData("CONFIG")]
    [InlineData("CONSOLE")]
    public void Resolve_NamesThatMerelyStartWithAReservedPrefix_AreAccepted(string segment)
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var result = resolver.Resolve(segment);

        result.Value.Should().Be(segment);
    }

    [Fact]
    public void Resolve_ReservedDeviceNameNestedInSubdirectory_Throws()
    {
        var resolver = new SandboxedPathResolver(MakeRoot());

        var act = () => resolver.Resolve("saves/COM1/data.txt");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    // --- Classic prefix-bug regression guard: a naive `fullPath.StartsWith(root)` (without the
    // appended separator) would incorrectly treat a sibling directory that merely starts with the same
    // characters as the root as being "inside" it. This must never regress. ---

    [Fact]
    public void Resolve_SiblingDirectoryWithRootAsStringPrefix_IsRejected()
    {
        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var evilSibling = root + "-evil" + Path.DirectorySeparatorChar + "x";

        var act = () => resolver.Resolve(evilSibling);

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_ConcatenatedSiblingWithNoSeparator_IsRejected()
    {
        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var concatenatedSibling = root + "x";

        var act = () => resolver.Resolve(concatenatedSibling);

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public void Resolve_ActualPathInsideRoot_IsAcceptedAlongsidePrefixBugGuard()
    {
        var root = MakeRoot();
        var resolver = new SandboxedPathResolver(root);
        var inside = Path.Combine(root, "x");

        var result = resolver.Resolve(inside);

        result.Value.Should().Be("x");
    }

    [Fact]
    public void Resolve_PrefixBugGuard_AlsoHoldsWithTrailingSeparatorOnRoot()
    {
        var root = MakeRoot();
        var rootWithTrailingSeparator = root + Path.DirectorySeparatorChar;
        var resolver = new SandboxedPathResolver(rootWithTrailingSeparator);

        var evilSibling = root + "-evil" + Path.DirectorySeparatorChar + "x";
        var concatenatedSibling = root + "x";
        var inside = Path.Combine(root, "x");

        resolver.Invoking(r => r.Resolve(evilSibling)).Should().Throw<PathEscapesSandboxException>();
        resolver.Invoking(r => r.Resolve(concatenatedSibling)).Should().Throw<PathEscapesSandboxException>();
        resolver.Resolve(inside).Value.Should().Be("x");
    }
}
