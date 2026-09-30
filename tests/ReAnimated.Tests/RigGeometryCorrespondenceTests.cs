using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;
using ReAnimated.Retargeting.Mapping;

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
    public void ConflictingAnatomicalLabelsRequireAnExplicitOverride()
    {
        var (template, rig, geometry) = Fixture();
        var renamed = new RigDefinition("misleading", "Misleading labels", rig.Bones.Select(b => new BoneDefinition(b.Index,
            b.Index switch { 3 => "LFoot", 12 => "Head", 16 => "Marker", _ => b.Name }, b.ParentIndex, b.LocalBindPose, b.Kind)));
        var evidence = geometry with { BoneNames = renamed.Bones.Select(static b => b.Name).ToImmutableArray() };
        var result = RigCorrespondenceSolver.Solve(template, renamed, new() { GeometryEvidence = evidence });
        Assert.Equal(RigBoneDisposition.Synthesized, result.Rows.Single(r => r.TemplateIndex == 3).Disposition);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceBoneIndex == 3).Disposition);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceBoneIndex == 12).Disposition);
        var reviewed = RigCorrespondenceSolver.Solve(template, renamed, new() {
            GeometryEvidence = evidence,
            RoleOverrides = ImmutableDictionary<string, string>.Empty.Add("body.head", "LFoot").Add("foot.left", "Head") });
        Assert.Equal(3, reviewed.Rows.Single(r => r.TemplateIndex == 3).SourceBoneIndex);
        Assert.Equal(12, reviewed.Rows.Single(r => r.TemplateIndex == 12).SourceBoneIndex);
        Assert.True(reviewed.Rows.Single(r => r.TemplateIndex == 3).Candidates[0].UserSelected);
    }

    [Fact]
    public void DecorativeRootSiblingCannotReplaceTheSupportedBipedRoot()
    {
        var (template, originalRig, originalGeometry) = Fixture();
        var names = new Dictionary<int, string>
        {
            [0] = "Bip001",
            [1] = "Bip001 Pelvis",
            [3] = "Bip001 Head",
            [10] = "Bip001 L Thigh",
            [13] = "Bip001 R Thigh",
        };
        var bones = originalRig.Bones.Select(bone => new BoneDefinition(
            bone.Index,
            names.GetValueOrDefault(bone.Index, bone.Name),
            bone.ParentIndex,
            bone.LocalBindPose,
            bone.Kind)).ToList();
        Vector3D rootPosition = originalGeometry.GlobalBindMatrices[0].Translation;
        Vector3D decorativePosition = originalGeometry.GlobalBindMatrices[1].Translation;
        bones.Add(new BoneDefinition(
            bones.Count,
            "Root",
            0,
            new TransformTRS(
                decorativePosition - rootPosition,
                QuaternionD.Identity,
                Vector3D.One),
            BoneKind.Helper));
        var rig = new RigDefinition("two-roots", "Biped with decoration", bones);
        var geometry = new RigGeometryEvidence(
            new string('a', 64),
            "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            originalGeometry.GlobalBindMatrices.Add(
                TransformMatrix.CreateTranslation(decorativePosition)),
            originalGeometry.Supports);

        RigCorrespondence result = RigCorrespondenceSolver.Solve(
            template,
            rig,
            new RigCorrespondenceOptions { GeometryEvidence = geometry });

        Assert.Equal("Bip001", result.Rows.Single(row =>
            row.TemplateIndex >= 0 && row.Role == "body.root").SourceName);
        Assert.Equal("Bip001 Pelvis", result.Rows.Single(row =>
            row.TemplateIndex >= 0 && row.Role == "body.pelvis").SourceName);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(row =>
            row.SourceName == "Root").Disposition);
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
        Assert.Equal(RigBoneDisposition.Synthesized, result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == "body.pelvis").Disposition);
        Assert.DoesNotContain(result.Rows.Where(r => r.TemplateIndex >= 0 && r.SourceName is not null), row =>
            HumanoidBoneSemanticClassifier.Classify(row.SourceName)?.Role is { } namedRole && namedRole != row.Role);
        Assert.Equal("Head", result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == "body.head").SourceName);
        foreach (string role in new[] { "body.root", "body.spine.base", "body.neck.1" })
            Assert.Equal(RigBoneDisposition.Synthesized, result.Rows.Single(r => r.TemplateIndex >= 0 && r.Role == role).Disposition);
        Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(r => r.SourceName == "Jaw").Disposition);
    }

    [Fact]
    public void NamedLimbAnchorsConstrainAxialMappingAndWeightedGarmentsRemainExtra()
    {
        var (template, originalRig, originalGeometry) = Fixture();
        var roleNames = new Dictionary<int, string> {
            [3] = "Head", [4] = "topArm.L", [5] = "bottomArm.L", [6] = "Hand.L",
            [7] = "topArm.R", [8] = "bottomArm.R", [9] = "Hand.R",
            [10] = "legTop.L", [11] = "legBottom.L", [12] = "footFront.L",
            [13] = "legTop.R", [14] = "legBottom.R", [15] = "footFront.R" };
        var bones = originalRig.Bones.Select(bone => new BoneDefinition(bone.Index,
            roleNames.GetValueOrDefault(bone.Index, bone.Name), bone.ParentIndex, bone.LocalBindPose, bone.Kind)).ToList();
        var globals = originalGeometry.GlobalBindMatrices.ToList();
        var supports = originalGeometry.Supports.ToList();
        foreach ((string name, int parent, int coincident) in new[] {
            ("Hair.L", 2, 3), ("Skirt_01", 0, 1), ("ClothPanel.R", 2, 2),
            ("Tassel.L", 2, 4), ("mixamorig:F_skirt1", 0, 1),
            ("HatCrown", 3, 4), ("Ornament", 3, 4) })
        {
            int index = bones.Count;
            Vector3D point = globals[coincident].Translation +
                (name == "HatCrown" ? new Vector3D(0, 3, 0) : Vector3D.Zero);
            bones.Add(new BoneDefinition(index, name, parent,
                new TransformTRS(point - globals[parent].Translation, QuaternionD.Identity, Vector3D.One)));
            globals.Add(TransformMatrix.CreateTranslation(point));
            supports.Add(new RigBoneGeometrySupport(index, 3, 1, point, point, point));
        }
        var rig = new RigDefinition("named-limbs", "Named limb and garment controls", bones);
        var geometry = new RigGeometryEvidence(new string('a', 64), "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            globals.ToImmutableArray(), supports.ToImmutableArray());

        var result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry });
        foreach ((string role, string name) in new[] {
            ("arm.left.upper", "topArm.L"), ("arm.left.lower", "bottomArm.L"),
            ("arm.right.upper", "topArm.R"), ("arm.right.lower", "bottomArm.R"),
            ("leg.left.upper", "legTop.L"), ("leg.left.lower", "legBottom.L"), ("foot.left", "footFront.L"),
            ("leg.right.upper", "legTop.R"), ("leg.right.lower", "legBottom.R"), ("foot.right", "footFront.R") })
            Assert.Equal(name, result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == role).SourceName);
        Assert.Equal("node_1", result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == "body.pelvis").SourceName);
        Assert.Equal("Head", result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == "body.head").SourceName);
        foreach (string name in new[] { "Hair.L", "Skirt_01", "ClothPanel.R", "Tassel.L", "mixamorig:F_skirt1", "HatCrown", "Ornament" })
        {
            Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(row => row.SourceName == name).Disposition);
            Assert.DoesNotContain(result.Rows.Where(row => row.TemplateIndex >= 0).SelectMany(row => row.Candidates),
                candidate => candidate.SourceName == name);
        }

        var manuallySelected = RigCorrespondenceSolver.Solve(template, rig, new() {
            GeometryEvidence = geometry,
            RoleOverrides = ImmutableDictionary<string, string>.Empty.Add("body.pelvis", "Skirt_01") });
        Assert.Equal("Skirt_01", manuallySelected.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == "body.pelvis").SourceName);
    }

    [Fact]
    public void NamedPelvisCanMapWhenSourceRootStartsAtFeetAndTargetRootSharesPelvisPosition()
    {
        var (originalTemplate, originalRig, originalGeometry) = Fixture();
        Dl1RigTemplateEntity[] entities = originalTemplate.Entities.ToArray();
        Vector3D pelvisPoint = entities[1].GlobalRestMatrix.Translation;
        entities[0] = entities[0] with {
            GlobalRestMatrix = TransformMatrix.CreateTranslation(pelvisPoint),
            LocalRestMatrix = TransformMatrix.CreateTranslation(pelvisPoint) };
        entities[1] = entities[1] with { LocalRestMatrix = TransformMatrix.Identity };
        var template = new Dl1RigTemplate("coincident-root", "synthetic", new string('b', 64), entities);
        var roleNames = new Dictionary<int, string> {
            [0] = "root", [1] = "pelvis", [3] = "Head", [10] = "thigh_l", [13] = "thigh_r" };
        var rig = new RigDefinition("pelvis-at-feet-root", "Named pelvis", originalRig.Bones.Select(bone =>
            new BoneDefinition(bone.Index, roleNames.GetValueOrDefault(bone.Index, bone.Name),
                bone.ParentIndex, bone.LocalBindPose, bone.Kind)));
        var geometry = originalGeometry with { BoneNames = rig.Bones.Select(static bone => bone.Name).ToImmutableArray() };

        var result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry });
        Assert.Equal("root", result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == "body.root").SourceName);
        Assert.Equal("pelvis", result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == "body.pelvis").SourceName);
    }

    [Fact]
    public void SiblingShoulderAndUpperArmKeepShoulderExtraWhileUpperArmMaps()
    {
        var (originalTemplate, originalRig, originalGeometry) = Fixture();
        var targetEntities = new List<Dl1RigTemplateEntity>();
        var indexMap = new Dictionary<int, int>();
        foreach (Dl1RigTemplateEntity entity in originalTemplate.Entities)
        {
            int parent = entity.ParentIndex < 0 ? -1 : indexMap[entity.ParentIndex];
            if (entity.SemanticRole is "arm.left.upper" or "arm.right.upper")
            {
                string role = entity.SemanticRole == "arm.left.upper" ? "arm.left.clavicle" : "arm.right.clavicle";
                Vector3D point = entity.GlobalRestMatrix.Translation;
                point = new Vector3D(point.X * 0.5, point.Y, point.Z);
                int clavicle = targetEntities.Count;
                targetEntities.Add(new Dl1RigTemplateEntity {
                    Index = clavicle, Name = role, SemanticRole = role, ParentIndex = parent,
                    Kind = BoneKind.Deform, IsDeform = true,
                    GlobalRestMatrix = TransformMatrix.CreateTranslation(point),
                    LocalRestMatrix = TransformMatrix.CreateTranslation(point - targetEntities[parent].GlobalRestMatrix.Translation) });
                parent = clavicle;
            }
            indexMap.Add(entity.Index, targetEntities.Count);
            targetEntities.Add(entity with {
                Index = targetEntities.Count, ParentIndex = parent,
                LocalRestMatrix = parent < 0 ? entity.GlobalRestMatrix :
                    TransformMatrix.CreateTranslation(entity.GlobalRestMatrix.Translation - targetEntities[parent].GlobalRestMatrix.Translation) });
        }
        var template = new Dl1RigTemplate("sibling-shoulder", "synthetic", new string('b', 64), targetEntities);
        var names = new Dictionary<int, string> {
            [3] = "Head", [4] = "topArm.L", [5] = "bottomArm.L", [6] = "Hand.L",
            [7] = "topArm.R", [8] = "bottomArm.R", [9] = "Hand.R",
            [10] = "legTop.L", [11] = "legBottom.L", [12] = "footFront.L",
            [13] = "legTop.R", [14] = "legBottom.R", [15] = "footFront.R",
            [16] = "FaceMarker" };
        var bones = originalRig.Bones.Select(bone => new BoneDefinition(bone.Index,
            names.GetValueOrDefault(bone.Index, bone.Name), bone.ParentIndex, bone.LocalBindPose, bone.Kind)).ToList();
        var globals = originalGeometry.GlobalBindMatrices.ToList();
        var supports = originalGeometry.Supports.ToList();
        foreach ((string name, int upperArm) in new[] { ("shoulder.L", 4), ("shoulder.R", 7) })
        {
            int index = bones.Count;
            Vector3D upper = globals[upperArm].Translation;
            Vector3D point = new(upper.X * 0.5, upper.Y, upper.Z);
            bones.Add(new BoneDefinition(index, name, 2,
                new TransformTRS(point - globals[2].Translation, QuaternionD.Identity, Vector3D.One)));
            globals.Add(TransformMatrix.CreateTranslation(point));
            supports.Add(new RigBoneGeometrySupport(index, 3, 1, point, point, point));
        }
        var rig = new RigDefinition("sibling-shoulder-source", "Sibling shoulder", bones);
        var geometry = new RigGeometryEvidence(new string('a', 64), "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            globals.ToImmutableArray(), supports.ToImmutableArray());

        RigCorrespondence result = RigCorrespondenceSolver.Solve(template, rig, new() { GeometryEvidence = geometry });
        foreach ((string side, string shoulder, string upperArm) in new[] {
            ("left", "shoulder.L", "topArm.L"), ("right", "shoulder.R", "topArm.R") })
        {
            Assert.Equal(RigBoneDisposition.Synthesized,
                result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == $"arm.{side}.clavicle").Disposition);
            Assert.Equal(upperArm,
                result.Rows.Single(row => row.TemplateIndex >= 0 && row.Role == $"arm.{side}.upper").SourceName);
            Assert.Equal(RigBoneDisposition.Extra, result.Rows.Single(row => row.SourceName == shoulder).Disposition);
        }
    }

    [Fact]
    public void NamedUnweightedFingerIntermediatesMapWhenTheirDistalChainHasSkinSupport()
    {
        var (baseTemplate, baseRig, baseGeometry) = Fixture();
        var targetEntities = baseTemplate.Entities.ToList();
        var sourceBones = baseRig.Bones.ToList();
        var sourceGlobals = baseGeometry.GlobalBindMatrices.ToList();
        Vector3D sourceOffset = sourceGlobals[0].Translation;
        int targetParent = 6;
        int sourceParent = 6;
        for (int segment = 1; segment <= 3; segment++)
        {
            string role = $"finger.left.index.{segment}";
            Vector3D targetPoint = new(.9 + (.08 * segment), 1.4, .15);
            Vector3D sourcePoint = (targetPoint * 1.4) + sourceOffset;
            int targetIndex = targetEntities.Count;
            int sourceIndex = sourceBones.Count;
            targetEntities.Add(new Dl1RigTemplateEntity
            {
                Index = targetIndex,
                Name = $"l_finger1{segment}",
                SemanticRole = role,
                ParentIndex = targetParent,
                Kind = BoneKind.Deform,
                IsDeform = true,
                GlobalRestMatrix = TransformMatrix.CreateTranslation(targetPoint),
                LocalRestMatrix = TransformMatrix.CreateTranslation(
                    targetPoint - targetEntities[targetParent].GlobalRestMatrix.Translation),
            });
            sourceBones.Add(new BoneDefinition(
                sourceIndex,
                $"mixamorig:LeftHandIndex{segment}",
                sourceParent,
                new TransformTRS(
                    sourcePoint - sourceGlobals[sourceParent].Translation,
                    QuaternionD.Identity,
                    Vector3D.One),
                segment < 3 ? BoneKind.Helper : BoneKind.Deform));
            sourceGlobals.Add(TransformMatrix.CreateTranslation(sourcePoint));
            targetParent = targetIndex;
            sourceParent = sourceIndex;
        }

        var template = new Dl1RigTemplate("synthetic", "synthetic", new string('b', 64), targetEntities);
        var rig = new RigDefinition("source-with-finger", "Named intermediate fingers", sourceBones);
        Vector3D distalPoint = sourceGlobals[sourceParent].Translation;
        var supports = baseGeometry.Supports.Add(new RigBoneGeometrySupport(
            sourceParent, 3, 1, distalPoint, distalPoint, distalPoint));
        var geometry = new RigGeometryEvidence(
            new string('a', 64),
            "synthetic-current-binding",
            rig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            sourceGlobals.ToImmutableArray(),
            supports);

        RigCorrespondence result = RigCorrespondenceSolver.Solve(
            template, rig, new() { GeometryEvidence = geometry });
        for (int segment = 1; segment <= 3; segment++)
        {
            RigCorrespondenceRow row = result.Rows.Single(candidate =>
                candidate.Role == $"finger.left.index.{segment}" &&
                candidate.TemplateIndex >= 0);
            Assert.Equal(RigBoneDisposition.Mapped, row.Disposition);
            Assert.Equal($"mixamorig:LeftHandIndex{segment}", row.SourceName);
        }
        Assert.Equal(BoneKind.Helper, rig.Bones[rig.BoneCount - 3].Kind);
        Assert.Equal(BoneKind.Helper, rig.Bones[rig.BoneCount - 2].Kind);

        TransformMatrix[] displacedGlobals = sourceGlobals.ToArray();
        for (int index = rig.BoneCount - 3; index < rig.BoneCount - 1; index++)
        {
            displacedGlobals[index] = TransformMatrix.CreateTranslation(
                displacedGlobals[index].Translation + new Vector3D(0, 0, .8));
        }
        var displacedRig = new RigDefinition(
            "source-with-displaced-finger",
            "Named intermediate fingers with changed rest",
            rig.Bones.Select(bone => new BoneDefinition(
                bone.Index,
                bone.Name,
                bone.ParentIndex,
                new TransformTRS(
                    bone.ParentIndex < 0
                        ? displacedGlobals[bone.Index].Translation
                        : displacedGlobals[bone.Index].Translation -
                          displacedGlobals[bone.ParentIndex].Translation,
                    QuaternionD.Identity,
                    Vector3D.One),
                bone.Index >= rig.BoneCount - 3 && bone.Index < rig.BoneCount - 1
                    ? BoneKind.Deform
                    : bone.Kind)));
        var displacedEvidence = new RigGeometryEvidence(
            new string('a', 64),
            "synthetic-current-binding",
            displacedRig.Bones.Select(static bone => bone.Name).ToImmutableArray(),
            displacedRig.Bones.Select(static bone => bone.ParentIndex).ToImmutableArray(),
            displacedRig.Bones.Select(static bone => bone.LocalBindPose).ToImmutableArray(),
            displacedGlobals.ToImmutableArray(),
            supports);
        RigCorrespondence lowScore = RigCorrespondenceSolver.Solve(
            template, displacedRig, new()
            {
                GeometryEvidence = displacedEvidence,
            });
        RigCorrespondenceRow proposed = lowScore.Rows.Single(row =>
            row.Role == "finger.left.index.1" && row.TemplateIndex >= 0);
        Assert.Equal(RigBoneDisposition.Mapped, proposed.Disposition);
        Assert.Equal(BoneKind.Deform, displacedRig.Bones[displacedRig.BoneCount - 3].Kind);
        Assert.True(proposed.Candidates[0].Score < 0.58, proposed.Evidence);
        Assert.False(proposed.WasAmbiguous, proposed.Evidence);
        Assert.Contains("unique canonical finger candidate", proposed.Evidence);

        var directlyWeighted = displacedEvidence with
        {
            Supports = displacedEvidence.Supports
                .Add(new RigBoneGeometrySupport(
                    displacedRig.BoneCount - 3, 3, 1,
                    displacedGlobals[displacedRig.BoneCount - 3].Translation,
                    displacedGlobals[displacedRig.BoneCount - 3].Translation,
                    displacedGlobals[displacedRig.BoneCount - 3].Translation))
                .Add(new RigBoneGeometrySupport(
                    displacedRig.BoneCount - 2, 3, 1,
                    displacedGlobals[displacedRig.BoneCount - 2].Translation,
                    displacedGlobals[displacedRig.BoneCount - 2].Translation,
                    displacedGlobals[displacedRig.BoneCount - 2].Translation)),
        };
        Dl1RigTemplateEntity[] rotatedEntities = template.Entities.ToArray();
        for (int segment = 1; segment <= 3; segment++)
        {
            int index = Array.FindIndex(rotatedEntities, entity =>
                entity.SemanticRole == $"finger.left.index.{segment}");
            Vector3D point = new(.9, 1.4 - (.08 * segment), .15);
            int parent = rotatedEntities[index].ParentIndex;
            rotatedEntities[index] = rotatedEntities[index] with
            {
                GlobalRestMatrix = TransformMatrix.CreateTranslation(point),
                LocalRestMatrix = TransformMatrix.CreateTranslation(
                    point - rotatedEntities[parent].GlobalRestMatrix.Translation),
            };
        }
        var rotatedTemplate = new Dl1RigTemplate(
            "synthetic-rotated-finger", "synthetic", new string('b', 64), rotatedEntities);
        RigCorrespondence weightedLowScore = RigCorrespondenceSolver.Solve(
            rotatedTemplate, displacedRig, new() { GeometryEvidence = directlyWeighted });
        RigCorrespondenceRow weightedProposal = weightedLowScore.Rows.Single(row =>
            row.Role == "finger.left.index.1" && row.TemplateIndex >= 0);
        Assert.Equal(RigBoneDisposition.Mapped, weightedProposal.Disposition);
        Assert.False(weightedProposal.WasAmbiguous, weightedProposal.Evidence);
        Assert.Equal("mixamorig:LeftHandIndex1", weightedProposal.SourceName);
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
