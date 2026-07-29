using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Converters;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Regression tests for the value comparer on <see cref="ProviderAccount.CredentialUrns"/>.
/// </summary>
/// <remarks>
/// A converted collection property without an explicit <c>ValueComparer</c> is compared by reference and never
/// snapshotted, so EF sees no change when the list is mutated in place, <c>SaveChanges</c> writes nothing, and
/// nothing anywhere reports a problem — the update is simply lost. Deleting the comparer argument from
/// <c>ProviderAccountConfiguration</c> must make <see cref="MutatingCredentialUrnsInPlace_IsDetectedAndPersisted"/>
/// fail; if it does not, this file is not testing what it claims to.
/// </remarks>
public class CredentialUrnsValueComparerTests
{
    [Fact]
    public void CredentialUrns_RoundTripAsAnOrderedCollection()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ProviderAccounts.Add(NewAccount(["urn:a", "urn:b", "urn:c"]));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // Equal(), not BeEquivalentTo(): the URN order is the resolution order and must survive the round trip.
        read.ProviderAccounts.Single().CredentialUrns.Should().Equal("urn:a", "urn:b", "urn:c");
    }

    [Fact]
    public void CredentialUrns_RoundTripWhenEmpty()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ProviderAccounts.Add(NewAccount([]));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        read.ProviderAccounts.Single().CredentialUrns.Should().BeEmpty();
    }

    [Fact]
    public void CredentialUrns_SurviveValuesContainingDelimiterLikeCharacters()
    {
        using var fixture = new SqliteDatabaseFixture();

        // The reason the converter is JSON rather than a delimited string: a URN legitimately contains
        // colons, semicolons, commas and pipes, and any of those as a naive separator would corrupt it.
        string[] awkward = ["urn:servyx:secret:a;b,c|d", "urn:servyx:secret:\"quoted\"", "urn:servyx:secret:\\slash"];

        using (var write = fixture.CreateContext())
        {
            write.ProviderAccounts.Add(NewAccount(awkward));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        read.ProviderAccounts.Single().CredentialUrns.Should().Equal(awkward);
    }

    [Fact]
    public void MutatingCredentialUrnsInPlace_IsDetectedAndPersisted()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var seed = fixture.CreateContext())
        {
            seed.ProviderAccounts.Add(NewAccount(["urn:original"]));
            seed.SaveChanges();
        }

        using (var mutate = fixture.CreateContext())
        {
            var tracked = mutate.ProviderAccounts.Single();

            // Deliberately mutated in place rather than reassigned. Reassigning the property would be caught
            // even by reference equality, so it would prove nothing; only an in-place edit exercises the
            // comparer's snapshot. The cast reflects what the converter actually materializes.
            var urns = (List<string>)tracked.CredentialUrns;
            urns.Add("urn:added");

            mutate.ChangeTracker.DetectChanges();
            mutate.Entry(tracked).State.Should().Be(EntityState.Modified);

            mutate.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        read.ProviderAccounts.Single().CredentialUrns.Should().Equal("urn:original", "urn:added");
    }

    [Fact]
    public void ReorderingCredentialUrnsInPlace_IsDetectedAndPersisted()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var seed = fixture.CreateContext())
        {
            seed.ProviderAccounts.Add(NewAccount(["urn:first", "urn:second"]));
            seed.SaveChanges();
        }

        using (var mutate = fixture.CreateContext())
        {
            var tracked = mutate.ProviderAccounts.Single();
            ((List<string>)tracked.CredentialUrns).Reverse();

            mutate.SaveChanges().Should().Be(1);
        }

        using var read = fixture.CreateContext();
        read.ProviderAccounts.Single().CredentialUrns.Should().Equal("urn:second", "urn:first");
    }

    [Fact]
    public void Comparer_TreatsIdenticalSequencesAsEqual_AndDifferentOnesAsNot()
    {
        var comparer = JsonCollectionConverters.StringListComparer;

        IReadOnlyList<string> a = new List<string> { "x", "y" };
        IReadOnlyList<string> b = new List<string> { "x", "y" };
        IReadOnlyList<string> reordered = new List<string> { "y", "x" };

        comparer.Equals(a, b).Should().BeTrue();
        comparer.GetHashCode(a).Should().Be(comparer.GetHashCode(b));
        comparer.Equals(a, reordered).Should().BeFalse();

        // The snapshot must be a copy; sharing the instance is exactly what defeats change detection.
        var snapshot = comparer.Snapshot(a);
        ((List<string>)a).Add("z");
        snapshot.Should().Equal("x", "y");
    }

    private static ProviderAccount NewAccount(IReadOnlyList<string> credentialUrns) => new()
    {
        Id = "hetzner-primary",
        ProviderId = "hetzner",
        DisplayName = "Hetzner (primary)",
        CredentialUrns = credentialUrns,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
