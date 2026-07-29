using Bunit;
using Servyx.Domain.Provisioning;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

/// <summary>
/// Guards the product-wide rule stated on <see cref="CostConfidence.Unknown"/>: an unknown cost is
/// rendered as the literal word "unknown", never as a number and never as zero.
/// </summary>
public class CostEstimateViewTests : BunitContext
{
    [Fact]
    public void UnknownConfidence_RendersTheLiteralWordUnknown()
    {
        var cut = Render<CostEstimateView>(p => p
            .Add(x => x.Estimate, CostEstimate.Unknown("The provider's pricing API was unreachable.")));

        cut.Find("[data-testid='cost-estimate']").TextContent.Trim().Should().Be("unknown");
        cut.Markup.Should().NotContain("0.00");
    }

    [Fact]
    public void NullEstimate_RendersTheLiteralWordUnknown()
    {
        var cut = Render<CostEstimateView>();

        cut.Find("[data-testid='cost-estimate']").TextContent.Trim().Should().Be("unknown");
    }

    [Fact]
    public void ConfidentEstimateWithNoFigures_StillRendersUnknown_RatherThanBlankOrZero()
    {
        // A confidence better than Unknown but with no amounts attached is still "we have no number".
        var cut = Render<CostEstimateView>(p => p
            .Add(x => x.Estimate, new CostEstimate(null, null, "USD", CostConfidence.ListPrice, "provider price list")));

        cut.Find("[data-testid='cost-estimate']").TextContent.Trim().Should().Be("unknown");
    }

    [Fact]
    public void RealFigures_RenderWithCurrencyAndConfidence()
    {
        var cut = Render<CostEstimateView>(p => p
            .Add(x => x.Estimate, new CostEstimate(0.0119m, 8.69m, "USD", CostConfidence.ListPrice, "provider price list")));

        var text = cut.Find("[data-testid='cost-estimate']").TextContent;
        text.Should().Contain("0.0119 USD/hr");
        text.Should().Contain("8.69 USD/mo");

        cut.Find("[data-testid='cost-confidence']").TextContent.Trim()
            .Should().Be(nameof(CostConfidence.ListPrice));
    }
}
