using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class RigChannelPolicyWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task ReopenedPolicyPreviewLoadsItsSavedReferenceWithoutReapplyingTheFit()
    {
        var fixture = StockPreviewFixture();
        int resolves = 0;
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (profile, _) =>
            {
                resolves++;
                Assert.Equal(fixture.Template.ProfileName, profile);
                return Task.FromResult(new Dl1RigTemplateResolution(fixture.Template, profile, fixture.Template.SourceResourceName,
                    fixture.Template.SourceFingerprint, "Generic reference"));
            },
            pickStockPolicySource: (template, _) =>
            {
                Assert.Same(fixture.Template, template);
                return Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload);
            });
        workspace.CommitProjectRestore(new(fixture.Model, "generic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var before = workspace.CaptureProjectSession().Model;
        long revision = workspace.PersistenceRevision;
        Assert.True(workspace.Conformance.PreviewStockChannelPoliciesCommand.CanExecute(null));
        await workspace.Conformance.PreviewStockChannelPoliciesCommand.ExecuteAsync(null);
        Assert.Equal(1, resolves);
        Assert.NotEmpty(workspace.Conformance.StockPolicyPreviewRows);
        Assert.Same(before, workspace.CaptureProjectSession().Model);
        Assert.Equal(revision, workspace.PersistenceRevision);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public async Task StockHumanoidPolicyRequiresRootChoicesAndReviewThenUndoesAsOneEdit()
    {
        var fixture = StockPreviewFixture();
        using var workspace = Workspace(fixture.Model);
        var wizard = workspace.Conformance;
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            fixture.Template, fixture.Template.ProfileName, fixture.Template.SourceResourceName,
            fixture.Template.SourceFingerprint, "Synthetic test reference")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);

        Assert.False(wizard.PreviewStockHumanoidPolicyCommand.CanExecute(null));
        Assert.Contains("Choose root position, rotation, and scale", wizard.StockHumanoidStatus, StringComparison.Ordinal);
        wizard.StockHumanoidRootPosition = wizard.StockHumanoidRootChoices.Single(choice => choice.Value == StockHumanoidRootChannel.Clip);
        wizard.StockHumanoidRootRotation = wizard.StockHumanoidRootPosition;
        wizard.StockHumanoidRootScale = wizard.StockHumanoidRootChoices.Single(choice => choice.Value == StockHumanoidRootChannel.Bind);
        Assert.True(wizard.PreviewStockHumanoidPolicyCommand.CanExecute(null));
        wizard.PreviewStockHumanoidPolicyCommand.Execute(null);
        Assert.True(wizard.StockHumanoidRows.Length > 0, wizard.StockHumanoidStatus);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        Assert.False(wizard.ApplyStockHumanoidPolicyCommand.CanExecute(null));

        wizard.StockHumanoidReviewed = true;
        Assert.True(wizard.ApplyStockHumanoidPolicyCommand.CanExecute(null));
        wizard.ApplyStockHumanoidPolicyCommand.Execute(null);
        var policies = Current(workspace).RiggingSession!.Recipe.ComponentPolicies;
        Assert.NotEmpty(policies);
        Assert.Contains(policies, policy => policy.EmittedMask ==
            (RigAnimationComponents.Position | RigAnimationComponents.Rotation));
        Assert.All(policies, policy => Assert.All(policy.Position.Evidence,
            evidence => Assert.Equal(RigEvidenceKind.UserOverride, evidence.Kind)));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public async Task StockHumanoidPolicyLeavesExistingRowsUntouchedWithoutSeparateConsent()
    {
        var fixture = StockPreviewFixture();
        using var workspace = Workspace(fixture.Model);
        var wizard = workspace.Conformance;
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            fixture.Template, fixture.Template.ProfileName, fixture.Template.SourceResourceName,
            fixture.Template.SourceFingerprint, "Synthetic test reference")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        wizard.StockHumanoidRootPosition = wizard.StockHumanoidRootChoices.Single(choice => choice.Value == StockHumanoidRootChannel.Clip);
        wizard.StockHumanoidRootRotation = wizard.StockHumanoidRootPosition;
        wizard.StockHumanoidRootScale = wizard.StockHumanoidRootPosition;
        wizard.PreviewStockHumanoidPolicyCommand.Execute(null);
        wizard.StockHumanoidReviewed = true;
        wizard.ApplyStockHumanoidPolicyCommand.Execute(null);
        var before = Current(workspace).RiggingSession!.Recipe.ComponentPolicies;
        wizard.PreviewStockHumanoidPolicyCommand.Execute(null);
        wizard.StockHumanoidReviewed = true;
        Assert.False(wizard.ApplyStockHumanoidPolicyCommand.CanExecute(null));
        Assert.Equal(before, Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public void StockHumanoidProposalSkipsOneExistingDecisionAndRejectsStaleReview()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = fixture.Model.Package.Document;
        var first = StockHumanoidChannelPolicyAuthoring.Propose(document, fixture.Template,
            StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Bind);
        Assert.True(first.Edits.Length >= 2);
        Assert.All(first.Edits, edit => Assert.Equal(RigAnimationLod.Off, edit.Lod));
        RigComponentPolicyEdit authored = first.Edits[0] with
        {
            Mask = RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale,
            Lod = RigAnimationLod.Lod2,
            PositionOwner = RigComponentOwner.Clip,
            RotationOwner = RigComponentOwner.Clip,
            ScaleOwner = RigComponentOwner.Clip,
        };
        Assert.True(RigComponentPolicyAuthoring.TryApply(document, first.Token, [authored], out RiggingSession withAuthored));
        document = document with { RiggingSession = withAuthored };
        var reviewed = StockHumanoidChannelPolicyAuthoring.Propose(document, fixture.Template,
            StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Bind);
        Assert.Contains(reviewed.Rows, row => row.Status == StockHumanoidPolicyRowStatus.ExistingDecision);
        Assert.True(StockHumanoidChannelPolicyAuthoring.TryApply(document, fixture.Template,
            reviewed, includeExistingDecisions: false, reviewed: true, out RiggingSession result));
        AnimationComponentPolicy preserved = result.Recipe.ComponentPolicies.Single(policy => policy.EntityId == authored.EntityId);
        Assert.Equal(authored.Mask, preserved.EmittedMask);
        Assert.Equal(RigAnimationLod.Lod2, preserved.AnimationLod);
        Assert.All(result.Recipe.ComponentPolicies.Where(policy => policy.EntityId != authored.EntityId),
            policy => Assert.Equal(RigAnimationLod.Off, policy.AnimationLod));
        Assert.Equal(first.Edits.Length, result.Recipe.ComponentPolicies.Length);
        Assert.False(StockHumanoidChannelPolicyAuthoring.TryApply(document with { RiggingSession = result },
            fixture.Template, reviewed, includeExistingDecisions: true, reviewed: true, out _));
    }

    [Fact]
    public void StockHumanoidProposalUsesBodyRootAndLeavesIndependentTemplateRootsUntouched()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument initial = fixture.Model.Package.Document;
        CustomModelBone sourceRoot = initial.Bones.Single(bone => bone.ParentIndex < 0);
        int sourceCount = initial.Bones.Length;
        CustomModelBone firstHolder = sourceRoot with
        {
            Index = sourceCount, ParentIndex = -1, FbxObjectId = sourceRoot.FbxObjectId + 100000,
            Name = "aux_holder_a", IsWeighted = false,
        };
        CustomModelBone secondHolder = firstHolder with
        {
            Index = sourceCount + 1, FbxObjectId = firstHolder.FbxObjectId + 1,
            Name = "aux_holder_b",
        };
        CustomModelDocument document = initial with
        {
            RigConformance = null, RiggingSession = null,
            Bones = initial.Bones.Add(firstHolder).Add(secondHolder),
        };
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };

        Dl1RigTemplateEntity bodyRoot = fixture.Template.Entities.Single(row => row.ParentIndex < 0);
        int count = fixture.Template.Entities.Length;
        var templateEntities = fixture.Template.Entities.Select(row => row.Index == bodyRoot.Index
            ? row with { SemanticRole = "body.root" } : row).ToImmutableArray()
            .Add(bodyRoot with { Index = count, Name = firstHolder.Name, SemanticRole = null, IsDeform = false })
            .Add(bodyRoot with { Index = count + 1, Name = secondHolder.Name, SemanticRole = null, IsDeform = false });
        var template = new Dl1RigTemplate("generic-multi-root", "synthetic-reference", new string('d', 64), templateEntities);

        StockHumanoidChannelPolicyProposal proposal = StockHumanoidChannelPolicyAuthoring.Propose(
            document, template, StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Clip, StockHumanoidRootChannel.Clip);
        Assert.Contains(proposal.Rows, row => row.Name == firstHolder.Name && row.Status == StockHumanoidPolicyRowStatus.UnmatchedExtra);
        Assert.Contains(proposal.Rows, row => row.Name == secondHolder.Name && row.Status == StockHumanoidPolicyRowStatus.UnmatchedExtra);
        Assert.DoesNotContain(proposal.Edits, edit =>
            proposal.Rows.Any(row => row.EntityId == edit.EntityId && row.Name is "aux_holder_a" or "aux_holder_b"));
        Assert.Equal(bodyRoot.Name, proposal.Rows.Single(row => row.EntityId == proposal.RootEntityId).Name);
        Assert.True(StockHumanoidChannelPolicyAuthoring.TryApply(document, template, proposal,
            includeExistingDecisions: false, reviewed: true, out RiggingSession applied));
        Assert.DoesNotContain(applied.Recipe.ComponentPolicies, policy =>
            proposal.Rows.Any(row => row.EntityId == policy.EntityId && row.Name is "aux_holder_a" or "aux_holder_b"));
    }

    [Fact]
    public async Task StockPolicyPreviewShowsSyntheticObservationsWithoutApplyingThem()
    {
        var fixture = StockPreviewFixture();
        var wizard = new RigConformanceWizardViewModel(
            (_, _) => Task.FromResult(new Dl1RigTemplateResolution(fixture.Template,
                fixture.Template.ProfileName, fixture.Template.SourceResourceName,
                fixture.Template.SourceFingerprint, "Synthetic test reference")),
            static _ => { });
        wizard.SetModel(fixture.Model);
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload));

        Assert.True(wizard.PreviewStockChannelPoliciesCommand.CanExecute(null));
        await wizard.PreviewStockChannelPoliciesCommand.ExecuteAsync(null);

        Assert.Equal(fixture.Template.EntityCount, wizard.StockPolicyPreviewRows.Length);
        Assert.All(wizard.StockPolicyPreviewRows, row => Assert.Equal("Proposed", row.Status));
        Assert.All(wizard.StockPolicyPreviewRows, row => Assert.Equal(fixture.Template.SourceFingerprint, row.SourceHash));
        Assert.Contains("No policy was applied", wizard.StockPolicyPreviewStatus, StringComparison.Ordinal);
        Assert.Empty(fixture.Model.Package.Document.RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public async Task TerminalHelperReviewUsesUnmatchedStockEvidenceAndUndoesAsOneEdit()
    {
        var fixture = StockPreviewFixture();
        FbxModelAuthoringImportResult imported = fixture.Model;
        CustomModelDocument source = imported.Package.Document;
        int helperIndex = source.Bones.Length;
        CustomModelBone helper = source.Bones[0] with
        {
            Index = helperIndex,
            ParentIndex = 0,
            FbxObjectId = 87001,
            Name = "aux_marker",
            Kind = BoneKind.Helper,
            IsWeighted = false,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
        };
        Guid clipId = Guid.NewGuid();
        var trackValue = new TransformTRS(new Vector3D(1, 0, 0), QuaternionD.Identity, Vector3D.One);
        var clip = new AnimationClip("synthetic_clip", new FrameRate(30, 1), 2,
            [new TransformTrack(helperIndex,
                [new TransformKeyframe(0, trackValue), new TransformKeyframe(1, trackValue)])]);
        CustomModelDocument document = source with
        {
            Bones = source.Bones.Add(helper),
            RiggingSession = null,
            AnimationClips = [new CustomModelAnimationClip
            {
                Id = clipId,
                SourceName = "synthetic_clip",
                DisplayName = "synthetic_clip",
                Included = true,
                FrameRate = new FrameRate(30, 1),
                StartFrame = 0,
                FrameCount = 2,
                SourceFingerprint = new string('a', 64),
                HasSkeletalTracks = true,
            }],
        };
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        var model = imported with
        {
            Package = imported.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(clipId, clip),
        };
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(model);
        document = document with
        {
            RigConformance = document.RigConformance! with
            {
                AppliedOutputRigSignature = RigSignature.Compute(prepared.SourceRig),
            },
        };
        model = model with { Package = model.Package with { Document = document } };
        using var workspace = Workspace(model);
        RigConformanceWizardViewModel wizard = workspace.Conformance;
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            fixture.Template, fixture.Template.ProfileName, fixture.Template.SourceResourceName,
            fixture.Template.SourceFingerprint, "Synthetic test reference")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload));
        Assert.False(wizard.PreviewTerminalHelperPolicyCommand.CanExecute(null));
        wizard.TerminalHelperLodChoice = wizard.ChannelLodChoices.Single(choice => choice.Lod == RigAnimationLod.Lod1);
        Assert.True(wizard.PreviewTerminalHelperPolicyCommand.CanExecute(null));

        await wizard.PreviewTerminalHelperPolicyCommand.ExecuteAsync(null);
        Assert.True(wizard.TerminalHelperPolicyRows.Length == 1,
            $"Terminal: {wizard.TerminalHelperPolicyStatus}; stock: {wizard.StockPolicyPreviewStatus}");
        RigTerminalHelperPolicyPreviewRow row = Assert.Single(wizard.TerminalHelperPolicyRows);
        Assert.Equal("aux_marker", row.Name);
        Assert.Equal("Proposed", row.Status);
        Assert.Contains("constant value differs from fitted bind", row.Evidence, StringComparison.Ordinal);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        Assert.False(wizard.ApplyTerminalHelperPolicyCommand.CanExecute(null));

        wizard.TerminalHelperPolicyReviewed = true;
        Assert.True(wizard.ApplyTerminalHelperPolicyCommand.CanExecute(null));
        wizard.ApplyTerminalHelperPolicyCommand.Execute(null);
        AnimationComponentPolicy saved = Assert.Single(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        Assert.Equal(RigAnimationComponents.None, saved.EmittedMask);
        Assert.Equal(RigAnimationLod.Lod1, saved.AnimationLod);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public async Task ReviewedStockRowsApplyWithExplicitOwnersAndUndoAsOneEdit()
    {
        var fixture = StockPreviewFixture();
        using var workspace = Workspace(fixture.Model);
        var wizard = workspace.Conformance;
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            fixture.Template, fixture.Template.ProfileName, fixture.Template.SourceResourceName,
            fixture.Template.SourceFingerprint, "Synthetic test reference")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload));
        await wizard.PreviewStockChannelPoliciesCommand.ExecuteAsync(null);

        Assert.Equal(2, wizard.StockPolicyPreviewRows.Length);
        Assert.False(wizard.ApplyReviewedStockPolicyCommand.CanExecute(null));
        wizard.StockEnabledChannelOwner = wizard.ExplicitChannelOwnerChoices.Single(choice =>
            choice.Owner == RigComponentOwner.Clip);
        wizard.StockOmittedChannelOwner = wizard.ExplicitChannelOwnerChoices.Single(choice =>
            choice.Owner == RigComponentOwner.BindInherited);
        wizard.StockPolicyApplyScope = wizard.StockPolicyApplyScopes.Single(scope => scope.IncludeHelpers && !scope.OnlyUnset);
        wizard.StockPolicyReviewed = true;
        Assert.True(wizard.ApplyReviewedStockPolicyCommand.CanExecute(null));
        wizard.ApplyReviewedStockPolicyCommand.Execute(null);

        CustomModelDocument saved = Current(workspace);
        Assert.Equal(2, saved.RiggingSession!.Recipe.ComponentPolicies.Length);
        Assert.All(saved.RiggingSession.Recipe.ComponentPolicies, policy =>
        {
            Assert.Contains(policy.LodEvidence, evidence =>
                evidence.Kind == RigEvidenceKind.ImportedSource &&
                evidence.ArtifactSha256 == fixture.Template.SourceFingerprint);
            Assert.All(policy.Position.Evidence, evidence =>
                Assert.Equal(RigEvidenceKind.UserOverride, evidence.Kind));
        });
        Assert.Contains("Native behavior remains unverified", wizard.StockPolicyApplyStatus, StringComparison.Ordinal);
        Assert.Empty(wizard.StockPolicyPreviewRows);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
    }

    [Fact]
    public async Task UnsetStockScopePreservesReviewedBodyChoiceWhileFillingMissingRows()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = fixture.Model.Package.Document;
        RiggingSession session = document.RiggingSession!;
        Guid retainedId = session.Recipe.Entities[0].EntityId;
        var retainedEdit = new RigComponentPolicyEdit(retainedId,
            RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale,
            RigAnimationLod.Lod2, RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.Clip);
        Assert.True(RigComponentPolicyAuthoring.TryApply(document, session.CreateJobToken(),
            [retainedEdit], out RiggingSession withRetained));
        var model = fixture.Model with { Package = fixture.Model.Package with
            { Document = document with { RiggingSession = withRetained } } };
        using var workspace = Workspace(model);
        var wizard = workspace.Conformance;
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            fixture.Template, fixture.Template.ProfileName, fixture.Template.SourceResourceName,
            fixture.Template.SourceFingerprint, "Synthetic test reference")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload));
        await wizard.PreviewStockChannelPoliciesCommand.ExecuteAsync(null);

        Assert.True(wizard.StockPolicyApplyScope!.OnlyUnset);
        wizard.StockEnabledChannelOwner = wizard.ExplicitChannelOwnerChoices.Single(choice =>
            choice.Owner == RigComponentOwner.Clip);
        wizard.StockOmittedChannelOwner = wizard.ExplicitChannelOwnerChoices.Single(choice =>
            choice.Owner == RigComponentOwner.BindInherited);
        wizard.StockPolicyReviewed = true;
        wizard.ApplyReviewedStockPolicyCommand.Execute(null);

        var saved = Current(workspace).RiggingSession!.Recipe.ComponentPolicies;
        Assert.Equal(2, saved.Length);
        AnimationComponentPolicy retained = saved.Single(policy => policy.EntityId == retainedId);
        Assert.Equal(retainedEdit.Mask, retained.EmittedMask);
        Assert.Equal(retainedEdit.Lod, retained.AnimationLod);
        Assert.All(saved.Where(policy => policy.EntityId != retainedId), policy =>
            Assert.Contains(policy.LodEvidence, evidence => evidence.Kind == RigEvidenceKind.ImportedSource));
        Assert.Contains("Saved 1 reviewed stock", wizard.StockPolicyApplyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingModelCancelsPendingStockPolicyPreviewAndClearsRows()
    {
        var fixture = StockPreviewFixture();
        var wizard = new RigConformanceWizardViewModel(
            (_, _) => Task.FromResult(new Dl1RigTemplateResolution(fixture.Template,
                fixture.Template.ProfileName, fixture.Template.SourceResourceName,
                fixture.Template.SourceFingerprint, "Synthetic test reference")),
            static _ => { });
        wizard.SetModel(fixture.Model);
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        var payloadReady = new TaskCompletionSource<Dl1MeshPreviewPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.SetStockPolicySourcePicker(async (_, _) => await payloadReady.Task);

        Task preview = wizard.PreviewStockChannelPoliciesCommand.ExecuteAsync(null);
        while (!wizard.IsStockPolicyPreviewRunning) await Task.Yield();
        wizard.SetModel(null);
        payloadReady.SetResult(fixture.Payload);
        await preview;

        Assert.Empty(wizard.StockPolicyPreviewRows);
        Assert.False(wizard.IsStockPolicyPreviewRunning);
    }

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

    [Fact]
    public void FilteringPoliciesKeepsTheSelectedNodeEditableAndRecoversAfterNoMatches()
    {
        using var workspace = Workspace(Imported());
        var wizard = workspace.Conformance;
        RigChannelPolicyRow target = wizard.ChannelPolicies.Last();

        wizard.ChannelPolicyNameFilter = target.Name;
        Assert.Contains(wizard.VisibleChannelPolicies, row => row.EntityId == target.EntityId);
        Assert.Equal(target.EntityId, wizard.SelectedChannelPolicy?.EntityId);
        Assert.True(wizard.ApplySelectedChannelPolicyCommand.CanExecute(null));

        wizard.ChannelPolicyNameFilter = "no-node-has-this-name";
        Assert.Empty(wizard.VisibleChannelPolicies);
        Assert.Null(wizard.SelectedChannelPolicy);
        Assert.False(wizard.ApplySelectedChannelPolicyCommand.CanExecute(null));

        wizard.ChannelPolicyNameFilter = "";
        Assert.Equal(wizard.ChannelPolicies.Count, wizard.VisibleChannelPolicies.Count);
        Assert.NotNull(wizard.SelectedChannelPolicy);
    }

    [Fact]
    public void FilteredPolicyApplyEditsOnlyMatchingNodesAndUndoesAsOneEdit()
    {
        using var workspace = Workspace(Imported());
        var wizard = workspace.Conformance;
        Assert.False(wizard.ApplyFilteredChannelPoliciesCommand.CanExecute(null));
        string filter = wizard.ChannelPolicies.Last().Name;

        wizard.ChannelPolicyNameFilter = filter;
        Guid[] selectedIds = wizard.VisibleChannelPolicies.Select(row => row.EntityId).ToArray();
        Assert.NotEmpty(selectedIds);
        Assert.True(selectedIds.Length < wizard.ChannelPolicies.Count);
        Assert.True(wizard.ApplyFilteredChannelPoliciesCommand.CanExecute(null));
        Choose(wizard, RigAnimationComponents.None, RigAnimationLod.Off);
        wizard.EmitPositionChannel = null;
        wizard.EmitRotationChannel = null;
        wizard.EmitScaleChannel = null;
        wizard.OmitAllChannelComponentsCommand.Execute(null);
        Assert.Equal("Position: omit · Rotation: omit · Scale: omit", wizard.ChannelMaskSummary);
        wizard.ApplyFilteredChannelPoliciesCommand.Execute(null);

        Assert.Equal(selectedIds.Order(), Current(workspace).RiggingSession!.Recipe.ComponentPolicies
            .Select(policy => policy.EntityId).Order());
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
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

    private static (FbxModelAuthoringImportResult Model, Dl1RigTemplate Template, Dl1MeshPreviewPayload Payload)
        StockPreviewFixture()
    {
        const string resourceName = "synthetic-stock-reference";
        string fingerprint = new string('c', 64);
        FbxModelAuthoringImportResult model = Imported();
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(model);
        var template = new Dl1RigTemplate("synthetic-preview-target", resourceName, fingerprint,
            prepared.Contract.Nodes.Select(node => new Dl1RigTemplateEntity
            {
                Index = node.PhysicalIndex,
                Name = node.Name,
                ParentIndex = node.ParentPhysicalIndex,
                Kind = node.Kind,
                IsDeform = node.IsDeform,
                LocalRestMatrix = node.LocalBindMatrix,
                GlobalRestMatrix = node.GlobalBindMatrix,
            }));
        var entities = prepared.Contract.Nodes.Select(node => new CompactMeshEntity(
            node.PhysicalIndex,
            node.Name,
            (uint)(((node.PhysicalIndex % 8) << 8) | ((node.PhysicalIndex % 5) << 12)),
            default,
            checked((short)node.ParentPhysicalIndex),
            node.Kind switch
            {
                BoneKind.Root or BoneKind.Deform => CompactMeshEntityType.Bone,
                BoneKind.Helper => CompactMeshEntityType.Helper,
                _ => CompactMeshEntityType.Hull,
            },
            0, 0, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0)).ToArray();
        var hierarchy = new CompactMeshDocument(entities.Length,
            entities.Count(static entity => entity.ParentIndex < 0), 0, entities, []);
        var conformance = new CustomModelRigConformance
        {
            TemplateId = template.TemplateId,
            TemplateProfileName = template.ProfileName,
            TemplateSourceResourceName = template.SourceResourceName,
            TemplateFingerprint = template.SourceFingerprint,
            SourceFbxSha256 = model.Package.Document.Source.ContentSha256,
            AppliedOutputRigSignature = RigSignature.Compute(prepared.SourceRig),
        };
        model = model with { Package = model.Package with { Document = model.Package.Document with { RigConformance = conformance } } };
        var source = new Dl1MeshData(resourceName, default, hierarchy, null, [], [], [], [], [], []);
        var payload = new Dl1MeshPreviewPayload(source, [], null, [], [], fingerprint);
        return (model, template, payload);
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
