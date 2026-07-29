using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Tests for the write-ahead resource ledger: the intent row must be writable before the provider knows
/// anything, must be findable by state afterwards, and must be able to be promoted in place.
/// </summary>
public class ProvisionedResourceRecordTests
{
    [Fact]
    public void IntentRow_IsWritable_BeforeAnyProviderResourceIdExists()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            // This is the shape of the row that must land on disk before the billable API call: no provider
            // resource id, because the provider has not been asked yet.
            write.ProvisionedResources.Add(new ProvisionedResourceRecord
            {
                Id = id,
                ProvisionerId = "hetzner",
                ProviderResourceId = null,
                Region = "fsn1",
                Tags = new Dictionary<string, string> { ["servyx.managed"] = "true", ["servyx.job"] = "job-1" },
                State = ResourceLifecycleState.Intended,
                JobId = "job-1",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });

            write.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.ProvisionedResources.Single();

        loaded.Id.Should().Be(id);
        loaded.State.Should().Be(ResourceLifecycleState.Intended);
        loaded.ProviderResourceId.Should().BeNull();
        loaded.ProvisionerId.Should().Be("hetzner");
        loaded.Region.Should().Be("fsn1");
        loaded.JobId.Should().Be("job-1");
        loaded.ServerId.Should().BeNull();
        loaded.HostId.Should().BeNull();
        loaded.CreatedAt.Should().Be(createdAt);
        loaded.UpdatedAt.Should().Be(createdAt);
        loaded.Tags.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["servyx.managed"] = "true",
            ["servyx.job"] = "job-1",
        });
    }

    [Fact]
    public void IntentRow_TransitionsToCreated_AndPersistsTheProviderAssignedId()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = Guid.NewGuid();
        var hostId = HostId.New();
        var serverId = ServerId.New();
        var createdAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var confirmedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 42, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.ProvisionedResources.Add(NewIntent(id, createdAt));
            write.SaveChanges();
        }

        // ...the provider call happens here, out of process, and may or may not be observed...

        using (var promote = fixture.CreateContext())
        {
            var record = promote.ProvisionedResources.Single(r => r.Id == id);
            record.State.Should().Be(ResourceLifecycleState.Intended);

            record.State = ResourceLifecycleState.Created;
            record.ProviderResourceId = "vm-991823";
            record.HostId = hostId;
            record.ServerId = serverId;
            record.UpdatedAt = confirmedAt;

            promote.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.ProvisionedResources.Single();

        loaded.State.Should().Be(ResourceLifecycleState.Created);
        loaded.ProviderResourceId.Should().Be("vm-991823");
        loaded.HostId.Should().Be(hostId);
        loaded.ServerId.Should().Be(serverId);
        loaded.CreatedAt.Should().Be(createdAt);
        loaded.UpdatedAt.Should().Be(confirmedAt);
    }

    [Fact]
    public void OrphanSweep_CanQueryByState()
    {
        using var fixture = new SqliteDatabaseFixture();

        var orphan = Guid.NewGuid();

        using (var write = fixture.CreateContext())
        {
            write.ProvisionedResources.AddRange(
                NewIntent(orphan, DateTimeOffset.UnixEpoch),
                Confirmed(Guid.NewGuid(), "vm-1"),
                Confirmed(Guid.NewGuid(), "vm-2"));

            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // The sweep's entry query: everything still recorded as intent, i.e. every resource that may exist at
        // the provider and be billing without Servyx having confirmed it.
        var intended = read.ProvisionedResources
            .Where(record => record.State == ResourceLifecycleState.Intended)
            .ToList();

        intended.Should().ContainSingle();
        intended[0].Id.Should().Be(orphan);

        read.ProvisionedResources
            .Count(record => record.State == ResourceLifecycleState.Created)
            .Should().Be(2);
    }

    [Fact]
    public void OrphanSweep_CanQueryByProvisionerAndProviderResourceId()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ProvisionedResources.AddRange(
                Confirmed(Guid.NewGuid(), "vm-1"),
                Confirmed(Guid.NewGuid(), "vm-2"));

            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // The composite-index lookup: "provider says vm-2 exists — do I know about it?"
        read.ProvisionedResources
            .Where(record => record.ProvisionerId == "hetzner" && record.ProviderResourceId == "vm-2")
            .Should().ContainSingle();
    }

    [Fact]
    public void State_IsStoredByName_NotByOrdinal()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ProvisionedResources.Add(NewIntent(Guid.NewGuid(), DateTimeOffset.UnixEpoch));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        var connection = read.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM ProvisionedResources";

        // Reordering the enum must not silently reinterpret existing rows, and an operator reading the ledger
        // during an incident must not have to map integers back to names.
        var stored = command.ExecuteScalar() as string;
        stored.Should().Be("Intended");
    }

    [Fact]
    public void MutatingTagsInPlace_IsDetectedAndPersisted()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = Guid.NewGuid();

        using (var write = fixture.CreateContext())
        {
            write.ProvisionedResources.Add(NewIntent(id, DateTimeOffset.UnixEpoch));
            write.SaveChanges();
        }

        using (var mutate = fixture.CreateContext())
        {
            var record = mutate.ProvisionedResources.Single();

            // In-place mutation, for the same reason as CredentialUrnsValueComparerTests: without the value
            // comparer this write is silently dropped.
            ((Dictionary<string, string>)record.Tags)["servyx.reconciled"] = "true";

            mutate.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        read.ProvisionedResources.Single().Tags.Should().ContainKey("servyx.reconciled");
    }

    private static ProvisionedResourceRecord NewIntent(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        ProvisionerId = "hetzner",
        ProviderResourceId = null,
        Region = "fsn1",
        Tags = new Dictionary<string, string> { ["servyx.managed"] = "true" },
        State = ResourceLifecycleState.Intended,
        JobId = "job-1",
        CreatedAt = at,
        UpdatedAt = at,
    };

    private static ProvisionedResourceRecord Confirmed(Guid id, string providerResourceId) => new()
    {
        Id = id,
        ProvisionerId = "hetzner",
        ProviderResourceId = providerResourceId,
        Region = "fsn1",
        Tags = new Dictionary<string, string> { ["servyx.managed"] = "true" },
        State = ResourceLifecycleState.Created,
        JobId = "job-1",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}
