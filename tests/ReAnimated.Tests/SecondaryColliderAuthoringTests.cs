using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class SecondaryColliderAuthoringTests
{
    [Fact]
    public void NativeRadiusEditPreservesCommentsOtherCallsAndUndo()
    {
        const string text = "MeshPartCloth()\n{ BonesGridSize(1, 1)\nBone(0, 0, \"root\", 1, 0, 0)\n" +
            "GravityMul(0.5)\nUnknownSetting(42)\nCollisionCapsuleBetween(\"root\", 0, \"root\", 0, /* radius */ 0.2 /* keep */)\n}\n";
        var definition = new SecondaryMotionDefinition { NativeSources =
        [new NativeClothSource { Kind = NativeClothSourceKind.Phx, ResourceName = "cloth.phx", Text = text }] };
        var model = new SecondaryMotionViewModel();
        model.Load(definition);
        model.SelectedNativeCollision = Assert.Single(model.NativeCollisionChoices);
        model.NativeCollisionRadiusText = "0.4";
        model.ApplyNativeCollisionCommand.Execute(null);
        Assert.Equal(text.Replace("0.2 /* keep */", "0.4 /* keep */", StringComparison.Ordinal),
            model.Definition.NativeSources[0].Text);
        Assert.Equal(text, model.Definition.NativeSources[0].OriginalText);
        Assert.Equal(0.5, Dl1ClothCodec.ReadPhx(model.Definition.NativeSources[0].Text).Parameters["GravityMul"][0]);
        model.UndoNativeCollisionCommand.Execute(null);
        Assert.Equal(definition.NativeSources, model.Definition.NativeSources);
    }

    [Fact]
    public void NativeRadiusEditRejectsAStaleSelectionAndInvalidRadius()
    {
        string text = Dl1ClothCodec.WritePhx(1, 1, [new(0, 0, "root", 1, 0, 0)],
            ["CollisionCapsuleBetween(\"root\", 0, \"root\", 0, 0.2)"]);
        var source = new NativeClothSource { Kind = NativeClothSourceKind.Phx, ResourceName = "cloth.phx", Text = text };
        var definition = new SecondaryMotionDefinition { NativeSources = [source] };
        var choice = Assert.Single(Dl1NativeCollisionAuthoring.Inspect(definition));
        Assert.Throws<InvalidOperationException>(() => Dl1NativeCollisionAuthoring.SetRadius(
            definition with { NativeSources = [source with { Text = text + "// changed" }] }, choice, 0.4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dl1NativeCollisionAuthoring.SetRadius(definition, choice, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dl1NativeCollisionAuthoring.SetRadius(definition, choice, double.NaN));
        var renamed = definition with { NativeSources = [source with { ResourceName = "other.phx" }] };
        Assert.Throws<InvalidOperationException>(() => Dl1NativeCollisionAuthoring.SetRadius(renamed, choice, 0.4));
    }
    [Fact]
    public void FitSphereUsesWeightedGeometryInBoneLocalSpace()
    {
        FbxModelAuthoringImportResult model = CreateModel();
        model = WithTrianglePositions(model, new(0, 0, 0), new(0, 2, 0), new(0, 100, 100));
        string boneName = model.Package.Document.Bones.Single(bone => bone.Name == "Root").Name;

        SecondaryCollider fit = SecondaryColliderAuthoring.FitSphere(model, boneName);

        Assert.Equal(boneName, fit.BoneName);
        Assert.Null(fit.EndBoneName);
        Assert.True(fit.LocalPosition.IsFinite);
        Assert.InRange(fit.LocalPosition.Y, 0.9, 1.1);
        Assert.InRange(fit.Radius, 0.9, 1.1);
    }

    [Fact]
    public void FitCapsuleUsesTheSelectedBoneChainAndEnclosesItsWeightedSupport()
    {
        FbxModelAuthoringImportResult model = CreateModel();
        model = WithTrianglePositions(model, new(0, 0, 0), new(0, 2, 0), new(0, 0, 2));
        model = WithChildBoneTranslation(model, Vector3D.UnitY);
        CustomModelDocument document = model.Package.Document;
        CustomModelBone start = document.Bones.Single(bone => bone.Name == "Root");
        CustomModelBone end = document.Bones.Single(bone => bone.Name == "Child");

        SecondaryCollider fit = SecondaryColliderAuthoring.FitCapsule(model, start.Name, end.Name);

        Assert.Equal(start.Name, fit.BoneName);
        Assert.Equal(end.Name, fit.EndBoneName);
        Assert.Equal(Vector3D.Zero, fit.LocalPosition);
        Assert.Equal(Vector3D.Zero, fit.EndLocalPosition);
        Assert.True(double.IsFinite(fit.Radius) && fit.Radius > 0);
    }

    [Fact]
    public void ApplyingCollisionProposalIsUndoableAndUsesTheExistingSetupContract()
    {
        var viewModel = new SecondaryMotionViewModel();
        viewModel.Load(Definition());
        var collider = new SecondaryCollider
        {
            BoneName = "root",
            LocalPosition = new(.1, .2, .3),
            EndBoneName = "child",
            EndLocalPosition = new(.4, .5, .6),
            Radius = .25,
        };

        viewModel.ApplyCollisionProposal(collider);

        Assert.Equal(collider, Assert.Single(viewModel.Definition.Groups[0].Colliders));
        Assert.True(viewModel.UndoCollisionEditCommand.CanExecute(null));
        viewModel.UndoCollisionEditCommand.Execute(null);
        Assert.Empty(viewModel.Definition.Groups[0].Colliders);

        viewModel.ApplyCollisionProposal(collider);
        SecondaryMotionDefinition reopened = SecondaryMotionSetupSerializer.Deserialize(
            SecondaryMotionSetupSerializer.Serialize(viewModel.Definition), ["root", "child"]);
        Assert.Equal(collider, Assert.Single(reopened.Groups[0].Colliders));
    }

    [Fact]
    public void UndoRestoresTheEditedGroupBeforeAnotherCollisionCanBeApplied()
    {
        SecondaryCollider firstGroupCollider = new()
        {
            BoneName = "root",
            LocalPosition = Vector3D.Zero,
            Radius = .2,
        };
        SecondaryCollider secondGroupCollider = new()
        {
            BoneName = "child",
            LocalPosition = Vector3D.UnitY,
            Radius = .3,
        };
        SecondaryMotionDefinition definition = Definition() with
        {
            Groups =
            [
                Definition().Groups[0] with { Name = "first", Colliders = [firstGroupCollider] },
                Definition().Groups[0] with { Name = "second", Colliders = [secondGroupCollider] },
            ],
        };
        var viewModel = new SecondaryMotionViewModel();
        viewModel.Load(definition);
        viewModel.SelectedColliderIndex = 0;
        viewModel.ApplyCollisionProposal(firstGroupCollider with { Radius = .4 });

        viewModel.SelectedGroup = "second";
        viewModel.UndoCollisionEditCommand.Execute(null);

        Assert.Equal("first", viewModel.SelectedGroup);
        Assert.Equal(firstGroupCollider, Assert.Single(viewModel.Definition.Groups.Single(group => group.Name == "first").Colliders));
        Assert.Equal(secondGroupCollider, Assert.Single(viewModel.Definition.Groups.Single(group => group.Name == "second").Colliders));

        SecondaryCollider restoredProposal = Assert.IsType<SecondaryCollider>(viewModel.CurrentColliderProposal);
        viewModel.ApplyCollisionProposal(restoredProposal with { Radius = .5 });

        Assert.Equal(secondGroupCollider, Assert.Single(viewModel.Definition.Groups.Single(group => group.Name == "second").Colliders));
    }

    [Fact]
    public void NewCollisionProposalAddsAnotherGroupShapeAndUndoRemovesOnlyThatShape()
    {
        var viewModel = new SecondaryMotionViewModel();
        viewModel.Load(Definition());
        viewModel.SetCollisionBoneNames(["root", "child"]);
        viewModel.ApplyCollisionProposal(new() { BoneName = "root", LocalPosition = Vector3D.Zero, Radius = .2 });

        viewModel.NewCollisionProposalCommand.Execute(null);
        Assert.Null(viewModel.SelectedColliderIndex);
        Assert.Single(viewModel.Definition.Groups[0].Colliders);
        Assert.Equal("Sphere", viewModel.CollisionShape);
        Assert.Equal("root", viewModel.StartBoneName);
        Assert.Equal("0.05", viewModel.CollisionRadiusText);
        SecondaryCollider draft = Assert.IsType<SecondaryCollider>(viewModel.CurrentColliderProposal);
        draft = draft with { LocalPosition = new(.1, 0, 0), Radius = .3 };
        viewModel.ApplyCollisionProposal(draft);

        Assert.Equal(2, viewModel.Definition.Groups[0].Colliders.Length);
        viewModel.UndoCollisionEditCommand.Execute(null);
        Assert.Equal(new SecondaryCollider { BoneName = "root", LocalPosition = Vector3D.Zero, Radius = .2 },
            Assert.Single(viewModel.Definition.Groups[0].Colliders));
    }

    [Fact]
    public void NativeCapsuleOverlayResolvesPivotAndBoundsEndpointsWithoutEditingSource()
    {
        const string capsule = "CollisionCapsuleBetween(\"root\", 0, \"child\", 1, 0.25)";
        string phx = Dl1ClothCodec.WritePhx(1, 1,
            [new(0, 0, "root", 1, 0, 0)], [capsule, "FutureNativeCall(7)"]);
        string wrapper = Dl1ClothCodec.WriteMpCloth([new("generic.phx", 1, 0)]);
        SecondaryMotionDefinition definition = Definition() with
        {
            Groups = [],
            NativeSources = [
                new() { Kind = NativeClothSourceKind.Phx, ResourceName = "generic.phx", Text = phx },
                new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "generic.mpcloth", Text = wrapper },
            ],
        };
        ImmutableArray<CustomModelBone> bones = [
            new() { Index = 0, Name = "root", ParentIndex = -1, LocalBounds = new(Vector3D.Zero, new(.2, .3, .4)) },
            new() { Index = 1, Name = "child", ParentIndex = 0, LocalBindTransform = new(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One),
                ExactLocalBindMatrix = TransformMatrix.CreateTranslation(Vector3D.UnitX), LocalBounds = new(new(0, .5, 0), new(.1, .2, .3)) },
        ];

        Dl1NativeClothCollisionOverlayResolution result = Dl1NativeClothCollisionOverlayResolver.Resolve(definition, bones);

        var overlay = Assert.Single(result.Overlays);
        Assert.Equal("generic.phx", overlay.ResourceName);
        Assert.Equal("root", overlay.BoneName);
        Assert.Equal(Vector3D.Zero, overlay.LocalPosition);
        Assert.Equal("child", overlay.EndBoneName);
        Assert.Equal(new Vector3D(0, .5, 0), overlay.EndLocalPosition);
        Assert.Equal(.25, overlay.Radius);
        Assert.Equal(Dl1NativeClothRadiusScale.ModelRoot, overlay.RadiusScale);
        Assert.Equal(phx, definition.NativeSources[0].Text);
        Assert.Contains(result.Diagnostics, detail => detail.Contains("FutureNativeCall", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeOverlayRequiresResolvedEndpointBounds()
    {
        string phx = Dl1ClothCodec.WritePhx(1, 1,
            [new(0, 0, "root", 1, 0, 0)], ["CollisionCapsuleBetween(\"root\", 1, \"child\", 0, 0.2)"]);
        SecondaryMotionDefinition definition = Definition() with
        {
            Groups = [],
            NativeSources = [
                new() { Kind = NativeClothSourceKind.Phx, ResourceName = "generic.phx", Text = phx },
                new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "generic.mpcloth", Text = Dl1ClothCodec.WriteMpCloth([new("generic.phx", 1, 0)]) },
            ],
        };
        ImmutableArray<CustomModelBone> bones = [
            new() { Index = 0, Name = "root", ParentIndex = -1 },
            new() { Index = 1, Name = "child", ParentIndex = 0 },
        ];

        Dl1NativeClothCollisionOverlayResolution result = Dl1NativeClothCollisionOverlayResolver.Resolve(definition, bones);

        Assert.Empty(result.Overlays);
        Assert.Contains(result.Diagnostics, detail => detail.Contains("bounds", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(phx, definition.NativeSources[0].Text);
    }

    [Fact]
    public void NativeDisabledBindingDoesNotCreateCollisionOverlay()
    {
        string phx = Dl1ClothCodec.WritePhx(1, 1,
            [new(0, 0, "root", 1, 0, 0)], ["CollisionCapsuleBetween(\"root\", 0, \"child\", 0, 0.2)"]);
        SecondaryMotionDefinition definition = Definition() with
        {
            Groups = [],
            NativeSources = [
                new() { Kind = NativeClothSourceKind.Phx, ResourceName = "generic.phx", Text = phx },
                new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "generic.mpcloth", Text = Dl1ClothCodec.WriteMpCloth([new("generic.phx", 0, 0)]) },
            ],
        };
        ImmutableArray<CustomModelBone> bones = [
            new() { Index = 0, Name = "root", ParentIndex = -1 },
            new() { Index = 1, Name = "child", ParentIndex = 0 },
        ];

        Dl1NativeClothCollisionOverlayResolution result = Dl1NativeClothCollisionOverlayResolver.Resolve(definition, bones);

        Assert.Empty(result.Overlays);
        Assert.Contains(result.Diagnostics, detail => detail.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(phx, definition.NativeSources[0].Text);
    }

    [Fact]
    public void NativeSphereOverlayUsesBoundCenterShiftAndRootScaledRadius()
    {
        string phx = Dl1ClothCodec.WritePhx(1, 1,
            [new(0, 0, "root", 1, 0, 0)], ["CollisionSphereShift(\"root\", [0.1, 0, 0], -0.5)"]);
        SecondaryMotionDefinition definition = Definition() with
        {
            NativeSources = [
                new() { Kind = NativeClothSourceKind.Phx, ResourceName = "generic.phx", Text = phx },
                new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "generic.mpcloth", Text = Dl1ClothCodec.WriteMpCloth([new("generic.phx", 1, 0)]) },
            ],
        };
        ImmutableArray<CustomModelBone> bones = [
            new() { Index = 0, Name = "root", ParentIndex = -1, LocalBounds = new(new(0, .5, 0), new(.1, .2, .3)) },
            new() { Index = 1, Name = "child", ParentIndex = 0 },
        ];

        Dl1NativeClothCollisionOverlayResolution result = Dl1NativeClothCollisionOverlayResolver.Resolve(definition, bones);

        var overlay = Assert.Single(result.Overlays);
        Assert.Equal("CollisionSphereShift", overlay.CommandName);
        Assert.Equal("root", overlay.BoneName);
        Assert.Equal(new Vector3D(.1, .5, 0), overlay.LocalPosition);
        Assert.Equal(.15, overlay.Radius, 10);
        Assert.Equal(Dl1NativeClothRadiusScale.ModelRoot, overlay.RadiusScale);
    }

    [Fact]
    public void PreviewActorScaleDefaultsToOneAndSurvivesBothSetupAndModelReopen()
    {
        Assert.Equal(1, new SecondaryMotionDefinition().PreviewActorScale);
        FbxModelAuthoringImportResult model = CreateModel();
        CustomModelDocument document = model.Package.Document;
        SecondaryMotionDefinition secondary = Definition() with
        {
            PreviewActorScale = 1.75,
            Groups = [new()
            {
                Name = "fabric",
                Particles = [new() { ReferenceBoneName = document.Bones[0].Name, Fixed = true },
                    new() { ReferenceBoneName = document.Bones[1].Name }],
                Constraints = [new() { First = 0, Second = 1 }],
            }],
        };
        CustomModelPackage package = model.Package with
        {
            Document = document with { SecondaryMotion = secondary },
        };

        SecondaryMotionDefinition setup = SecondaryMotionSetupSerializer.Deserialize(
            SecondaryMotionSetupSerializer.Serialize(secondary), document.Bones.Select(bone => bone.Name));
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "scaled-model.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, path);
            CustomModelPackage reopened = CustomModelPackageSerializer.Load(path);
            Assert.Equal(1.75, setup.PreviewActorScale);
            Assert.Equal(1.75, reopened.Document.SecondaryMotion.PreviewActorScale);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void PreviewActorScaleEditUpdatesTheModelOwnedPreviewDefinition()
    {
        var viewModel = new SecondaryMotionViewModel();
        viewModel.Load(Definition());
        int changes = 0;
        viewModel.Changed += (_, _) => changes++;

        viewModel.PreviewActorScale = 2.0;

        Assert.Equal(2.0, viewModel.Definition.PreviewActorScale);
        Assert.Equal(1, changes);
        viewModel.PreviewActorScale = 5.0;
        Assert.Equal(2.0, viewModel.Definition.PreviewActorScale);
        Assert.Contains("0.1 and 4", viewModel.Status, StringComparison.Ordinal);
    }

    private static FbxModelAuthoringImportResult CreateModel() => FbxModelAuthoringImporter.Import(
        BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generated-collision-model.fbx");

    private static FbxModelAuthoringImportResult WithTrianglePositions(
        FbxModelAuthoringImportResult model, Vector3D first, Vector3D second, Vector3D third)
    {
        FbxModelSurface surface = Assert.Single(model.Surfaces.Where(candidate => candidate.IsSkinned));
        Vector3D[] positions = [first, second, third];
        FbxModelSurface updated = surface with
        {
            Vertices = surface.Vertices.Select((vertex, index) => vertex with { Position = positions[index % positions.Length] }).ToImmutableArray(),
        };
        int surfaceIndex = model.Surfaces.IndexOf(surface);
        return model with { Surfaces = model.Surfaces.SetItem(surfaceIndex, updated) };
    }

    private static FbxModelAuthoringImportResult WithChildBoneTranslation(
        FbxModelAuthoringImportResult model, Vector3D translation)
    {
        CustomModelDocument source = model.Package.Document;
        int childIndex = Array.FindIndex(source.Bones.ToArray(), bone => bone.Name == "Child");
        CustomModelBone child = source.Bones[childIndex];
        TransformTRS local = child.LocalBindTransform with
        {
            Translation = child.LocalBindTransform.Translation + translation,
        };
        CustomModelDocument updated = source with
        {
            Bones = source.Bones.SetItem(childIndex, child with
            {
                LocalBindTransform = local,
                ExactLocalBindMatrix = local.ToMatrix(),
            }),
        };
        return model with
        {
            Package = model.Package with { Document = updated },
            Rig = updated.CreateRigDefinition(),
        };
    }

    private static SecondaryMotionDefinition Definition() => new()
    {
        Groups = [new()
        {
            Name = "fabric",
            Particles = [new() { ReferenceBoneName = "root", Fixed = true }, new() { ReferenceBoneName = "child" }],
            Constraints = [new() { First = 0, Second = 1 }],
        }],
    };
}
