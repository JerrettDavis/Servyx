using System.Runtime.CompilerServices;

// Servyx.Web.Tests owns the write-grant suite (WriteGrantCache/WriteGrantService/WriteGrantRevocation), so
// the one internal test seam in this assembly — WriteGrantCache.LoadInterleaveHookForTests, which makes the
// cache's publish race deterministically reproducible — is visible to it and to nothing else. Mirrors the
// same declaration Servyx.Web and Servyx.Infrastructure.Docker already carry for their own test assemblies.
[assembly: InternalsVisibleTo("Servyx.Web.Tests")]
