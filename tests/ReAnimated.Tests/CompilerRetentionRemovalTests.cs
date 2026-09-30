using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CompilerRetentionRemovalTests : IDisposable
{
    private readonly string directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void AddThenRemoveRestoresTheSourceHierarchyAndPersistsCleanly()
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        Guid parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child").EntityId;
        StructuralHelperPreview addedPreview = FbxCompilerRetentionAuthoring.Preview(source, parent);
        FbxModelAuthoringImportResult added = addedPreview.Candidate;
        CustomModelAuthoredHelper retention = added.Package.Document.AuthoredHelpers[^1];
        Assert.Equal(FbxCompilerRetentionAuthoring.RoleId,
            added.Package.Document.RiggingSession!.Recipe.Helpers.Single(h => h.EntityId == retention.Id).RoleId);
        string sourceSecondary = SecondaryMotionSetupSerializer.Serialize(source.Package.Document.SecondaryMotion);

        StructuralHelperPreview removal = FbxCompilerRetentionAuthoring.PreviewRemoval(added, retention.Id);
        Assert.Equal(retention.Id, removal.RemovedHelperId);
        Assert.True(FbxStructuralHelperAuthoring.TryApply(added, removal, out FbxModelAuthoringImportResult restored));
        Assert.Equal(source.Package.SourceFbx, restored.Package.SourceFbx);
        Assert.Equal<byte>(source.Package.AuthoredLayerPayload, restored.Package.AuthoredLayerPayload);
        Assert.Equal<CustomModelBone>(source.Package.Document.Bones, restored.Package.Document.Bones);
        Assert.Equal<CustomModelAuthoredHelper>(source.Package.Document.AuthoredHelpers,
            restored.Package.Document.AuthoredHelpers);
        Assert.Equal<FbxModelSurface>(source.Surfaces, restored.Surfaces);
        Assert.Equal(source.AnimationClips, restored.AnimationClips);
        Assert.Equal(sourceSecondary,
            SecondaryMotionSetupSerializer.Serialize(restored.Package.Document.SecondaryMotion));
        Assert.All(restored.Surfaces.SelectMany(surface => surface.Vertices), vertex =>
            Assert.All(vertex.BoneWeights, weight => Assert.True(weight > 0)));

        string path = Path.Combine(directory, "retention-roundtrip.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(restored.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        Assert.Equal<CustomModelBone>(restored.Package.Document.Bones, reopened.Package.Document.Bones);
        Assert.Equal<CustomModelAuthoredHelper>(restored.Package.Document.AuthoredHelpers,
            reopened.Package.Document.AuthoredHelpers);
        Assert.Equal<byte>(source.Package.SourceFbx, reopened.Package.SourceFbx);
        Assert.Equal(sourceSecondary,
            SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
    }

    [Fact]
    public void RemovingAHelperInTheMiddleReindexesLaterWeightedAndAnimatedHelpers()
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        Guid parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child").EntityId;
        FbxModelAuthoringImportResult added = FbxCompilerRetentionAuthoring.Preview(source, parent).Candidate;
        CustomModelAuthoredHelper retention = added.Package.Document.AuthoredHelpers[^1];
        int childIndex = added.Package.Document.Bones.Single(b => b.Name == "Child").Index;
        CustomModelDocument withLaterHelper = CustomModelHelperAuthoring.DuplicateAsHelper(
            added.Package.Document, childIndex, CustomModelAuthoredHelperKind.Helper, "later_helper");
        int laterIndex = withLaterHelper.CreateEffectiveBones().Single(b => b.Name == "later_helper").Index;
        TransformMatrix[] globals = FbxRestPoseAuthoringTests.ExactGlobals(withLaterHelper);
        ImmutableArray<FbxModelSurface> surfaces = added.Surfaces.Select(surface =>
        {
            if (!surface.IsSkinned) return surface;
            int slot = surface.PaletteBoneIndices.Length;
            return surface with
            {
                PaletteBoneIndices = surface.PaletteBoneIndices.Add(laterIndex),
                InverseBindMatrices = surface.InverseBindMatrices.Add(globals[laterIndex].InvertedAffine()),
                Vertices = surface.Vertices.SetItem(0, surface.Vertices[0] with
                {
                    BoneIndices = [slot],
                    BoneWeights = [1],
                }),
            };
        }).ToImmutableArray();
        Guid clipId = Guid.NewGuid();
        AnimationClip clip = new("later helper motion", new FrameRate(30, 1), 2,
            [new TransformTrack(laterIndex,
                [new TransformKeyframe(0, TransformTRS.Identity),
                 new TransformKeyframe(1, TransformTRS.Identity with { Translation = new(.1, .2, .3) })])]);
        FbxModelAuthoringImportResult middle = FbxAuthoredModelLayer.Capture(added with
        {
            Package = added.Package with { Document = withLaterHelper },
            Rig = withLaterHelper.CreateRigDefinition(),
            Surfaces = surfaces,
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(clipId, clip),
        });

        StructuralHelperPreview removal = FbxCompilerRetentionAuthoring.PreviewRemoval(middle, retention.Id);
        Assert.True(FbxStructuralHelperAuthoring.TryApply(middle, removal, out FbxModelAuthoringImportResult result));
        int resultLater = result.Package.Document.CreateEffectiveBones().Single(b => b.Name == "later_helper").Index;
        Assert.Equal(laterIndex - 1, resultLater);
        FbxModelSurface resultSurface = result.Surfaces.Single(surface => surface.IsSkinned);
        int resultSlot = resultSurface.Vertices[0].BoneIndices.Single();
        Assert.Equal(resultLater, resultSurface.PaletteBoneIndices[resultSlot]);
        Assert.Equal<double>([1d], resultSurface.Vertices[0].BoneWeights);
        TransformTrack track = Assert.Single(result.AnimationClips[clipId].TransformTracks);
        Assert.Equal(laterIndex - 1, track.BoneIndex);
        Assert.Equal<TransformKeyframe>(clip.TransformTracks[0].Keyframes, track.Keyframes);
        Assert.Equal(retention.Id, removal.RemovedHelperId);
    }

    [Fact]
    public void CompanionReferencesToRemovedHelperRejectAndUnrelatedReferencesSurvive()
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        Guid parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child").EntityId;
        FbxModelAuthoringImportResult added = FbxCompilerRetentionAuthoring.Preview(source, parent).Candidate;
        CustomModelAuthoredHelper retention = added.Package.Document.AuthoredHelpers[^1];

        FbxModelAuthoringImportResult removedReference = WithCompanion(added, retention.Name);
        Assert.Throws<InvalidDataException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(
            removedReference, retention.Id));

        FbxModelAuthoringImportResult unrelatedReference = WithCompanion(added, "Child");
        StructuralHelperPreview removal = FbxCompilerRetentionAuthoring.PreviewRemoval(
            unrelatedReference, retention.Id);
        Assert.True(removal.HasChanges);
        Assert.Equal("Child", removal.Candidate.Package.Document.SecondaryMotion.Groups[0].Particles[0].ReferenceBoneName);
        Assert.Contains("\"Child\"", removal.Candidate.Package.Document.SecondaryMotion.NativeSources[0].Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalBlockersStalePreviewsAndCancellationAreRejected()
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        Guid parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child").EntityId;
        FbxModelAuthoringImportResult added = FbxCompilerRetentionAuthoring.Preview(source, parent).Candidate;
        Guid helperId = added.Package.Document.AuthoredHelpers[^1].Id;
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(
            added, helperId, ["animation variant 'generic' references this model"]));
        Assert.Throws<OperationCanceledException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(
            added, helperId, cancellationToken: new CancellationToken(true)));

        StructuralHelperPreview preview = FbxCompilerRetentionAuthoring.PreviewRemoval(added, helperId);
        FbxModelAuthoringImportResult stale = added with
        {
            Package = added.Package with
            {
                Document = added.Package.Document with { Name = added.Package.Document.Name + "-changed" },
            },
        };
        Assert.False(FbxStructuralHelperAuthoring.TryApply(stale, preview, out _));
    }

    [Fact]
    public void NativeOnlyReferencesUnknownStatementsAndExternalIncludesAreNotSilentlyDropped()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child");
        var added = FbxCompilerRetentionAuthoring.Preview(source, parent.EntityId).Candidate;
        var marker = added.Package.Document.AuthoredHelpers[^1];
        var referenced = WithCompanion(added, marker.Name);
        referenced = referenced with { Package = referenced.Package with { Document = referenced.Package.Document with
            { SecondaryMotion = referenced.Package.Document.SecondaryMotion with { Groups = [] } } } };
        Assert.Throws<InvalidDataException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(referenced, marker.Id));
        var unrelated = WithCompanion(added, "Child");
        foreach (string statement in new[] { "Unknown(3)", "!include(\"external.phx\")" })
        {
            var setup = unrelated.Package.Document.SecondaryMotion;
            setup = setup with { NativeSources = setup.NativeSources.SetItem(0, setup.NativeSources[0] with { Text = setup.NativeSources[0].Text + statement }) };
            var changed = unrelated with { Package = unrelated.Package with { Document = unrelated.Package.Document with { SecondaryMotion = setup } } };
            Assert.Throws<InvalidDataException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(changed, marker.Id));
        }
    }

    [Fact]
    public void ProtectionSavedDecisionsAndOwnedAnimationTracksPreventRemoval()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child");
        var added = FbxCompilerRetentionAuthoring.Preview(source, parent.EntityId).Candidate;
        var marker = added.Package.Document.AuthoredHelpers[^1];
        var doc = added.Package.Document; var session = doc.RiggingSession!;
        var locked = doc with { RiggingSession = session with { Recipe = session.Recipe with
            { Helpers = session.Recipe.Helpers.Select(h => h.EntityId == marker.Id ? h with { LockedFields = RigHelperEditFields.Name } : h).ToImmutableArray() } } };
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(added with { Package = added.Package with { Document = locked } }, marker.Id));
        var referenced = doc with { RiggingSession = session with { Recipe = session.Recipe with { Assignments = [new("saved-role", marker.Id)] } } };
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(added with { Package = added.Package with { Document = referenced } }, marker.Id));
        var clip = new AnimationClip("marker motion", new FrameRate(30, 1), 1,
            [new TransformTrack(doc.CreateEffectiveBones().Length - 1, [new TransformKeyframe(0, TransformTRS.Identity)])]);
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.PreviewRemoval(added with { AnimationClips = added.AnimationClips.Add(Guid.NewGuid(), clip) }, marker.Id));
    }

    [Fact]
    public void UnusedZeroWeightPaletteSlotCanBeRemovedWithoutChangingPositiveWeights()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child");
        var added = FbxCompilerRetentionAuthoring.Preview(source, parent.EntityId).Candidate;
        var marker = added.Package.Document.AuthoredHelpers[^1];
        int index = added.Package.Document.CreateEffectiveBones().Length - 1;
        var changed = added with { Surfaces = added.Surfaces.Select(surface => surface with
        {
            PaletteBoneIndices = surface.PaletteBoneIndices.Add(index),
            InverseBindMatrices = surface.InverseBindMatrices.Add(TransformMatrix.Identity),
            Vertices = surface.Vertices.Select(v => v with { BoneIndices = v.BoneIndices.Add(surface.PaletteBoneIndices.Length), BoneWeights = v.BoneWeights.Add(0) }).ToImmutableArray(),
        }).ToImmutableArray() };
        var result = FbxCompilerRetentionAuthoring.PreviewRemoval(changed, marker.Id).Candidate;
        for (int i = 0; i < result.Surfaces.Length; i++)
        {
            Assert.Equal<int>(added.Surfaces[i].PaletteBoneIndices, result.Surfaces[i].PaletteBoneIndices);
            Assert.Equal<TransformMatrix>(added.Surfaces[i].InverseBindMatrices, result.Surfaces[i].InverseBindMatrices);
            for (int j = 0; j < result.Surfaces[i].Vertices.Length; j++)
            {
                Assert.Equal<int>(added.Surfaces[i].Vertices[j].BoneIndices, result.Surfaces[i].Vertices[j].BoneIndices);
                Assert.Equal<double>(added.Surfaces[i].Vertices[j].BoneWeights, result.Surfaces[i].Vertices[j].BoneWeights);
            }
        }
    }

    private static FbxModelAuthoringImportResult WithCompanion(
        FbxModelAuthoringImportResult source, string referenceName)
    {
        SecondaryMotionDefinition secondary = new()
        {
            Groups =
            [
                new()
                {
                    Name = "retention-companion",
                    Particles = [new() { ReferenceBoneName = referenceName, Fixed = true }],
                },
            ],
            NativeSources =
            [
                new()
                {
                    Kind = NativeClothSourceKind.Phx,
                    ResourceName = "retention.phx",
                    Text = $"!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{{\nBonesGridSize(1, 1)\nBone(0, 0, \"{referenceName}\", 1, 0, 0)\n}}\n",
                },
            ],
        };
        CustomModelDocument document = source.Package.Document with { SecondaryMotion = secondary };
        return source with { Package = source.Package with { Document = document } };
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(directory);
}
