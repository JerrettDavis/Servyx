using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Servyx.Infrastructure.Docker.Tests")]

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// Assembly marker type, used by test discovery and reflection-based wiring that needs a stable type
/// reference into this assembly.
/// </summary>
public static class AssemblyMarker
{
}
