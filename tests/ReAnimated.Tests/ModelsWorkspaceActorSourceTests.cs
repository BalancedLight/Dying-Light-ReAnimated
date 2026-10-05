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

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceActorSourceTests
{
    private const string ActorId = "vf:actors/definition.scr";
    private const string ActorScript = "Actor(\"one\") { Model(\"characters/body.msh\"); PhysicsScript(\"one.phx\"); " +
        "UnknownResource(\"lead.fed\"); } Actor(\"two\") { Model(\"characters/body.msh\"); PhysicsScript(\"two.phx\"); }";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ActorSourceReviewRequiresSelectionAndReviewThenSurvivesUndoRedoAndReopen()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string packagePath = Path.Combine(directory, "generic-stock.dlrmodel");
            CustomModelPackage original = CreateGenericStockPackage();
            CustomModelPackageSerializer.SaveAtomic(original, packagePath);
            var dialogs = new ActorReviewDialogs(packagePath);
            using var workspace = CreateWorkspace(dialogs);
            await workspace.OpenPackagePathAsync(packagePath);
            Assert.True(workspace.HasModel);
            Assert.Equal(ActorId, Assert.Single(workspace.ActorSourceChoices).Id);
            Assert.Equal(2, workspace.ActorDeclarationChoices.Count);
            Assert.Equal(1, workspace.SelectedActorDeclaration!.CallIndex);
            Assert.Equal(0, workspace.SelectedActorScope!.CallIndex);

            ActorSourceReferenceRow reviewed = Assert.Single(workspace.ActorSourceReferences, r => r.Resource == "one.phx");
            ActorSourceReferenceRow lead = Assert.Single(workspace.ActorSourceReferences, r => r.Resource == "two.phx");
            Assert.True(reviewed.CanReview);
            Assert.False(lead.CanReview);
            lead.IsReviewed = true;
            Assert.False(lead.IsReviewed);
            reviewed.IsReviewed = true;
            workspace.RecordActorSourceReviewCommand.Execute(null);
            Assert.Empty(workspace.ActorSourceReceipts);
            Assert.Contains("review", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);

            workspace.ActorSourceReviewed = true;
            workspace.SelectedActorDeclaration = workspace.ActorDeclarationChoices[1];
            Assert.False(workspace.ActorSourceReviewed);
            Assert.DoesNotContain(workspace.ActorSourceReferences, r => r.IsReviewed);
            workspace.SelectedActorDeclaration = workspace.ActorDeclarationChoices[0];
            workspace.ActorSourceReviewed = true;
            workspace.SelectedActorScope = workspace.ActorScopeChoices[0];
            Assert.False(workspace.ActorSourceReviewed);
            workspace.SelectedActorScope = workspace.ActorScopeChoices[1];
            workspace.SelectedActorSource = null;
            Assert.False(workspace.ActorSourceReviewed);
            Assert.Empty(workspace.ActorSourceReferences);
            workspace.SelectedActorSource = Assert.Single(workspace.ActorSourceChoices);
            Assert.False(workspace.ActorSourceReviewed);

            reviewed = Assert.Single(workspace.ActorSourceReferences, r => r.Resource == "one.phx");
            reviewed.IsReviewed = true;
            workspace.ActorSourceReviewed = true;
            workspace.RecordActorSourceReviewCommand.Execute(null);
            CharacterActorSourceReceipt receipt = Assert.Single(workspace.ActorSourceReceipts);
            Assert.Equal("characters/body.msh", receipt.ExactModelName);
            CustomModelPackage accepted = CurrentPackage(workspace);
            Assert.Equal(CharacterDependencyStatus.Preserved,
                Assert.Single(accepted.Document.CharacterResources!.Resources, r => r.Id == ActorId).Status);
            Assert.Equal(CharacterDependencyStatus.Ambiguous,
                Assert.Single(accepted.Document.CharacterResources.Resources, r => r.Id == "vf:actors/unrelated.phx").Status);
            Assert.Equal(original.SourceFbx.ToArray(), accepted.SourceFbx.ToArray());
            Assert.Equal(BoneSemantics(original), BoneSemantics(accepted));
            Assert.Equal(MeshSemantics(original), MeshSemantics(accepted));
            Assert.Equal(original.Document.RigSignature, accepted.Document.RigSignature);
            Assert.Equal(original.Document.MorphSignature, accepted.Document.MorphSignature);
            Assert.Equal(original.CompanionPayloads["character/resources/actor.bin"].ToArray(),
                accepted.CompanionPayloads["character/resources/actor.bin"].ToArray());

            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Empty(CurrentPackage(workspace).Document.CharacterResources!.ActorSourceReviews);
            Assert.Equal(CharacterDependencyStatus.Ambiguous,
                Assert.Single(CurrentPackage(workspace).Document.CharacterResources!.Resources, r => r.Id == ActorId).Status);
            Assert.True(workspace.RedoHelperEditCommand.CanExecute(null));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Single(CurrentPackage(workspace).Document.CharacterResources!.ActorSourceReviews);

            workspace.SavePackageCommand.Execute(null);
            Assert.DoesNotContain("Save failed", workspace.BuildStatus, StringComparison.Ordinal);
            using var reopened = CreateWorkspace(dialogs);
            await reopened.OpenPackagePathAsync(packagePath);
            Assert.Single(reopened.ActorSourceReceipts);
            CharacterActorSourceAuthoring.RevalidateAll(CurrentPackage(reopened));
            Assert.Equal(BoneSemantics(original), BoneSemantics(CurrentPackage(reopened)));
            Assert.Equal(MeshSemantics(original), MeshSemantics(CurrentPackage(reopened)));
            Assert.Equal(original.SourceFbx.ToArray(), CurrentPackage(reopened).SourceFbx.ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task StaleDependencyResolverRefusalKeepsLoadedModelAndSourceIntact()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-stock.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericStockPackage(), path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            CustomModelPackage before = CurrentPackage(workspace);
            workspace.SetCharacterDependencyResolver((_, _) =>
                throw new InvalidDataException("stale exact source"));
            workspace.CharacterCompanionRoots = ActorId;

            await workspace.ResolveCharacterDependenciesCommand.ExecuteAsync(null);

            Assert.Contains("Dependency resolution failed: stale exact source", workspace.BuildStatus,
                StringComparison.Ordinal);
            CustomModelPackage after = CurrentPackage(workspace);
            Assert.Same(before, after);
            Assert.Equal(before.SourceFbx.ToArray(), after.SourceFbx.ToArray());
            Assert.Equal(BoneSemantics(before), BoneSemantics(after));
            Assert.Equal(MeshSemantics(before), MeshSemantics(after));
            Assert.Equal(before.CompanionPayloads["character/resources/actor.bin"].ToArray(),
                after.CompanionPayloads["character/resources/actor.bin"].ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task DependencyResolutionRejectsInterveningBoundsEditWithoutAddingUndoHistory()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericStockPackage(), path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            CustomModelPackage original = CurrentPackage(workspace);
            var completion = new TaskCompletionSource<FbxModelAuthoringImportResult>();
            workspace.SetCharacterDependencyResolver((_, _) => completion.Task);

            Task resolution = workspace.ResolveCharacterDependenciesCommand.ExecuteAsync(null);
            Assert.False(resolution.IsCompleted);
            workspace.SelectedBone = workspace.Bones[0];
            workspace.BoneBoundsText = "0 0 0 2 2 2";
            workspace.ApplyBoneBoundsCommand.Execute(null);
            CustomModelPackage edited = CurrentPackage(workspace);
            Assert.NotEqual(original.Document.Bones[0].LocalBounds, edited.Document.Bones[0].LocalBounds);

            completion.SetResult(FbxModelAuthoringImporter.ImportPackage(original));
            await resolution;

            Assert.Same(edited, CurrentPackage(workspace));
            Assert.Contains("changed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(original.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Equal(edited.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task SuccessfulDependencyResolutionUndoKeepsEarlierBoundsEdit()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            CustomModelPackage original = CreateGenericStockPackage();
            CustomModelPackageSerializer.SaveAtomic(original, path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            workspace.SelectedBone = workspace.Bones[0];
            workspace.BoneBoundsText = "0 0 0 2 2 2";
            workspace.ApplyBoneBoundsCommand.Execute(null);
            CustomModelPackage edited = CurrentPackage(workspace);
            var inventory = original.Document.CharacterResources!;
            CustomModelPackage refreshed = original with
            {
                Document = original.Document with
                {
                    CharacterResources = inventory with
                    {
                        Resources = inventory.Resources.Select(resource => resource.Id == ActorId
                            ? resource with { Status = CharacterDependencyStatus.Preserved } : resource).ToImmutableArray(),
                    },
                },
            };
            var completion = new TaskCompletionSource<FbxModelAuthoringImportResult>();
            workspace.SetCharacterDependencyResolver((_, _) => completion.Task);
            workspace.CharacterCompanionRoots = ActorId;

            Task resolution = workspace.ResolveCharacterDependenciesCommand.ExecuteAsync(null);
            completion.SetResult(FbxModelAuthoringImporter.ImportPackage(refreshed));
            await resolution;

            Assert.Equal("Dependencies updated.", workspace.BuildStatus);
            Assert.Equal(CharacterDependencyStatus.Preserved,
                Assert.Single(CurrentPackage(workspace).Document.CharacterResources!.Resources, resource => resource.Id == ActorId).Status);
            Assert.Equal(edited.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(CharacterDependencyStatus.Ambiguous,
                Assert.Single(CurrentPackage(workspace).Document.CharacterResources!.Resources, resource => resource.Id == ActorId).Status);
            Assert.Equal(edited.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(original.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task DependencyResolutionRejectsRevisionChangeWithoutReplacingTheModel()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericStockPackage(), path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            var original = workspace.CaptureProjectSession().Model!;
            var completion = new TaskCompletionSource<FbxModelAuthoringImportResult>();
            workspace.SetCharacterDependencyResolver((_, _) => completion.Task);

            Task resolution = workspace.ResolveCharacterDependenciesCommand.ExecuteAsync(null);
            workspace.ShowMeshes = !workspace.ShowMeshes;
            bool showMeshes = workspace.ShowMeshes;
            Assert.Same(original, workspace.CaptureProjectSession().Model);
            completion.SetResult(original);
            await resolution;

            Assert.Same(original, workspace.CaptureProjectSession().Model);
            Assert.Equal(showMeshes, workspace.ShowMeshes);
            Assert.Contains("changed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task DependencyResolutionRejectsReloadedOrClearedModel(bool clear)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericStockPackage(), path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            var original = workspace.CaptureProjectSession().Model!;
            var completion = new TaskCompletionSource<FbxModelAuthoringImportResult>();
            workspace.SetCharacterDependencyResolver((_, _) => completion.Task);

            Task resolution = workspace.ResolveCharacterDependenciesCommand.ExecuteAsync(null);
            if (clear) workspace.ClearProjectSession();
            else await workspace.OpenPackagePathAsync(path);
            var current = workspace.CaptureProjectSession().Model;
            Assert.NotSame(original, current);
            completion.SetResult(original);
            await resolution;

            Assert.Same(current, workspace.CaptureProjectSession().Model);
            Assert.Equal(!clear, workspace.HasModel);
            Assert.Contains("changed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(ActorReviewDialogs dialogs) =>
        new(dialogs, static _ => { }, static _ => Task.CompletedTask, static () => null);

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task PresetSourceChoicesInspectAndSaveTheExactMeshValueToken()
    {
        const string script = "PresetDef(\"Character\") { Preset(\"one\") { SetField(\"MeshName\", \"characters/body.msh\"); " +
            "SetField(\"PhysicsScript\", \"one.phx\"); SetField(\"UnknownResource\", \"lead.fed\"); } " +
            "Preset(\"other\") { SetField(\"MeshName\", \"characters/body.msh\"); SetField(\"PhysicsScript\", \"other.phx\"); } Preset(\"unrelated\") { SetField(\"MeshName\", \"characters/unrelated.msh\"); } }";
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package = CreateGenericStockPackage();
            const string entry = "character/resources/actor.bin";
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            var inventory = package.Document.CharacterResources!;
            package = package with
            {
                CompanionPayloads = package.CompanionPayloads.SetItem(entry, ImmutableArray.Create(bytes)),
                Document = package.Document with { CharacterResources = inventory with
                {
                    Resources = inventory.Resources.Select(record => record.Id == ActorId ? record with
                    {
                        LogicalName = "actors/definition.pre",
                        ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        ByteLength = bytes.Length,
                    } : record).ToImmutableArray(),
                } },
            };
            string path = Path.Combine(directory, "preset-stock.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, path);
            using var workspace = CreateWorkspace(new ActorReviewDialogs(path));
            await workspace.OpenPackagePathAsync(path);
            Assert.Equal("actors/definition.pre", Assert.Single(workspace.ActorSourceChoices).LogicalName);
            Assert.Equal(2, workspace.ActorDeclarationChoices.Count);
            Assert.Null(workspace.SelectedActorDeclaration);
            workspace.SelectedActorDeclaration = workspace.ActorDeclarationChoices.Single(choice => choice.CallIndex == 2);
            Assert.Equal(1, Assert.Single(workspace.ActorScopeChoices).CallIndex);
            var reference = Assert.Single(workspace.ActorSourceReferences, row => row.Resource == "one.phx");
            Assert.True(reference.CanReview);
            Assert.False(Assert.Single(workspace.ActorSourceReferences, row => row.Resource == "other.phx").CanReview);
            reference.IsReviewed = true;
            workspace.ActorSourceReviewed = true;
            workspace.RecordActorSourceReviewCommand.Execute(null);
            var receipt = Assert.Single(workspace.ActorSourceReceipts);
            Assert.Equal(1, receipt.ModelArgumentIndex);
            Assert.Equal("characters/body.msh", receipt.ExactModelName);
            Assert.Equal("\"characters/body.msh\"", script.Substring(receipt.ModelTokenStart, receipt.ModelTokenLength));
            Assert.Equal(bytes, CurrentPackage(workspace).CompanionPayloads[entry].ToArray());
            Assert.False(CurrentPackage(workspace).Document.CharacterResources!.IsGameReady);
            workspace.SavePackageCommand.Execute(null);
            CharacterActorSourceAuthoring.RevalidateAll(CustomModelPackageSerializer.Load(path));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static CustomModelPackage CurrentPackage(ModelsWorkspaceViewModel workspace) =>
        workspace.CaptureProjectSession().Model!.Package;

    private static (string Name, int Index, int Parent, TransformMatrix Matrix, Dl1AuthoredBoneBounds? Bounds)[] BoneSemantics(
        CustomModelPackage package) => package.Document.Bones.Select(b =>
            (b.Name, b.Index, b.ParentIndex, b.ExactLocalBindMatrix, b.LocalBounds)).ToArray();

    private static (string Name, int ControlPoints, int Triangles, int Vertices, int Materials)[] MeshSemantics(
        CustomModelPackage package) => package.Document.Meshes.Select(m =>
            (m.Name, m.ControlPointCount, m.TriangleCount, m.ExpandedVertexCount, m.MaterialSlotCount)).ToArray();

    /// <summary>Generic stock package for private, source-blind workflow harnesses.</summary>
    internal static CustomModelPackage CreateGenericStockPackage()
    {
        FbxModelAuthoringImportResult baseline = CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: false);
        byte[] sourceBytes = baseline.Package.SourceFbx.ToArray();
        byte[] actorBytes = Encoding.UTF8.GetBytes(ActorScript);
        byte[] unrelatedBytes = Encoding.UTF8.GetBytes("unreviewed source");
        CharacterResourceRecord[] resources =
        [
            Record("root", "characters/body.msh", "character/resources/root.bin", sourceBytes, CharacterDependencyStatus.Preserved),
            Record(ActorId, "actors/definition.scr", "character/resources/actor.bin", actorBytes, CharacterDependencyStatus.Ambiguous),
            Record("vf:actors/unrelated.phx", "actors/unrelated.phx", "character/resources/unrelated.bin",
                unrelatedBytes, CharacterDependencyStatus.Ambiguous),
        ];
        var inventory = new CharacterResourceInventory
        {
            RootResourceId = "root", DecodedSha256 = new string('0', 64), DecodedByteLength = 1,
            Resources = resources.ToImmutableArray(),
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(s =>
                new CharacterSubsystemReview(s, CharacterDependencyStatus.Missing, "Source review pending.")).ToImmutableArray(),
        };
        CustomModelDocument document = baseline.Package.Document with
        {
            Source = baseline.Package.Document.Source with
            {
                Kind = CustomModelSourceKind.StockCharacter, OriginalFileName = "characters/body.skn",
                ContentSha256 = Sha(sourceBytes), EmbeddedEntryPath = "source/character.bin",
            },
            CharacterResources = inventory,
        };
        FbxModelSurface surface = baseline.Surfaces.Single();
        FbxModelSurface retained = surface with
        {
            SourceGeometry = new GeometrySourceComponent("surface", surface.Vertices.Select(v => v.Position).ToImmutableArray()),
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2)],
            SourceTriangles = [new(0, 0)],
        };
        document = document with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(document.Bones),
            MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(document.MorphChannels, [retained]),
        };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(document, [retained], []));
        inventory = inventory with { DecodedSha256 = Sha(decoded.AsSpan()), DecodedByteLength = decoded.Length };
        document = document with { CharacterResources = inventory };
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        payloads.Add(resources[0].EntryPath!, ImmutableArray.Create(sourceBytes));
        payloads.Add(resources[1].EntryPath!, ImmutableArray.Create(actorBytes));
        payloads.Add(resources[2].EntryPath!, ImmutableArray.Create(unrelatedBytes));
        CustomModelPackage package = baseline.Package with
        {
            Document = document, DecodedCharacterPayload = decoded, CompanionPayloads = payloads.ToImmutable(),
        };
        document.Validate();
        return package;
    }

    private static CharacterResourceRecord Record(string id, string name, string path, byte[] payload,
        CharacterDependencyStatus status) => new()
    {
        Id = id, LogicalName = name, ProviderIdentity = "synthetic-provider", SourceFingerprint = new string('a', 64),
        ContentSha256 = Sha(payload), EntryPath = path, ByteLength = payload.Length,
        Subsystem = CharacterSubsystem.Damage, Status = status, ReferencedBy = ["root"],
    };

    private static string Sha(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));
    private static string Sha(ReadOnlySpan<byte> payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    private sealed class ActorReviewDialogs(string packagePath) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => packagePath;
    }
}
