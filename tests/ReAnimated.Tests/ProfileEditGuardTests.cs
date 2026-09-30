using System.Collections.Immutable;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProfileEditGuardTests
{
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    internal static FbxModelAuthoringImportResult Model(RigHelperEditFields allowed = RigHelperEditFields.None, string name = "normal_marker_2", bool allowRemoval = false)
    {
        var source = StructuralHelperAuthoringTests.Source(); var doc = source.Package.Document; var session = doc.RiggingSession!;
        var entity = session.Recipe.Entities.Single(e => e.NativeName == name);
        var profile = RigCapabilityProfileSerializer.Seal(new()
        {
            Identity = new() { Id = "generic-edit-profile", Version = "1", ContentSha256 = new string('0', 64) }, FamilyId = "synthetic",
            Roles = [new() { Id = "selected", NativeName = name, OwnerAssetRoleId = "character", EntityKind = entity.Kind, AllowedEdits = allowed,
                Requirement = RigRoleRequirementKind.Optional, ValidationRules = new() { Edits = new() { AllowRemoval = allowRemoval } } }],
            Capabilities = [new() { Id = "authoring", RoleIds = ["selected"] }],
        });
        doc = doc with { RiggingSession = session with { Recipe = session.Recipe with
            { Profile = profile.Identity, ProfileSnapshot = profile, SelectedCapabilityIds = ["authoring"], Assignments = [new("selected", entity.EntityId)] } } };
        return source with { Package = source.Package with { Document = doc } };
    }
    private static Guid Selected(CustomModelDocument doc) => doc.RiggingSession!.Recipe.Assignments.Single(a => a.RoleId == "selected").EntityId;

    [Fact]
    public void HelpersAndWeightedRestEditsHonorTheDeclaredFields()
    {
        var model = Model(); var doc = model.Package.Document; Guid id = Selected(doc);
        Assert.Throws<RigProfileEditException>(() => CustomModelHelperAuthoring.SetLocalTransform(doc, id, new(new(.25, .3, .4), QuaternionD.Identity, Vector3D.One)));
        Assert.Throws<RigProfileEditException>(() => FbxStructuralHelperAuthoring.PreviewOffset(model, id, new(new(.05, 0, 0), QuaternionD.Identity, Vector3D.One)));
        var allowed = Model(RigHelperEditFields.All);
        var moved = FbxStructuralHelperAuthoring.PreviewOffset(allowed, Selected(allowed.Package.Document), new(new(.05, 0, 0), QuaternionD.Identity, Vector3D.One));
        Assert.True(moved.HasChanges);
        var weighted = Model(name: "Child"); var row = FbxStructuralHelperAuthoring.Inspect(weighted).Single(r => r.Name == "Child");
        var global = weighted.Rig!.CreateBindPose().GlobalMatrices[row.SourceIndex];
        Assert.Throws<RigProfileEditException>(() => RigRestPoseAuthoring.Apply(weighted.Package.Document, weighted.Package.Document.RiggingSession!.CreateJobToken(),
            row.EntityId, TransformMatrix.CreateTranslation(new(.1, 0, 0)) * global, RigRestDescendantMode.KeepGlobal));
    }

    [Fact]
    public void ClearingAssignmentsOrProfileCannotSmuggleAProtectedRename()
    {
        var model = Model(); var session = model.Package.Document.RiggingSession!; Guid id = Selected(model.Package.Document);
        var replacement = session with { Recipe = session.Recipe with { Profile = null, ProfileSnapshot = null, Assignments = [], SelectedCapabilityIds = [],
            Entities = session.Recipe.Entities.Select(e => e.EntityId == id ? e with { NativeName = "renamed_marker" } : e).ToImmutableArray() } };
        var error = Assert.Throws<RigProfileEditException>(() => RiggingSessions.Change(session, replacement, RiggingEditKind.Profile));
        Assert.Throws<RigProfileEditException>(() => RiggingSessions.Change(session, replacement, RiggingEditKind.Profile, allowLockedChanges: true));
        Assert.Contains(error.Diagnostics, d => d.RoleId == "selected" && d.EntityId == id && d.Message.Contains("Name", StringComparison.Ordinal));
        var onlyProfile = replacement with { Recipe = replacement.Recipe with { Entities = session.Recipe.Entities } };
        var changed = RiggingSessions.Change(session, onlyProfile, RiggingEditKind.Profile);
        Assert.Null(changed.Recipe.ProfileSnapshot);
    }

    [Fact]
    public void ChannelsAreProtectedButEvidenceCanBeEnriched()
    {
        var model = Model(); var session = model.Package.Document.RiggingSession!; Guid id = Selected(model.Package.Document);
        var policy = new AnimationComponentPolicy { EntityId = id, EmittedMask = RigAnimationComponents.None, AnimationLod = RigAnimationLod.Off,
            Position = new() { Owners = [RigComponentOwner.BindInherited] }, Rotation = new() { Owners = [RigComponentOwner.BindInherited] }, Scale = new() { Owners = [RigComponentOwner.BindInherited] } };
        Assert.Throws<RigProfileEditException>(() => RiggingSessions.Change(session, session with { Recipe = session.Recipe with { ComponentPolicies = [policy] } }, RiggingEditKind.Helpers));
        var current = session with { Recipe = session.Recipe with { ComponentPolicies = [policy] } };
        var evidence = new RigEvidenceReference { Id = "generic-observation", Kind = RigEvidenceKind.UserOverride, Description = "Additional review context" };
        var annotated = policy with { Position = policy.Position with { Evidence = [evidence] } };
        var updated = RiggingSessions.Change(current, current with { Recipe = current.Recipe with { ComponentPolicies = [annotated] } }, RiggingEditKind.Helpers);
        Assert.Single(updated.Recipe.ComponentPolicies[0].Position.Evidence);
    }

    [Fact]
    public void ReparentingAndRemovalAreCheckedAgainstThePreviousProfile()
    {
        var model = Model(); var doc = model.Package.Document;
        var updated = doc with { AuthoredHelpers = doc.AuthoredHelpers.SetItem(0, doc.AuthoredHelpers[0] with { ParentNodeIndex = 1 }) };
        updated = updated with { RigSignature = CustomModelContractSignatures.ComputeRig(updated.CreateEffectiveBones()) };
        Assert.Throws<RigProfileEditException>(() => RigProfileEditGuard.RequireDocumentAllowed(doc, updated));
        foreach (bool allowed in new[] { false, true })
        {
            var leaf = Model(name: "unknown_extra", allowRemoval: allowed); var old = leaf.Package.Document; var session = old.RiggingSession!; Guid id = Selected(old);
            var recipe = session.Recipe with { Entities = session.Recipe.Entities.Where(e => e.EntityId != id).ToImmutableArray(), Assignments = [],
                FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != id).ToImmutableArray() };
            var next = old with { AuthoredHelpers = old.AuthoredHelpers.RemoveAt(1), RiggingSession = session with { Recipe = recipe } };
            next = next with { RigSignature = CustomModelContractSignatures.ComputeRig(next.CreateEffectiveBones()) };
            if (allowed) RigProfileEditGuard.RequireDocumentAllowed(old, next);
            else Assert.Throws<RigProfileEditException>(() => RigProfileEditGuard.RequireDocumentAllowed(old, next));
        }
    }

    [Fact]
    public void IndirectPreparedBoundsChangesAreRejectedAndReportedWithoutCommit()
    {
        var model = Model(RigHelperEditFields.All & ~RigHelperEditFields.Extents); var doc = model.Package.Document;
        var child = doc.AuthoredHelpers.Single(h => h.Name == "unknown_extra");
        var changed = CustomModelHelperAuthoring.SetLocalTransform(doc, child.Id, new(new(.4, 0, 0), QuaternionD.Identity, Vector3D.One));
        var candidate = model with { Package = model.Package with { Document = changed }, Rig = changed.CreateRigDefinition() };
        // The protected parent's stored fields are unchanged; only its derived bounds differ.
        RigProfileEditGuard.RequireDocumentAllowed(doc, changed);
        var error = Assert.Throws<RigProfileEditException>(() => FbxProfileEditGuard.RequireAllowed(model, candidate));
        Assert.Contains(error.Diagnostics, d => d.Message.Contains("prepared output", StringComparison.Ordinal) && d.Message.Contains("Extents", StringComparison.Ordinal));
        var editor = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")), static _ => { });
        editor.SetModel(model); bool committed = false;
        EventHandler<BodyModelEventArgs> handler = (_, _) => committed = true;
        bool accepted = (bool)typeof(RigConformanceWizardViewModel).GetMethod("RequestBodyChange", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [handler, new BodyModelEventArgs(model, candidate, "Attempted edit")])!;
        Assert.False(accepted); Assert.False(committed); Assert.True(editor.HasProfileEditRefusal);
        Assert.Contains("Change not applied", editor.ProfileEditStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyHelperFieldsCannotBypassPreparedProtectionAndOpeningRemainsSeparate()
    {
        var source = Model(RigHelperEditFields.All & ~RigHelperEditFields.Extents);
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")));
        workspace.CommitProjectRestore(new(source, "guard-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.SelectedBone = workspace.Bones.Single(b => b.Name == "unknown_extra");
        var before = workspace.CaptureProjectSession().Model!;
        workspace.HelperTranslationX = .4;
        workspace.ApplyHelperTransformCommand.Execute(null);
        Assert.Contains("protected Extents", workspace.BuildStatus, StringComparison.Ordinal);
        var after = workspace.CaptureProjectSession().Model!;
        Assert.Equal<CustomModelAuthoredHelper>(before.Package.Document.AuthoredHelpers, after.Package.Document.AuthoredHelpers);
        Assert.Equal<CustomModelBone>(before.Package.Document.Bones, after.Package.Document.Bones);
        Assert.Equal(before.Package.Document.RiggingSession!.ComputeInputFingerprint(), after.Package.Document.RiggingSession!.ComputeInputFingerprint());
        Assert.Equal<FbxModelSurface>(before.Surfaces, after.Surfaces);
        Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        var opened = Model(RigHelperEditFields.All, "Child");
        workspace.CommitProjectRestore(new(opened, "another-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.Equal(opened.Package.Document.ModelId, workspace.CaptureProjectSession().Model!.Package.Document.ModelId);
    }

    [Fact]
    public async Task PermissionControlsCreateReviewedLocalDraftWithoutRelaxingNativeChecks()
    {
        var source = CapabilityRoleRuleTests.Model(); var originalProfile = source.Package.Document.RiggingSession!.Recipe.ProfileSnapshot!;
        var originalRole = originalProfile.Roles.Single(r => r.Id == "marker");
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")));
        workspace.CommitProjectRestore(new(source, "permission-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var editor = workspace.Conformance;
        editor.CapabilityRole = editor.CapabilityRoles.Single(r => r.Id == "marker");
        editor.EnforceRoleEditPermissions = true; editor.PermitRolePosition = true;
        Assert.Equal(originalProfile.Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
        Assert.False(editor.CanApplyCapabilityProfile);
        await editor.PreviewCapabilityProfileCommand.ExecuteAsync(null); editor.CapabilityProfileReviewed = true;
        editor.ApplyCapabilityProfileCommand.Execute(null);
        var profile = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.ProfileSnapshot!;
        var role = profile.Roles.Single(r => r.Id == "marker");
        Assert.NotEqual(originalProfile.Identity.ContentSha256, profile.Identity.ContentSha256);
        Assert.Equal(RigHelperEditFields.Position, role.AllowedEdits); Assert.NotNull(role.ValidationRules!.Edits);
        Assert.Equal(originalRole.ValidationRules!.Frame, role.ValidationRules.Frame);
        Assert.Equal(originalRole.ValidationRules.Bounds, role.ValidationRules.Bounds);
        Assert.Equal(originalRole.ValidationRules.Channels, role.ValidationRules.Channels);
        Assert.Equal(originalRole.ValidationRules.Retention, role.ValidationRules.Retention);
        Assert.Contains(role.Evidence, e => e.Kind == RigEvidenceKind.UserOverride && e.ArtifactSha256 == originalProfile.Identity.ContentSha256);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.ValidationHistory);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(originalProfile.Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
    }

    [Fact]
    public async Task ExplicitProfileRevisionAndUndoRedoRemainUsable()
    {
        var source = Model(); var profile = source.Package.Document.RiggingSession!.Recipe.ProfileSnapshot!;
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic profile")));
        workspace.CommitProjectRestore(new(source, "guard-source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var editor = workspace.Conformance; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        var permissive = RigCapabilityProfileSerializer.Seal(profile with { Roles = profile.Roles.Select(r => r with { AllowedEdits = RigHelperEditFields.All }).ToImmutableArray() });
        editor.SetCapabilityProfileDraft(permissive); await editor.PreviewCapabilityProfileCommand.ExecuteAsync(null); editor.CapabilityProfileReviewed = true;
        editor.ApplyCapabilityProfileCommand.Execute(null);
        Assert.Equal(permissive.Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
        await editor.ScanStructuralCommand.ExecuteAsync(null); editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "normal_marker_2"); editor.StructuralOffset.X = .05;
        await editor.PreviewStructuralFrameCommand.ExecuteAsync(null); editor.StructuralReviewed = true; editor.ApplyStructuralCommand.Execute(null);
        Assert.False(editor.HasProfileEditRefusal);
        workspace.UndoHelperEditCommand.Execute(null); workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(profile.Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
        Assert.Equal(source.Package.Document.AuthoredHelpers[0].ExactLocalMatrix, workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers[0].ExactLocalMatrix);
        workspace.RedoHelperEditCommand.Execute(null); workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(permissive.Identity, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Recipe.Profile);
    }
}
