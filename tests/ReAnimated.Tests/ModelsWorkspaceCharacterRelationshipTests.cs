using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceCharacterRelationshipTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task BodyRegionRelationshipEditIsScopedReviewedUndoableAndPersistent()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-character.dlrmodel");
            CustomModelPackage original = CreatePackage();
            CustomModelPackageSerializer.SaveAtomic(original, path);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            Assert.True(workspace.HasModel);
            workspace.CompanionFamily = CharacterCompanionFamily.BodyElements;
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "body");

            Assert.Equal(3, workspace.CompanionGroups.Count); // all settings and two body regions
            workspace.SelectedCompanionGroup = workspace.CompanionGroups[1];
            Assert.Contains("Detached parts", workspace.CompanionGroupDetail, StringComparison.Ordinal);
            Assert.Contains(workspace.CompanionRelationships, row => row.Role == "Body-region cut helper" &&
                row.Resolution.Contains("Bone found.", StringComparison.Ordinal));
            Assert.Contains(workspace.CompanionRelationships, row => row.Role == "Hide on original body" &&
                row.Resolution.Contains("Mesh found.", StringComparison.Ordinal));
            Assert.Contains(workspace.CompanionRelationships, row => row.Role == "Detached-part physics" &&
                row.Resolution.Contains("Resource found.", StringComparison.Ordinal));
            Assert.Contains(workspace.CompanionRelationships, row => row.Role == "Detached-part effect" &&
                row.Resolution.Contains("Resource found.", StringComparison.Ordinal));
            Assert.Contains(workspace.CompanionRelationships, row => row.Kind == NativeCharacterReferenceKind.MeshResource && row.ArgumentIndex == 0 &&
                row.Resolution.Contains("matching model resource", StringComparison.Ordinal));
            workspace.CompanionEditReviewed = true;
            workspace.CompanionNumericValue = 31.5;
            Assert.False(workspace.CompanionEditReviewed);
            workspace.SelectedCompanionGroup = workspace.CompanionGroups[2];
            Assert.Contains(workspace.CompanionRelationships, row => row.Role == "Hide on original body" &&
                row.Resolution.Contains("Missing mesh entity", StringComparison.Ordinal));
            workspace.SelectedCompanionGroup = workspace.CompanionGroups[1];

            CharacterCompanionRelationshipRow helper = Assert.Single(workspace.CompanionRelationships,
                row => row.Role == "Body-region cut helper");
            workspace.SelectedCompanionRelationship = helper;
            workspace.CompanionNewName = original.Document.CreateEffectiveBones()[1].Name;
            Assert.False(workspace.CompanionEditReviewed);
            workspace.CompanionEditReviewed = true;
            workspace.CompanionNewName = original.Document.CreateEffectiveBones()[0].Name;
            Assert.False(workspace.CompanionEditReviewed);
            workspace.ApplySelectedCompanionReferenceCommand.Execute(null);
            Assert.Equal(BodyText(original), BodyText(CurrentPackage(workspace)));
            Assert.Contains("Review", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);

            workspace.CompanionNewName = original.Document.CreateEffectiveBones()[1].Name;
            workspace.CompanionEditReviewed = true;
            workspace.ApplySelectedCompanionReferenceCommand.Execute(null);
            CustomModelPackage edited = CurrentPackage(workspace);
            Assert.NotEqual(BodyText(original), BodyText(edited));
            Assert.Contains($"BodyElement(_A, 1, 0, 0., 20., \"{workspace.CompanionNewName}\")", BodyText(edited), StringComparison.Ordinal);
            Assert.Contains($"BodyElement(_B, 1, 0, 0., 20., \"{original.Document.CreateEffectiveBones()[0].Name}\")", BodyText(edited), StringComparison.Ordinal);
            Assert.Equal(BodyText(original), BodyText(edited, "original:body"));
            Assert.True(Record(edited, "original:body").IsOriginalArchive);
            Assert.Null(edited.Document.CharacterResources!.CompiledSemanticSha256);
            Assert.Null(edited.Document.CharacterResources.LoadedResourceSha256);
            Assert.Empty(edited.Document.CharacterResources.VerifiedPlayerScenarios);
            Assert.Equal(original.SourceFbx.ToArray(), edited.SourceFbx.ToArray());

            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(BodyText(original), BodyText(CurrentPackage(workspace)));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Equal(BodyText(edited), BodyText(CurrentPackage(workspace)));

            workspace.SavePackageCommand.Execute(null);
            Assert.DoesNotContain("Save failed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            using var reopened = CreateWorkspace(path);
            await reopened.OpenPackagePathAsync(path);
            Assert.Equal(BodyText(edited), BodyText(CurrentPackage(reopened)));
            Assert.Equal(BodyText(original), BodyText(CurrentPackage(reopened), "original:body"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task PhysicsResolutionReportsAmbiguityAndReplacementChoicesKeepResourceType()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreatePackage(ambiguousPhysics: true), path);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            workspace.CompanionFamily = CharacterCompanionFamily.BodyElements;
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "body");
            workspace.SelectedCompanionGroup = workspace.CompanionGroups[1];

            CharacterCompanionRelationshipRow physics = Assert.Single(workspace.CompanionRelationships,
                row => row.Role == "Detached-part physics");
            Assert.Contains("Ambiguous retained resource identity", physics.Resolution, StringComparison.Ordinal);
            workspace.SelectedCompanionRelationship = physics;
            Assert.All(workspace.CompanionReplacementNames, name => Assert.EndsWith(".phx", name, StringComparison.Ordinal));
            Assert.DoesNotContain(workspace.CompanionReplacementNames, name => name.EndsWith(".fx", StringComparison.Ordinal));
            Assert.Contains("data/physics/alternate.phx", workspace.CompanionReplacementNames);
            workspace.CompanionNewName = "data/effects/impact.fx";
            workspace.CompanionEditReviewed = true;
            workspace.ApplySelectedCompanionReferenceCommand.Execute(null);
            Assert.Contains("replacement is absent", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentPackage(workspace).Document.CharacterResources!.Resources, row => row.IsOriginalArchive);

            CharacterCompanionRelationshipRow relicName = Assert.Single(workspace.CompanionRelationships,
                row => row.Kind == NativeCharacterReferenceKind.MeshResource && row.ArgumentIndex == 0);
            workspace.SelectedCompanionRelationship = relicName;
            workspace.CompanionNewName = "data/parts/alternate.msh";
            workspace.CompanionEditReviewed = true;
            workspace.ApplySelectedCompanionReferenceCommand.Execute(null);
            Assert.Contains("matching resource and naming rule", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentPackage(workspace).Document.CharacterResources!.Resources, row => row.IsOriginalArchive);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    internal static CustomModelPackage CreatePackage(bool ambiguousPhysics = false)
    {
        CustomModelPackage package = ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        string helper = package.Document.CreateEffectiveBones()[0].Name;
        string mesh = Assert.Single(FbxModelAuthoringImporter.ImportPackage(package).Surfaces).MeshName;
        string body = $"""
            BodyElement(_A, 1, 0, 0., 20., "{helper}")
            AddMesh2Disable("{mesh}")
            AddRelics("PartA", PHYSICS_SINGLE, "part.phx", "impact.fx", [0,0,0], [0,0,0])
            BodyElement(_B, 1, 0, 0., 20., "{helper}")
            AddMesh2Disable("missing_mesh")
            AddRelics("PartB", PHYSICS_SINGLE, "part.phx", "impact.fx", [0,0,0], [0,0,0])
            """;
        var inventory = package.Document.CharacterResources!;
        var resources = inventory.Resources.ToBuilder();
        var payloads = package.CompanionPayloads.ToBuilder();
        Add("body", "data/characters/body.bel", body, CharacterSubsystem.Damage);
        Add("physics", "data/physics/part.phx", "PhysicsParams() { QuickStepNumIterations(12) }\n", CharacterSubsystem.Ragdoll);
        Add("physics-alternate", "data/physics/alternate.phx", "PhysicsParams() { QuickStepNumIterations(16) }\n", CharacterSubsystem.Ragdoll);
        Add("effect", "data/effects/impact.fx", "Effect()\n", CharacterSubsystem.Damage);
        Add("mesh-part", "data/parts/alternate.msh", "MeshPart()\n", CharacterSubsystem.Geometry);
        if (ambiguousPhysics)
            Add("physics-duplicate", "data/other/part.phx", "PhysicsParams() { QuickStepNumIterations(20) }\n", CharacterSubsystem.Ragdoll);
        inventory = inventory with { Resources = resources.ToImmutable() };
        package = package with { Document = package.Document with { CharacterResources = inventory }, CompanionPayloads = payloads.ToImmutable() };
        package.Document.Validate();
        return package;

        void Add(string id, string logicalName, string text, CharacterSubsystem subsystem)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string entryPath = $"character/resources/{id}.bin";
            resources.Add(new CharacterResourceRecord
            {
                Id = id, LogicalName = logicalName, ProviderIdentity = "synthetic-provider",
                SourceFingerprint = new string('a', 64), ContentSha256 = hash, EntryPath = entryPath,
                ByteLength = bytes.Length, Subsystem = subsystem, Status = CharacterDependencyStatus.Preserved,
            });
            payloads.Add(entryPath, ImmutableArray.Create(bytes));
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AddedBoneHideRelationshipIsReviewedUndoableAndPersistent()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            var package = CreatePackage();
            CustomModelPackageSerializer.SaveAtomic(package, path);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            workspace.CompanionFamily = CharacterCompanionFamily.BodyElements;
            workspace.SelectedCompanion = workspace.CompanionCandidates.Single(resource => resource.Id == "body");
            workspace.SelectedCompanionGroup = workspace.CompanionGroups.Single(group => group.Name == "_A");
            string bone = package.Document.CreateEffectiveBones()[0].Name;
            Assert.Contains(bone, workspace.BodyHideEntityChoices);
            workspace.SelectedBodyHideEntity = bone;
            workspace.AddBodyHideEntityCommand.Execute(null);
            Assert.Equal(BodyText(package), BodyText(CurrentPackage(workspace)));
            workspace.CompanionEditReviewed = true;
            workspace.AddBodyHideEntityCommand.Execute(null);
            Assert.Contains("AddMesh2Disable(\"" + bone + "\")", BodyText(CurrentPackage(workspace)), StringComparison.Ordinal);
            Assert.Contains(workspace.CompanionRelationships, row => row.Name == bone && row.Resolution == "Bone found.");
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(BodyText(package), BodyText(CurrentPackage(workspace)));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Contains("AddMesh2Disable(\"" + bone + "\")", BodyText(CurrentPackage(workspace)), StringComparison.Ordinal);
            workspace.SavePackageCommand.Execute(null);
            var saved = CustomModelPackageSerializer.Load(path);
            Assert.Contains("AddMesh2Disable(\"" + bone + "\")", BodyText(saved), StringComparison.Ordinal);
            Assert.Equal(BodyText(package), BodyText(saved, "original:body"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(string path) =>
        new(new PackageDialogs(path), static _ => { }, static _ => Task.CompletedTask, static () => null);

    private static CustomModelPackage CurrentPackage(ModelsWorkspaceViewModel workspace) =>
        workspace.CaptureProjectSession().Model!.Package;

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(resource => resource.Id == id);

    private static string BodyText(CustomModelPackage package, string id = "body") =>
        Encoding.UTF8.GetString(package.CompanionPayloads[Record(package, id).EntryPath!].AsSpan());

    private sealed class PackageDialogs(string packagePath) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => packagePath;
    }
}
