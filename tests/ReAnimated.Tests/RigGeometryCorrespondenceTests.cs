using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class RigGeometryCorrespondenceTests
{
    [Fact]
    public void RenamedRigResolvesThroughGeometryAndHierarchyWhileMisleadingUnweightedNameRemainsExtra()
    {
        var (template, rig, geometry) = Fixture();
        var before = rig.Bones.Select(static b => b.LocalBindPose).ToArray();
        var result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry });
        foreach (var entity in template.Entities)
        {
            var row = result.Rows.Single(r => r.TemplateIndex == entity.Index);
            Assert.Equal(RigBoneDisposition.Mapped, row.Disposition);
            Assert.Equal(entity.Index, row.SourceBoneIndex);
            Assert.True(row.WasAmbiguous);
            Assert.Contains("selected-surface support", row.Evidence);
            Assert.NotEmpty(row.Candidates);
        }
        var head = result.Rows.Single(r => r.Role == "body.head" && r.TemplateIndex >= 0);
        Assert.Contains(head.Candidates, c => c.SourceName == "Head" && c.InfluenceSupport == 0);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceBoneIndex == 16).Disposition);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceName == "ForearmTwist").Disposition);
        Assert.Equal(before, rig.Bones.Select(static b => b.LocalBindPose));
        Assert.Equal(rig.BoneCount, result.Rows.Count(static r => r.SourceBoneIndex >= 0));
    }

    [Fact]
    public void ExplicitUnknownNameOverrideIsReservedAndDoesNotSilentlyRebindOrDropOtherBones()
    {
        var (template, rig, geometry) = Fixture();
        var result = RigCorrespondenceSolver.Solve(template, rig, new() {
            GeometryEvidence = geometry, RoleOverrides = ImmutableDictionary<string, string>.Empty.Add("body.head", "Head") });
        var head = result.Rows.Single(r => r.TemplateIndex == 3);
        Assert.Equal(16, head.SourceBoneIndex);
        Assert.True(head.Candidates[0].UserSelected);
        Assert.False(head.WasAmbiguous);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceBoneIndex == 3).Disposition);
        Assert.Equal(0, result.DroppedCount);
        Assert.Throws<ArgumentException>(() => RigCorrespondenceSolver.Solve(template, rig, new() {
            GeometryEvidence = geometry, RoleOverrides = ImmutableDictionary<string, string>.Empty.Add("body.head", "missing") }));
    }

    [Fact]
    public void CoincidentSupportedRoleCandidatesStayAmbiguousAndWrongBranchesCannotClaimKnownRoles()
    {
        var (template, rig, geometry) = Fixture();
        var supports = geometry.Supports.Add(new(16, 3, 1, geometry.GlobalBindMatrices[16].Translation,
            geometry.GlobalBindMatrices[16].Translation, geometry.GlobalBindMatrices[16].Translation));
        var result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry with { Supports = supports } });
        var head = result.Rows.Single(r => r.TemplateIndex == 3);
        Assert.True(head.WasAmbiguous);
        Assert.Equal(2, head.Candidates.Length);
        var movedBones = rig.Bones.Select(b => b.Index == 16 ? new BoneDefinition(16, "Head", 12,
            new TransformTRS(geometry.GlobalBindMatrices[16].Translation - geometry.GlobalBindMatrices[12].Translation, QuaternionD.Identity, Vector3D.One)) : b).ToArray();
        var changed = new RigDefinition("changed", "Changed", movedBones);
        var changedEvidence = geometry with { ParentIndices = changed.Bones.Select(static b => b.ParentIndex).ToImmutableArray(),
            RigLocalBindPoses = changed.Bones.Select(static b => b.LocalBindPose).ToImmutableArray(), Supports = supports };
        var constrained = RigCorrespondenceSolver.Solve(template, changed, new() { GeometryEvidence = changedEvidence });
        Assert.DoesNotContain(constrained.Rows.Single(r => r.TemplateIndex == 3).Candidates, c => c.SourceBoneIndex == 16);
    }

    [Fact]
    public void ChangedRestRigCannotReuseOldGeometryEvidenceAndCancellationStopsTheSolve()
    {
        var (template, rig, geometry) = Fixture();
        var changed = new RigDefinition("changed", "Changed", rig.Bones.Select(b => b.Index == 4 ? new BoneDefinition(b.Index, b.Name,
            b.ParentIndex, b.LocalBindPose with { Translation = b.LocalBindPose.Translation + Vector3D.UnitZ }) : b));
        Assert.Throws<ArgumentException>(() => RigCorrespondenceSolver.Solve(template, changed, new() { GeometryEvidence = geometry }));
        Assert.ThrowsAny<OperationCanceledException>(() => RigCorrespondenceSolver.Solve(template, rig,
            new() { GeometryEvidence = geometry }, new(true)));
    }

    [Fact]
    public void StrongGeometryAndHierarchyCanContradictSwappedAnatomicalLabelsForReview()
    {
        var (template, rig, geometry) = Fixture();
        var renamed = new RigDefinition("misleading", "Misleading labels", rig.Bones.Select(b => new BoneDefinition(b.Index,
            b.Index switch { 3 => "LFoot", 12 => "Head", 16 => "Marker", _ => b.Name }, b.ParentIndex, b.LocalBindPose, b.Kind)));
        var evidence = geometry with { BoneNames = renamed.Bones.Select(static b => b.Name).ToImmutableArray() };
        var result = RigCorrespondenceSolver.Solve(template, renamed, new() { GeometryEvidence = evidence });
        Assert.Equal(3, result.Rows.Single(r => r.TemplateIndex == 3).SourceBoneIndex);
        Assert.Equal(12, result.Rows.Single(r => r.TemplateIndex == 12).SourceBoneIndex);
        Assert.True(result.Rows.Single(r => r.TemplateIndex == 3).WasAmbiguous);
        Assert.Contains("foot.left", result.Rows.Single(r => r.TemplateIndex == 3).Evidence);
    }

    [Fact]
    public void PelvisRootAndExtraNativeAxialSlotsDoNotStealSpineHeadOrFacialBones()
    {
        var (originalTemplate, originalRig, originalGeometry) = Fixture();
        var globals = originalGeometry.GlobalBindMatrices.Skip(1).ToArray();
        globals[16] = TransformMatrix.CreateTranslation(globals[2].Translation + new Vector3D(0, -.015, .01));
        var bones = originalRig.Bones.Skip(1).Select(b => {
            int index = b.Index - 1;
            int parent = index == 16 ? 2 : b.ParentIndex - 1;
            string name = index switch { 0 => "spine", 1 => "spine_001", 2 => "Head", 15 => "FaceMarker", 16 => "Jaw", _ => b.Name };
            return new BoneDefinition(index, name, parent, new TransformTRS(parent < 0 ? globals[index].Translation :
                globals[index].Translation - globals[parent].Translation, QuaternionD.Identity, Vector3D.One), parent < 0 ? BoneKind.Root : b.Kind);
        }).ToArray();
        var rig = new RigDefinition("pelvic-root", "Pelvic root", bones);
        var supports = originalGeometry.Supports.Select(s => s with { BoneIndex = s.BoneIndex - 1,
            Centroid = globals[s.BoneIndex - 1].Translation, Min = globals[s.BoneIndex - 1].Translation, Max = globals[s.BoneIndex - 1].Translation }).ToImmutableArray();
        var geometry = new RigGeometryEvidence(new string('a', 64), "synthetic-current-binding", rig.Bones.Select(static b => b.Name).ToImmutableArray(),
            rig.Bones.Select(static b => b.ParentIndex).ToImmutableArray(), rig.Bones.Select(static b => b.LocalBindPose).ToImmutableArray(), globals.ToImmutableArray(), supports);
        var entities = new List<Dl1RigTemplateEntity>();
        var indexes = new Dictionary<int, int>();
        foreach (var entity in originalTemplate.Entities)
        {
            int parent = entity.ParentIndex < 0 ? -1 : indexes[entity.ParentIndex];
            if (entity.Index is 2 or 3)
            {
                string role = entity.Index == 2 ? "body.spine.base" : "body.neck.1";
                entities.Add(new() { Index = entities.Count, Name = role, SemanticRole = role, ParentIndex = parent,
                    Kind = BoneKind.Deform, IsDeform = true, GlobalRestMatrix = entity.GlobalRestMatrix,
                    LocalRestMatrix = entities[parent].GlobalRestMatrix.InvertedAffine() * entity.GlobalRestMatrix });
                parent = entities.Count - 1;
            }
            indexes.Add(entity.Index, entities.Count);
            entities.Add(entity with { Index = entities.Count, ParentIndex = parent,
                LocalRestMatrix = parent < 0 ? entity.GlobalRestMatrix : entities[parent].GlobalRestMatrix.InvertedAffine() * entity.GlobalRestMatrix });
        }
        var template = new Dl1RigTemplate("synthetic", "synthetic", new string('b', 64), entities);
        var result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry });
        Assert.Equal("spine", result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == "body.pelvis").SourceName);
        Assert.Equal("spine_001", result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == "body.spine.0").SourceName);
        Assert.Equal("Head", result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == "body.head").SourceName);
        foreach (string role in new[] { "body.root", "body.spine.base", "body.neck.1" })
            Assert.Equal(RigBoneDisposition.Synthesized, result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == role).Disposition);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceName == "Jaw").Disposition);
    }

    internal static (Dl1RigTemplate Template, RigDefinition Rig, RigGeometryEvidence Geometry) Fixture()
    {
        (string Role, int Parent, Vector3D Position)[] joints = [
            ("body.root", -1, new(0, 0, 0)), ("body.pelvis", 0, new(0, 1, 0)),
            ("body.spine.0", 1, new(0, 1.3, 0)), ("body.head", 2, new(0, 1.8, 0)),
            ("arm.left.upper", 2, new(.3, 1.4, 0)), ("arm.left.lower", 4, new(.6, 1.4, .1)), ("hand.left", 5, new(.9, 1.4, .15)),
            ("arm.right.upper", 2, new(-.3, 1.4, 0)), ("arm.right.lower", 7, new(-.6, 1.4, .1)), ("hand.right", 8, new(-.9, 1.4, .15)),
            ("leg.left.upper", 1, new(.15, .95, 0)), ("leg.left.lower", 10, new(.15, .5, .05)), ("foot.left", 11, new(.15, .1, .1)),
            ("leg.right.upper", 1, new(-.15, .95, 0)), ("leg.right.lower", 13, new(-.15, .5, .05)), ("foot.right", 14, new(-.15, .1, .1)) ];
        var template = new Dl1RigTemplate("synthetic", "synthetic", new string('b', 64), joints.Select((j, i) => new Dl1RigTemplateEntity {
            Index = i, Name = j.Role, SemanticRole = j.Role, ParentIndex = j.Parent, Kind = i == 0 ? BoneKind.Root : BoneKind.Deform,
            IsDeform = i > 0, GlobalRestMatrix = TransformMatrix.CreateTranslation(j.Position),
            LocalRestMatrix = TransformMatrix.CreateTranslation(j.Parent < 0 ? j.Position : j.Position - joints[j.Parent].Position) }));
        var offset = new Vector3D(3, -2, 1);
        var globals = joints.Select(j => TransformMatrix.CreateTranslation(j.Position * 1.4 + offset)).ToList();
        globals.Add(globals[3]);
        globals.Add(TransformMatrix.CreateTranslation(new Vector3D(.7, 1.4, .12) * 1.4 + offset));
        var bones = joints.Select((j, i) => new BoneDefinition(i, $"node_{i}", j.Parent,
            new TransformTRS(j.Parent < 0 ? globals[i].Translation : globals[i].Translation - globals[j.Parent].Translation, QuaternionD.Identity, Vector3D.One),
            i == 0 ? BoneKind.Root : BoneKind.Deform)).ToList();
        bones.Add(new(16, "Head", 2, new(globals[16].Translation - globals[2].Translation, QuaternionD.Identity, Vector3D.One)));
        bones.Add(new(17, "ForearmTwist", 5, new(globals[17].Translation - globals[5].Translation, QuaternionD.Identity, Vector3D.One)));
        var rig = new RigDefinition("source", "Renamed source", bones);
        var supports = Enumerable.Range(1, 15).Append(17).Select(i => new RigBoneGeometrySupport(i, 3, 1, globals[i].Translation,
            globals[i].Translation, globals[i].Translation)).ToImmutableArray();
        var geometry = new RigGeometryEvidence(new string('a', 64), "synthetic-current-binding", rig.Bones.Select(static b => b.Name).ToImmutableArray(),
            rig.Bones.Select(static b => b.ParentIndex).ToImmutableArray(), rig.Bones.Select(static b => b.LocalBindPose).ToImmutableArray(), globals.ToImmutableArray(), supports);
        return (template, rig, geometry);
    }
}
