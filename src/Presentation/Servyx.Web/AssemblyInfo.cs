using System.Runtime.CompilerServices;

// Lets Servyx.Web.Tests unit-test internal helpers directly (e.g.
// LiveDashboardDataService.MaskIfSecret) without making them part of this project's public API,
// mirroring the same pattern already used by Servyx.Infrastructure.Docker.
[assembly: InternalsVisibleTo("Servyx.Web.Tests")]
