using Servyx.Domain.Backups;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> BackupKeys =
        new HashSet<string>(StringComparer.Ordinal) { "include", "exclude", "quiesce", "resume", "adopt", "defaultRetention" };

    /// <summary>
    /// The keys a single control step may declare. Shared by <c>backup.quiesce</c> and <c>backup.resume</c>
    /// deliberately: the two phases differ only in when they run, so a step that is legal in one is legal in
    /// the other, and a typo is an Error in both.
    /// </summary>
    private static readonly IReadOnlySet<string> ControlStepKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "channel", "command", "timeout" };
    private static readonly IReadOnlySet<string> AdoptKeys = new HashSet<string>(StringComparer.Ordinal) { "adapter", "path", "pattern", "ownership", "note" };
    private static readonly IReadOnlySet<string> RetentionKeys = new HashSet<string>(StringComparer.Ordinal) { "keepHourly", "keepDaily", "keepWeekly" };

    private static BackupPolicy? ParseBackup(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var map = RequireMapping(root, "backup", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, BackupKeys, issues, "'backup'");

        var include = ParsePathList(map, "include", issues, state, "'backup.include'");
        var exclude = ParsePathList(map, "exclude", issues, state, "'backup.exclude'");

        var quiesce = ParseControlSteps(map, "quiesce", issues, state);

        // Optional, and empty when absent: every definition written before this key existed keeps parsing
        // with zero Errors. Registered in BackupKeys above so a misspelling ('resumes', 'unquiesce') is a
        // hard Error like every other unknown key here, rather than a silently-ignored block whose absence
        // would only be discovered as "saving is still disabled" long after the backup finished.
        var resume = ParseControlSteps(map, "resume", issues, state);

        var adopt = new List<BackupAdoptSource>();
        foreach (var node in OptionalSequence(map, "adopt", issues, "'backup'"))
        {
            var source = ParseAdoptSource(node, issues, state);
            if (source is not null)
            {
                adopt.Add(source);
            }
        }

        RetentionPolicy? defaultRetention = null;
        if (map.TryGet("defaultRetention", out var retentionNode))
        {
            var retentionMap = AsMapping(retentionNode, issues, "'backup.defaultRetention'");
            if (retentionMap is not null)
            {
                RejectUnknownKeys(retentionMap, RetentionKeys, issues, "'backup.defaultRetention'");
                var keepHourly = OptionalInt(retentionMap, "keepHourly", issues, "'backup.defaultRetention'") ?? 0;
                var keepDaily = OptionalInt(retentionMap, "keepDaily", issues, "'backup.defaultRetention'") ?? 0;
                var keepWeekly = OptionalInt(retentionMap, "keepWeekly", issues, "'backup.defaultRetention'") ?? 0;
                defaultRetention = new RetentionPolicy(keepHourly, keepDaily, keepWeekly);
            }
        }

        return new BackupPolicy(include, exclude, quiesce, adopt, defaultRetention) { Resume = resume };
    }

    private static IReadOnlyList<string> ParsePathList(YamlMappingNode map, string key, ParseIssues issues, ParseState state, string context)
    {
        var result = new List<string>();
        foreach (var node in OptionalSequence(map, key, issues, "'backup'"))
        {
            var value = AsString(node, issues, $"An entry of {context}");
            if (value is null)
            {
                continue;
            }

            ValidateContainedPath(value, node, issues, $"An entry of {context}");
            QueueTemplateTokens(value, node, state);
            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Parses one of the two control-step phases under <c>backup</c> — <c>quiesce</c> (before capture) or
    /// <c>resume</c> (after it, guaranteed). Both take an identical entry shape, so they share one parser
    /// and one key set; only the block path woven into every diagnostic differs, which is what keeps a
    /// <c>backup.resume</c> mistake from being reported as a <c>backup.quiesce</c> one.
    /// </summary>
    private static IReadOnlyList<QuiesceStep> ParseControlSteps(YamlMappingNode map, string key, ParseIssues issues, ParseState state)
    {
        var steps = new List<QuiesceStep>();
        foreach (var node in OptionalSequence(map, key, issues, "'backup'"))
        {
            var step = ParseControlStep(node, $"backup.{key}", issues, state);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static QuiesceStep? ParseControlStep(YamlNode node, string blockPath, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, $"An entry of '{blockPath}'");
        if (map is null)
        {
            return null;
        }

        var kind = RequireString(map, "kind", issues, $"A '{blockPath}' entry");
        if (kind is not null && kind != "control")
        {
            map.TryGet("kind", out var kindNode);
            issues.Error($"A '{blockPath}' entry declares 'kind: {kind}'; only 'control' is recognized.", kindNode);
            return null;
        }

        RejectUnknownKeys(map, ControlStepKeys, issues, $"A '{blockPath}' entry");
        var channel = RequireString(map, "channel", issues, $"A '{blockPath}' entry");
        var command = RequireString(map, "command", issues, $"A '{blockPath}' entry");
        var timeout = ParseDuration(map, "timeout", issues, $"A '{blockPath}' entry");

        // Deferred rather than checked inline: 'backup' happens to be parsed after 'control' today (see
        // ParseRoot), but this queues through the same mechanism PendingStopCommandRefs uses so the check
        // does not silently depend on that block ordering never changing. Resume steps queue through the
        // identical path, so an undeclared channel or command is caught in 'resume' exactly as it is in
        // 'quiesce' — see GameDefinitionYamlParser.Semantics.cs, which already reads the context label off
        // the queued tuple and needs no change to cover the new block.
        if (channel is not null && command is not null)
        {
            map.TryGet("channel", out var channelNode);
            map.TryGet("command", out var commandNode);
            state.PendingChannelCommandRefs.Add((channel, command, channelNode, commandNode, $"'{blockPath}'"));
        }

        return channel is not null && command is not null && timeout is not null
            ? new QuiesceStep.Control(channel, command, timeout.Value)
            : null;
    }

    private static BackupAdoptSource? ParseAdoptSource(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'backup.adopt'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, AdoptKeys, issues, "A 'backup.adopt' entry");

        var adapter = RequireString(map, "adapter", issues, "A 'backup.adopt' entry");
        if (adapter is not null)
        {
            // docs/schema.md: this block is unread by any DI-aware code path in this phase, and the Domain
            // layer this parser lives above can never see which IBackupAdopter.AdapterId values are actually
            // registered — so an adopt entry's adapter can only ever be a Warning, never a verified Error.
            map.TryGet("adapter", out var adapterNode);
            issues.Warning($"'backup.adopt' declares adapter '{adapter}', which cannot be checked against registered backup adapters at parse time.", adapterNode);
        }

        var path = RequireString(map, "path", issues, "A 'backup.adopt' entry");
        if (path is not null)
        {
            map.TryGet("path", out var pathNode);
            ValidateContainedPath(path, pathNode, issues, "A 'backup.adopt' entry's 'path'");
            QueueTemplateTokens(path, pathNode, state);
        }

        var pattern = RequireString(map, "pattern", issues, "A 'backup.adopt' entry");

        var ownershipRaw = RequireString(map, "ownership", issues, "A 'backup.adopt' entry");
        if (ownershipRaw is not null && ownershipRaw != "foreign")
        {
            map.TryGet("ownership", out var ownershipNode);
            issues.Error(
                $"A 'backup.adopt' entry declares 'ownership: {ownershipRaw}'; an adopted source is always "
                + "'foreign' — Servyx never manages the lifecycle of a discovered backup.",
                ownershipNode);
        }

        var note = OptionalString(map, "note", issues, "A 'backup.adopt' entry");

        return adapter is not null && path is not null && pattern is not null && ownershipRaw == "foreign"
            ? new BackupAdoptSource(adapter, path, pattern, BackupOwnership.Foreign, note)
            : null;
    }
}
