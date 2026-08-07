using Microsoft.Extensions.Configuration;
using Servyx.Domain.Secrets;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Tests for <see cref="SecretImport"/> — the one operator write path into <see cref="ISecretStore"/> for
/// connector credentials that have to arrive on disk (an SSH private key) before they can be imported.
/// </summary>
public sealed class SecretImportTests : IDisposable
{
    private const string Urn = "secret://host/example-remote/ssh/private-key";
    private const string SecondUrn = "secret://host/example-remote/ssh/known-hosts";

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup; nothing under test depends on it succeeding.
            }
        }
    }

    private string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"servyx-secret-import-test-{Guid.NewGuid():N}.key");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public async Task Import_writes_the_exact_file_bytes_including_trailing_newline()
    {
        var keyBytes = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\n-----END OPENSSH PRIVATE KEY-----\n"u8.ToArray();
        var path = WriteTempFile(keyBytes);

        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));
        var store = new RecordingSecretStore();
        var logger = new RecordingLogger();

        var report = await SecretImport.RunAsync(configuration, store, logger);

        report.Imported.Should().ContainSingle(u => u.Value == Urn);
        store.Writes.Should().ContainSingle();
        store.Writes[0].Should().Equal(keyBytes);
    }

    [Fact]
    public async Task Existing_secret_is_not_overwritten_and_is_reported_as_skipped()
    {
        var path = WriteTempFile("brand-new-bytes"u8.ToArray());
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));

        var store = new RecordingSecretStore();
        SecretUrn.TryParse(Urn, out var urn).Should().BeTrue();
        await store.SetAsync(urn, "already-here"u8.ToArray(), "test-setup");

        var logger = new RecordingLogger();
        var report = await SecretImport.RunAsync(configuration, store, logger);

        report.Imported.Should().BeEmpty();
        report.Skipped.Should().ContainSingle(u => u.Value == Urn);
        store.SetCalls.Should().Be(1, "the pre-existing write from setup, and nothing from the import");

        using var lease = await store.GetAsync(urn);
        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task Report_counts_are_accurate_across_a_mixed_batch()
    {
        var path = WriteTempFile("fresh-key-bytes"u8.ToArray());
        var configuration = Config(
            (SecretImport.SectionKey + ":" + Urn, path),
            (SecretImport.SectionKey + ":" + SecondUrn, path));

        var store = new RecordingSecretStore();
        SecretUrn.TryParse(SecondUrn, out var existing).Should().BeTrue();
        await store.SetAsync(existing, "already-there"u8.ToArray(), "test-setup");

        var report = await SecretImport.RunAsync(configuration, store, new RecordingLogger());

        report.Imported.Should().ContainSingle(u => u.Value == Urn);
        report.Skipped.Should().ContainSingle(u => u.Value == SecondUrn);
    }

    [Fact]
    public async Task Missing_configuration_section_is_a_no_op()
    {
        var configuration = Config(("Some:Other:Key", "value"));
        var store = new RecordingSecretStore();

        var report = await SecretImport.RunAsync(configuration, store, new RecordingLogger());

        report.Should().Be(SecretImportReport.Empty);
        store.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_named_but_unreadable_source_path_throws_naming_the_urn_and_path()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"servyx-does-not-exist-{Guid.NewGuid():N}.key");
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, missingPath));
        var store = new RecordingSecretStore();

        var act = async () => await SecretImport.RunAsync(configuration, store, new RecordingLogger());

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain(Urn);
        thrown.Which.Message.Should().Contain(missingPath);
        store.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_urn_key_throws_naming_the_offending_key()
    {
        const string badKey = "not-a-secret-urn";
        var configuration = Config((SecretImport.SectionKey + ":" + badKey, "C:\\anything"));
        var store = new RecordingSecretStore();

        var act = async () => await SecretImport.RunAsync(configuration, store, new RecordingLogger());

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain(badKey);
        store.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_source_file_throws()
    {
        var path = WriteTempFile([]);
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));
        var store = new RecordingSecretStore();

        var act = async () => await SecretImport.RunAsync(configuration, store, new RecordingLogger());

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain(Urn);
        store.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_successful_import_warns_that_the_plaintext_source_should_be_deleted()
    {
        var path = WriteTempFile("key-bytes"u8.ToArray());
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));
        var store = new RecordingSecretStore();
        var logger = new RecordingLogger();

        await SecretImport.RunAsync(configuration, store, logger);

        logger.Entries.Should().Contain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains(path)
            && e.Message.Contains(Urn));
    }

    [Fact]
    public async Task Logger_never_receives_the_secret_value()
    {
        const string secretText = "super-secret-private-key-material";
        var path = WriteTempFile(System.Text.Encoding.UTF8.GetBytes(secretText));
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));
        var store = new RecordingSecretStore();
        var logger = new RecordingLogger();

        await SecretImport.RunAsync(configuration, store, logger);

        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().OnlyContain(e => !e.Message.Contains(secretText));
    }

    [Fact]
    public async Task Cancellation_is_honoured_between_entries()
    {
        var path = WriteTempFile("key-bytes"u8.ToArray());
        var configuration = Config((SecretImport.SectionKey + ":" + Urn, path));
        var store = new RecordingSecretStore();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await SecretImport.RunAsync(configuration, store, new RecordingLogger(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
