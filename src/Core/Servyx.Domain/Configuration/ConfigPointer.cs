namespace Servyx.Domain.Configuration;

/// <summary>
/// Addresses a specific value within a <see cref="ConfigDocument"/> — a key, a JSON-pointer-like path, or
/// a codec member — e.g. <c>["/Script/Pal.PalGameWorldSettings"].OptionSettings</c>,
/// <c>services.palworld.ports[0]</c>, or a bare dotenv key.
/// </summary>
/// <param name="Path">The pointer expression.</param>
public sealed record ConfigPointer(string Path);
