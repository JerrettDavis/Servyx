using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Asserts the exact argv and declared <see cref="CommandIntent"/> of every <see cref="DockerCli"/> factory.
/// The important case is <see cref="ExecReadOnly_and_Exec_produce_identical_argv_but_different_intent"/>:
/// intent is declared by the caller, never derived from the command text, and identical argv with different
/// intent is the only way to prove that in a unit test.
/// </summary>
public class DockerCliTests
{
    [Fact]
    public void Ps_declares_read_only_intent_with_json_format_argv()
    {
        var spec = DockerCli.Ps();

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("container", "ls", "--all", "--no-trunc", "--format", "{{json .}}");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public void Inspect_declares_read_only_intent()
    {
        var spec = DockerCli.Inspect("palworld");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("container", "inspect", "palworld");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inspect_rejects_null_or_whitespace_container(string? container)
    {
        var act = () => DockerCli.Inspect(container!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Logs_declares_read_only_intent_with_timestamps_and_tail()
    {
        var spec = DockerCli.Logs("palworld", 40);

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("logs", "--tail", "40", "--timestamps", "palworld");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public void Logs_formats_tail_lines_using_invariant_culture()
    {
        // A culture that uses non-ASCII digit grouping (e.g. Arabic-Indic digits) would corrupt a
        // culture-sensitive ToString(); invariant formatting is what keeps this "40", not "٤٠".
        var spec = DockerCli.Logs("c", 40);

        spec.Arguments[2].Should().Be("40");
    }

    [Fact]
    public void Logs_rejects_negative_tail_lines()
    {
        var act = () => DockerCli.Logs("palworld", -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Logs_rejects_null_or_whitespace_container(string? container)
    {
        var act = () => DockerCli.Logs(container!, 10);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stats_declares_read_only_intent_with_no_stream_and_json_format()
    {
        var spec = DockerCli.Stats("palworld");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("stats", "--no-stream", "--format", "{{json .}}", "palworld");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public void Stats_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Stats("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Version_declares_read_only_intent_with_json_format()
    {
        var spec = DockerCli.Version();

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("version", "--format", "{{json .}}");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public void ExecReadOnly_declares_read_only_intent()
    {
        var spec = DockerCli.ExecReadOnly("palworld", ["rcon-cli", "Info"]);

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("exec", "palworld", "rcon-cli", "Info");
        spec.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public void ExecReadOnly_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.ExecReadOnly(" ", ["rcon-cli", "Info"]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExecReadOnly_rejects_null_argv()
    {
        var act = () => DockerCli.ExecReadOnly("palworld", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Start_relies_on_the_default_mutating_intent()
    {
        var spec = DockerCli.Start("palworld");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("start", "palworld");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Start_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Start(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stop_relies_on_the_default_mutating_intent_with_invariant_timeout()
    {
        var spec = DockerCli.Stop("palworld", 30);

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("stop", "--time", "30", "palworld");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Stop_rejects_negative_timeout()
    {
        var act = () => DockerCli.Stop("palworld", -5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Stop_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Stop(null!, 30);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Restart_relies_on_the_default_mutating_intent()
    {
        var spec = DockerCli.Restart("palworld");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("restart", "palworld");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Restart_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Restart("\t");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exec_relies_on_the_default_mutating_intent()
    {
        var spec = DockerCli.Exec("palworld", ["rcon-cli", "Shutdown"]);

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("exec", "palworld", "rcon-cli", "Shutdown");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Exec_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Exec(" ", ["rcon-cli", "Shutdown"]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exec_rejects_null_argv()
    {
        var act = () => DockerCli.Exec("palworld", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Pull_relies_on_the_default_mutating_intent()
    {
        var spec = DockerCli.Pull("thijsvanloef/palworld-server-docker:latest");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("pull", "thijsvanloef/palworld-server-docker:latest");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Pull_rejects_null_or_whitespace_image()
    {
        var act = () => DockerCli.Pull("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Kill_declares_mutating_intent()
    {
        var spec = DockerCli.Kill("palworld");

        spec.Executable.Should().Be("docker");
        spec.Arguments.Should().Equal("kill", "palworld");
        spec.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public void Kill_omits_the_signal_flag_when_no_signal_is_given()
    {
        var spec = DockerCli.Kill("palworld", signal: null);

        spec.Arguments.Should().NotContain("--signal");
        spec.Arguments.Should().Equal("kill", "palworld");
    }

    [Fact]
    public void Kill_includes_the_signal_flag_when_a_signal_is_given()
    {
        var spec = DockerCli.Kill("palworld", "SIGTERM");

        spec.Arguments.Should().Equal("kill", "--signal", "SIGTERM", "palworld");
    }

    [Fact]
    public void Kill_rejects_null_or_whitespace_container()
    {
        var act = () => DockerCli.Kill(" ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Kill_rejects_whitespace_signal()
    {
        var act = () => DockerCli.Kill("palworld", "   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExecReadOnly_and_Exec_produce_identical_argv_but_different_intent()
    {
        IReadOnlyList<string> argv = ["rcon-cli", "Info"];

        var readOnlySpec = DockerCli.ExecReadOnly("palworld", argv);
        var mutatingSpec = DockerCli.Exec("palworld", argv);

        readOnlySpec.Executable.Should().Be(mutatingSpec.Executable);
        readOnlySpec.Arguments.Should().Equal(mutatingSpec.Arguments);

        readOnlySpec.Intent.Should().Be(CommandIntent.ReadOnly);
        mutatingSpec.Intent.Should().Be(CommandIntent.Mutating);
        readOnlySpec.Intent.Should().NotBe(mutatingSpec.Intent);
    }

    [Theory]
    [MemberData(nameof(GoTemplateFormatSpecs))]
    public void Go_template_format_strings_are_preserved_verbatim_and_not_pre_escaped(CommandSpec spec)
    {
        // {{json .}} is a Go text/template expression consumed by the docker CLI itself; it must survive
        // construction and later shell-quoting completely unmangled, or docker will fail to parse it.
        spec.Arguments.Should().Contain("{{json .}}");
    }

    public static TheoryData<CommandSpec> GoTemplateFormatSpecs() => new()
    {
        DockerCli.Ps(),
        DockerCli.Stats("palworld"),
        DockerCli.Version(),
    };
}
