using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxGeneratedHandAuthoringTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void AppendingHandPreservesSurfacesWeightsMetadataAndReopens()
    {
        FbxModelAuthoringImportResult model = WeightedGeneratedBody();
        ImmutableArray<FbxModelSurface> surfaces = model.Surfaces;
        ImmutableArray<CustomModelAnimationClip> clips = model.Package.Document.AnimationClips;
        FbxModelAuthoringImportResult appended = FbxGeneratedHandAuthoring.Append(model, RigHandSide.Left);

        Assert.Equal(22, appended.Package.Document.Bones.Length);
        Assert.Equal(surfaces, appended.Surfaces);
        Assert.Equal(clips, appended.Package.Document.AnimationClips);
        Assert.NotNull(appended.Package.Document.AuthoredLayer);
        AuthoredModelLayer layer = CustomModelPackageSerializer.ValidateAuthoredLayer(appended.Package)!;
        Assert.Equal(CustomModelContractSignatures.ComputeRig(appended.Package.Document.Bones), layer.TargetRigSignature);
        Assert.Contains(layer.Components, component => component.Points.Any(point => point.ReplaceWeights));
        Assert.All(appended.Surfaces.SelectMany(static surface => surface.Vertices), vertex => Assert.Equal(1, vertex.BoneWeights.Sum(), 12));

        string path = Path.Combine(_directory, "generated-hand.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(appended.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        Assert.Equal<CustomModelBone>(appended.Package.Document.Bones, reopened.Package.Document.Bones);
        Assert.Equal(appended.Package.Document.AnimationClips.ToArray(), reopened.Package.Document.AnimationClips.ToArray());
        Assert.Equal(22, reopened.Package.Document.Bones.Length);
        Assert.Equal(SurfaceWeightNames(appended), SurfaceWeightNames(reopened));
        Assert.True(appended.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(reopened.Package.AuthoredLayerPayload.AsSpan()));
    }

    [Fact]
    public void AppendingOtherHandKeepsFirstHandIdentityAndIsIdempotent()
    {
        FbxModelAuthoringImportResult model = WeightedGeneratedBody();
        FbxModelAuthoringImportResult left = FbxGeneratedHandAuthoring.Append(model, RigHandSide.Left);
        FbxModelAuthoringImportResult same = FbxGeneratedHandAuthoring.Append(left, RigHandSide.Left);
        Assert.Same(left, same);
        FbxModelAuthoringImportResult right = FbxGeneratedHandAuthoring.Append(left, RigHandSide.Right);
        Assert.Equal(23, right.Package.Document.Bones.Length);
        Assert.Equal(left.Surfaces, right.Surfaces);
        Assert.Equal(SurfaceWeightNames(left), SurfaceWeightNames(right));
        Assert.Contains(right.Package.Document.Bones, bone => bone.Name == "finger_right_thumb_1");
        Assert.True(GeneratedBodyRig.IsGenerated(right.Package.Document));
    }

    [Fact]
    public void AppendRefusesMissingOrUnreviewedHandSetup()
    {
        FbxModelAuthoringImportResult model = WeightedGeneratedBody();
        RiggingSession session = model.Package.Document.RiggingSession!;
        RigHandSetup setup = session.Hands.Single(hand => hand.Side == RigHandSide.Left);
        CustomModelDocument missing = model.Package.Document with { RiggingSession = session with { Hands = [session.Hands.Single(hand => hand.Side == RigHandSide.Right)] } };
        Assert.Throws<InvalidOperationException>(() => FbxGeneratedHandAuthoring.Append(model with { Package = model.Package with { Document = missing } }, RigHandSide.Left));
        CustomModelDocument unreviewed = model.Package.Document with { RiggingSession = session with { Hands = [setup with { UserApproved = false }, session.Hands.Single(hand => hand.Side == RigHandSide.Right)] } };
        Assert.Throws<InvalidOperationException>(() => FbxGeneratedHandAuthoring.Append(model with { Package = model.Package with { Document = unreviewed } }, RigHandSide.Left));
    }

    private static FbxModelAuthoringImportResult WeightedGeneratedBody()
    {
        FbxModelAuthoringImportResult source = GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source());
        CustomModelDocument sourceDocument = source.Package.Document;
        RiggingSession sourceSession = sourceDocument.RiggingSession!;
        RigLandmark wristLeft = sourceSession.Landmarks.Single(landmark => landmark.RoleId == "hand.left");
        RigLandmark wristRight = sourceSession.Landmarks.Single(landmark => landmark.RoleId == "hand.right");
        ImmutableArray<RigLandmark> fingerGuides =
        [
            new() { Id = new Guid("40000000-0000-0000-0000-000000000001"), RoleId = "finger.left.index.1", Position = wristLeft.Position + new Vector3D(-.2, 0, 0) },
            new() { Id = new Guid("40000000-0000-0000-0000-000000000002"), RoleId = "finger.left.index.2", Position = wristLeft.Position + new Vector3D(-.4, 0, 0) },
            new() { Id = new Guid("40000000-0000-0000-0000-000000000003"), RoleId = "finger.left.index.3", Position = wristLeft.Position + new Vector3D(-.6, 0, 0) },
            new() { Id = new Guid("40000000-0000-0000-0000-000000000004"), RoleId = "finger.left.index.4", Position = wristLeft.Position + new Vector3D(-.8, 0, 0) },
            new() { Id = new Guid("40000000-0000-0000-0000-000000000011"), RoleId = "finger.right.thumb.1", Position = wristRight.Position + new Vector3D(.2, 0, 0) },
            new() { Id = new Guid("40000000-0000-0000-0000-000000000012"), RoleId = "finger.right.thumb.2", Position = wristRight.Position + new Vector3D(.4, 0, 0) },
        ];
        RigHandSetup left = new()
        {
            Side = RigHandSide.Left, WristGuideId = wristLeft.Id, PalmFrame = TransformMatrix.Identity, UserApproved = true,
            Fingers = [new RigFingerDeclaration { Id = "index", Presence = RigFingerPresence.Present, JointGuideIds = fingerGuides.Take(4).Select(static guide => guide.Id).ToImmutableArray(), CurlPlaneNormal = Vector3D.UnitY, UserApproved = true },
                new RigFingerDeclaration { Id = "ring", Presence = RigFingerPresence.Absent }],
        };
        RigHandSetup right = new()
        {
            Side = RigHandSide.Right, WristGuideId = wristRight.Id, PalmFrame = TransformMatrix.Identity, UserApproved = true,
            Fingers = [new RigFingerDeclaration { Id = "thumb", Presence = RigFingerPresence.Present, JointGuideIds = fingerGuides.Skip(4).Select(static guide => guide.Id).ToImmutableArray(), CurlPlaneNormal = Vector3D.UnitY, UserApproved = true },
                new RigFingerDeclaration { Id = "index", Presence = RigFingerPresence.Absent }],
        };
        RiggingSession session = sourceSession with { Landmarks = sourceSession.Landmarks.AddRange(fingerGuides), Hands = [left, right] };
        session.Validate();
        FbxModelAuthoringImportResult withHands = source with { Package = source.Package with { Document = sourceDocument with { RiggingSession = session } } };
        FbxModelAuthoringImportResult generated = FbxGeneratedBodyBinding.Generate(withHands);
        ImmutableArray<FbxModelSurface> weighted = generated.Surfaces.Select(surface => surface with
        {
            Vertices = surface.Vertices.Select(vertex => vertex with { BoneIndices = [0], BoneWeights = [1d] }).ToImmutableArray(),
            PaletteBoneIndices = [0], InverseBindMatrices = [TransformMatrix.Identity], IsSkinned = true,
        }).ToImmutableArray();
        return generated with { Surfaces = weighted };
    }

    private static string[] SurfaceWeightNames(FbxModelAuthoringImportResult model) => model.Surfaces.SelectMany(surface => surface.Vertices.Select((vertex, index) =>
        string.Join(";", vertex.BoneIndices.Select((slot, weight) => model.Package.Document.Bones[surface.PaletteBoneIndices[slot]].Name + ":" + vertex.BoneWeights[weight].ToString("R", System.Globalization.CultureInfo.InvariantCulture))))).ToArray();

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}
