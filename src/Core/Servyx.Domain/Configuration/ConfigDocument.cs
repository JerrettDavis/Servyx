namespace Servyx.Domain.Configuration;

/// <summary>
/// A parsed configuration document, as produced by an <see cref="IConfigAdapter"/>. <see cref="Root"/> is
/// an opaque, format-specific parse tree (its concrete shape is owned by the adapter that produced it);
/// <see cref="RawLines"/> retains the original source lines so an adapter's <see cref="IConfigAdapter.Render"/>
/// step can reproduce untouched regions byte-for-byte.
/// </summary>
/// <param name="Root">The format-specific parsed representation.</param>
/// <param name="RawLines">The original source, split into lines, for round-trip fidelity.</param>
public sealed record ConfigDocument(object Root, IReadOnlyList<string> RawLines);
