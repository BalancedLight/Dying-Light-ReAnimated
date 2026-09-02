using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

/// <summary>
/// Synthetic controls for the DL1 rig-conformance seam. These use a miniature
/// stand-in for the retail player skeleton so the suite never depends on an
/// installed game; the installed controls live separately.
/// </summary>
public sealed class RigConformanceTests
{
    [Fact]
    public void TemplateFactoryKeepsSkeletonRowsAndDropsSkinnedMeshRoots()
    {
        Dl1RigTemplate template = CreateTemplate();

        Assert.Equal(12, template.EntityCount);
        Assert.DoesNotContain(
            template.Entities,
            static entity => entity.Name.StartsWith("sc_", StringComparison.Ordinal));
        Assert.Equal("body.root", template[0].SemanticRole);
        Assert.Equal(BoneKind.Root, template[0].Kind);
        Assert.Equal(BoneKind.Camera, template[template.TryFindByName("eyecamera")!.Value].Kind);
        Assert.False(template[template.TryFindByName("eyecamera")!.Value].IsDeform);
        Assert.StartsWith("dl1rig:", template.TemplateId, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateGlobalRestReconstructsFromParentAndLocal()
    {
        Dl1RigTemplate template = CreateTemplate();

        foreach (Dl1RigTemplateEntity entity in template.Entities)
        {
            TransformMatrix expected = entity.ParentIndex < 0
                ? entity.LocalRestMatrix
                : template[entity.ParentIndex].GlobalRestMatrix * entity.LocalRestMatrix;
            Assert.True(expected.NearlyEquals(entity.GlobalRestMatrix, 1e-6));
        }
    }

    [Fact]
    public void ScalingATemplateScalesEveryRestOffsetAndChangesIdentity()
    {
        Dl1RigTemplate template = CreateTemplate();

        Dl1RigTemplate scaled = template.CreateScaled(0.5);

        Assert.Equal(template.ReferenceHeight * 0.5, scaled.ReferenceHeight, 9);
        Assert.NotEqual(template.TemplateId, scaled.TemplateId);
        for (int index = 0; index < template.EntityCount; index++)
        {
            Assert.Equal(
                template.GetRestSegmentLength(index) * 0.5,
                scaled.GetRestSegmentLength(index),
                9);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void TemplateRejectsNonPositiveScale(double scale)
    {
        Dl1RigTemplate template = CreateTemplate();

        Assert.Throws<ArgumentOutOfRangeException>(() => template.CreateScaled(scale));
    }

    [Fact]
    public void CorrespondenceJoinsByRoleAndClassifiesEveryRowExactlyOnce()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        // Every template entity appears exactly once, and every source bone is
        // either claimed by a template row or emitted as its own row.
        Assert.Equal(
            template.EntityCount,
            correspondence.Rows.Count(static row => row.TemplateIndex >= 0));
        Assert.Equal(
            source.BoneCount,
            correspondence.Rows.Count(static row => row.SourceBoneIndex >= 0));

        RigCorrespondenceRow upperArm = Row(correspondence, "l_upperarm");
        Assert.Equal(RigBoneDisposition.Mapped, upperArm.Disposition);
        Assert.Equal("CC_Base_L_Upperarm", upperArm.SourceName);
        Assert.Equal("arm.left.upper", upperArm.Role);
    }

    [Fact]
    public void StructuralDl1HelpersAreSynthesizedRatherThanGuessed()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        Assert.Equal(
            RigBoneDisposition.Synthesized,
            Row(correspondence, "eyecamera").Disposition);
        Assert.Equal(
            RigBoneDisposition.Synthesized,
            Row(correspondence, "hspine").Disposition);
    }

    [Fact]
    public void TwistAndShareBonesStayExtraInsteadOfBeingMisMapped()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        foreach (string name in new[]
                 {
                     "CC_Base_L_ForearmTwist01",
                     "CC_Base_L_ElbowShareBone",
                 })
        {
            RigCorrespondenceRow row = correspondence.Rows.Single(
                candidate => string.Equals(candidate.SourceName, name, StringComparison.Ordinal));
            Assert.Equal(RigBoneDisposition.Extra, row.Disposition);
        }
    }

    [Fact]
    public void DuplicateRoleResolvesToTheCommonAncestorAndIsReportedForReview()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        RigCorrespondenceRow pelvis = Row(correspondence, "pelvis");
        Assert.Equal("CC_Base_Hip", pelvis.SourceName);
        Assert.True(pelvis.WasAmbiguous);

        RigCorrespondenceAmbiguity ambiguity = Assert.Single(
            correspondence.Ambiguities,
            candidate => candidate.Role == "body.pelvis");
        Assert.Contains("CC_Base_Pelvis", ambiguity.CandidateSourceNames);
    }

    [Fact]
    public void ExplicitRoleOverrideBeatsTheAutomaticTieBreak()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(
            template,
            source,
            new RigCorrespondenceOptions
            {
                RoleOverrides = ImmutableDictionary<string, string>.Empty
                    .Add("body.pelvis", "CC_Base_Pelvis"),
            });

        Assert.Equal("CC_Base_Pelvis", Row(correspondence, "pelvis").SourceName);
    }

    [Fact]
    public void DroppingExtraBonesMovesThemOutOfTheEmittedRig()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();

        RigCorrespondence kept = RigCorrespondenceSolver.Solve(template, source);
        RigCorrespondence dropped = RigCorrespondenceSolver.Solve(
            template,
            source,
            new RigCorrespondenceOptions { DropExtraBones = true });

        Assert.True(kept.ExtraCount > 0);
        Assert.Equal(0, dropped.ExtraCount);
        Assert.Equal(kept.ExtraCount, dropped.DroppedCount);
        Assert.Equal(kept.MappedCount, dropped.MappedCount);
    }

    [Fact]
    public void LandmarkSolveRecoversAKnownUniformScale()
    {
        Dl1RigTemplate template = CreateTemplate();
        const double Expected = 0.8;
        RigDefinition source = CreateCharacterCreatorRig(Expected);
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        RigLandmarkSolution solution =
            RigLandmarkSolver.Solve(template, source, correspondence);

        Assert.Equal(Expected, solution.UniformScale, 3);
        // A uniformly scaled copy has no proportion disagreement at all.
        Assert.True(
            solution.ProportionResidual < 1e-6,
            $"expected a scaled copy to fit exactly, got {solution.ProportionResidual}");
    }

    [Fact]
    public void LandmarkSolveExcludesCrossBodyWidthFromTheScaleFit()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);

        RigLandmarkSolution solution =
            RigLandmarkSolver.Solve(template, source, correspondence);

        Assert.Contains(solution.Samples, static sample => sample.Region == RigScaleRegion.Width);
        Assert.All(
            solution.Samples.Where(static sample => sample.Region == RigScaleRegion.Width),
            static sample => Assert.True(sample.Ratio > 0.0));
    }

    [Fact]
    public void LandmarkAnchorIsThePelvisNotAFloorLevelSourceRoot()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);
        ImmutableArray<TransformMatrix> globals = source.CreateBindPose().GlobalMatrices;

        RigLandmarkSolution solution =
            RigLandmarkSolver.Solve(template, source, correspondence);

        Vector3D pelvis = globals[source.GetBoneIndex("CC_Base_Hip")].Translation;
        Assert.Equal(pelvis.Y, solution.PelvisAnchor.Y, 9);
        Assert.NotEqual(0.0, solution.PelvisAnchor.Y, 6);
    }

    [Fact]
    public void ZeroConformanceStrengthKeepsTheModelsOwnSegmentLengths()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigConformanceResult fit = SolveFit(strength: 0.0);
        RigDefinition source = CreateCharacterCreatorRig();
        ImmutableArray<TransformMatrix> globals = source.CreateBindPose().GlobalMatrices;
        int measured = 0;

        foreach (RigConformedBone bone in fit.Bones)
        {
            if (bone.Disposition != RigBoneDisposition.Mapped ||
                bone.ParentIndex < 0 ||
                bone.TemplateIndex < 0)
            {
                continue;
            }

            RigConformedBone parent = fit.Bones[bone.ParentIndex];
            if (parent.Disposition != RigBoneDisposition.Mapped ||
                parent.TemplateIndex < 0)
            {
                continue;
            }

            // A template-declared coincidence - bip01 with pelvis - stays
            // coincident at every strength, so there is no source length to
            // reproduce there.
            double templateLength = Vector3D.Distance(
                template[parent.TemplateIndex].GlobalRestMatrix.Translation,
                template[bone.TemplateIndex].GlobalRestMatrix.Translation);
            double sourceLength = Vector3D.Distance(
                globals[parent.SourceBoneIndex].Translation,
                globals[bone.SourceBoneIndex].Translation);
            if (sourceLength < 1e-6 || templateLength < 1e-6)
            {
                continue;
            }

            Assert.Equal(
                sourceLength,
                Vector3D.Distance(parent.Position, bone.Position),
                6);
            measured++;
        }

        Assert.True(measured > 0, "the fixture must contain a mapped chain to measure");
    }

    /// <summary>
    /// Bone directions are the part that is not negotiable: DL1 couples a
    /// bone's inverse bind to its place in the hierarchy and its clips key
    /// per-bone translation, so a bind that keeps the model's own joint angles
    /// cannot play stock animation cleanly at any length setting.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void EveryStrengthReproducesDl1RestDirectionsExactly(double strength)
    {
        Dl1RigTemplate template = CreateTemplate();
        RigConformanceResult fit = SolveFit(strength);
        int measured = 0;

        foreach (RigConformedBone bone in fit.Bones)
        {
            if (bone.TemplateIndex < 0 || bone.ParentIndex < 0)
            {
                continue;
            }

            RigConformedBone parent = fit.Bones[bone.ParentIndex];
            if (parent.TemplateIndex < 0)
            {
                continue;
            }

            Vector3D templateOffset =
                template[bone.TemplateIndex].GlobalRestMatrix.Translation -
                template[parent.TemplateIndex].GlobalRestMatrix.Translation;
            Vector3D fittedOffset = bone.Position - parent.Position;
            if (!templateOffset.TryNormalize(out Vector3D wanted) ||
                !fittedOffset.TryNormalize(out Vector3D actual))
            {
                continue;
            }

            Assert.Equal(1.0, Vector3D.Dot(wanted, actual), 6);
            measured++;
        }

        Assert.True(measured > 0, "the fixture must contain a measurable chain");
    }

    /// <summary>
    /// The transfer must land every mapped joint exactly on the target it was
    /// given. Any residual means a chain could not be oriented, which would
    /// show up as mangled geometry.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void RestPoseTransferPinsEveryMappedJointOnItsTarget(double strength)
    {
        RigConformanceResult fit = SolveFit(strength);

        Assert.Equal(0.0, fit.RestPoseTransfer.MaximumJointResidual, 9);
        Assert.All(
            fit.RestPoseTransfer.PosedGlobals,
            static posed => Assert.True(posed.IsFinite));
        Assert.All(
            fit.RestPoseTransfer.SkinningTransforms,
            static transform => Assert.True(
                Math.Abs(transform.LinearDeterminant - 1.0) < 1e-6,
                "a rest-pose transform must be a rotation and a translation, never a scale or a reflection"));
    }

    /// <summary>
    /// Transferring onto the pose the model already has must change nothing.
    /// </summary>
    [Fact]
    public void TransferOntoTheSourcesOwnRestPoseIsTheIdentity()
    {
        RigDefinition source = CreateCharacterCreatorRig();
        ImmutableArray<TransformMatrix> globals = source.CreateBindPose().GlobalMatrices;
        var targets = ImmutableArray.CreateBuilder<Vector3D?>(source.BoneCount);
        foreach (TransformMatrix global in globals)
        {
            targets.Add(global.Translation);
        }

        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            source,
            globals,
            targets.MoveToImmutable());

        Assert.Equal(0.0, transfer.MaximumJointResidual, 9);
        Assert.All(
            transfer.SkinningTransforms,
            static transform => Assert.True(
                transform.NearlyEquals(TransformMatrix.Identity, 1e-6),
                "an unchanged rest pose must produce identity transforms"));
    }

    [Fact]
    public void FullConformanceMatchesTemplateSegmentLengthsAtTheSolvedScale()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigConformanceResult fit = SolveFit(strength: 1.0);
        double scale = fit.Landmark.UniformScale;

        foreach (RigConformedBone bone in fit.Bones)
        {
            if (bone.Disposition != RigBoneDisposition.Mapped ||
                bone.ParentIndex < 0 ||
                bone.TemplateIndex < 0)
            {
                continue;
            }

            RigConformedBone parent = fit.Bones[bone.ParentIndex];
            if (parent.TemplateIndex < 0)
            {
                continue;
            }

            double expected = Vector3D.Distance(
                template[parent.TemplateIndex].GlobalRestMatrix.Translation,
                template[bone.TemplateIndex].GlobalRestMatrix.Translation) * scale;
            double actual = Vector3D.Distance(parent.Position, bone.Position);
            Assert.Equal(expected, actual, 6);
        }
    }

    [Fact]
    public void TemplateDeclaredCoincidencesSurviveEveryConformanceStrength()
    {
        foreach (double strength in new[] { 0.0, 0.5, 1.0 })
        {
            RigConformanceResult fit = SolveFit(strength);
            Vector3D root = fit.Bones.Single(static bone => bone.Name == "bip01").Position;
            Vector3D pelvis = fit.Bones.Single(static bone => bone.Name == "pelvis").Position;

            Assert.Equal(0.0, Vector3D.Distance(root, pelvis), 9);
        }
    }

    [Fact]
    public void ConformedRigStaysTopologicallyOrderedWithUniqueNames()
    {
        RigConformanceResult fit = SolveFit(strength: 1.0);

        Assert.All(fit.Bones, bone => Assert.True(bone.ParentIndex < bone.Index));
        Assert.All(fit.Bones, static bone => Assert.True(bone.Position.IsFinite));
        Assert.Equal(
            fit.Bones.Length,
            fit.Bones.Select(static bone => bone.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void ProportionMismatchIsReportedRatherThanSilentlyAbsorbed()
    {
        // The arm is deliberately half the template's proportion while the rest
        // of the rig matches, so conforming must flag it.
        RigConformanceResult fit = SolveFit(strength: 1.0, armScale: 0.5);

        Assert.Contains(
            fit.Warnings,
            static warning => warning.BoneName == "l_forearm");
        Assert.All(
            fit.Warnings,
            static warning => Assert.False(string.IsNullOrWhiteSpace(warning.Message)));
    }

    [Fact]
    public void ManualPositionOverrideWins()
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig();
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);
        RigLandmarkSolution landmark =
            RigLandmarkSolver.Solve(template, source, correspondence);
        var placed = new Vector3D(0.25, 1.11, -0.05);

        RigConformanceResult fit = RigConformanceSolver.Solve(
            template,
            source,
            correspondence,
            landmark,
            new RigConformanceOptions
            {
                PositionOverrides = ImmutableDictionary<string, Vector3D>.Empty
                    .Add("l_hand", placed),
            });

        Assert.Equal(
            0.0,
            Vector3D.Distance(
                placed,
                fit.Bones.Single(static bone => bone.Name == "l_hand").Position),
            9);
    }

    private static RigCorrespondenceRow Row(RigCorrespondence correspondence, string name) =>
        correspondence.Rows.Single(
            row => string.Equals(row.Name, name, StringComparison.Ordinal));

    private static RigConformanceResult SolveFit(double strength, double armScale = 1.0)
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateCharacterCreatorRig(armScale: armScale);
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(template, source);
        RigLandmarkSolution landmark =
            RigLandmarkSolver.Solve(template, source, correspondence);
        return RigConformanceSolver.Solve(
            template,
            source,
            correspondence,
            landmark,
            new RigConformanceOptions { ConformanceStrength = strength });
    }

    /// <summary>
    /// A miniature stand-in for the retail player skeleton. It reproduces the
    /// structural facts that matter: a root co-located with the pelvis, a
    /// helper spine row, a camera helper, and named limb chains. Positions are
    /// arbitrary but consistent.
    /// </summary>
    private static Dl1RigTemplate CreateTemplate()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("bip01", -1, CompactMeshEntityType.Bone, 0.0, 0.0, 0.0),
            ("pelvis", 0, CompactMeshEntityType.Bone, 0.0, 0.0, 0.0),
            ("hspine", 1, CompactMeshEntityType.Helper, 0.0, 0.10, 0.0),
            ("spine", 2, CompactMeshEntityType.Bone, 0.0, 0.0, 0.0),
            ("head", 3, CompactMeshEntityType.Bone, 0.0, 0.50, 0.0),
            ("eyecamera", 4, CompactMeshEntityType.Helper, 0.0, 0.05, 0.10),
            ("l_upperarm", 3, CompactMeshEntityType.Bone, 0.20, 0.40, 0.0),
            ("l_forearm", 6, CompactMeshEntityType.Bone, 0.30, 0.0, 0.0),
            ("l_hand", 7, CompactMeshEntityType.Bone, 0.25, 0.0, 0.0),
            ("r_upperarm", 3, CompactMeshEntityType.Bone, -0.20, 0.40, 0.0),
            ("r_forearm", 9, CompactMeshEntityType.Bone, -0.30, 0.0, 0.0),
            ("r_hand", 10, CompactMeshEntityType.Bone, -0.25, 0.0, 0.0),
            ("sc_body", -1, CompactMeshEntityType.SkinnedMesh, 0.0, 0.0, 0.0));

        return Dl1RigTemplateFactory.TryCreate(
            Dl1RigTemplateFactory.PlayerProfileName,
            "player_1_tpp",
            "synthetic-fingerprint",
            hierarchy)!;
    }

    /// <summary>
    /// A Character Creator style source rig: a floor-level root, both a hip and
    /// a pelvis competing for the same role, and twist and share bones that
    /// must not be auto-mapped.
    /// </summary>
    private static RigDefinition CreateCharacterCreatorRig(
        double scale = 1.0,
        double armScale = 1.0)
    {
        var rows = new List<(string Name, int Parent, Vector3D Offset)>
        {
            ("RL_BoneRoot", -1, Vector3D.Zero),
            ("CC_Base_Hip", 0, new Vector3D(0.0, 0.95, 0.0)),
            ("CC_Base_Pelvis", 1, Vector3D.Zero),
            ("CC_Base_Waist", 2, new Vector3D(0.0, 0.10, 0.0)),
            ("CC_Base_Head", 3, new Vector3D(0.0, 0.50, 0.0)),
            ("CC_Base_L_Upperarm", 3, new Vector3D(0.20, 0.40, 0.0)),
            ("CC_Base_L_Forearm", 5, new Vector3D(0.30 * armScale, 0.0, 0.0)),
            ("CC_Base_L_Hand", 6, new Vector3D(0.25 * armScale, 0.0, 0.0)),
            ("CC_Base_L_ForearmTwist01", 6, new Vector3D(0.10, 0.0, 0.0)),
            ("CC_Base_L_ElbowShareBone", 5, new Vector3D(0.28, 0.0, 0.0)),
            ("CC_Base_R_Upperarm", 3, new Vector3D(-0.20, 0.40, 0.0)),
            ("CC_Base_R_Forearm", 10, new Vector3D(-0.30 * armScale, 0.0, 0.0)),
            ("CC_Base_R_Hand", 11, new Vector3D(-0.25 * armScale, 0.0, 0.0)),
        };

        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            (string name, int parent, Vector3D offset) = rows[index];
            bones.Add(new BoneDefinition(
                index,
                name,
                parent,
                new TransformTRS(offset * scale, QuaternionD.Identity, Vector3D.One),
                parent < 0 ? BoneKind.Root : BoneKind.Deform));
        }

        return new RigDefinition("source:test", "synthetic", bones.MoveToImmutable());
    }

    private static CompactMeshDocument CreateHierarchy(
        params (string Name, short Parent, CompactMeshEntityType Type, double X, double Y, double Z)[] rows)
    {
        CompactMeshEntity[] entities = rows
            .Select((row, index) => new CompactMeshEntity(
                index,
                row.Name,
                0,
                new CompactBounds(0, 0, 0, 1, 1, 1),
                row.Parent,
                row.Type,
                0,
                1,
                new CompactMatrix3x4(
                    1, 0, 0, (float)row.X,
                    0, 1, 0, (float)row.Y,
                    0, 0, 1, (float)row.Z),
                CompactMatrix3x4.Identity,
                0,
                0))
            .ToArray();
        return new CompactMeshDocument(
            entities.Length,
            entities.Count(static entity => entity.ParentIndex < 0),
            0,
            entities,
            []);
    }
}
