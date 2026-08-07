using Microsoft.Extensions.Configuration;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The white-label identity read from <c>Servyx:Branding</c>. An operator who configures nothing must get
/// <see cref="BrandingOptions.Default"/> back, byte-for-byte — every page in the app renders that unless
/// this proves otherwise.
/// </summary>
public class BrandingOptionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Empty_configuration_yields_all_defaults()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration());

        branding.ProductName.Should().Be("Servyx");
        branding.ShortName.Should().Be("Servyx");
        branding.LogoAssetPath.Should().BeNull();
        branding.FaviconAssetPath.Should().Be("favicon.png");
        branding.AccentTokenOverride.Should().BeNull();
        branding.SupportUrl.Should().BeNull();
    }

    [Fact]
    public void Partial_configuration_overrides_only_the_supplied_fields()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:ProductName", "Acme Server Manager"),
            ("Servyx:Branding:SupportUrl", "https://support.example.com")));

        branding.ProductName.Should().Be("Acme Server Manager");
        branding.SupportUrl.Should().Be("https://support.example.com");

        // Everything not supplied keeps its default.
        branding.ShortName.Should().Be("Servyx");
        branding.LogoAssetPath.Should().BeNull();
        branding.FaviconAssetPath.Should().Be("favicon.png");
        branding.AccentTokenOverride.Should().BeNull();
    }

    [Fact]
    public void An_https_url_is_accepted_verbatim_for_asset_paths()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:LogoAssetPath", "https://cdn.example.com/brand/logo.svg"),
            ("Servyx:Branding:FaviconAssetPath", "https://cdn.example.com/brand/favicon.png")));

        branding.LogoAssetPath.Should().Be("https://cdn.example.com/brand/logo.svg");
        branding.FaviconAssetPath.Should().Be("https://cdn.example.com/brand/favicon.png");
    }

    [Fact]
    public void A_wwwroot_relative_asset_path_is_accepted()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:LogoAssetPath", "images/brand-logo.png")));

        branding.LogoAssetPath.Should().Be("images/brand-logo.png");
    }

    [Fact]
    public void A_traversal_logo_path_falls_back_to_the_default_rather_than_throwing()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:LogoAssetPath", "../../etc/passwd")));

        branding.LogoAssetPath.Should().BeNull("a traversal path must degrade to the default, not be trusted");
    }

    [Fact]
    public void A_backslash_smuggled_path_falls_back_to_the_default()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:LogoAssetPath", "..\\..\\secrets\\file.png")));

        branding.LogoAssetPath.Should().BeNull();
    }

    [Fact]
    public void An_invalid_favicon_path_falls_back_to_the_bundled_default_rather_than_null()
    {
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:FaviconAssetPath", "../outside/favicon.ico")));

        branding.FaviconAssetPath.Should().Be("favicon.png",
            "FaviconAssetPath's default is the bundled favicon.png, not null, unlike LogoAssetPath");
    }

    [Fact]
    public void A_root_relative_path_that_would_escape_wwwroot_falls_back_to_the_default()
    {
        // Not caught by the ".." or backslash checks, but Path.Combine treats a leading-slash path as
        // rooted and discards the wwwroot prefix entirely — exactly the case the wwwroot containment
        // check exists to catch.
        var branding = BrandingOptions.FromConfiguration(Configuration(
            ("Servyx:Branding:LogoAssetPath", "/etc/passwd")));

        branding.LogoAssetPath.Should().BeNull(
            "a rooted path resolves outside wwwroot once combined, and must fall back to the default " +
            "rather than being trusted verbatim");
    }
}
