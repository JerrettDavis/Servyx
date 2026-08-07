using Bunit;
using Servyx.Domain.Definitions;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

/// <summary>
/// A fourth badge in the <see cref="StateBadge"/>/<see cref="HealthBadge"/>/<see cref="DriftBadge"/> family,
/// mirroring their shape exactly rather than sharing a base — that consolidation was deliberately deferred.
/// </summary>
public class TrustBadgeTests : BunitContext
{
    [Theory]
    [InlineData(TrustTier.Builtin, "trust-builtin")]
    [InlineData(TrustTier.Verified, "trust-verified")]
    [InlineData(TrustTier.Unverified, "trust-unverified")]
    public void Renders_the_trust_tier_as_text_and_a_lowercased_modifier_class(TrustTier trust, string expectedClass)
    {
        var cut = Render<TrustBadge>(p => p.Add(x => x.Trust, trust));

        var span = cut.Find("span.trust-badge");
        span.ClassList.Should().Contain("svx-badge");
        span.ClassList.Should().Contain(expectedClass);
        span.TextContent.Trim().Should().Be(trust.ToString());
    }
}
