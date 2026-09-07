using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionPresentationFrameTests
{
    [Fact]
    public void SourceLocalOffsetsAndPhysicsSurviveChromeAxesAndPhysicalReordering()
    {
        var model = Model();
        RigDefinition rig = model.Rig!;
        var sourceBind = rig.CreateBindPose();
        var sourcePreview = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        var chrome = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.Dl1Output);
        var chromeBind = chrome.CreatePresentationPose(sourceBind);
        Assert.Equal(CustomModelPreviewMode.Dl1Output, chrome.EffectiveMode);
        int sourceIndex = rig.GetBoneIndex("tip"), physicalIndex = chrome.GetPresentationBoneIndex(sourceIndex);
        Assert.NotEqual(sourceIndex, physicalIndex);
        Assert.Equal("tip", chromeBind.Rig.Bones[physicalIndex].Name);
        Vector3D localTip = new(0.2, 0.1, 0.3);
        Assert.True(Vector3D.Distance(sourceBind.GlobalMatrices[sourceIndex].TransformPoint(localTip),
            chromeBind.GlobalMatrices[physicalIndex].TransformPoint(localTip)) > 0.1);
        var definition = new SecondaryMotionDefinition { Groups = [new()
        {
            Name = "strand",
            Particles = [new() { ReferenceBoneName = "anchor", Fixed = true },
                new() { ReferenceBoneName = "tip", DrivenBoneName = "tip", AimParticleIndex = 2 },
                new() { ReferenceBoneName = "tip", LocalPosition = localTip }],
            Constraints = [new() { First = 0, Second = 1 }, new() { First = 1, Second = 2 },
                new() { First = 0, Second = 2, Kind = SecondaryConstraintKind.Bend }],
            Preview = new() { RestShapeStiffness = 100, Damping = 18 },
        }] };
        var physics = new SecondaryMotionSession(definition, rig.Bones.Select(b => b.Name));
        var result = physics.Sample(0.4, _ => new(sourceBind.GlobalMatrices, TransformMatrix.Identity));
        var delta = sourceBind.GlobalMatrices[sourceIndex].InvertedAffine() * result.Globals[sourceIndex];
        var converted = SecondaryMotionRenderAdapter.RebaseBoneLocalDelta(delta,
            sourceBind.GlobalMatrices[sourceIndex], chromeBind.GlobalMatrices[physicalIndex]);
        var rendered = SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(chrome.CreateSkeleton(sourceBind),
            new Dictionary<string, TransformMatrix>(StringComparer.Ordinal) { ["tip"] = converted });
        var sourceLocals = rig.Bones.Select((b, i) => (b.ParentIndex < 0 ? result.Globals[i]
            : result.Globals[b.ParentIndex].InvertedAffine() * result.Globals[i]).Decompose(1e-5));
        var sourceSimulated = new SkeletonPose(rig, sourceLocals);
        var sourceRendered = sourcePreview.CreateSkeleton(sourceSimulated);
        var sourceVertices = CpuMeshDeformationEvaluator.Evaluate(sourcePreview.Meshes[0], sourceRendered, []);
        var chromeVertices = CpuMeshDeformationEvaluator.Evaluate(chrome.Meshes[0], rendered, []);
        for (int i = 0; i < sourceVertices.Length; i++)
            Assert.InRange(System.Numerics.Vector3.Distance(sourceVertices[i].Position, chromeVertices[i].Position), 0, 2e-5f);
        foreach (var bone in rendered.Bones.Where(b => b.Name != "tip"))
            Assert.Equal(chrome.CreateSkeleton(sourceBind).Bones.Single(b => b.Name == bone.Name), bone);
    }

    private static FbxModelAuthoringImportResult Model()
    {
        var original = CustomModelPreviewSessionTests.CreateModel(true);
        var tipTransform = new TransformTRS(new(0, 0.6, 0), QuaternionD.FromAxisAngle(Vector3D.UnitX, Math.PI / 2), Vector3D.One);
        var document = original.Package.Document with { Bones = [
            original.Package.Document.Bones[0],
            new() { Index = 1, FbxObjectId = 3, Name = "anchor", ParentIndex = 0, Kind = BoneKind.Helper,
                LocalBindTransform = new(new(0, 0.4, 0), QuaternionD.Identity, Vector3D.One), ExactLocalBindMatrix = TransformMatrix.CreateTranslation(new(0, 0.4, 0)) },
            new() { Index = 2, FbxObjectId = 4, Name = "sibling", ParentIndex = 0, Kind = BoneKind.Helper,
                LocalBindTransform = new(new(2, 0, 0), QuaternionD.Identity, Vector3D.One), ExactLocalBindMatrix = TransformMatrix.CreateTranslation(new(2, 0, 0)) },
            original.Package.Document.Bones[1] with { Index = 3, ParentIndex = 1, LocalBindTransform = tipTransform, ExactLocalBindMatrix = tipTransform.ToMatrix() },
        ] };
        var rig = document.CreateRigDefinition();
        var binds = rig.CreateBindPose().GlobalMatrices;
        var surface = original.Surfaces[0] with
        {
            PaletteBoneIndices = [0, 3],
            InverseBindMatrices = [binds[0].InvertedAffine(), binds[3].InvertedAffine()],
            Vertices = original.Surfaces[0].Vertices.Select((v, i) => v with { BoneIndices = [i == 0 ? 0 : 1] }).ToImmutableArray(),
        };
        return original with { Package = new(document, original.Package.SourceFbx, original.Package.TexturePayloads), Rig = rig, Surfaces = [surface] };
    }
}
