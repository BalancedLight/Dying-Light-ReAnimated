using System.Collections.Immutable;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// What the conformance pipeline does with one row of the converted rig.
/// </summary>
public enum RigBoneDisposition
{
    /// <summary>
    /// A template entity claimed by a source bone. The emitted row carries the
    /// DL1 name so stock animation descriptors resolve, and is fitted to the
    /// source bone's position.
    /// </summary>
    Mapped = 0,

    /// <summary>
    /// A template entity with no source counterpart. The row is generated from
    /// its parent using the template's own local rest offset at the solved
    /// scale. Every DL1 structural helper reaches the output this way.
    /// </summary>
    Synthesized = 1,

    /// <summary>
    /// A source bone with no template counterpart, retained under its mapped
    /// DL1 ancestor with its skin weights intact. Stock clips carry no
    /// descriptor for it, so it simply rests at bind.
    /// </summary>
    Extra = 2,

    /// <summary>
    /// A source bone deliberately removed. Its skin weights fold into the
    /// nearest surviving ancestor.
    /// </summary>
    Dropped = 3,
}

/// <summary>
/// One reviewable row of the source-to-template correspondence.
/// </summary>
public sealed record RigCorrespondenceRow
{
    public required RigBoneDisposition Disposition { get; init; }

    /// <summary>Template entity index, or -1 for an extra or dropped source bone.</summary>
    public required int TemplateIndex { get; init; }

    /// <summary>Source rig bone index, or -1 for a synthesized template entity.</summary>
    public required int SourceBoneIndex { get; init; }

    /// <summary>The emitted DL1 entity name for this row.</summary>
    public required string Name { get; init; }

    public string? TemplateName { get; init; }

    public string? SourceName { get; init; }

    /// <summary>The shared humanoid role that joined the two rigs, when any.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// Confidence in the join, from the shared name classifier. Synthesized
    /// and extra rows are structural conclusions rather than guesses and
    /// report 1.0.
    /// </summary>
    public required double Confidence { get; init; }

    public required string Evidence { get; init; }

    /// <summary>
    /// True when more than one source bone claimed this row's role and the
    /// solver had to choose. These rows are surfaced for explicit review.
    /// </summary>
    public bool WasAmbiguous { get; init; }
}

/// <summary>
/// An unresolved or resolved-by-tie-break role, retained so the wizard can ask
/// instead of silently trusting a guess.
/// </summary>
public sealed record RigCorrespondenceAmbiguity(
    string Role,
    string TemplateName,
    ImmutableArray<string> CandidateSourceNames,
    string ChosenSourceName,
    string Reason);

/// <summary>
/// The complete, immutable source-to-DL1 correspondence for one conversion.
/// </summary>
public sealed class RigCorrespondence
{
    public RigCorrespondence(
        string templateId,
        IEnumerable<RigCorrespondenceRow> rows,
        IEnumerable<RigCorrespondenceAmbiguity> ambiguities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ambiguities);

        TemplateId = templateId;
        Rows = rows.ToImmutableArray();
        Ambiguities = ambiguities.ToImmutableArray();
    }

    public string TemplateId { get; }

    public ImmutableArray<RigCorrespondenceRow> Rows { get; }

    public ImmutableArray<RigCorrespondenceAmbiguity> Ambiguities { get; }

    public int MappedCount => CountOf(RigBoneDisposition.Mapped);

    public int SynthesizedCount => CountOf(RigBoneDisposition.Synthesized);

    public int ExtraCount => CountOf(RigBoneDisposition.Extra);

    public int DroppedCount => CountOf(RigBoneDisposition.Dropped);

    /// <summary>
    /// Template entities that no source bone claimed and that carry a humanoid
    /// role. A body role landing here means the source rig is missing a limb
    /// the solver expected, which is worth telling the user about; a purely
    /// structural DL1 helper is not.
    /// </summary>
    public ImmutableArray<string> UnmatchedRoles =>
        Rows
            .Where(static row =>
                row.Disposition == RigBoneDisposition.Synthesized &&
                row.Role is not null)
            .Select(static row => row.Role!)
            .ToImmutableArray();

    private int CountOf(RigBoneDisposition disposition) =>
        Rows.Count(row => row.Disposition == disposition);
}
