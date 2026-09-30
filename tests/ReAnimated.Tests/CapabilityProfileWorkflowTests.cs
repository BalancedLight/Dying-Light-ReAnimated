using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class CapabilityProfileWorkflowTests
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static Dl1RigTemplate SyntheticTemplate() => new("synthetic-reference", "synthetic-resource", new string('a', 64),
    [
        new() { Index = 0, Name = "SyntheticRoot", ParentIndex = -1, Kind = BoneKind.Root, IsDeform = false,
            LocalRestMatrix = TransformMatrix.Identity, GlobalRestMatrix = TransformMatrix.Identity },
        new() { Index = 1, Name = "SyntheticChild", ParentIndex = 0, Kind = BoneKind.Deform, IsDeform = true,
            SemanticRole = "synthetic-anchor", LocalRestMatrix = TransformMatrix.Identity, GlobalRestMatrix = TransformMatrix.Identity },
    ]);
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    internal static RigCapabilityProfile Profile() => RigCapabilityProfileSerializer.Seal(new()
    {
        Identity = new() { Id = "generic-partial-profile", Version = "1", ContentSha256 = new string('0', 64) },
        FamilyId = "synthetic-biped",
        Roles =
        [
            new() { Id = "marker", NativeName = "normal_marker_2", OwnerAssetRoleId = "character", Category = RigRoleCategory.Structural,
                EntityKind = RigNativeEntityKind.Helper, Requirement = RigRoleRequirementKind.Required, SkinInfluenceAllowed = false },
            new() { Id = "body", NativeName = "Child", OwnerAssetRoleId = "character", Category = RigRoleCategory.Body,
                EntityKind = RigNativeEntityKind.Bone, Requirement = RigRoleRequirementKind.Required, SkinInfluenceAllowed = false },
            new() { Id = "other-family", Requirement = RigRoleRequirementKind.Required },
        ],
        Capabilities = [new() { Id = "partial", RoleIds = ["marker"] }, new() { Id = "body", RoleIds = ["body"], PrerequisiteCapabilityIds = ["partial"] },
            new() { Id = "other-family", RoleIds = ["other-family"] }],
        Consumers = [new() { ConsumerId = "unverified-test-consumer", CapabilityIds = ["partial", "body"], DiscoveredRoleIds = ["marker"], Unknowns = ["native-driver"] }],
    });

    [Fact]
    public void CanonicalIdentityRejectsTamperingAndUnknownFields()
    {
        var profile = Profile(); byte[] encoded = RigCapabilityProfileSerializer.Serialize(profile);
        using var document = JsonDocument.Parse(encoded);
        var pretty = JsonSerializer.SerializeToUtf8Bytes(document, PrettyJson);
        Assert.Equal(profile.Identity, RigCapabilityProfileSerializer.Deserialize(pretty).Identity);
        Assert.Throws<ArgumentException>(() => RigCapabilityProfileSerializer.Verify(profile with { FamilyId = "changed" }));
        Assert.Throws<ArgumentException>(() => RigCapabilityProfileSerializer.Deserialize(new byte[RigCapabilityProfileSerializer.MaximumBytes + 1]));
        string invalid = Encoding.UTF8.GetString(encoded).Replace("\"familyId\":", "\"unexpectedField\":1,\"familyId\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => RigCapabilityProfileSerializer.Deserialize(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ArgumentException>(() => RigCapabilityProfileSerializer.Verify(profile with { Roles = [null!] }));
    }

    [Fact]
    public void PartialRolesMissingAssignmentsAndActualWeightsAreReportedSeparately()
    {
        var source = StructuralHelperAuthoringTests.Source(); var recipe = source.Package.Document.RiggingSession!.Recipe;
        var partial = FbxCapabilityProfileAuthoring.Preview(source, Profile(), ["partial"], [], recipe.AssetRoles);
        Assert.Equal("Needs repair", partial.Review.Roles.Single(r => r.Id == "marker").Status);
        Assert.Equal("Not selected", partial.Review.Roles.Single(r => r.Id == "other-family").Status);
        Assert.Equal<string>(["partial"], partial.Review.EffectiveCapabilities);
        var body = FbxStructuralHelperAuthoring.Inspect(source).First(r => r.CanProtect && r.WeightedCorners > 0);
        var weighted = FbxCapabilityProfileAuthoring.Preview(source, Profile(), ["body"], [new("body", body.EntityId)], recipe.AssetRoles);
        Assert.Contains(weighted.Review.Diagnostics, d => d.Code == "role-weight-conflict" && d.EntityId == body.EntityId);
        Assert.Equal<string>(["body", "partial"], weighted.Review.EffectiveCapabilities);
        Assert.Contains(weighted.Review.Diagnostics, d => d.Code == "consumer-coverage-unverified");
        Assert.Equal<FbxModelSurface>(source.Surfaces, weighted.Candidate.Surfaces);
        Assert.Same(source.Rig, weighted.Candidate.Rig); Assert.Same(source.AnimationClips, weighted.Candidate.AnimationClips);
        Assert.False(FbxCapabilityProfileAuthoring.TryApply(source with { Rig = source.Rig }, weighted, out _));
        var previous = FbxCapabilityProfileAuthoring.Preview(source, Profile(), ["partial"], [new("previous-role", body.EntityId)], recipe.AssetRoles);
        Assert.Equal("Needs migration", previous.Review.Roles.Single(r => r.Id == "previous-role").Status);
        Assert.Contains(previous.Candidate.Package.Document.RiggingSession!.Recipe.Entities, e => e.EntityId == body.EntityId);
    }

    [Fact]
    public async Task MissingRequiredRolesBlockSourceExportWithoutCreatingOutput()
    {
        var source = StructuralHelperAuthoringTests.Source();
        var candidate = FbxCapabilityProfileAuthoring.Preview(source, Profile(), ["partial"], [], source.Package.Document.RiggingSession!.Recipe.AssetRoles).Candidate;
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string output = Path.Combine(directory, "output");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(new()
                { Model = candidate, OutputDirectory = output, ResourceName = "generic_profile_control" }));
            Assert.Contains("capability profile blocks export", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
            var legacy = candidate with { Package = candidate.Package with { Document = candidate.Package.Document with
                { RiggingSession = candidate.Package.Document.RiggingSession! with { Recipe = candidate.Package.Document.RiggingSession.Recipe with { ProfileSnapshot = null } } } } };
            Assert.Contains(FbxCapabilityProfileAuthoring.ValidateExport(legacy), d => d.Code == "profile-definition-unavailable" && d.Status == RigValidationStatus.Unverified);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void ProfileDefinitionSurvivesPackageReopenWithoutExternalFiles()
    {
        var source = StructuralHelperAuthoringTests.Source(); var recipe = source.Package.Document.RiggingSession!.Recipe;
        var node = recipe.Entities.Single(e => e.NativeName == "normal_marker_2");
        var candidate = FbxCapabilityProfileAuthoring.Preview(source, Profile(), ["partial"], [new("marker", node.EntityId)], recipe.AssetRoles).Candidate;
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "profile.dlrmodel"); CustomModelPackageSerializer.SaveAtomic(candidate.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(Profile().Identity, reopened.Package.Document.RiggingSession!.Recipe.ProfileSnapshot!.Identity);
            Assert.Equal("Needs evidence", FbxCapabilityProfileAuthoring.Inspect(reopened).Roles.Single(r => r.Id == "marker").Status);
            Assert.Contains(reopened.Package.Document.AuthoredHelpers, h => h.Name == "unknown_extra");
            var mismatched = reopened.Package.Document.RiggingSession.Recipe with { Profile = Profile().Identity with { Version = "2" } };
            Assert.Throws<ArgumentException>(() => mismatched.Validate());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task WorkspaceRequiresReviewPersistsChoicesAndUndoesOneTransaction()
    {
        var source = StructuralHelperAuthoringTests.Source();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")));
        workspace.CommitProjectRestore(new(source, "profile-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true; var editor = workspace.Conformance; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        editor.SetCapabilityProfileDraft(Profile());
        editor.ProfileCapabilities.Single(c => c.Id == "partial").Selected = true;
        editor.CapabilityRole = editor.CapabilityRoles.Single(r => r.Id == "marker");
        editor.CapabilityEntity = editor.CapabilityEntities.Single(e => e.NativeName == "normal_marker_2");
        editor.AssignCapabilityRoleCommand.Execute(null);
        await editor.PreviewCapabilityProfileCommand.ExecuteAsync(null);
        Assert.NotEmpty(editor.CapabilityReviewRows); Assert.False(editor.CanApplyCapabilityProfile);
        editor.CapabilityProfileReviewed = true;
        editor.StudioStage = RigStudioStage.VerifyAndExport; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.True(editor.CanApplyCapabilityProfile);
        editor.ApplyCapabilityProfileCommand.Execute(null);
        var saved = workspace.CaptureProjectSession().Model!;
        Assert.NotNull(saved.Package.Document.RiggingSession!.Recipe.ProfileSnapshot);
        Assert.Empty(saved.Package.Document.RiggingSession.ValidationHistory);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.ProfileSnapshot);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(Profile().Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
        Assert.Equal<FbxModelSurface>(source.Surfaces, saved.Surfaces);
    }

    [Fact]
    public async Task ChangingDraftOrSourceCancelsPendingReview()
    {
        var editor = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")), static _ => { });
        editor.SetModel(StructuralHelperAuthoringTests.Source()); editor.SetCapabilityProfileDraft(Profile());
        editor.ProfileCapabilities[0].Selected = true;
        Task job = editor.PreviewCapabilityProfileCommand.ExecuteAsync(null);
        editor.SetModel(null); await job;
        Assert.False(editor.IsBusy); Assert.Empty(editor.CapabilityReviewRows); Assert.False(editor.CanApplyCapabilityProfile);
        editor.SetModel(StructuralHelperAuthoringTests.Source()); editor.SetCapabilityProfileDraft(Profile()); editor.ProfileCapabilities[0].Selected = true;
        await editor.PreviewCapabilityProfileCommand.ExecuteAsync(null); editor.CapabilityProfileReviewed = true;
        editor.ProfileCapabilities[1].Selected = true;
        Assert.Empty(editor.CapabilityReviewRows); Assert.False(editor.CanApplyCapabilityProfile);
    }

    [Fact]
    public async Task ResolvedTemplateCreatesIncompleteObservedInventoryThroughReviewAndApply()
    {
        var template = SyntheticTemplate();
        var profile = ObservedRigCapabilityProfileStarter.Create(template);
        Assert.Equal(2, profile.Roles.Length);
        Assert.Equal("SyntheticChild", profile.Roles.Single(r => r.Id == "observed-anchor-synthetic-anchor").NativeName);
        Assert.Equal("observed-node-0000", profile.Roles.Single(r => r.NativeName == "SyntheticRoot").Id);
        Assert.Equal("observed-node-0000", profile.Roles.Single(r => r.NativeName == "SyntheticChild").ParentRoleId);
        Assert.Contains("source kind Deform", profile.Roles.Single(r => r.NativeName == "SyntheticChild").Evidence.Single().Description, StringComparison.Ordinal);
        Assert.Contains("semantic anchor 'synthetic-anchor'", profile.Roles.Single(r => r.NativeName == "SyntheticChild").Evidence.Single().Description, StringComparison.Ordinal);
        Assert.All(profile.Roles, role =>
        {
            Assert.Equal(RigRoleRequirementKind.Unknown, role.Requirement);
            Assert.Equal(RigRoleCategory.Unknown, role.Category);
            Assert.Null(role.SkinInfluenceAllowed);
            Assert.False(role.RulesComplete);
            Assert.Empty(role.Aliases);
            Assert.Empty(role.RequiredLods);
            Assert.Single(role.Evidence, evidence => evidence.Kind == RigEvidenceKind.ImportedSource && evidence.ArtifactSha256 == template.SourceFingerprint);
        });
        Assert.Null(profile.Identity.BuildFingerprint);
        Assert.Empty(profile.Consumers);
        Assert.False(profile.ConsumerCoverageComplete);

        var source = StructuralHelperAuthoringTests.Source();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("synthetic-reference", "Synthetic profile")));
        workspace.CommitProjectRestore(new(source, "profile-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var editor = workspace.Conformance;
        editor.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(template, template.ProfileName, template.SourceResourceName, template.SourceFingerprint, "synthetic")));
        editor.StudioStage = RigStudioStage.HelpersAndHooks;
        await editor.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        Assert.True(editor.CanCreateObservedNodeProfile);
        editor.CreateObservedNodeProfileCommand.Execute(null);
        Assert.True(editor.CanPreviewCapabilityProfile);
        Assert.Contains(template.SourceFingerprint, editor.CapabilityProfileProvenance, StringComparison.Ordinal);
        await editor.PreviewCapabilityProfileCommand.ExecuteAsync(null);
        Assert.NotEmpty(editor.CapabilityReviewRows);
        editor.CapabilityProfileReviewed = true;
        Assert.True(editor.CanApplyCapabilityProfile);
        editor.ApplyCapabilityProfileCommand.Execute(null);
        var saved = workspace.CaptureProjectSession().Model!;
        Assert.Equal(profile.Identity, saved.Package.Document.RiggingSession!.Recipe.ProfileSnapshot!.Identity);
        Assert.Empty(saved.Package.Document.RiggingSession.ValidationHistory);
    }
}
