using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Application.Servers;

/// <summary>
/// Pairs a definition-derived <see cref="Servers.AdoptionCriteria"/> with the exact
/// <see cref="GameDefinitionRef"/> (id + content hash + source) it was derived from, so a binding decision
/// can be pinned to that content rather than to a mutable definition id — see
/// <see cref="Servyx.Domain.Definitions.IServerDefinitionBindingStore"/>.
/// </summary>
public sealed record DefinitionAdoptionCriteria(AdoptionCriteria Criteria, GameDefinitionRef DefinitionRef);

/// <summary>
/// Derives <see cref="AdoptionCriteria"/> from a <see cref="GameDefinition"/>'s docker deployment profile.
/// Extracted so every caller that needs this — the single-definition bootstrap path in <c>Servyx.Web</c>'s
/// <c>Program.cs</c>, and <see cref="DeriveAll"/> below, used once more than one definition is loaded —
/// shares one implementation rather than three subtly different reimplementations of "what does an
/// adoptable container of this game look like".
/// </summary>
public static class AdoptionCriteriaFactory
{
    /// <summary>
    /// Derives <see cref="AdoptionCriteria"/> from <paramref name="definition"/>'s docker-kind deployment
    /// profile, or <see langword="null"/> if there is no such profile, no <c>detect</c> block, or no
    /// required mount declared — any of which means this definition cannot answer "what does an adoptable
    /// container of this game look like".
    /// </summary>
    public static AdoptionCriteria? TryDerive(GameDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }

        var dockerProfile = definition.Deployments.FirstOrDefault(d => d.Kind == DeploymentKind.Docker);
        var imageRepo = dockerProfile?.Detect?.ImageRepo;
        var firstMount = dockerProfile?.Detect?.RequiredMounts.FirstOrDefault();

        if (imageRepo is null || firstMount is null)
        {
            return null;
        }

        return new AdoptionCriteria(definition.Metadata.Id, definition.Metadata.Name, imageRepo, firstMount.ContainerPath);
    }

    /// <summary>
    /// Derives one <see cref="DefinitionAdoptionCriteria"/> per loaded definition that has a derivable
    /// docker profile, tagged with the exact <see cref="GameDefinitionRef"/> it came from. A definition
    /// with no docker deployment, no <c>detect</c> block, or no required mount is silently omitted — the
    /// same "cannot answer what an adoptable container looks like" case <see cref="TryDerive"/> returns
    /// <see langword="null"/> for, which meant "fall back to the hardcoded default" in the single-definition
    /// era and now simply means "this definition contributes no adoption criteria".
    /// </summary>
    public static IReadOnlyList<DefinitionAdoptionCriteria> DeriveAll(
        IEnumerable<(GameDefinitionRef Ref, GameDefinition Definition)> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var results = new List<DefinitionAdoptionCriteria>();
        foreach (var (reference, definition) in definitions)
        {
            var criteria = TryDerive(definition);
            if (criteria is not null)
            {
                results.Add(new DefinitionAdoptionCriteria(criteria, reference));
            }
        }

        return results;
    }
}
