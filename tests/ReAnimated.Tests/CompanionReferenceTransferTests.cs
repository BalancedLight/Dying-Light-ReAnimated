using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CompanionReferenceTransferTests
{
    private const string PhxText = "!include(\"MeshPartCloth.def\")\n" +
        "MeshPartCloth()\n{\n" +
        "  BonesGridSize(1, 2)\n" +
        "  Bone(0, 0, \"anchor\", 1, 0, 1)\n" +
        "  Bone(0, 1, \"\", 0, 0, 0)\n" +
        "  CollisionSphere(\"anchor\", 0.2)\n" +
        "  CollisionSphereShift(\"anchor\", [0.1, 0.2, 0.3], 0.3)\n" +
        "  CollisionCapsule(\"anchor\", 0.4, 0.5)\n" +
        "  CollisionCapsuleBetween(\"anchor\", 0, \"tip\", 0, 0.6)\n" +
        "  StructuralStiffness(0.31, 0.42)\n" +
        "  Mode3D(1) // retain this authored mode\n" +
        "  Opaque(\"fixed_literal\")\n" +
        "}\n";

    [Fact]
    public void RenamesPreviewAndPhxReferencesPreservesNativeTextLineageAndRoundTrips()
    {
        SecondaryMotionDefinition source = Definition(PhxText);
        var (before, after, transfers) = Skeleton("root", "anchor", "tip", "root_target", "anchor_target", "tip_target");
        NativePhxDocument original = Dl1ClothCodec.ReadPhx(PhxText, before.Select(b => b.Name));

        Dl1CompanionReferenceTransferResult result = Dl1CompanionReferenceTransfer.Apply(
            source, before, after, [0, 1, 2], transfers);

        SecondaryMotionGroup group = Assert.Single(result.Definition.Groups);
        Assert.Equal(["anchor_target", "tip_target"], group.Particles.Select(p => p.ReferenceBoneName));
        Assert.Equal(["anchor_target", "tip_target"], group.Particles.Select(p => p.DrivenBoneName));
        Assert.Equal("anchor_target", group.Colliders[0].BoneName);
        Assert.Equal("tip_target", group.Colliders[0].EndBoneName);
        Assert.Equal(3, result.PreviewBindings);
        Assert.Equal(6, result.NativeBindings);

        NativeClothSource native = Assert.Single(result.Definition.NativeSources, s => s.Kind == NativeClothSourceKind.Phx);
        string expectedText = PhxText.Replace("\"anchor\"", "\"anchor_target\"", StringComparison.Ordinal)
            .Replace("\"tip\"", "\"tip_target\"", StringComparison.Ordinal);
        Assert.Equal(expectedText, native.Text);
        Assert.Equal(PhxText, native.OriginalText);
        Assert.Contains("Bone(0, 1, \"\", 0, 0, 0)", native.Text, StringComparison.Ordinal);
        Assert.Contains("Opaque(\"fixed_literal\")", native.Text, StringComparison.Ordinal);

        NativePhxDocument rewritten = Dl1ClothCodec.ReadPhx(native.Text, after.Select(b => b.Name));
        Assert.Equal(original.Parameters.Keys.OrderBy(k => k), rewritten.Parameters.Keys.OrderBy(k => k));
        foreach (string key in original.Parameters.Keys)
            Assert.Equal(original.Parameters[key].ToArray(), rewritten.Parameters[key].ToArray());
        Assert.Equal(original.Nodes.Select(n => (n.X, n.Y, n.Type, n.RightDistance, n.DownDistance)),
            rewritten.Nodes.Select(n => (n.X, n.Y, n.Type, n.RightDistance, n.DownDistance)));
        Assert.Equal(source.NativeSources[1], result.Definition.NativeSources[1]);

        string serialized = SecondaryMotionSetupSerializer.Serialize(result.Definition);
        SecondaryMotionDefinition reopened = SecondaryMotionSetupSerializer.Deserialize(serialized, after.Select(b => b.Name));
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(result.Definition),
            SecondaryMotionSetupSerializer.Serialize(reopened));
        Assert.Equal(PhxText, reopened.NativeSources[0].OriginalText);

        var (_, final, identityTransfers) = Skeleton("root_target", "anchor_target", "tip_target",
            "root_final", "anchor_final", "tip_final");
        Dl1CompanionReferenceTransferResult second = Dl1CompanionReferenceTransfer.Apply(
            result.Definition, after, final, [0, 1, 2], identityTransfers);
        Assert.Equal(PhxText, second.Definition.NativeSources[0].OriginalText);
        Assert.Contains("\"anchor_final\"", second.Definition.NativeSources[0].Text, StringComparison.Ordinal);
        Assert.Contains("\"tip_final\"", second.Definition.NativeSources[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TransportsPreviewOffsetsThroughOldAndNewGlobalFrames()
    {
        SecondaryMotionDefinition source = Definition();
        var (before, after, transfers) = Skeleton("root", "anchor", "tip", "root_target", "anchor_target", "tip_target");

        Dl1CompanionReferenceTransferResult result = Dl1CompanionReferenceTransfer.Apply(
            source, before, after, [0, 1, 2], transfers);
        SecondaryMotionGroup group = Assert.Single(result.Definition.Groups);

        AssertNear(new(-5.5, -1, 0), group.Particles[0].LocalPosition);
        AssertNear(new(-5.2, -4.7, 0), group.Particles[1].LocalPosition);
        AssertNear(new(-5.9, -0.8, 0), group.Colliders[0].LocalPosition);
        AssertNear(new(-4.6, -5.1, 0), group.Colliders[0].EndLocalPosition);
        Assert.Equal(17.25, group.Preview.Damping);
        Assert.Equal(.31, group.Preview.StructuralStiffness);
        Assert.Equal(.47, group.Preview.ShearStiffness);
        Assert.Equal(.23, group.Preview.BendStiffness);
        Assert.Equal(.81, group.Preview.AnimationFollow);
        Assert.Equal(.14, group.Preview.CollisionFriction);
        Assert.Equal(source.Groups[0].Constraints, group.Constraints);
        Assert.Equal(source.Groups[0].Colliders[0].Radius, group.Colliders[0].Radius);
        Assert.True(group.Particles[0].Fixed);
        Assert.False(group.Particles[1].Fixed);
    }

    [Fact]
    public void DroppedPreviewOrNativeReferenceFailsBeforePublishingAResult()
    {
        SecondaryMotionDefinition source = Definition(PhxText);
        var (before, after, transfers) = Skeleton("root", "anchor", "tip", "root_target", "anchor_target", null);

        InvalidDataException previewFailure = Assert.Throws<InvalidDataException>(() =>
            Dl1CompanionReferenceTransfer.Apply(source, before, after, [0, 1, -1], transfers));
        Assert.Contains("tip", previewFailure.Message, StringComparison.Ordinal);

        SecondaryMotionDefinition nativeOnly = source with { Groups = [] };
        InvalidDataException nativeFailure = Assert.Throws<InvalidDataException>(() =>
            Dl1CompanionReferenceTransfer.Apply(nativeOnly, before, after, [0, 1, -1], transfers));
        Assert.Contains("tip", nativeFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownQuotedNativeReferenceFailsWhenItsBoneWasRenamed()
    {
        const string text = PhxText + "  Opaque(\"anchor\")\n";
        SecondaryMotionDefinition source = Definition(text) with { Groups = [] };
        var (before, after, transfers) = Skeleton("root", "anchor", "tip", "root_target", "anchor_target", "tip_target");

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            Dl1CompanionReferenceTransfer.Apply(source, before, after, [0, 1, 2], transfers));
        Assert.Contains("unsupported", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anchor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConformanceFixtureSavesReopensAndPublishesRenamedNativeCompanions()
    {
        var (model, fit) = ConformanceHelperPreservationTests.Fixture(false, false);
        ImmutableArray<CustomModelBone> sourceBones = model.Package.Document.CreateEffectiveBones();
        string anchor = sourceBones[1].Name;
        string tip = sourceBones[2].Name;
        string phx = "!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n" +
            "  BonesGridSize(1, 1)\n" +
            "  Bone(0, 0, \"authored_tip\", 1, 0, 0)\n" +
            $"  CollisionSphere(\"{anchor}\", 0.2)\n" +
            "}\n";
        CustomModelDocument document = model.Package.Document with { SecondaryMotion = Definition(phx, anchor, tip) };
        model = model with { Package = model.Package with { Document = document } };

        var applied = Dl1RigConformanceApplier.Apply(model, fit);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string packagePath = Path.Combine(directory, "conformed-companions.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(applied.Package, packagePath);
            CustomModelPackage reopenedPackage = CustomModelPackageSerializer.Load(packagePath);
            SecondaryMotionDefinition saved = applied.Package.Document.SecondaryMotion;
            SecondaryMotionDefinition reopened = reopenedPackage.Document.SecondaryMotion;
            Assert.Equal(SecondaryMotionSetupSerializer.Serialize(saved), SecondaryMotionSetupSerializer.Serialize(reopened));
            Assert.Equal(saved.NativeSources[0].OriginalText, reopened.NativeSources[0].OriginalText);

            string[] emittedBones = reopenedPackage.Document.CreateEffectiveBones().Select(b => b.Name).ToArray();
            Guid structuralRootId = Guid.NewGuid();
            CustomModelDocument exportDocument = reopenedPackage.Document with
            {
                RiggingSession = new RiggingSession
                {
                    OwnerModelId = reopenedPackage.Document.ModelId,
                    Recipe = new RuntimeRigRecipe
                    {
                        ProfileSnapshot = new RigCapabilityProfile
                        {
                            Roles = [new RigRuntimeRole
                            {
                                Id = "cloth.root", Category = RigRoleCategory.Cloth, EntityKind = RigNativeEntityKind.Bone,
                            }],
                        },
                        Entities = [new RigEntityBinding
                        {
                            EntityId = structuralRootId, OwnerAssetId = reopenedPackage.Document.ModelId,
                            NativeName = "authored_tip", Kind = RigNativeEntityKind.Bone, Imported = false,
                        }],
                        Assignments = [new("cloth.root", structuralRootId)],
                    },
                },
            };
            Dl1NativeCompanionBuild native = Dl1NativeCompanionWriter.Build(exportDocument, "fixture_model", emittedBones);
            Assert.Contains("fixture_model_000.phx", native.Files.Keys);
            Assert.Contains("fixture_model.mpcloth", native.Files.Keys);
            string wrapper = System.Text.Encoding.UTF8.GetString(native.Files["fixture_model.mpcloth"]);
            NativeClothBinding binding = Assert.Single(Dl1ClothCodec.ReadMpCloth(wrapper, ["fixture_model_000.phx"]).Bindings);
            Assert.Equal(0, binding.Enabled);
            Assert.Equal(1, binding.Flag);
            Assert.Contains(native.Notes, note => note.Contains("coefficients", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void NewRotatedBoneBasisPreservesTransportedAnchorWorldPosition()
    {
        var source = Definition();
        var (before, after, transfers) = Skeleton("root", "anchor", "tip", "root_target", "anchor_target", "tip_target");
        var frame = new TransformTRS(new(0, 4, 0), QuaternionD.FromAxisAngle(Vector3D.UnitZ, Math.PI / 2), Vector3D.One);
        after = after.SetItem(1, after[1] with { LocalBindTransform = frame, ExactLocalBindMatrix = frame.ToMatrix() });
        var result = Dl1CompanionReferenceTransfer.Apply(source, before, after, [0, 1, 2], transfers);
        AssertNear(new(-1, 5.5, 0), result.Definition.Groups[0].Particles[0].LocalPosition);
    }

    [Fact]
    public void IdentityTransferKeepsExactTextOffsetsAndDoesNotInventOriginalText()
    {
        var source = Definition(PhxText);
        var (before, _, _) = Skeleton("root", "anchor", "tip", "a", "b", "c");
        var result = Dl1CompanionReferenceTransfer.Apply(source, before, before, [0, 1, 2],
            [TransformMatrix.Identity, TransformMatrix.Identity, TransformMatrix.Identity]);
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(source), SecondaryMotionSetupSerializer.Serialize(result.Definition));
        Assert.Null(result.Definition.NativeSources[0].OriginalText);
        Assert.Equal(0, result.NativeBindings);
    }

    private static SecondaryMotionDefinition Definition(string? phx = null, string anchorName = "anchor", string tipName = "tip")
    {
        var sources = ImmutableArray.CreateBuilder<NativeClothSource>();
        if (phx is not null)
        {
            sources.Add(new() { Kind = NativeClothSourceKind.Phx, ResourceName = "cloth.phx", Text = phx });
            sources.Add(new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "cloth.mpcloth",
                Text = "// authored flags\nMeshPartCloth(\"cloth.phx\", 0, 1)\n" });
        }
        return new SecondaryMotionDefinition
        {
            Groups =
            [
                new()
                {
                    Name = "fabric",
                    Particles =
                    [
                        new() { ReferenceBoneName = anchorName, DrivenBoneName = anchorName, LocalPosition = new(.5, 0, 0), Fixed = true },
                        new() { ReferenceBoneName = tipName, DrivenBoneName = tipName, LocalPosition = new(-.2, .3, 0) },
                    ],
                    Constraints = [new() { First = 0, Second = 1, Kind = SecondaryConstraintKind.Structural, RestLength = 1.25 }],
                    Colliders = [new() { BoneName = anchorName, LocalPosition = new(.1, .2, 0), EndBoneName = tipName,
                        EndLocalPosition = new(.4, -.1, 0), Radius = .2 }],
                    Preview = new() { Damping = 17.25, StructuralStiffness = .31, ShearStiffness = .47,
                        BendStiffness = .23, AnimationFollow = .81, CollisionFriction = .14 },
                },
            ],
            NativeSources = sources.ToImmutable(),
        };
    }

    private static (ImmutableArray<CustomModelBone> Before, ImmutableArray<CustomModelBone> After,
        ImmutableArray<TransformMatrix> Transfers) Skeleton(
        string rootName, string anchorName, string tipName,
        string rootTargetName, string anchorTargetName, string? tipTargetName)
    {
        ImmutableArray<CustomModelBone> before =
        [
            Bone(0, rootName, -1, new(1, 0, 0)),
            Bone(1, anchorName, 0, new(0, 2, 0)),
            Bone(2, tipName, 1, new(0, 3, 0)),
        ];
        ImmutableArray<CustomModelBone> after = tipTargetName is null
            ? [Bone(0, rootTargetName, -1, new(10, 0, 0)), Bone(1, anchorTargetName, 0, new(0, 4, 0))]
            : [Bone(0, rootTargetName, -1, new(10, 0, 0)), Bone(1, anchorTargetName, 0, new(0, 4, 0)),
                Bone(2, tipTargetName, 1, new(0, 5, 0))];
        return (before, after,
            [TransformMatrix.Identity, TransformMatrix.CreateTranslation(new(3, 1, 0)),
                TransformMatrix.CreateTranslation(new(4, -1, 0))]);
    }

    private static CustomModelBone Bone(int index, string name, int parent, Vector3D translation) => new()
    {
        Index = index,
        FbxObjectId = 1000 + index,
        Name = name,
        ParentIndex = parent,
        LocalBindTransform = new(translation, QuaternionD.Identity, Vector3D.One),
        ExactLocalBindMatrix = TransformMatrix.CreateTranslation(translation),
    };

    private static void AssertNear(Vector3D expected, Vector3D actual, double tolerance = 1e-9)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
