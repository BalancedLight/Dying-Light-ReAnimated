using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RigChannelPolicyWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void MissingDecisionsRequireExplicitChannelsLodAndOwners()
    {
        var source = Imported();
        using var workspace = Workspace(source);
        var wizard = workspace.Conformance;
        Assert.True(wizard.HasChannelPolicies);
        Assert.Null(wizard.EmitPositionChannel);
        Assert.Null(wizard.EmitRotationChannel);
        Assert.Null(wizard.EmitScaleChannel);
        Assert.Null(wizard.ChannelLod);
        Assert.Null(wizard.PositionChannelOwner!.Owner);
        wizard.ApplyAllChannelPoliciesCommand.Execute(null);
        Assert.Same(source.Package.Document.RiggingSession, Current(workspace).RiggingSession);
        Choose(wizard, RigAnimationComponents.None, RigAnimationLod.Off);
        wizard.PositionChannelOwner = wizard.ChannelOwnerChoices[0];
        wizard.ApplyAllChannelPoliciesCommand.Execute(null);
        Assert.Contains("cannot be preserved", wizard.ChannelPolicyStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        wizard.PositionChannelOwner = wizard.ChannelOwnerChoices.Single(c => c.Owner == RigComponentOwner.BindInherited);
        wizard.ApplyAllChannelPoliciesCommand.Execute(null);
        Assert.All(Current(workspace).RiggingSession!.Recipe.ComponentPolicies, policy => Assert.Equal(RigAnimationComponents.None, policy.EmittedMask));
    }

    [Fact]
    public void GeneratedRigDecisionsSaveReopenAndUndoWithoutChangingAuthoredPayload()
    {
        var generated = FbxGeneratedBodyBinding.Generate(GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source()));
        using var workspace = Workspace(generated);
        var wizard = workspace.Conformance;
        Assert.Equal(19, wizard.ChannelPolicies.Count);
        Choose(wizard, RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale, RigAnimationLod.Lod2);
        wizard.ApplyAllChannelPoliciesCommand.Execute(null);
        var saved = workspace.CaptureProjectSession().Model!;
        var decisions = saved.Package.Document.RiggingSession!.Recipe.ComponentPolicies;
        Assert.Equal(19, decisions.Length);
        Assert.True(generated.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(saved.Package.AuthoredLayerPayload.AsSpan()));
        Assert.True(generated.Package.SourceFbx.AsSpan().SequenceEqual(saved.Package.SourceFbx.AsSpan()));
        Assert.Equal(generated.Surfaces, saved.Surfaces);
        Assert.All(decisions, p => Assert.Equal(saved.Package.Document.Source.ContentSha256, Assert.Single(p.LodEvidence).ArtifactSha256));
        Assert.Empty(saved.Package.Document.RiggingSession.ValidationHistory);
        var contract = Dl1CustomModelRigPreparer.Prepare(saved).Contract;
        Assert.Equal(19, Dl1BoneScriptPolicyResolver.Resolve(saved.Package.Document, contract).Length);
        string path = Path.Combine(_directory, "channel-decisions.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(saved.Package, path);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(19, reopened.Package.Document.RiggingSession!.Recipe.ComponentPolicies.Length);
        Assert.Equal(decisions.Select(p => (p.EntityId, p.EmittedMask, p.AnimationLod)), reopened.Package.Document.RiggingSession.Recipe.ComponentPolicies.Select(p => (p.EntityId, p.EmittedMask, p.AnimationLod)));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        Assert.True(wizard.HasGeneratedBodyRig);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(19, Current(workspace).RiggingSession!.Recipe.ComponentPolicies.Length);
        Assert.True(generated.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload.AsSpan()));
    }

    [Fact]
    public void SelectedNodeAppliesByIdentityAndReapplyingDoesNotAddUndo()
    {
        using var workspace = Workspace(Imported());
        var wizard = workspace.Conformance;
        var row = wizard.ChannelPolicies.Last();
        wizard.SelectedChannelPolicy = row;
        Choose(wizard, RigAnimationComponents.Rotation, RigAnimationLod.Lod1);
        wizard.ApplySelectedChannelPolicyCommand.Execute(null);
        Assert.Equal(row.EntityId, Assert.Single(Current(workspace).RiggingSession!.Recipe.ComponentPolicies).EntityId);
        Assert.Equal(row.EntityId, wizard.SelectedChannelPolicy!.EntityId);
        long revision = Current(workspace).RiggingSession!.Revision;
        wizard.ApplySelectedChannelPolicyCommand.Execute(null);
        Assert.Equal(revision, Current(workspace).RiggingSession!.Revision);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public void ApplyAllWithKeepSavedOwnersRetainsMixedCompositionAndPerNodeEvidence()
    {
        var model = Imported();
        var session = model.Package.Document.RiggingSession!;
        var evidence = new RigEvidenceReference { Id = "source-review", Kind = RigEvidenceKind.UserOverride, ArtifactSha256 = model.Package.Document.Source.ContentSha256 };
        var mixed = new RigChannelOwnership { Owners = [RigComponentOwner.Clip, RigComponentOwner.Procedural], CompositionRuleId = "authored-overlay-review", Evidence = [evidence] };
        var policies = session.Recipe.Entities.Select(e => Dl1BoneScriptPolicyTests.Policy(e.EntityId, RigAnimationComponents.Rotation, RigAnimationLod.Lod0) with { Rotation = mixed }).ToImmutableArray();
        model = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session with { Recipe = session.Recipe with { ComponentPolicies = policies } } } } };
        using var workspace = Workspace(model);
        var wizard = workspace.Conformance;
        wizard.EmitScaleChannel = true;
        wizard.ChannelLod = wizard.ChannelLodChoices.Single(c => c.Lod == RigAnimationLod.Lod3);
        wizard.ApplyAllChannelPoliciesCommand.Execute(null);
        Assert.All(Current(workspace).RiggingSession!.Recipe.ComponentPolicies, p => {
            Assert.Same(mixed, p.Rotation);
            Assert.Equal(RigAnimationComponents.Rotation | RigAnimationComponents.Scale, p.EmittedMask);
            Assert.Equal(RigAnimationLod.Lod3, p.AnimationLod);
        });
    }

    [Fact]
    public void ChangingModelsClearsPendingDecisionsAndUnriggedSourceHidesEditor()
    {
        using var workspace = Workspace(Imported());
        var wizard = workspace.Conformance;
        Choose(wizard, RigAnimationComponents.Scale, RigAnimationLod.Lod3);
        workspace.CommitProjectRestore(new(Imported(), "another.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.Null(wizard.ChannelLod);
        Assert.Null(wizard.EmitScaleChannel);
        Assert.Null(wizard.ScaleChannelOwner!.Owner);
        workspace.CommitProjectRestore(new(GeneratedBodyWorkflowTests.Source(), "unrigged.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.False(wizard.HasChannelPolicies);
        Assert.False(wizard.ApplyAllChannelPoliciesCommand.CanExecute(null));
    }

    private static void Choose(RigConformanceWizardViewModel wizard, RigAnimationComponents mask, RigAnimationLod lod)
    {
        wizard.EmitPositionChannel = mask.HasFlag(RigAnimationComponents.Position);
        wizard.EmitRotationChannel = mask.HasFlag(RigAnimationComponents.Rotation);
        wizard.EmitScaleChannel = mask.HasFlag(RigAnimationComponents.Scale);
        wizard.ChannelLod = wizard.ChannelLodChoices.Single(c => c.Lod == lod);
        wizard.PositionChannelOwner = wizard.ChannelOwnerChoices.Single(c => c.Owner == RigComponentOwner.Clip);
        wizard.RotationChannelOwner = wizard.PositionChannelOwner;
        wizard.ScaleChannelOwner = wizard.PositionChannelOwner;
    }

    private static FbxModelAuthoringImportResult Imported()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig);
        return model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
    }

    private static ModelsWorkspaceViewModel Workspace(FbxModelAuthoringImportResult model)
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(model, "generic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        return workspace;
    }
    private static CustomModelDocument Current(ModelsWorkspaceViewModel workspace) => workspace.CaptureProjectSession().Model!.Package.Document;
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
