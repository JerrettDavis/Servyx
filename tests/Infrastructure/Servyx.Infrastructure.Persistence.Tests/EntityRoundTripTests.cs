using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Tests;

public class EntityRoundTripTests
{
    [Fact]
    public void Server_RoundTripsEveryField_AcrossContextInstances()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = ServerId.New();
        var hostId = HostId.New();
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var changedAt = new DateTimeOffset(2026, 3, 5, 6, 7, 8, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(new Server
            {
                Id = id,
                Name = "palworld-eu-1",
                ContainerId = "abc123containerid",
                GameDefinitionId = "palworld",
                DefinitionContentHash = "sha256:4f2c",
                HostId = hostId,
                AdoptionMode = AdoptionMode.Provisioned,
                WriteMode = ServerWriteMode.PreviewOnly,
                WriteModeChangedBy = "operator@servyx",
                WriteModeChangedAt = changedAt,
                CreatedAt = createdAt,
            });

            write.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.Servers.Single();

        loaded.Id.Should().Be(id);
        loaded.HostId.Should().Be(hostId);
        loaded.Name.Should().Be("palworld-eu-1");
        loaded.ContainerId.Should().Be("abc123containerid");
        loaded.GameDefinitionId.Should().Be("palworld");
        loaded.DefinitionContentHash.Should().Be("sha256:4f2c");
        loaded.AdoptionMode.Should().Be(AdoptionMode.Provisioned);
        loaded.WriteMode.Should().Be(ServerWriteMode.PreviewOnly);
        loaded.WriteModeChangedBy.Should().Be("operator@servyx");
        loaded.WriteModeChangedAt.Should().Be(changedAt);
        loaded.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void Server_HostId_RoundTripsNull_WhenNoHostIsModeled()
    {
        using var fixture = new SqliteDatabaseFixture();
        var id = ServerId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(new Server
            {
                Id = id,
                Name = "adopted-server",
                ContainerId = "def456containerid",
                GameDefinitionId = "palworld",
                DefinitionContentHash = "sha256:4f2c",
                HostId = null,
                AdoptionMode = AdoptionMode.Adopted,
                WriteMode = ServerWriteMode.ReadOnly,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });

            write.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.Servers.Single();

        // Honest "no Host row exists for this yet" — not a fabricated id, see Server.HostId's own remarks.
        loaded.HostId.Should().BeNull();
    }

    [Fact]
    public void Server_ContainerId_IsUnique()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(new Server
            {
                Id = ServerId.New(),
                Name = "first",
                ContainerId = "shared-container-id",
                GameDefinitionId = "palworld",
                DefinitionContentHash = "sha256:4f2c",
                AdoptionMode = AdoptionMode.Adopted,
                WriteMode = ServerWriteMode.ReadOnly,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            write.SaveChanges();
        }

        using var write2 = fixture.CreateContext();
        write2.Servers.Add(new Server
        {
            Id = ServerId.New(),
            Name = "second",
            ContainerId = "shared-container-id",
            GameDefinitionId = "palworld",
            DefinitionContentHash = "sha256:4f2c",
            AdoptionMode = AdoptionMode.Adopted,
            WriteMode = ServerWriteMode.ReadOnly,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        // Enforced at the database, not only in ServerAdoptionService's own pre-check — see
        // ServerConfiguration's unique index on ContainerId.
        var act = () => write2.SaveChanges();
        act.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public void Server_StronglyTypedIds_PersistAsTheirUnderlyingGuid()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = ServerId.New();
        var hostId = HostId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(NewServer(id, hostId));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // The converter has to apply on the query side too, not just on materialization.
        read.Servers.Where(server => server.HostId == hostId).Should().ContainSingle();

        // ...and the columns must literally hold the underlying Guid, not some serialized form of the struct.
        // Read straight through ADO.NET so EF's own converter cannot mask a wrong storage shape.
        var connection = read.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, HostId FROM Servers";
        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetGuid(0).Should().Be(id.Value);
        reader.GetGuid(1).Should().Be(hostId.Value);
    }

    [Fact]
    public void Host_RoundTripsEveryField_AcrossContextInstances()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = HostId.New();
        var createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.Hosts.Add(new Host
            {
                Id = id,
                Name = "fsn1-node-7",
                ConnectorId = "ssh:fsn1-node-7",
                ProvisionedByJobId = "job-2f8c",
                ProviderResourceId = "vm-991823",
                ProviderAccountId = "hetzner-primary",
                CreatedAt = createdAt,
            });

            write.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.Hosts.Single();

        loaded.Id.Should().Be(id);
        loaded.Name.Should().Be("fsn1-node-7");
        loaded.ConnectorId.Should().Be("ssh:fsn1-node-7");
        loaded.ProvisionedByJobId.Should().Be("job-2f8c");
        loaded.ProviderResourceId.Should().Be("vm-991823");
        loaded.ProviderAccountId.Should().Be("hetzner-primary");
        loaded.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void Host_RoundTripsNullOptionalFields()
    {
        using var fixture = new SqliteDatabaseFixture();

        var id = HostId.New();

        using (var write = fixture.CreateContext())
        {
            write.Hosts.Add(new Host
            {
                Id = id,
                Name = "adopted-box",
                ConnectorId = "ssh:adopted-box",
                CreatedAt = DateTimeOffset.UnixEpoch,
            });

            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        var loaded = read.Hosts.Single();

        loaded.ProvisionedByJobId.Should().BeNull();
        loaded.ProviderResourceId.Should().BeNull();
        loaded.ProviderAccountId.Should().BeNull();
    }

    [Fact]
    public void ProviderAccount_RoundTripsEveryField_AcrossContextInstances()
    {
        using var fixture = new SqliteDatabaseFixture();

        var createdAt = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.ProviderAccounts.Add(new ProviderAccount
            {
                Id = "hetzner-primary",
                ProviderId = "hetzner",
                DisplayName = "Hetzner (primary)",
                DefaultRegion = "fsn1",
                CredentialUrns = ["urn:servyx:secret:hetzner/token", "urn:servyx:secret:hetzner/ssh-key"],
                ScopeHint = "full account access, including billing and delete",
                CreatedAt = createdAt,
            });

            write.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        var loaded = read.ProviderAccounts.Single();

        loaded.Id.Should().Be("hetzner-primary");
        loaded.ProviderId.Should().Be("hetzner");
        loaded.DisplayName.Should().Be("Hetzner (primary)");
        loaded.DefaultRegion.Should().Be("fsn1");
        loaded.ScopeHint.Should().Be("full account access, including billing and delete");
        loaded.CreatedAt.Should().Be(createdAt);
        loaded.CredentialUrns.Should().Equal("urn:servyx:secret:hetzner/token", "urn:servyx:secret:hetzner/ssh-key");
    }

    [Fact]
    public void RequiredColumns_AreEnforcedByTheDatabase()
    {
        using var fixture = new SqliteDatabaseFixture();

        using var write = fixture.CreateContext();
        write.Hosts.Add(new Host
        {
            Id = HostId.New(),
            Name = null!,
            ConnectorId = "ssh:broken",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        // Proves the fixture is exercising real relational constraints: the EF Core InMemory provider would
        // have accepted this write without complaint.
        var act = () => write.SaveChanges();
        act.Should().Throw<DbUpdateException>();
    }

    private static Server NewServer(ServerId id, HostId hostId) => new()
    {
        Id = id,
        Name = "server-" + id,
        ContainerId = "container-" + id,
        GameDefinitionId = "palworld",
        DefinitionContentHash = "sha256:0000",
        HostId = hostId,
        AdoptionMode = AdoptionMode.Adopted,
        WriteMode = ServerWriteMode.ReadOnly,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
