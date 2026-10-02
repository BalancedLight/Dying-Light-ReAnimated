using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class ViewModelAnimationMappingFingerprintTests
{
    [Fact]
    public void InitialFingerprintMustUseTheSameNormalizedRowsAsPersistence()
    {
        RigDefinition source = Rig("source", "source_bone", 0x01010101);
        RigDefinition target = Rig("target", "target_bone", 0x02020202);
        ImmutableArray<ProjectBoneMapping> rows = [Row(false, "", "", "")];
        ImmutableArray<ProjectBoneMapping> normalized =
            ProjectSerializer.NormalizeMappingProvenanceForPersistence(rows);
        string raw = Fingerprint(source, target, rows);
        string persisted = Fingerprint(source, target, normalized);
        string cliReload = Fingerprint(source, target, [rows[0] with
        {
            Evidence = "No mapping evidence recorded.",
        }]);

        Assert.NotEqual(raw, cliReload);
        Assert.Equal(raw, persisted);
    }

    [Fact]
    public void NonLegacyReviewEvidenceSurvivesNormalizationAndFingerprintRoundTrip()
    {
        RigDefinition source = Rig("source-reviewed", "source_bone", 0x11010101);
        RigDefinition target = Rig("target-reviewed", "target_bone", 0x22020202);
        ImmutableArray<ProjectBoneMapping> rows = [Row(
            true, "Reviewed evidence.", "review-scorer-v2", new string('e', 64))];
        ImmutableArray<ProjectBoneMapping> normalized =
            ProjectSerializer.NormalizeMappingProvenanceForPersistence(rows);

        Assert.Equal(rows[0].SourceBoneName, normalized[0].SourceBoneName);
        Assert.Equal(rows[0].Evidence, normalized[0].Evidence);
        Assert.Equal(rows[0].ScorerVersion, normalized[0].ScorerVersion);
        Assert.Equal(rows[0].EvidenceFingerprint, normalized[0].EvidenceFingerprint);
        Assert.Equal(Fingerprint(source, target, rows), Fingerprint(source, target, normalized));
    }

    [Fact]
    public void AssistedUnreviewedPlaceholderUsesTheSameParsedSentinelAsCliReload()
    {
        RigDefinition source = Rig("source-assisted", "source_bone", 0x31010101);
        RigDefinition target = Rig("target-assisted", "target_bone", 0x32020202);
        ImmutableArray<ProjectBoneMapping> rows = [new ProjectBoneMapping
        {
            SourceBoneName = "source_bone",
            TargetBoneName = "target_bone",
            Method = BoneMappingMethod.Manual.ToString(),
            Confidence = 0.5,
            Evidence = "No mapping evidence recorded.",
            ReviewOrigin = ProjectMappingReviewOrigin.None,
            ScorerVersion = "dlra-assisted-review-v1",
            EvidenceFingerprint = new string('a', 64),
            IsReviewed = false,
            MappingKind = RetargetMappingKind.Bone,
            TransferPolicy = RetargetTransferPolicy.GlobalBindBasis,
            ComponentPolicy = RetargetComponentPolicy.FullTransform,
            TransformComponents = RetargetTransformComponents.All,
        }];

        Assert.Equal(Fingerprint(source, target, rows),
            Fingerprint(source, target, ProjectSerializer.NormalizeMappingProvenanceForPersistence(rows)));
    }

    private static ProjectBoneMapping Row(
        bool reviewed,
        string evidence,
        string scorer,
        string fingerprint) => new()
        {
            SourceBoneName = "source_bone",
            TargetBoneName = "target_bone",
            Method = BoneMappingMethod.Manual.ToString(),
            Confidence = reviewed ? 1.0 : 0.75,
            Evidence = evidence,
            ReviewOrigin = reviewed ? ProjectMappingReviewOrigin.Explicit : ProjectMappingReviewOrigin.None,
            ScorerVersion = scorer,
            EvidenceFingerprint = fingerprint,
            IsReviewed = reviewed,
            MappingKind = RetargetMappingKind.Bone,
            TransferPolicy = RetargetTransferPolicy.GlobalBindBasis,
            ComponentPolicy = RetargetComponentPolicy.FullTransform,
            TransformComponents = RetargetTransformComponents.All,
        };

    private static string Fingerprint(
        RigDefinition source,
        RigDefinition target,
        IEnumerable<ProjectBoneMapping> rows)
    {
        RetargetMap map = new(
            source.Id,
            target.Id,
            rows.Select(row => new BoneMapEntry(
                source.GetBoneIndex(row.SourceBoneName),
                target.GetBoneIndex(row.TargetBoneName),
                Enum.Parse<BoneMappingMethod>(row.Method),
                row.Confidence,
                isReviewed: row.IsReviewed,
                mappingKind: row.MappingKind,
                transferPolicy: row.TransferPolicy,
                componentPolicy: row.ComponentPolicy,
                evidence: string.IsNullOrWhiteSpace(row.Evidence)
                    ? []
                    : [new MappingEvidence(MappingEvidenceKind.ManualSelection, row.Evidence)],
                scorerVersion: row.ScorerVersion,
                evidenceFingerprint: row.EvidenceFingerprint,
                transformComponents: row.TransformComponents)));
        return RetargetMapFingerprint.Compute(
            RigSignature.Compute(source),
            RigSignature.Compute(target),
            new string('a', 64),
            map);
    }

    private static RigDefinition Rig(string id, string name, uint descriptor) =>
        new(id, id, [new BoneDefinition(
            0, name, -1, TransformTRS.Identity, BoneKind.Root,
            descriptorHash: descriptor)]);
}
