namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>saves</c> block: the on-disk shape of world saves, so the saves
/// page can enumerate and label them.
/// </summary>
/// <param name="WorldRoot">The directory containing one subdirectory per world, e.g. <c>${DATA_DIR}/Pal/Saved/SaveGames/0</c>.</param>
/// <param name="WorldIdPattern">
/// A regex constraining what counts as a valid world folder name directly under <see cref="WorldRoot"/>, if
/// declared, e.g. <c>^[0-9A-F]{32}$</c>. Null means every subdirectory of <see cref="WorldRoot"/> is treated
/// as a world.
/// </param>
/// <param name="LevelFile">The filename, within a world folder, holding the world's level data, e.g. <c>Level.sav</c>.</param>
/// <param name="MetaFile">The filename, within a world folder, holding the world's metadata, e.g. <c>LevelMeta.sav</c>.</param>
/// <param name="PlayerDir">The subdirectory, within a world folder, holding per-player save data, if the game separates it, e.g. <c>Players</c>.</param>
public sealed record SavesLayout(
    string WorldRoot,
    string? WorldIdPattern,
    string LevelFile,
    string MetaFile,
    string? PlayerDir);
