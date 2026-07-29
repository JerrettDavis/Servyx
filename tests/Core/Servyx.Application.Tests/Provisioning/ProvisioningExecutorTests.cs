using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="ProvisioningExecutor"/>. The provider is an NSubstitute-substituted
/// <see cref="IProvisioningOperation"/>, so these tests are entirely provider-agnostic — nothing here
/// knows Docker exists, which is the point of the abstraction.
/// </summary>
public class ProvisioningExecutorTests
{
    private static readonly IReadOnlyDictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["servyx.managed"] = "true",
        ["servyx.instance-id"] = "srv-0001",
        ["servyx.job-id"] = "job-42",
    };

    private static ProvisionedResource Resource(string providerResourceId = "container-1") => new(
        Handle: new ResourceHandle("docker-container", providerResourceId, null, Tags),
        ConnectorId: "docker-local",
        Target: new TargetDescriptor(
            "docker",
            "npipe://./pipe/dockerDesktopLinuxEngine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerId"] = providerResourceId,
                ["rootPath"] = "/palworld",
            }),
        Facts: new ResourceFacts(null, "172.18.0.2", CostEstimate.Unknown("test"), DateTimeOffset.UnixEpoch));

    private static IProvisioningOperation Operation(ProvisionedResource? result = null)
    {
        var operation = Substitute.For<IProvisioningOperation>();
        operation.ProvisionerId.Returns("docker-container");
        operation.Region.Returns((string?)null);
        operation.Tags.Returns(Tags);
        operation.CreateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(result ?? Resource()));
        return operation;
    }

    [Fact]
    public async Task The_ledger_records_intent_strictly_before_the_create_call_is_made()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        var operation = Operation();

        await new ProvisioningExecutor(ledger).ExecuteAsync(operation, "job-42");

        Received.InOrder(() =>
        {
            ledger.RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
            operation.CreateAsync(Arg.Any<CancellationToken>());
            ledger.MarkCreatedAsync(Arg.Any<Guid>(), "container-1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_intent_row_carries_the_tags_that_are_about_to_be_applied()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        ProvisioningIntent? captured = null;
        ledger.RecordIntentAsync(Arg.Do<ProvisioningIntent>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await new ProvisioningExecutor(ledger).ExecuteAsync(Operation(), "job-42");

        captured.Should().NotBeNull();
        captured!.ProvisionerId.Should().Be("docker-container");
        captured.JobId.Should().Be("job-42");
        captured.Tags.Should().BeEquivalentTo(Tags);
        captured.LedgerRowId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Nothing_is_created_before_the_intent_row_is_durable()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        ledger.RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        var operation = Operation();

        var act = async () => await new ProvisioningExecutor(ledger).ExecuteAsync(operation);

        await act.Should().ThrowAsync<IOException>();
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_row_is_advanced_to_created_with_the_real_provider_assigned_id()
    {
        var ledger = new InMemoryProvisioningLedger();
        var executor = new ProvisioningExecutor(ledger);

        await executor.ExecuteAsync(Operation(Resource("abc123def456")), "job-42");

        var intended = await ledger.ListIntendedAsync("docker-container");
        intended.Should().BeEmpty("the row must no longer be Intended once the provider confirmed the resource");
    }

    [Fact]
    public async Task The_resource_the_operation_produced_is_returned_verbatim_including_its_transport_target()
    {
        var expected = Resource();
        var ledger = Substitute.For<IProvisioningLedger>();

        var actual = await new ProvisioningExecutor(ledger).ExecuteAsync(Operation(expected));

        actual.Should().BeSameAs(expected);
        actual.RequireTarget().Should().BeSameAs(expected.RequireTarget(), "the executor must not translate the descriptor on its way to the caller");
    }

    [Fact]
    public async Task A_create_failure_triggers_compensation_and_the_error_is_surfaced_not_swallowed()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("port already allocated"));

        var act = async () => await new ProvisioningExecutor(ledger).ExecuteAsync(operation, "job-42");

        var thrown = await act.Should().ThrowAsync<ProvisioningExecutionException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("port already allocated");
        thrown.Which.Compensated.Should().BeTrue();
        thrown.Which.LedgerRowId.Should().NotBe(Guid.Empty);

        await operation.Received(1).CompensateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_compensation_is_reported_alongside_the_original_failure()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("create failed"));
        operation.CompensateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("remove timed out"));

        var act = async () => await new ProvisioningExecutor(ledger).ExecuteAsync(operation);

        var thrown = await act.Should().ThrowAsync<ProvisioningExecutionException>();
        thrown.Which.Compensated.Should().BeFalse();
        thrown.Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(2);
        thrown.Which.Message.Should().Contain("may still exist at the provider");
    }

    [Fact]
    public async Task A_failed_creation_leaves_the_row_intended_so_a_later_sweep_can_find_it()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var act = async () => await new ProvisioningExecutor(ledger).ExecuteAsync(operation, "job-42");
        await act.Should().ThrowAsync<ProvisioningExecutionException>();

        var intended = await ledger.ListIntendedAsync("docker-container");
        intended.Should().HaveCount(1);
        intended[0].Tags.Should().BeEquivalentTo(Tags);
    }

    [Fact]
    public async Task A_ledger_failure_after_a_successful_create_is_surfaced_without_destroying_the_resource()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        ledger.MarkCreatedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        var operation = Operation();

        var act = async () => await new ProvisioningExecutor(ledger).ExecuteAsync(operation);

        var thrown = await act.Should().ThrowAsync<ProvisioningExecutionException>();
        thrown.Which.Message.Should().Contain("could not be advanced to Created");
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_null_operation_is_rejected()
    {
        var act = async () => await new ProvisioningExecutor(Substitute.For<IProvisioningLedger>()).ExecuteAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void A_null_ledger_is_rejected()
    {
        var act = () => new ProvisioningExecutor(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task The_in_memory_ledger_refuses_to_mark_a_row_created_that_was_never_intended()
    {
        var ledger = new InMemoryProvisioningLedger();

        var act = async () => await ledger.MarkCreatedAsync(Guid.NewGuid(), "abc", DateTimeOffset.UnixEpoch);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
