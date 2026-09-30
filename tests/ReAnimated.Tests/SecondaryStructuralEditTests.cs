using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class SecondaryStructuralEditTests : IDisposable
{
    private const string NativeText = "MeshPartCloth()\n{\n  BonesGridSize(1, 1)\n  Bone(0, 0, \"body_head\", 1, 0, 0)\n  StructuralStiffness(0.31, 0.42) // authored coefficients\n}\n";
    private readonly string directory = RpackTestData.CreateTemporaryDirectory();

    [Theory]
    [InlineData(RigRestDescendantMode.KeepGlobal, RigRestSurfaceMode.PreserveSurface)]
    [InlineData(RigRestDescendantMode.FollowLocal, RigRestSurfaceMode.PreserveSurface)]
    [InlineData(RigRestDescendantMode.KeepGlobal, RigRestSurfaceMode.BakePose)]
    [InlineData(RigRestDescendantMode.FollowLocal, RigRestSurfaceMode.BakePose)]
    public void RestEditPreservesOrFollowsSecondaryWorldPointsAndReopens(
        RigRestDescendantMode descendants, RigRestSurfaceMode surfaces)
    {
        FbxModelAuthoringImportResult source = FbxRestPoseAuthoringTests.Source(true);
        CustomModelDocument originalDocument = source.Package.Document;
        int head = originalDocument.Bones.Single(b => b.Name == "body_head").Index;
        int neck = originalDocument.Bones.Single(b => b.Name == "body_neck_0").Index;
        source = WithSecondary(source, "body_head", "body_neck_0");
        string originalSecondary = SecondaryMotionSetupSerializer.Serialize(source.Package.Document.SecondaryMotion);
        TransformMatrix[] oldGlobals = FbxRestPoseAuthoringTests.ExactGlobals(source.Package.Document);
        SecondaryMotionGroup oldGroup = Assert.Single(source.Package.Document.SecondaryMotion.Groups);

        Guid entity = RiggingSessions.ObserveSourceHierarchy(source.Package.Document)[neck].EntityId;
        TransformMatrix desired = oldGlobals[neck] * TransformMatrix.CreateTranslation(new(.02, .03, .01)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, .4));
        FbxRestPosePreview preview = FbxRestPoseAuthoring.Preview(source, entity, desired, descendants, surfaces);
        Assert.True(preview.HasChanges);
        Assert.False(string.IsNullOrWhiteSpace(preview.Report.SecondaryMotionReview));
        Assert.Contains(surfaces == RigRestSurfaceMode.PreserveSurface ? "stay in place" : "follow the posed bones",
            preview.Report.SecondaryMotionReview, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NativeText, preview.PreviewModel.Package.Document.SecondaryMotion.NativeSources[0].Text);

        Assert.True(FbxRestPoseAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult result));
        SecondaryMotionDefinition actual = result.Package.Document.SecondaryMotion;
        Assert.Equal(originalSecondary, SecondaryMotionSetupSerializer.Serialize(source.Package.Document.SecondaryMotion));
        Assert.Equal(NativeText, actual.NativeSources[0].Text);
        Assert.Equal("body_head", actual.Groups[0].Particles[0].ReferenceBoneName);
        Assert.Equal("body_neck_0", actual.Groups[0].Particles[1].ReferenceBoneName);
        Assert.Equal(oldGroup.Particles[0].DrivenBoneName, actual.Groups[0].Particles[0].DrivenBoneName);
        Assert.Equal(oldGroup.Particles[1].DrivenBoneName, actual.Groups[0].Particles[1].DrivenBoneName);

        TransformMatrix[] newGlobals = FbxRestPoseAuthoringTests.ExactGlobals(result.Package.Document);
        Assert.False(oldGlobals[neck].NearlyEquals(newGlobals[neck], 1e-8));
        Assert.Equal(descendants == RigRestDescendantMode.KeepGlobal, oldGlobals[head].NearlyEquals(newGlobals[head], 1e-8));
        AssertSecondaryPoint(oldGlobals[head], oldGroup.Particles[0].LocalPosition,
            newGlobals[head], actual.Groups[0].Particles[0].LocalPosition,
            surfaces == RigRestSurfaceMode.PreserveSurface);
        AssertSecondaryPoint(oldGlobals[neck], oldGroup.Particles[1].LocalPosition,
            newGlobals[neck], actual.Groups[0].Particles[1].LocalPosition,
            surfaces == RigRestSurfaceMode.PreserveSurface);
        AssertSecondaryPoint(oldGlobals[head], oldGroup.Colliders[0].LocalPosition,
            newGlobals[head], actual.Groups[0].Colliders[0].LocalPosition,
            surfaces == RigRestSurfaceMode.PreserveSurface);
        AssertSecondaryPoint(oldGlobals[neck], oldGroup.Colliders[0].EndLocalPosition,
            newGlobals[neck], actual.Groups[0].Colliders[0].EndLocalPosition,
            surfaces == RigRestSurfaceMode.PreserveSurface);
        if (surfaces == RigRestSurfaceMode.BakePose)
        {
            Assert.Equal(oldGroup.Particles[0].LocalPosition, actual.Groups[0].Particles[0].LocalPosition);
            Assert.Equal(oldGroup.Particles[1].LocalPosition, actual.Groups[0].Particles[1].LocalPosition);
            Assert.Equal(oldGroup.Colliders[0].LocalPosition, actual.Groups[0].Colliders[0].LocalPosition);
            Assert.Equal(oldGroup.Colliders[0].EndLocalPosition, actual.Groups[0].Colliders[0].EndLocalPosition);
        }

        string path = Path.Combine(directory, $"rest-{descendants}-{surfaces}.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(result.Package, path);
        CustomModelPackage reopenedPackage = CustomModelPackageSerializer.Load(path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(reopenedPackage);
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(actual),
            SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
        Assert.Equal(actual.NativeSources[0].Text, reopened.Package.Document.SecondaryMotion.NativeSources[0].Text);
    }

    [Fact]
    public void HierarchyReparentKeepsWeightedAuthoredHelperAndSecondaryLocalAnchors()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelDocument document = source.Package.Document;
        int child = document.Bones.Single(b => b.Name == "Child").Index;
        document = CustomModelHelperAuthoring.DuplicateAsHelper(document, child,
            CustomModelAuthoredHelperKind.Helper, "weighted_helper");
        document = document with { SecondaryMotion = SecondaryDefinition("weighted_helper", "Child") };
        int helper = document.CreateEffectiveBones().Single(b => b.Name == "weighted_helper").Index;
        TransformMatrix[] oldGlobals = FbxRestPoseAuthoringTests.ExactGlobals(document);
        int surfaceIndex = Enumerable.Range(0, source.Surfaces.Length).Single(i => source.Surfaces[i].IsSkinned);
        FbxModelSurface surface = source.Surfaces[surfaceIndex];
        int helperSlot = surface.PaletteBoneIndices.Length;
        FbxModelSurface weighted = surface with
        {
            PaletteBoneIndices = surface.PaletteBoneIndices.Add(helper),
            InverseBindMatrices = surface.InverseBindMatrices.Add(oldGlobals[helper].InvertedAffine()),
            Vertices = surface.Vertices.SetItem(0, surface.Vertices[0] with
            {
                BoneIndices = [helperSlot],
                BoneWeights = [1],
            }),
        };
        source = source with
        {
            Package = source.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
            Surfaces = source.Surfaces.SetItem(surfaceIndex, weighted),
        };
        string secondaryBefore = SecondaryMotionSetupSerializer.Serialize(document.SecondaryMotion);
        string nativeBefore = document.SecondaryMotion.NativeSources[0].Text;
        Vector3D worldBefore = (oldGlobals[helper] * weighted.InverseBindMatrices[helperSlot])
            .TransformPoint(weighted.Vertices[0].Position);
        CustomModelAuthoredHelper helperBefore = document.AuthoredHelpers.Single(h => h.Name == "weighted_helper");

        FbxHierarchyPreview preview = FbxHierarchyAuthoring.Preview(source,
            FbxHierarchyAuthoringTests.Entity(source, "Child"),
            FbxHierarchyAuthoringTests.Entity(source, "aux_eye"));
        Assert.True(preview.HasChanges);
        Assert.True(FbxHierarchyAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult result));
        CustomModelDocument actual = result.Package.Document;
        actual.Validate();
        Assert.Equal(secondaryBefore, SecondaryMotionSetupSerializer.Serialize(actual.SecondaryMotion));
        Assert.Equal(nativeBefore, actual.SecondaryMotion.NativeSources[0].Text);
        CustomModelAuthoredHelper helperAfter = actual.AuthoredHelpers.Single(h => h.Id == helperBefore.Id);
        Assert.Equal(helperBefore.Name, helperAfter.Name);
        Assert.Equal(helperBefore.LocalTransform, helperAfter.LocalTransform);
        Assert.Equal(helperBefore.ExactLocalMatrix, helperAfter.ExactLocalMatrix);

        ImmutableArray<CustomModelBone> effective = actual.CreateEffectiveBones();
        Assert.Contains(effective, b => b.Name == "Child");
        Assert.Contains(effective, b => b.Name == "aux_eye");
        int actualHelper = effective.Single(b => b.Name == "weighted_helper").Index;
        FbxModelSurface actualSurface = result.Surfaces[surfaceIndex];
        int actualSlot = actualSurface.Vertices[0].BoneIndices.Single();
        Assert.Equal(actualHelper, actualSurface.PaletteBoneIndices[actualSlot]);
        Assert.Equal<double>([1d], actualSurface.Vertices[0].BoneWeights);
        TransformMatrix[] newGlobals = FbxRestPoseAuthoringTests.ExactGlobals(actual);
        Vector3D worldAfter = (newGlobals[actualHelper] * actualSurface.InverseBindMatrices[actualSlot])
            .TransformPoint(actualSurface.Vertices[0].Position);
        AssertNear(worldBefore, worldAfter);
        Assert.True(result.Rig!.GetBoneIndex("weighted_helper") >= actual.Bones.Length);
        Assert.Contains(actual.Diagnostics, d => d.Code == FbxHierarchyAuthoring.ReviewDiagnosticCode);
        string path = Path.Combine(directory, "weighted-helper-hierarchy.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(result.Package, path);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(secondaryBefore, SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
        var reopenedSurface = reopened.Surfaces[surfaceIndex];
        for (int i = 0; i < actualSurface.Vertices.Length; i++)
        {
            var originalVertex = actualSurface.Vertices[i];
            var reopenedVertex = reopenedSurface.Vertices[i];
            Assert.Equal(originalVertex.BoneIndices.Select((slot, w) => (Bone: actualSurface.PaletteBoneIndices[slot], Weight: originalVertex.BoneWeights[w])).OrderBy(w => w.Bone),
                reopenedVertex.BoneIndices.Select((slot, w) => (Bone: reopenedSurface.PaletteBoneIndices[slot], Weight: reopenedVertex.BoneWeights[w])).OrderBy(w => w.Bone));
        }
        foreach (int index in reopenedSurface.PaletteBoneIndices)
            Assert.Equal(actualSurface.InverseBindMatrices[actualSurface.PaletteBoneIndices.IndexOf(index)],
                reopenedSurface.InverseBindMatrices[reopenedSurface.PaletteBoneIndices.IndexOf(index)]);
    }

    private static FbxModelAuthoringImportResult WithSecondary(
        FbxModelAuthoringImportResult source, string anchor, string driven)
    {
        CustomModelDocument document = source.Package.Document with
        {
            SecondaryMotion = SecondaryDefinition(anchor, driven),
        };
        return source with { Package = source.Package with { Document = document } };
    }

    internal static FbxModelAuthoringImportResult RestSource() => WithSecondary(FbxRestPoseAuthoringTests.Source(true), "body_head", "body_neck_0");

    private static SecondaryMotionDefinition SecondaryDefinition(string anchor, string driven) => new()
    {
        Groups =
        [
            new()
            {
                Name = "secondary",
                Particles =
                [
                    new() { ReferenceBoneName = anchor, DrivenBoneName = anchor, LocalPosition = new(.12, .03, -.02), Fixed = true },
                    new() { ReferenceBoneName = driven, DrivenBoneName = driven, LocalPosition = new(-.08, .11, .04) },
                ],
                Constraints = [new() { First = 0, Second = 1, Kind = SecondaryConstraintKind.Structural, RestLength = .25 }],
                Colliders = [new() { BoneName = anchor, LocalPosition = new(.03, .07, 0), EndBoneName = driven,
                    EndLocalPosition = new(-.02, .09, .01), Radius = .04 }],
            },
        ],
        NativeSources =
        [
            new() { Kind = NativeClothSourceKind.Phx, ResourceName = "secondary.phx", Text = NativeText.Replace("\"body_head\"", $"\"{anchor}\"", StringComparison.Ordinal) },
            new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "secondary.mpcloth",
                Text = "// authored binding\nMeshPartCloth(\"secondary.phx\", 0, 1)\n" },
        ],
    };

    private static void AssertSecondaryPoint(TransformMatrix oldGlobal, Vector3D oldLocal,
        TransformMatrix newGlobal, Vector3D actualLocal, bool preserveWorld)
    {
        Vector3D oldWorld = oldGlobal.TransformPoint(oldLocal);
        Vector3D actualWorld = newGlobal.TransformPoint(actualLocal);
        if (preserveWorld) AssertNear(oldWorld, actualWorld);
    }

    private static void AssertNear(Vector3D expected, Vector3D actual, double tolerance = 1e-8)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(directory);
}
