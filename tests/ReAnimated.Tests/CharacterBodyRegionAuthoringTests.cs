using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class CharacterBodyRegionAuthoringTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BoneElementHideTargetsAreAcceptedWithoutPretendingTheyAreDrawMeshes()
    {
        var model = CreateModel();
        string bone = model.Package.Document.CreateEffectiveBones()[0].Name;
        string text = BodyText(model).Replace("AddMesh2Disable(\"" + model.Surfaces[0].MeshName + "\")",
            "AddMesh2Disable(\"" + bone + "\")", StringComparison.Ordinal);
        model = ReplaceBody(model, text);
        var entity = CharacterModelEntityInventory.RequireUnique(model, bone);
        Assert.True(entity.IsBone);
        var reviewed = CharacterBodyRegionAuthoring.Apply(model, Proposal(model));
        Assert.Contains(bone, Assert.Single(reviewed.Package.Document.CharacterResources!.BodyRegionReviews).BodyHideEntityNames);
        Assert.Empty(CharacterBodyRegionAuthoring.ExportBlockers(reviewed));
        var ambiguous = model with { Surfaces = model.Surfaces.SetItem(1, model.Surfaces[1] with { MeshName = bone }) };
        Assert.Throws<InvalidDataException>(() => CharacterModelEntityInventory.RequireUnique(ambiguous, bone));
    }

    [Fact]
    public void TypedNativeDetachedMeshCanBeReviewedWithoutAFilenameExtension()
    {
        var model=CreateModel();var inventory=model.Package.Document.CharacterResources!;
        var row=inventory.Resources.Single(resource=>resource.Id=="detached");var bytes=model.Package.CompanionPayloads[row.EntryPath!];
        row=row with {LogicalName="generic_part",Subsystem=CharacterSubsystem.DetachedParts,NativeResource=new(){HeaderVersion=1,HeaderUnknown=1,
            ResourceName="generic_part",ResourceType=272,SourceResourceIndex=3,Items=[new(0,0,0,3,0,16,514,1,2,0,bytes.Length,row.ContentSha256!,new string('a',64))]}};
        model=model with {Package=model.Package with {Document=model.Package.Document with {CharacterResources=inventory with
            {Resources=inventory.Resources.Select(resource=>resource.Id==row.Id?row:resource).ToImmutableArray()}}}};
        var reviewed=CharacterBodyRegionAuthoring.Apply(model,Proposal(model));
        Assert.Equal("generic_part",Assert.Single(Assert.Single(reviewed.Package.Document.CharacterResources!.BodyRegionReviews).DetachedAssets).LogicalName);
        CustomModelPackageSerializer.Serialize(reviewed.Package);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task OpeningDifferentModelWithoutBodySourceClearsPriorReviewAndSelectionState()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string reviewedPath = Path.Combine(directory, "reviewed-generic.dlrmodel");
            string targetPath = Path.Combine(directory, "other-generic.dlrmodel");
            FbxModelAuthoringImportResult source = CreateModel();
            CustomModelPackage reviewed = CharacterBodyRegionAuthoring.Apply(source, Proposal(source)).Package;
            CustomModelPackageSerializer.SaveAtomic(reviewed, reviewedPath);
            CustomModelPackage other = ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
            other = other with { Document = other.Document with { ModelId = Guid.NewGuid() } };
            CustomModelPackageSerializer.SaveAtomic(other, targetPath);

            using var workspace = new ModelsWorkspaceViewModel(new PackageDialogs(reviewedPath),
                static _ => { }, static _ => Task.CompletedTask, static () => null);
            await workspace.OpenPackagePathAsync(reviewedPath);
            workspace.CompanionFamily = CharacterCompanionFamily.BodyElements;
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "body");
            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.CallIndex == 0);
            Assert.True(Assert.Single(workspace.BodyAssemblyReviews).Accepted);
            Assert.NotEmpty(workspace.CompanionCalls);
            Assert.NotEmpty(workspace.CompanionArguments);
            workspace.SelectedBodyCapSurface = "cap/0";
            workspace.AddBodyCapCommand.Execute(null);
            Assert.Single(workspace.SelectedBodyCapSurfaceIds);
            workspace.BodyArtistGeometryReviewed = true;
            workspace.BodyRelationshipsReviewed = true;
            workspace.CompanionEditReviewed = true;

            await workspace.OpenPackagePathAsync(targetPath);

            Assert.Equal(other.Document.ModelId, workspace.CaptureProjectSession().Model!.Package.Document.ModelId);
            Assert.Empty(workspace.CompanionCandidates);
            Assert.Null(workspace.SelectedCompanion);
            Assert.Empty(workspace.CompanionGroups);
            Assert.Null(workspace.SelectedCompanionGroup);
            Assert.Empty(workspace.CompanionCalls);
            Assert.Null(workspace.SelectedCompanionCall);
            Assert.Empty(workspace.CompanionArguments);
            Assert.Null(workspace.SelectedCompanionArgument);
            Assert.Empty(workspace.CompanionRelationships);
            Assert.Null(workspace.SelectedCompanionRelationship);
            Assert.Empty(workspace.BodyAssemblyReviews);
            Assert.Empty(workspace.BodyCapSurfaceChoices);
            Assert.Empty(workspace.SelectedBodyCapSurfaceIds);
            Assert.Empty(workspace.SelectedBodyDetachedAssets);
            Assert.Empty(workspace.SelectedBodyPhysicsAssets);
            Assert.Empty(workspace.SelectedBodyEffectAssets);
            Assert.Empty(workspace.RagdollSourceShapeTokens);
            Assert.Null(workspace.RagdollProposedShape);
            Assert.False(workspace.BodyArtistGeometryReviewed);
            Assert.False(workspace.BodyRelationshipsReviewed);
            Assert.False(workspace.CompanionEditReviewed);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.RecordBodyAssemblyCommand.Execute(null);
            Assert.Contains("Choose a body-element source", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.CharacterResources!.BodyRegionReviews);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task WorkspaceRecordsReviewedRegionPreviewsVisibilityAndRetainsUndoableReceipt()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-body-region.dlrmodel");
            FbxModelAuthoringImportResult source = CreateModel();
            CustomModelPackageSerializer.SaveAtomic(ModelGeometryRevisionCodec.Capture(source).Package, path);
            using var workspace = new ModelsWorkspaceViewModel(new PackageDialogs(path),
                static _ => { }, static _ => Task.CompletedTask, static () => null);
            await workspace.OpenPackagePathAsync(path);
            Assert.Equal(2, workspace.CaptureProjectSession().Model!.Surfaces.Length);
            string rootResourceId = workspace.CaptureProjectSession().Model!.Package.Document.CharacterResources!.RootResourceId;
            Assert.DoesNotContain(workspace.BodyDetachedAssets, resource => resource.Id == rootResourceId);
            Assert.Contains(workspace.BodyDetachedAssets, resource => resource.Id == "detached");
            byte[] sourceBytes = workspace.CaptureProjectSession().Model!.Package.SourceFbx.ToArray();
            byte[] bodyBytes = Bytes(workspace.CaptureProjectSession().Model!.Package, "body");
            var originalPositions = workspace.CaptureProjectSession().Model!.Surfaces
                .Select(surface => surface.Vertices.Select(vertex => vertex.Position).ToArray()).ToArray();

            workspace.CompanionFamily = CharacterCompanionFamily.BodyElements;
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "body");
            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.CallIndex == 0);
            workspace.SelectedBodyCapSurface = "cap/0";
            workspace.BodyArtistGeometryReviewed = true;
            workspace.BodyRelationshipsReviewed = true;
            workspace.AddBodyCapCommand.Execute(null);
            Assert.Equal(["cap/0"], workspace.SelectedBodyCapSurfaceIds.ToArray());
            Assert.False(workspace.BodyArtistGeometryReviewed);
            Assert.False(workspace.BodyRelationshipsReviewed);
            workspace.SelectedDetachedAsset = Assert.Single(workspace.BodyDetachedAssets, resource => resource.Id == "detached");
            workspace.AddBodyDetachedCommand.Execute(null);
            workspace.SelectedBodyPhysicsAsset = Assert.Single(workspace.BodyPhysicsAssets, resource => resource.Id == "physics");
            workspace.AddBodyPhysicsCommand.Execute(null);
            workspace.SelectedBodyEffectAsset = Assert.Single(workspace.BodyEffectAssets, resource => resource.Id == "effect");
            workspace.AddBodyEffectCommand.Execute(null);
            Assert.Single(workspace.SelectedBodyDetachedAssets);
            Assert.Single(workspace.SelectedBodyPhysicsAssets);
            Assert.Single(workspace.SelectedBodyEffectAssets);

            workspace.RecordBodyAssemblyCommand.Execute(null);
            Assert.Contains("rejected", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.CharacterResources!.BodyRegionReviews);

            workspace.PreviewBodyRegionCommand.Execute(null);
            MeshRenderData preview = Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().Meshes);
            Assert.Equal("cap/0", preview.Id);
            Assert.False(preview.IsSkinned);
            Assert.True(preview.InverseBindMatrices.IsEmpty);
            Assert.True(preview.SkinBoneIndices.IsEmpty);
            Assert.True(RenderMeshValidation.TryValidate(preview, null, out string? validationError), validationError);
            workspace.RestoreBodyPreviewCommand.Execute(null);
            Assert.Equal(2, workspace.Viewport.SceneSource.CaptureFrame().Meshes.Count);

            workspace.BodyArtistGeometryReviewed = true;
            workspace.BodyRelationshipsReviewed = true;
            workspace.RecordBodyAssemblyCommand.Execute(null);
            FbxModelAuthoringImportResult accepted = workspace.CaptureProjectSession().Model!;
            Assert.True(Assert.Single(accepted.Package.Document.CharacterResources!.BodyRegionReviews).Accepted);
            Assert.Equal(sourceBytes, accepted.Package.SourceFbx.ToArray());
            Assert.Equal(bodyBytes, Bytes(accepted.Package, "body"));
            Assert.Equal(originalPositions[0], accepted.Surfaces[0].Vertices.Select(vertex => vertex.Position).ToArray());
            Assert.Equal(originalPositions[1], accepted.Surfaces[1].Vertices.Select(vertex => vertex.Position).ToArray());

            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.CharacterResources!.BodyRegionReviews);
            Assert.Empty(workspace.BodyAssemblyReviews);
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.True(Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.CharacterResources!.BodyRegionReviews).Accepted);
            Assert.True(Assert.Single(workspace.BodyAssemblyReviews).Accepted);
            workspace.SavePackageCommand.Execute(null);
            Assert.DoesNotContain("Save failed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            using var reopened = new ModelsWorkspaceViewModel(new PackageDialogs(path),
                static _ => { }, static _ => Task.CompletedTask, static () => null);
            await reopened.OpenPackagePathAsync(path);
            Assert.True(Assert.Single(reopened.CaptureProjectSession().Model!.Package.Document.CharacterResources!.BodyRegionReviews).Accepted);
            Assert.Equal(bodyBytes, Bytes(reopened.CaptureProjectSession().Model!.Package, "body"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewedAssemblyRecordsExactRolesAndSurvivesPackageRoundTrip()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult source = CreateModel();
            ReviewedBodyRegionAssembly proposal = Proposal(source);
            byte[] originalSource = source.Package.SourceFbx.ToArray();
            byte[] originalBody = Bytes(source.Package, "body");
            var originalSurfaces = source.Surfaces;

            FbxModelAuthoringImportResult accepted = CharacterBodyRegionAuthoring.Apply(source, proposal);
            CharacterBodyRegionAssemblyReview receipt = Assert.Single(accepted.Package.Document.CharacterResources!.BodyRegionReviews);
            Assert.True(receipt.Accepted);
            Assert.True(receipt.ArtistGeometryReviewed);
            Assert.True(receipt.RelationshipsReviewed);
            Assert.Equal(proposal.BodyElementCallIndex, receipt.BodyElementCallIndex);
            Assert.Equal(proposal.ExpectedSourceSha256, receipt.BodySourceSha256);
            Assert.Equal(source.Package.Document.CreateEffectiveBones()[0].Name, receipt.HelperName);
            Assert.Equal(["cap/0"], receipt.CutCapSurfaceIds.ToArray());
            Assert.Equal([source.Surfaces[0].MeshName], receipt.BodyHideEntityNames.ToArray());
            Assert.Equal(["cap"], receipt.RelicHideEntityNames.ToArray());
            Assert.Equal("detached", Assert.Single(receipt.DetachedAssets).ResourceId);
            Assert.Equal("physics", Assert.Single(receipt.PhysicsAssets).ResourceId);
            Assert.Equal("effect", Assert.Single(receipt.EffectAssets).ResourceId);
            Assert.Equal("data/physics/part.phx", receipt.PhysicsAssets[0].LogicalName);
            Assert.Equal("data/effects/impact.fx", receipt.EffectAssets[0].LogicalName);
            Assert.Equal(originalSource, accepted.Package.SourceFbx.ToArray());
            Assert.Equal(originalBody, Bytes(accepted.Package, "body"));
            Assert.Equal(originalSurfaces[0].Vertices.Select(vertex => vertex.Position).ToArray(),
                accepted.Surfaces[0].Vertices.Select(vertex => vertex.Position).ToArray());
            Assert.Equal(originalSurfaces[1].Vertices.Select(vertex => vertex.Position).ToArray(),
                accepted.Surfaces[1].Vertices.Select(vertex => vertex.Position).ToArray());
            Assert.Null(accepted.Package.Document.CharacterResources.CompiledSemanticSha256);
            Assert.Null(accepted.Package.Document.CharacterResources.LoadedResourceSha256);
            Assert.Empty(accepted.Package.Document.CharacterResources.VerifiedPlayerScenarios);
            Assert.Empty(CharacterBodyRegionAuthoring.ExportBlockers(accepted));

            string path = Path.Combine(directory, "generic-review.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(accepted.Package, path);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.True(Assert.Single(reopened.Package.Document.CharacterResources!.BodyRegionReviews).Accepted);
            Assert.Empty(CharacterBodyRegionAuthoring.ExportBlockers(reopened));
            Assert.Equal(originalSource, reopened.Package.SourceFbx.ToArray());
            Assert.Equal(originalBody, Bytes(reopened.Package, "body"));
            Assert.Equal(2, reopened.Surfaces.Length);
            Assert.Contains(reopened.Surfaces, surface => surface.Id == "cap/0");
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void MissingHelperCapWrongTypeStaleHashUnreviewedAmbiguityAndHiddenCapRefuse()
    {
        FbxModelAuthoringImportResult source = CreateModel();
        ReviewedBodyRegionAssembly valid = Proposal(source);
        Assert.Throws<InvalidOperationException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { ArtistGeometryReviewed = false }));
        Assert.Throws<InvalidOperationException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { RelationshipsReviewed = false }));
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { ExpectedSourceSha256 = new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { CutCapSurfaceIds = ["missing-cap"] }));
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { PhysicsResourceIds = ["effect"] }));
        string rootId = source.Package.Document.CharacterResources!.RootResourceId;
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(source,
            valid with { DetachedResourceIds = [rootId] }));
        Assert.Empty(source.Package.Document.CharacterResources.BodyRegionReviews);
        Assert.Equal(BodyText(source), Encoding.UTF8.GetString(Bytes(source.Package, "body")));

        CustomModelDocument noHelper = source.Package.Document with
        {
            Bones = source.Package.Document.Bones.Select(bone => bone with { Name = "different_" + bone.Name }).ToImmutableArray(),
        };
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(
            source with { Package = source.Package with { Document = noHelper } }, valid));

        FbxModelAuthoringImportResult ambiguous = AddResource(source, "duplicate-physics", "data/other/part.phx",
            "PhysicsParams() { QuickStepNumIterations(20) }\n", CharacterSubsystem.Ragdoll);
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(ambiguous, valid));

        FbxModelAuthoringImportResult hiddenCap = ReplaceBody(source,
            BodyText(source).Replace($"AddMesh2Disable(\"{source.Surfaces[0].MeshName}\")",
                "AddMesh2Disable(\"cap\")", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CharacterBodyRegionAuthoring.Apply(hiddenCap, Proposal(hiddenCap)));
        InvalidDataException unchangedBody = Assert.Throws<InvalidDataException>(() =>
            CharacterBodyRegionAuthoring.Apply(hiddenCap, Proposal(hiddenCap) with
            { CutCapSurfaceIds = [source.Surfaces[0].Id] }));
        Assert.Contains("artist-supplied cap geometry", unchangedBody.Message, StringComparison.Ordinal);

        Assert.Empty(source.Package.Document.CharacterResources!.BodyRegionReviews);
        Assert.Equal(BodyText(source), Encoding.UTF8.GetString(Bytes(source.Package, "body")));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ReconcileRejectsDriftAndStandardWriterBlocksStaleReview()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult source = CreateModel();
            FbxModelAuthoringImportResult accepted = CharacterBodyRegionAuthoring.Apply(source, Proposal(source));
            var moved = accepted.Surfaces[0] with
            {
                Vertices = accepted.Surfaces[0].Vertices.SetItem(0,
                    accepted.Surfaces[0].Vertices[0] with { Position = new Vector3D(99, 0, 0) }),
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Surfaces = accepted.Surfaces.SetItem(0, moved) }));
            var normalChanged = accepted.Surfaces[0] with
            {
                Vertices = accepted.Surfaces[0].Vertices.SetItem(0,
                    accepted.Surfaces[0].Vertices[0] with { Normal = new Vector3D(0, 0, -1) }),
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Surfaces = accepted.Surfaces.SetItem(0, normalChanged) }));
            var weighted = accepted.Surfaces[0].Vertices[0];
            var weightChanged = accepted.Surfaces[0] with
            {
                Vertices = accepted.Surfaces[0].Vertices.SetItem(0,
                    weighted with { BoneWeights = weighted.BoneWeights.SetItem(0, weighted.BoneWeights[0] * 0.9) }),
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Surfaces = accepted.Surfaces.SetItem(0, weightChanged) }));

            CharacterResourceInventory originalInventory = accepted.Package.Document.CharacterResources!;
            CharacterBodyRegionAssemblyReview originalReceipt = Assert.Single(originalInventory.BodyRegionReviews);
            CharacterResourceRecord root = originalInventory.Resources.Single(resource => resource.Id == originalInventory.RootResourceId);
            var rootSelected = originalInventory with
            {
                BodyRegionReviews = [originalReceipt with
                { DetachedAssets = [new CharacterGoreAssetSelection(root.Id, root.LogicalName, root.ContentSha256!)] }],
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Package = accepted.Package with { Document = accepted.Package.Document with { CharacterResources = rootSelected } } }));
            var changedBodyRole = originalInventory with
            { BodyRegionReviews = [originalReceipt with { BodyHideEntityNames = ["other_body_mesh"] }] };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Package = accepted.Package with { Document = accepted.Package.Document with { CharacterResources = changedBodyRole } } }));
            var changedRelicRole = originalInventory with
            { BodyRegionReviews = [originalReceipt with { RelicHideEntityNames = ["other_relic_mesh"] }] };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Package = accepted.Package with { Document = accepted.Package.Document with { CharacterResources = changedRelicRole } } }));

            CustomModelDocument shiftedHelper = accepted.Package.Document with
            {
                Bones = accepted.Package.Document.Bones.SetItem(0,
                    accepted.Package.Document.Bones[0] with
                    { ExactLocalBindMatrix = TransformMatrix.CreateTranslation(new Vector3D(3, 0, 0)) }),
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Package = accepted.Package with { Document = shiftedHelper } }));

            FbxModelAuthoringImportResult childSource = CreateModel(useChildHelper: true);
            FbxModelAuthoringImportResult childAccepted = CharacterBodyRegionAuthoring.Apply(childSource, Proposal(childSource));
            CustomModelDocument shiftedParent = childAccepted.Package.Document with
            {
                Bones = childAccepted.Package.Document.Bones.SetItem(0,
                    childAccepted.Package.Document.Bones[0] with
                    { ExactLocalBindMatrix = TransformMatrix.CreateTranslation(new Vector3D(4, 0, 0)) }),
            };
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(childAccepted with
            { Package = childAccepted.Package with { Document = shiftedParent } }));

            FbxModelAuthoringImportResult changedSource = ReplaceBody(accepted,
                BodyText(accepted).Replace("20.", "21.", StringComparison.Ordinal));
            AssertStale(CharacterBodyRegionAuthoring.Reconcile(changedSource));

            CharacterResourceRecord physics = Record(accepted.Package, "physics");
            var changedPayloads = accepted.Package.CompanionPayloads.SetItem(physics.EntryPath!,
                ImmutableArray.Create(Encoding.UTF8.GetBytes("changed retained bytes")));
            FbxModelAuthoringImportResult changedAsset = CharacterBodyRegionAuthoring.Reconcile(accepted with
            { Package = accepted.Package with { CompanionPayloads = changedPayloads } });
            AssertStale(changedAsset);
            InvalidDataException blocked = await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(new()
            { Model = changedAsset, OutputDirectory = directory, ResourceName = "generic_body_region" }));
            Assert.Contains("Body region", blocked.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }

        static void AssertStale(FbxModelAuthoringImportResult model)
        {
            Assert.False(Assert.Single(model.Package.Document.CharacterResources!.BodyRegionReviews).Accepted);
            Assert.Single(CharacterBodyRegionAuthoring.ExportBlockers(model));
            Assert.Null(model.Package.Document.CharacterResources.CompiledSemanticSha256);
            Assert.Null(model.Package.Document.CharacterResources.LoadedResourceSha256);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReferenceAdoptionArchivesReviewButDoesNotGrantTargetAcceptance()
    {
        FbxModelAuthoringImportResult reference = CreateModel();
        reference = CharacterBodyRegionAuthoring.Apply(reference, Proposal(reference));
        FbxModelAuthoringImportResult target = CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: false);
        string targetSourceHash = Convert.ToHexStringLower(SHA256.HashData(target.Package.SourceFbx.AsSpan()));
        target = target with { Package = target.Package with { Document = target.Package.Document with
        { Source = target.Package.Document.Source with { ContentSha256 = targetSourceHash } } } };

        FbxModelAuthoringImportResult adopted = CharacterReferenceAuthoring.AdoptReference(target, reference);

        CharacterResourceInventory inventory = adopted.Package.Document.CharacterResources!;
        Assert.Empty(inventory.BodyRegionReviews);
        CharacterResourceRecord archive = Assert.Single(inventory.Resources, resource =>
            resource.LogicalName == "reference-body-region-reviews.json");
        Assert.True(archive.IsOriginalArchive);
        Assert.False(archive.Required);
        byte[] archived = adopted.Package.CompanionPayloads[archive.EntryPath!].ToArray();
        Assert.Contains("BodyResourceId", Encoding.UTF8.GetString(archived), StringComparison.Ordinal);
        Assert.Equal(target.Surfaces.Select(surface => surface.Id), adopted.Surfaces.Select(surface => surface.Id));
    }

    internal static CustomModelPackage CreateGenericBodyRegionPackage() =>
        ModelGeometryRevisionCodec.Capture(CreateModel()).Package;

    private static FbxModelAuthoringImportResult CreateModel(bool useChildHelper = false)
    {
        CustomModelPackage package = ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        FbxModelAuthoringImportResult source = FbxModelAuthoringImporter.ImportPackage(package);
        FbxModelSurface original = Assert.Single(source.Surfaces);
        FbxModelSurface cap = original with
        {
            Id = "cap/0", MeshName = "cap",
            SourceGeometry = new GeometrySourceComponent("cap", original.SourceGeometry!.ControlPoints),
        };
        CustomModelDocument document = package.Document with
        {
            Meshes = package.Document.Meshes.Add(package.Document.Meshes[0] with { Name = "cap" }),
            MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(package.Document.MorphChannels, [original, cap]),
        };
        source = source with { Surfaces = [original, cap], Package = package with { Document = document } };
        string helper = document.CreateEffectiveBones()[useChildHelper ? 1 : 0].Name;
        string body = $"""
            BodyElement(_PART, 1, 0, 0., 20., "{helper}")
            AddMesh2Disable("{original.MeshName}")
            AddMesh2DisableFromRelic("cap")
            AddRelics("Part", PHYSICS_SINGLE, "part.phx", "impact.fx", [0,0,0], [0,0,0])
            """;
        source = AddResource(source, "body", "data/characters/body.bel", body, CharacterSubsystem.Damage);
        source = AddResource(source, "detached", "data/parts/part.msh", "MeshPart()\n", CharacterSubsystem.Geometry);
        source = AddResource(source, "physics", "data/physics/part.phx", "PhysicsParams() { QuickStepNumIterations(12) }\n", CharacterSubsystem.Ragdoll);
        source = AddResource(source, "effect", "data/effects/impact.fx", "Effect()\n", CharacterSubsystem.Damage);
        return source;
    }

    private static ReviewedBodyRegionAssembly Proposal(FbxModelAuthoringImportResult model) =>
        new("body", 0, Record(model.Package, "body").ContentSha256!, ["cap/0"], ["detached"], ["physics"], ["effect"], true, true);

    private static FbxModelAuthoringImportResult AddResource(FbxModelAuthoringImportResult model, string id,
        string logicalName, string text, CharacterSubsystem subsystem)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string entryPath = $"character/resources/{id}.bin";
        CharacterResourceRecord resource = new()
        {
            Id = id, LogicalName = logicalName, ProviderIdentity = "synthetic-provider",
            SourceFingerprint = new string('a', 64), ContentSha256 = hash, EntryPath = entryPath,
            ByteLength = bytes.Length, Subsystem = subsystem, Status = CharacterDependencyStatus.Preserved,
        };
        CharacterResourceInventory inventory = model.Package.Document.CharacterResources!;
        CustomModelPackage package = model.Package with
        {
            Document = model.Package.Document with { CharacterResources = inventory with { Resources = inventory.Resources.Add(resource) } },
            CompanionPayloads = model.Package.CompanionPayloads.Add(entryPath, ImmutableArray.Create(bytes)),
        };
        return model with { Package = package };
    }

    private static FbxModelAuthoringImportResult ReplaceBody(FbxModelAuthoringImportResult model, string text)
    {
        CharacterResourceRecord before = Record(model.Package, "body");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        CharacterResourceInventory inventory = model.Package.Document.CharacterResources!;
        CustomModelPackage package = model.Package with
        {
            Document = model.Package.Document with { CharacterResources = inventory with
            {
                Resources = inventory.Resources.Select(resource => resource.Id == "body" ? resource with
                { ContentSha256 = hash, ByteLength = bytes.Length } : resource).ToImmutableArray(),
            } },
            CompanionPayloads = model.Package.CompanionPayloads.SetItem(before.EntryPath!, ImmutableArray.Create(bytes)),
        };
        return model with { Package = package };
    }

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(resource => resource.Id == id);

    private static byte[] Bytes(CustomModelPackage package, string id) =>
        package.CompanionPayloads[Record(package, id).EntryPath!].ToArray();

    private static string BodyText(FbxModelAuthoringImportResult model) => Encoding.UTF8.GetString(Bytes(model.Package, "body"));

    private sealed class PackageDialogs(string packagePath) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => packagePath;
    }
}
