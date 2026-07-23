namespace Servyx.Domain.Control;

/// <summary>
/// A composable requirement over a <see cref="ControlCapabilitySet"/>: what must be true of the
/// capabilities Servyx holds for some higher-level guarantee (e.g. a <c>ControlTier</c>) to apply.
/// </summary>
public abstract record CapabilityRequirement
{
    /// <summary>True when <paramref name="set"/> satisfies this requirement.</summary>
    public abstract bool IsSatisfiedBy(ControlCapabilitySet set);

    /// <summary>
    /// When this requirement is not satisfied by <paramref name="set"/>, returns the capabilities that
    /// would satisfy it. The exact meaning depends on the requirement kind: for <see cref="All"/> it is
    /// the individual missing bits; for <see cref="AnyOf"/> it is the full list of alternatives (since
    /// any single one would do); for <see cref="Every"/> it is the union across unsatisfied parts.
    /// Returns an empty list when the requirement is already satisfied.
    /// </summary>
    public abstract IReadOnlyList<ControlCapability> UnsatisfiedAlternatives(ControlCapabilitySet set);

    /// <summary>Requires every bit in <paramref name="Mask"/> to be granted.</summary>
    /// <param name="Mask">The capability bits (combined with bitwise OR) that must all be granted.</param>
    public sealed record All(ControlCapability Mask) : CapabilityRequirement
    {
        /// <inheritdoc />
        public override bool IsSatisfiedBy(ControlCapabilitySet set) => set.Has(Mask);

        /// <inheritdoc />
        /// <remarks>Splits the missing bits of <see cref="Mask"/> into individual single-bit capabilities.</remarks>
        public override IReadOnlyList<ControlCapability> UnsatisfiedAlternatives(ControlCapabilitySet set)
        {
            var missing = set.Missing(Mask);
            if (missing == ControlCapability.None)
            {
                return [];
            }

            var bits = new List<ControlCapability>();
            for (var i = 0; i < 64; i++)
            {
                var bit = (ControlCapability)(1UL << i);
                if ((missing & bit) == bit)
                {
                    bits.Add(bit);
                }
            }

            return bits;
        }
    }

    /// <summary>
    /// Requires at least one of <paramref name="Alternatives"/> to be granted — the "alternative
    /// mechanisms" pattern: any qualifying mechanism satisfies the same underlying intent.
    /// </summary>
    /// <param name="Alternatives">
    /// The candidate capabilities, any one of which satisfies this requirement. An empty list is
    /// explicitly unsatisfiable: there is nothing that could hold to satisfy it, so
    /// <see cref="IsSatisfiedBy"/> always returns false and <see cref="UnsatisfiedAlternatives"/> always
    /// returns an empty list (there is nothing to suggest).
    /// </param>
    public sealed record AnyOf(params ControlCapability[] Alternatives) : CapabilityRequirement
    {
        /// <inheritdoc />
        public override bool IsSatisfiedBy(ControlCapabilitySet set) => Alternatives.Any(set.Has);

        /// <inheritdoc />
        public override IReadOnlyList<ControlCapability> UnsatisfiedAlternatives(ControlCapabilitySet set)
            => IsSatisfiedBy(set) ? [] : Alternatives;
    }

    /// <summary>Requires every one of <paramref name="Parts"/> to be independently satisfied.</summary>
    /// <param name="Parts">
    /// The sub-requirements, all of which must hold. An empty list is vacuously true: there is nothing
    /// that must hold, so <see cref="IsSatisfiedBy"/> always returns true and
    /// <see cref="UnsatisfiedAlternatives"/> always returns an empty list.
    /// </param>
    public sealed record Every(params CapabilityRequirement[] Parts) : CapabilityRequirement
    {
        /// <inheritdoc />
        public override bool IsSatisfiedBy(ControlCapabilitySet set) => Parts.All(p => p.IsSatisfiedBy(set));

        /// <inheritdoc />
        /// <remarks>Returns the deduplicated union of <see cref="UnsatisfiedAlternatives"/> across every unsatisfied part.</remarks>
        public override IReadOnlyList<ControlCapability> UnsatisfiedAlternatives(ControlCapabilitySet set)
            => Parts
                .Where(p => !p.IsSatisfiedBy(set))
                .SelectMany(p => p.UnsatisfiedAlternatives(set))
                .Distinct()
                .ToArray();
    }
}
