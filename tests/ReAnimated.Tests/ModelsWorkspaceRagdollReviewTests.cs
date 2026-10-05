using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceRagdollReviewTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReviewedShapeControlChangesOnePhysicalDeclarationAndKeepsOriginalSourceUndoable()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-ragdoll-shape.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreatePackage(), path);
            using var workspace = new ModelsWorkspaceViewModel(new PackageDialogs(path),
                static _ => { }, static _ => Task.CompletedTask, static () => null);
            await workspace.OpenPackagePathAsync(path);
            workspace.ShowBodyElementsCommand.Execute(null);
            Assert.Equal(CharacterCompanionFamily.BodyElements, workspace.CompanionFamily);
            Assert.True(workspace.IsBodyElementsCompanionSelected);
            Assert.False(workspace.IsRagdollCompanionSelected);
            workspace.ShowRagdollCommand.Execute(null);
            Assert.Equal(CharacterCompanionFamily.Ragdoll, workspace.CompanionFamily);
            Assert.True(workspace.IsRagdollCompanionSelected);
            Assert.False(workspace.IsBodyElementsCompanionSelected);
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "ragdoll");
            string root = CurrentPackage(workspace).Document.Bones[0].Name;
            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.Name == root);
            Assert.Equal(["box", "capsule", "sphere"], workspace.RagdollSourceShapeTokens.ToArray());
            Assert.Equal("capsule", workspace.RagdollProposedShape);
            CustomModelPackage original = CurrentPackage(workspace);
            byte[] originalBytes = Bytes(original, "ragdoll");
            workspace.CompanionEditReviewed = true;
            workspace.RagdollProposedShape = "sphere";
            Assert.False(workspace.CompanionEditReviewed);
            workspace.ApplyRagdollShapeCommand.Execute(null);
            Assert.Contains("rejected", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Same(original, CurrentPackage(workspace));

            workspace.CompanionEditReviewed = true;
            workspace.ApplyRagdollShapeCommand.Execute(null);
            CustomModelPackage edited = CurrentPackage(workspace);
            string originalText = Encoding.UTF8.GetString(originalBytes);
            string editedText = Encoding.UTF8.GetString(Bytes(edited, "ragdoll"));
            Assert.Equal(originalText.Replace($"UseBoneScale(\"{root}\", \"capsule\", 2, 0.75)",
                $"UseBoneScale(\"{root}\", \"sphere\", 2, 0.75)", StringComparison.Ordinal), editedText);
            Assert.Contains($"UseBone(\"{original.Document.Bones[1].Name}\", \"sphere\", 3)", editedText, StringComparison.Ordinal);
            Assert.Equal(originalBytes, Bytes(edited, "original:ragdoll"));
            Assert.Equal(original.SourceFbx.ToArray(), edited.SourceFbx.ToArray());
            Assert.NotEqual(Record(original, "ragdoll").ContentSha256, Record(edited, "ragdoll").ContentSha256);
            Assert.Null(edited.Document.CharacterResources!.CompiledSemanticSha256);
            Assert.Null(edited.Document.CharacterResources.LoadedResourceSha256);
            Assert.Empty(edited.Document.CharacterResources.VerifiedPlayerScenarios);

            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(originalBytes, Bytes(CurrentPackage(workspace), "ragdoll"));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Equal(Bytes(edited, "ragdoll"), Bytes(CurrentPackage(workspace), "ragdoll"));
            Assert.Equal(originalBytes, Bytes(CurrentPackage(workspace), "original:ragdoll"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task PhysicalBoneGroupShowsExactRelationshipsAndSelectsUndoableBounds()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-ragdoll.dlrmodel");
            CustomModelPackage source = CreatePackage();
            CustomModelPackageSerializer.SaveAtomic(source, path);
            using var workspace = new ModelsWorkspaceViewModel(new PackageDialogs(path),
                static _ => { }, static _ => Task.CompletedTask, static () => null);
            await workspace.OpenPackagePathAsync(path);
            workspace.IsCharacterTabSelected = true;
            workspace.CompanionFamily = CharacterCompanionFamily.Ragdoll;
            workspace.SelectedCompanion = Assert.Single(workspace.CompanionCandidates, resource => resource.Id == "ragdoll");
            Assert.Equal(4, workspace.CompanionGroups.Count); // all settings and three physical declarations

            CustomModelPackage before = CurrentPackage(workspace);
            string root = before.Document.Bones[0].Name;
            string tip = before.Document.Bones[1].Name;
            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.Name == root);
            Assert.Equal(root, workspace.SelectedBone!.Name);
            Assert.True(workspace.ShowCharacterBounds);
            Assert.False(string.IsNullOrWhiteSpace(workspace.BoneBoundsText));
            Assert.True(workspace.Viewport.SceneSource.CaptureFrame().Gizmos.Count >= 15);
            Assert.Contains("Shape token: capsule", workspace.CompanionGroupDetail, StringComparison.Ordinal);
            Assert.Contains("relative mass: 2", workspace.CompanionGroupDetail, StringComparison.Ordinal);
            Assert.Contains("declared scale multiplier: 0.75", workspace.CompanionGroupDetail, StringComparison.Ordinal);
            Assert.Contains("Related joints: 1; collision settings: 1; synchronization settings: 1",
                workspace.CompanionGroupDetail, StringComparison.Ordinal);
            Assert.Equal(["UseBoneScale", "CollisionHelper", "BoneSynchro", "DefineJoint", "Set1DOFStops"],
                workspace.CompanionCalls.Select(call => call.Name).ToArray());
            Assert.DoesNotContain(workspace.CompanionCalls, call => call.Name is "!include" or "QuickStepNumIterations" or "NoCollidedPair");
            Assert.Equal(0, workspace.SelectedCompanionArgument!.Index);
            workspace.CompanionEditReviewed = true;
            workspace.SelectedCompanionCall = Assert.Single(workspace.CompanionCalls, call => call.Name == "CollisionHelper");
            Assert.Equal(0, workspace.SelectedCompanionArgument!.Index);
            Assert.False(workspace.CompanionEditReviewed);
            workspace.CompanionEditReviewed = true;
            workspace.SelectedCompanionArgument = workspace.CompanionArguments[1];
            Assert.False(workspace.CompanionEditReviewed);
            Assert.Same(before, CurrentPackage(workspace));
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));

            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.Name == root + "_extra");
            Assert.Null(workspace.SelectedBone);
            Assert.False(workspace.ShowCharacterBounds);
            Assert.Contains("no exact unique hierarchy match", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(workspace.CompanionCalls, call => call.Name == "CollisionHelper");
            Assert.Contains(workspace.CompanionCalls, call => call.Name == "NoCollidedPair");
            Assert.Same(before, CurrentPackage(workspace));

            workspace.SelectedCompanionGroup = Assert.Single(workspace.CompanionGroups, group => group.Name == root);
            Assert.Equal(root, workspace.SelectedBone!.Name);
            Dl1AuthoredBoneBounds originalBounds = Assert.IsType<Dl1AuthoredBoneBounds>(before.Document.Bones[0].LocalBounds);
            workspace.FitBoneBoundsCommand.Execute(null);
            Assert.Contains("Bounds fitted", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            workspace.ApplyBoneBoundsCommand.Execute(null);
            CustomModelPackage edited = CurrentPackage(workspace);
            Assert.NotEqual(originalBounds, edited.Document.Bones[0].LocalBounds);
            Assert.Equal(Bytes(before, "ragdoll"), Bytes(edited, "ragdoll"));
            Assert.Equal(before.SourceFbx.ToArray(), edited.SourceFbx.ToArray());
            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Equal(originalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Equal(edited.Document.Bones[0].LocalBounds, CurrentPackage(workspace).Document.Bones[0].LocalBounds);
            Assert.Equal(Bytes(before, "ragdoll"), Bytes(CurrentPackage(workspace), "ragdoll"));
            Assert.Contains(workspace.CompanionGroups, group => group.Name == tip);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static CustomModelPackage CreatePackage()
    {
        CustomModelPackage package = CharacterBodyRegionAuthoringTests.CreateGenericBodyRegionPackage();
        string root = package.Document.Bones[0].Name;
        string tip = package.Document.Bones[1].Name;
        string source = $$"""
            !include("generic_base.phx")
            PhysicsParams() { QuickStepNumIterations(12) }
            Bones() {
                UseBoneScale("{{root}}", "capsule", 2, 0.75)
                UseBone("{{tip}}", "sphere", 3)
                UseBone("{{root}}_extra", "capsule", 1)
                CollisionHelper("{{tip}}", "{{root}}", 1.25)
                NoCollidedPair("{{root}}_extra", "{{tip}}")
                BoneSynchro("{{root}}", "{{tip}}")
            }
            Joints() {
                DefineJoint("{{root}}", "{{tip}}", "hinge", 0, 1, 0, 0, 0, 0)
                Set1DOFStops("{{root}} {{tip}}", -0.1, 0.3, 0)
            }
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        const string entry = "character/resources/ragdoll.bin";
        CharacterResourceRecord resource = new()
        {
            Id = "ragdoll", LogicalName = "data/physics/generic_ragdoll.phx",
            ProviderIdentity = "synthetic-provider", SourceFingerprint = new string('a', 64),
            ContentSha256 = hash, EntryPath = entry, ByteLength = bytes.Length,
            Subsystem = CharacterSubsystem.Ragdoll, Status = CharacterDependencyStatus.Preserved,
        };
        var inventory = package.Document.CharacterResources!;
        var bones = package.Document.Bones.SetItem(0, package.Document.Bones[0] with
        {
            LocalBounds = new Dl1AuthoredBoneBounds(Vector3D.Zero, new Vector3D(.25, .25, .25)),
        });
        package = package with
        {
            Document = package.Document with
            {
                Bones = bones,
                RigSignature = CustomModelContractSignatures.ComputeRig(bones),
                CharacterResources = inventory with { Resources = inventory.Resources.Add(resource) },
            },
            CompanionPayloads = package.CompanionPayloads.Add(entry, ImmutableArray.Create(bytes)),
        };
        package.Document.Validate();
        return package;
    }

    private static CustomModelPackage CurrentPackage(ModelsWorkspaceViewModel workspace) =>
        workspace.CaptureProjectSession().Model!.Package;

    private static byte[] Bytes(CustomModelPackage package, string id)
    {
        CharacterResourceRecord record = package.Document.CharacterResources!.Resources.Single(resource => resource.Id == id);
        return package.CompanionPayloads[record.EntryPath!].ToArray();
    }

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(resource => resource.Id == id);

    private sealed class PackageDialogs(string packagePath) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => packagePath;
    }
}
