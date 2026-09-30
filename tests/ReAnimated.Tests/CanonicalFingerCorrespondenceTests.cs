using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class CanonicalFingerCorrespondenceTests
{
    [Fact]
    public void UniqueCanonicalFingerChainsResolveWhileToeAlternativesRemainReviewable()
    {
        var (baseTemplate, baseRig, baseGeometry) = RigGeometryCorrespondenceTests.Fixture();
        var targetEntities = baseTemplate.Entities.ToList();
        var sourceBones = baseRig.Bones.ToList();
        var sourceGlobals = baseGeometry.GlobalBindMatrices.ToList();
        var supports = baseGeometry.Supports.ToList();

        int targetFinger1 = AddTarget(targetEntities, "finger.left.index.1", 6,
            targetEntities[6].GlobalRestMatrix.Translation + new Vector3D(.08, 0, 0));
        int targetFinger2 = AddTarget(targetEntities, "finger.left.index.2", targetFinger1,
            targetEntities[targetFinger1].GlobalRestMatrix.Translation + new Vector3D(.08, 0, 0));
        int targetToe = AddTarget(targetEntities, "toe.left", 12,
            targetEntities[12].GlobalRestMatrix.Translation + new Vector3D(.05, 0, 0));

        int sourceFinger1 = AddSource(sourceBones, sourceGlobals, supports,
            "anatomicalFingerOne", "finger.left.index.1", 6,
            sourceGlobals[6].Translation + new Vector3D(.112, 0, 0));
        AddSource(sourceBones, sourceGlobals, supports,
            "unclassifiedHandHelper", null, 6,
            sourceGlobals[6].Translation + new Vector3D(.112, 0, 0));
        int sourceFinger2 = AddSource(sourceBones, sourceGlobals, supports,
            "anatomicalFingerTwo", "finger.left.index.2", sourceFinger1,
            sourceGlobals[sourceFinger1].Translation + new Vector3D(.112, 0, 0));
        AddSource(sourceBones, sourceGlobals, supports,
            "unclassifiedFingerHelper", null, sourceFinger1,
            sourceGlobals[sourceFinger1].Translation + new Vector3D(.112, 0, 0));
        AddSource(sourceBones, sourceGlobals, supports,
            "toeCandidateA", "toe.left", 12,
            sourceGlobals[12].Translation + new Vector3D(.07, 0, 0));
        AddSource(sourceBones, sourceGlobals, supports,
            "toeCandidateB", "toe.left", 12,
            sourceGlobals[12].Translation + new Vector3D(.07, 0, 0));

        var template = new Dl1RigTemplate(
            "synthetic-finger-policy",
            "Synthetic finger correspondence",
            new string('b', 64),
            targetEntities);
        var rig = new RigDefinition("synthetic-finger-source", "Synthetic source rig", sourceBones);
        var geometry = new RigGeometryEvidence(
            new string('a', 64),
            "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            sourceGlobals.ToImmutableArray(),
            supports.ToImmutableArray());

        RigCorrespondence result = RigCorrespondenceSolver.Solve(
            template,
            rig,
            new() { GeometryEvidence = geometry });

        RigCorrespondenceRow first = result.Rows.Single(row => row.TemplateIndex == targetFinger1);
        RigCorrespondenceRow second = result.Rows.Single(row => row.TemplateIndex == targetFinger2);
        Assert.Equal(RigBoneDisposition.Mapped, first.Disposition);
        Assert.Equal(RigBoneDisposition.Mapped, second.Disposition);
        Assert.Equal("anatomicalFingerOne", first.SourceName);
        Assert.Equal("anatomicalFingerTwo", second.SourceName);
        Assert.False(first.WasAmbiguous, first.Evidence);
        Assert.False(second.WasAmbiguous, second.Evidence);
        Assert.Single(first.Candidates);
        Assert.Single(second.Candidates);
        Assert.DoesNotContain(result.Ambiguities, ambiguity =>
            ambiguity.Role is "finger.left.index.1" or "finger.left.index.2");

        RigCorrespondenceRow toe = result.Rows.Single(row => row.TemplateIndex == targetToe);
        Assert.Equal(RigBoneDisposition.Mapped, toe.Disposition);
        Assert.True(toe.WasAmbiguous, toe.Evidence);
        Assert.True(toe.Candidates.Length >= 2, toe.Evidence);
        Assert.Contains(result.Ambiguities, ambiguity => ambiguity.Role == "toe.left");
    }

    [Fact]
    public void UnnamedFingerChainKeepsGeometryCandidatesForReview()
    {
        var (baseTemplate, baseRig, baseGeometry) = RigGeometryCorrespondenceTests.Fixture();
        var targetEntities = baseTemplate.Entities.ToList();
        var sourceBones = baseRig.Bones.ToList();
        var sourceGlobals = baseGeometry.GlobalBindMatrices.ToList();
        var supports = baseGeometry.Supports.ToList();

        int targetFirst = AddTarget(targetEntities, "finger.left.index.1", 6,
            targetEntities[6].GlobalRestMatrix.Translation + new Vector3D(.08, 0, 0));
        int targetSecond = AddTarget(targetEntities, "finger.left.index.2", targetFirst,
            targetEntities[targetFirst].GlobalRestMatrix.Translation + new Vector3D(.08, 0, 0));
        int sourceFirst = AddSource(sourceBones, sourceGlobals, supports, "unnamed_a", null, 6,
            sourceGlobals[6].Translation + new Vector3D(.112, 0, 0));
        int sourceSecond = AddSource(sourceBones, sourceGlobals, supports, "unnamed_b", null, sourceFirst,
            sourceGlobals[sourceFirst].Translation + new Vector3D(.112, 0, 0));
        int disconnected = AddSource(sourceBones, sourceGlobals, supports, "unrelated_a", null, 0,
            sourceGlobals[sourceFirst].Translation);
        var template = new Dl1RigTemplate(
            "synthetic-unnamed-fingers", "Synthetic finger correspondence", new string('b', 64), targetEntities);
        var rig = new RigDefinition("synthetic-unnamed-source", "Synthetic source rig", sourceBones);
        var geometry = new RigGeometryEvidence(
            new string('a', 64), "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            sourceGlobals.ToImmutableArray(), supports.ToImmutableArray());

        RigCorrespondence result = RigCorrespondenceSolver.Solve(
            template, rig, new() { GeometryEvidence = geometry });

        foreach (var (targetIndex, sourceIndex) in new[] { (targetFirst, sourceFirst), (targetSecond, sourceSecond) })
        {
            RigCorrespondenceRow row = result.Rows.Single(candidate => candidate.TemplateIndex == targetIndex);
            Assert.Equal(RigBoneDisposition.Mapped, row.Disposition);
            Assert.Equal(sourceIndex, row.SourceBoneIndex);
            Assert.True(row.WasAmbiguous, row.Evidence);
            Assert.NotEmpty(row.Candidates);
            Assert.All(row.Candidates, candidate => Assert.False(candidate.NameAgrees));
            Assert.DoesNotContain(row.Candidates, candidate => candidate.SourceBoneIndex == disconnected);
            Assert.Contains(result.Ambiguities, ambiguity => ambiguity.Role == row.Role);
        }
    }

    private static int AddTarget(
        List<Dl1RigTemplateEntity> entities,
        string role,
        int parent,
        Vector3D point)
    {
        int index = entities.Count;
        entities.Add(new Dl1RigTemplateEntity
        {
            Index = index,
            Name = role,
            SemanticRole = role,
            ParentIndex = parent,
            Kind = BoneKind.Deform,
            IsDeform = true,
            GlobalRestMatrix = TransformMatrix.CreateTranslation(point),
            LocalRestMatrix = TransformMatrix.CreateTranslation(
                point - entities[parent].GlobalRestMatrix.Translation),
        });
        return index;
    }

    private static int AddSource(
        List<BoneDefinition> bones,
        List<TransformMatrix> globals,
        List<RigBoneGeometrySupport> supports,
        string name,
        string? role,
        int parent,
        Vector3D point)
    {
        int index = bones.Count;
        bones.Add(new BoneDefinition(
            index,
            name,
            parent,
            new TransformTRS(
                point - globals[parent].Translation,
                QuaternionD.Identity,
                Vector3D.One),
            BoneKind.Deform,
            semanticRole: role));
        globals.Add(TransformMatrix.CreateTranslation(point));
        supports.Add(new RigBoneGeometrySupport(index, 3, 1, point, point, point));
        return index;
    }
}
