using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>
/// Merges edits into a <see cref="ConfigDocument"/> without disturbing anything the edit set doesn't
/// explicitly touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pointer grammar for codec-scoped members.</b> A plain <see cref="ConfigPointer"/> (one with a
/// registered <see cref="ConfigSpan"/> in the document) is edited by splicing directly into that span. To
/// address a single member packed inside a codec-encoded scalar — e.g. one setting inside Palworld's
/// <c>OptionSettings=(...)</c> — use a pointer of the form
/// <c>"{scalarSpanPointerPath}#{codecId}:{memberName}"</c>, for example
/// <c>"[/Script/Pal.PalGameWorldSettings].OptionSettings#unreal-option-settings:ServerName"</c>. The part
/// before <c>#</c> must match the <see cref="ConfigPointer.Path"/> of the scalar's own span exactly; the
/// part after names the codec and, after the <c>:</c>, the member within it.
/// </para>
/// <para>
/// <see cref="MergeAll"/> groups edits by that <c>(scalarSpanPath, codecId)</c> pair so each scalar is
/// decoded once, has all of its targeted members updated in memory, and is encoded once — never once per
/// edit.
/// </para>
/// </remarks>
public sealed class ConfigMerger : IConfigMerger
{
    private readonly IReadOnlyDictionary<string, IConfigValueCodec> _codecsById;

    /// <summary>Creates a merger that can resolve codec-scoped edits against the given codecs, keyed by <see cref="IConfigValueCodec.CodecId"/>.</summary>
    public ConfigMerger(IEnumerable<IConfigValueCodec> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        _codecsById = codecs.ToDictionary(c => c.CodecId, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ConfigDocument Merge(ConfigDocument existing, ConfigPointer target, string newValue, MergePolicy policy) =>
        MergeAll(existing, [new ConfigEdit(target, newValue)], policy);

    /// <inheritdoc />
    public ConfigDocument MergeAll(ConfigDocument existing, IReadOnlyList<ConfigEdit> edits, MergePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(edits);

        var document = existing;
        var directEdits = new List<ConfigEdit>();
        var scoped = new Dictionary<(string ScalarPath, string CodecId), List<(string Member, string NewValue)>>();

        foreach (var edit in edits)
        {
            var scope = TryParseScopedPointer(edit.Target.Path);
            if (scope is null)
            {
                directEdits.Add(edit);
                continue;
            }

            var (scalarPath, codecId, member) = scope.Value;
            var key = (scalarPath, codecId);
            if (!scoped.TryGetValue(key, out var members))
            {
                members = [];
                scoped[key] = members;
            }

            members.Add((member, edit.NewValue));
        }

        foreach (var edit in directEdits)
        {
            EnsureWritable(document, edit.Target, policy);
            document = document.WithValue(edit.Target, edit.NewValue);
        }

        foreach (var ((scalarPath, codecId), members) in scoped)
        {
            var scalarPointer = new ConfigPointer(scalarPath);
            EnsureWritable(document, scalarPointer, policy);

            if (!_codecsById.TryGetValue(codecId, out var codec))
            {
                throw new InvalidOperationException($"No codec registered with id '{codecId}'.");
            }

            var span = document.Spans.LastOrDefault(s => s.Pointer == scalarPointer)
                ?? throw new KeyNotFoundException($"No span is registered for pointer '{scalarPath}'.");

            var rawScalar = document.RawLines[span.LineIndex].Substring(span.ValueStart, span.ValueLength);
            var decoded = codec.Decode(rawScalar);

            // Re-inserting into a fresh OrderedDictionary preserves the decode order for every member;
            // edits below only ever overwrite an existing key's value, never add or remove members, so
            // original member order is untouched even for the members being edited.
            var ordered = new OrderedDictionary<string, string>();
            foreach (var (k, v) in decoded)
            {
                ordered[k] = v;
            }

            foreach (var (member, newValue) in members)
            {
                if (!ordered.ContainsKey(member))
                {
                    throw new KeyNotFoundException($"Codec '{codecId}' has no member '{member}' in '{scalarPath}'.");
                }

                ordered[member] = ApplyMemberEdit(ordered[member], newValue);
            }

            var encoded = codec.Encode(ordered);
            document = document.WithValue(scalarPointer, encoded);
        }

        return document;
    }

    /// <summary>
    /// Replaces a codec member's raw value with <paramref name="newValue"/>, preserving the member's
    /// original quoting style: if the current raw value is wrapped in matching quotes, only the inner
    /// content is replaced and the quotes are kept; otherwise the raw value is replaced outright (the
    /// caller is expected to supply already-formatted text for unquoted members, e.g. <c>"2.000000"</c>).
    /// </summary>
    private static string ApplyMemberEdit(string currentRawValue, string newValue)
    {
        if (currentRawValue.Length >= 2)
        {
            var quote = currentRawValue[0];
            if ((quote == '"' || quote == '\'') && currentRawValue[^1] == quote)
            {
                return quote + newValue + quote;
            }
        }

        return newValue;
    }

    private static void EnsureWritable(ConfigDocument document, ConfigPointer target, MergePolicy policy)
    {
        if (policy != MergePolicy.ManagedBlock)
        {
            return;
        }

        var span = document.Spans.LastOrDefault(s => s.Pointer == target)
            ?? throw new KeyNotFoundException($"No span is registered for pointer '{target.Path}'.");

        var startLine = -1;
        var endLine = -1;
        for (var i = 0; i < document.RawLines.Count; i++)
        {
            var line = document.RawLines[i];
            if (line.Contains(">>> servyx:managed >>>", StringComparison.Ordinal))
            {
                startLine = i;
            }
            else if (line.Contains("<<< servyx:managed <<<", StringComparison.Ordinal))
            {
                endLine = i;
                break;
            }
        }

        if (startLine < 0 || endLine < 0 || span.LineIndex <= startLine || span.LineIndex >= endLine)
        {
            throw new InvalidOperationException(
                $"MergePolicy.ManagedBlock forbids writing '{target.Path}': it falls outside the '# >>> servyx:managed >>>' … '# <<< servyx:managed <<<' region.");
        }
    }

    /// <summary>Parses <c>"{scalarPath}#{codecId}:{member}"</c>, or returns <see langword="null"/> if <paramref name="path"/> isn't in that form.</summary>
    private static (string ScalarPath, string CodecId, string Member)? TryParseScopedPointer(string path)
    {
        var hash = path.IndexOf('#');
        if (hash < 0)
        {
            return null;
        }

        var remainder = path[(hash + 1)..];
        var colon = remainder.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var scalarPath = path[..hash];
        var codecId = remainder[..colon];
        var member = remainder[(colon + 1)..];
        return (scalarPath, codecId, member);
    }
}
