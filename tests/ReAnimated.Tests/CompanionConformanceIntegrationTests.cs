using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class CompanionConformanceIntegrationTests
{
    internal static (FbxModelAuthoringImportResult Model, RigConformanceResult Fit) Fixture()
    {
        var model = StructuralHelperAuthoringTests.Source();
        var doc = model.Package.Document;
        var bones = doc.CreateEffectiveBones();
        string anchor = bones[0].Name, tip = bones[^1].Name;
        string phx = Dl1ClothCodec.WritePhx(1, 1,
            [new(0, 0, tip, 1, -1, -1)],
            [$"CollisionCapsuleBetween(\"{anchor}\", 0, \"{tip}\", 0, 0.04)"]);
        doc = doc with { SecondaryMotion = new()
        {
            NativeSources = [new() { Kind = NativeClothSourceKind.Phx, ResourceName = "panel.phx", Text = phx },
                new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "panel.mpcloth",
                    Text = "MeshPartCloth( /* panel.phx */ \"panel.phx\",\t1, /* flag */ 0)\r\n" }],
            Groups = [new() { Name = "panel",
                Particles = [new() { ReferenceBoneName = anchor, Fixed = true, LocalPosition = new(.1, .2, 0) },
                    new() { ReferenceBoneName = tip, DrivenBoneName = tip, LocalPosition = new(0, .2, .3) }],
                Constraints = [new() { First = 0, Second = 1 }] }],
        } };
        model = model with { Package = model.Package with { Document = doc } };
        var globals = model.Rig!.CreateBindPose().GlobalMatrices;
        var shift = TransformMatrix.CreateTranslation(new(.2, .1, 0));
        var rows = bones.Select(b => new RigConformedBone
        {
            Index = b.Index, Name = b.Index < doc.Bones.Length ? "target_" + b.Name : b.Name,
            ParentIndex = b.ParentIndex, Kind = b.Kind, IsDeform = b.IsWeighted,
            Disposition = RigBoneDisposition.Extra, SourceBoneIndex = b.Index, TemplateIndex = -1,
            Position = (shift * globals[b.Index]).Translation, Orientation = globals[b.Index],
            OffsetFromSourceJoint = .2, SegmentRatio = 1,
        });
        var fit = new RigConformanceResult(CameraTemplateCreationTests.Template(), new()
            { UniformScale = 1, PelvisAnchor = Vector3D.Zero, Samples = [], RegionFits = [], ProportionResidual = 0,
                WorstSampleDeviation = 0, Evidence = "Synthetic translated control" }, 0, rows, [], new()
            { PosedGlobals = globals.Select(g => shift * g).ToImmutableArray(),
                SkinningTransforms = bones.Select(_ => shift).ToImmutableArray(), MaximumJointResidual = 0 });
        return (model, fit);
    }

    [Fact]
    public void RealFbxLayerReopenKeepsMigratedCompanionsAndOriginalSource()
    {
        var (model, fit) = Fixture();
        var original = model.Package.Document.SecondaryMotion;
        var applied = FbxAuthoredModelLayer.Capture(Dl1RigConformanceApplier.Apply(model, fit));
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "conformed.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(applied.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal<byte>(model.Package.SourceFbx, reopened.Package.SourceFbx);
            Assert.Equal(SecondaryMotionSetupSerializer.Serialize(applied.Package.Document.SecondaryMotion),
                SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
            Assert.Equal(original.NativeSources[0].Text, reopened.Package.Document.SecondaryMotion.NativeSources[0].OriginalText);
            Assert.Equal("target_Root", reopened.Package.Document.SecondaryMotion.Groups[0].Particles[0].ReferenceBoneName);
            var prepared = Dl1CustomModelRigPreparer.Prepare(reopened);
            string gridRootName = Assert.Single(Dl1ClothCodec.ReadPhx(
                reopened.Package.Document.SecondaryMotion.NativeSources[0].Text).Nodes).BoneName;
            Guid gridRootId = Guid.NewGuid();
            CustomModelDocument exportDocument = reopened.Package.Document with
            {
                RiggingSession = new RiggingSession
                {
                    OwnerModelId = reopened.Package.Document.ModelId,
                    Recipe = new RuntimeRigRecipe
                    {
                        ProfileSnapshot = new RigCapabilityProfile
                        {
                            Roles = [new RigRuntimeRole
                            {
                                Id = "cloth.root", Category = RigRoleCategory.Structural, EntityKind = RigNativeEntityKind.Helper,
                            }],
                        },
                        Entities = [new RigEntityBinding { EntityId = gridRootId, OwnerAssetId = reopened.Package.Document.ModelId,
                            NativeName = gridRootName, Kind = RigNativeEntityKind.Helper, Imported = false }],
                        Assignments = [new("cloth.root", gridRootId)],
                    },
                },
            };
            var output = Dl1NativeCompanionWriter.Build(exportDocument, "control", prepared.Contract.Nodes.Select(n => n.Name));
            Assert.Equal(reopened.Package.Document.SecondaryMotion.NativeSources[0].Text, Encoding.UTF8.GetString(output.Files["control_000.phx"]));
            Assert.Equal("MeshPartCloth( /* panel.phx */ \"control_000.phx\",\t1, /* flag */ 0)\r\n",
                Encoding.UTF8.GetString(output.Files["control.mpcloth"]));
            Assert.Null(model.Package.Document.SecondaryMotion.NativeSources[0].OriginalText);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void PreparedParentHelperRebaseUsesOriginalOffsetsOnlyOnce()
    {
        var (model, fit) = Fixture();
        var doc = model.Package.Document;
        doc = doc with { RiggingSession = RiggingSessions.Create(doc, RigStudioEntryPath.AdaptExistingRig) };
        var parentId = RiggingSessions.ObserveSourceHierarchy(doc)[0].EntityId;
        var frame = model.Rig!.CreateBindPose().GlobalMatrices[0] * TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, .5));
        doc = doc with { RiggingSession = doc.RiggingSession with { Recipe = doc.RiggingSession!.Recipe with
            { FramePolicies = [new() { EntityId = parentId, FramePolicy = RigFramePolicy.Manual, SolvedGlobalFrame = frame }] } } };
        model = model with { Package = model.Package with { Document = doc } };
        var result = Dl1RigConformanceApplier.ApplyDetailed(model, fit);
        var before = model.Rig.CreateBindPose().GlobalMatrices;
        var after = result.Model.Rig!.CreateBindPose().GlobalMatrices;
        for (int i = 0; i < doc.SecondaryMotion.Groups[0].Particles.Length; i++)
        {
            var old = doc.SecondaryMotion.Groups[0].Particles[i];
            var next = result.Model.Package.Document.SecondaryMotion.Groups[0].Particles[i];
            int index = model.Rig.GetBoneIndex(old.ReferenceBoneName);
            var expected = fit.RestPoseTransfer.SkinningTransforms[index].TransformPoint(before[index].TransformPoint(old.LocalPosition));
            var actual = after[result.SourceToEffective[index]].TransformPoint(next.LocalPosition);
            Assert.InRange(Vector3D.Distance(expected, actual), 0, 1e-6);
        }
        Assert.Equal(doc.SecondaryMotion.NativeSources[0].Text, result.Model.Package.Document.SecondaryMotion.NativeSources[0].OriginalText);
    }
}
