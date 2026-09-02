using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

/// <summary>
/// Headless controls for the rig-conformance wizard. No renderer, no installed
/// game: the template is supplied through the same callback the app uses.
/// </summary>
public sealed class RigConformanceWizardTests
{
    [Fact]
    public void WizardWithoutATemplateCannotAdvanceAndReportsWhy()
    {
        var statuses = new List<string>();
        var wizard = new RigConformanceWizardViewModel(
            (_, _) => Task.FromResult(
                Dl1RigTemplateResolution.Failed(
                    "player",
                    "No Dying Light installation is indexed yet.")),
            statuses.Add);
        wizard.SetModel(CreateModel());

        wizard.ResolveTemplateCommand.Execute(null);

        Assert.False(wizard.HasTemplate);
        Assert.False(wizard.CanAdvance);
        Assert.False(wizard.NextStageCommand.CanExecute(null));
        Assert.Contains("indexed", wizard.TemplateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(statuses, status => status.Contains("indexed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolvingATemplateSolvesAndUnblocksTheNextStage()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();

        Assert.True(wizard.HasTemplate);
        Assert.True(wizard.CanAdvance);
        Assert.True(wizard.CanApply);
        Assert.NotNull(wizard.Fit);
        Assert.True(wizard.MappedCount > 0);
        Assert.True(wizard.SynthesizedCount > 0);
        Assert.NotEmpty(wizard.Mappings);
    }

    [Fact]
    public void StageNavigationStaysInsideTheSequence()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();

        Assert.Equal(RigConformanceStage.Target, wizard.Stage);
        Assert.False(wizard.PreviousStageCommand.CanExecute(null));

        for (int step = 0; step < 4; step++)
        {
            Assert.True(wizard.NextStageCommand.CanExecute(null));
            wizard.NextStageCommand.Execute(null);
        }

        Assert.Equal(RigConformanceStage.Verify, wizard.Stage);
        Assert.False(wizard.NextStageCommand.CanExecute(null));
        Assert.True(wizard.PreviousStageCommand.CanExecute(null));
    }

    [Fact]
    public void AmbiguousRoleIsSurfacedWithItsCandidatesAndAnOverrideChangesTheMapping()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();

        RigConformanceMappingItemViewModel pelvis = wizard.Mappings.Single(
            row => row.Name == "pelvis");
        Assert.True(pelvis.IsAmbiguous);
        Assert.True(pelvis.CanChooseSource);
        Assert.Contains("CC_Base_Pelvis", pelvis.Candidates);
        Assert.Equal("CC_Base_Hip", pelvis.SourceName);

        pelvis.SelectedSourceName = "CC_Base_Pelvis";

        Assert.Equal(
            "CC_Base_Pelvis",
            wizard.Mappings.Single(row => row.Name == "pelvis").SourceName);
    }

    [Fact]
    public void KeepingOrDroppingExtraBonesResolvesImmediately()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        int keptExtras = wizard.ExtraCount;

        wizard.KeepExtraBones = false;

        Assert.True(keptExtras > 0);
        Assert.Equal(0, wizard.ExtraCount);
        Assert.Equal(keptExtras, wizard.DroppedCount);
    }

    [Fact]
    public void ScaleModeAndStrengthReSolveTheFit()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        double automatic = wizard.Landmark!.UniformScale;

        wizard.ScaleMode = CustomModelConformanceScaleMode.Manual;
        wizard.ManualScale = 0.5;

        Assert.Equal(0.5, wizard.Landmark!.UniformScale, 9);
        Assert.NotEqual(automatic, wizard.Landmark.UniformScale);

        wizard.ConformanceStrength = 0.0;
        Assert.Equal(0.0, wizard.Fit!.ConformanceStrength, 9);
    }

    [Fact]
    public void GuidedSequenceReportsWhichJointsExistOnTheFittedRig()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();

        RigConformanceLandmarkViewModel pelvis =
            wizard.Landmarks.Single(row => row.BoneName == "pelvis");
        Assert.True(pelvis.IsResolved);
        Assert.Equal("Good", pelvis.Severity);

        // The miniature fixture has no toes, so that step must report itself as
        // absent rather than pretending to be placed.
        RigConformanceLandmarkViewModel toe =
            wizard.Landmarks.Single(row => row.BoneName == "l_toebase");
        Assert.False(toe.IsResolved);
        Assert.Equal("Missing", toe.Severity);
    }

    [Fact]
    public void PlacingAJointMovesItAndItsDescendants()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        Vector3D originalHand = wizard.TryGetBonePosition("l_hand")!.Value;
        Vector3D originalElbow = wizard.TryGetBonePosition("l_forearm")!.Value;
        var placed = new Vector3D(originalElbow.X, originalElbow.Y + 0.10, originalElbow.Z);

        wizard.MirrorEdits = false;
        wizard.SetBonePosition("l_forearm", placed);

        Assert.Equal(0.0, Vector3D.Distance(placed, wizard.TryGetBonePosition("l_forearm")!.Value), 9);
        Assert.True(wizard.HasOverride("l_forearm"));
        Assert.NotEqual(
            originalHand,
            wizard.TryGetBonePosition("l_hand")!.Value);
    }

    [Fact]
    public void MirroringPlacesTheOppositeJointAcrossTheModelCentreline()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        var placed = new Vector3D(0.33, 1.40, 0.05);

        wizard.MirrorEdits = true;
        wizard.SetBonePosition("l_forearm", placed);

        Vector3D mirrored = wizard.TryGetBonePosition("r_forearm")!.Value;
        Assert.Equal(-placed.X, mirrored.X, 9);
        Assert.Equal(placed.Y, mirrored.Y, 9);
        Assert.Equal(placed.Z, mirrored.Z, 9);
    }

    [Fact]
    public void ResettingRestoresTheSolvedPlacement()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        Vector3D solved = wizard.TryGetBonePosition("l_forearm")!.Value;

        wizard.SetBonePosition("l_forearm", new Vector3D(0.9, 0.9, 0.9));
        wizard.SelectedLandmark = wizard.Landmarks.Single(row => row.BoneName == "l_forearm");
        Assert.True(wizard.ResetBoneCommand.CanExecute(null));
        wizard.ResetBoneCommand.Execute(null);

        Assert.False(wizard.HasOverride("l_forearm"));
        Assert.Equal(0.0, Vector3D.Distance(solved, wizard.TryGetBonePosition("l_forearm")!.Value), 9);
    }

    [Fact]
    public void ResetAllClearsEveryManualPlacement()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.SetBonePosition("l_forearm", new Vector3D(0.4, 1.3, 0.0));
        wizard.SetBonePosition("head", new Vector3D(0.0, 1.6, 0.0));
        Assert.True(wizard.ResetAllCommand.CanExecute(null));

        wizard.ResetAllCommand.Execute(null);

        Assert.False(wizard.HasOverride("l_forearm"));
        Assert.False(wizard.HasOverride("head"));
        Assert.False(wizard.ResetAllCommand.CanExecute(null));
    }

    [Fact]
    public void CapturedSettingsRoundTripThroughTheDocument()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.KeepExtraBones = false;
        wizard.ScaleMode = CustomModelConformanceScaleMode.Leg;
        wizard.ConformanceStrength = 0.75;
        wizard.MirrorEdits = false;
        wizard.SetBonePosition("l_forearm", new Vector3D(0.4, 1.3, 0.02));
        wizard.Mappings.Single(row => row.Name == "pelvis").SelectedSourceName = "CC_Base_Pelvis";

        CustomModelRigConformance settings = wizard.CreateSettings()!;
        settings.Validate(nameof(settings));

        Assert.True(settings.DropExtraBones);
        Assert.Equal(CustomModelConformanceScaleMode.Leg, settings.ScaleMode);
        Assert.Null(settings.ManualScale);
        Assert.Equal(0.75, settings.ConformanceStrength, 9);
        Assert.Equal("CC_Base_Pelvis", Assert.Single(settings.RoleOverrides).SourceBoneName);
        Assert.Equal("l_forearm", Assert.Single(settings.PositionOverrides).BoneName);
    }

    [Fact]
    public void RestoringSettingsFromADocumentReplaysEveryDecision()
    {
        RigConformanceWizardViewModel authored = CreateResolvedWizard();
        authored.KeepExtraBones = false;
        authored.ScaleMode = CustomModelConformanceScaleMode.Manual;
        authored.ManualScale = 0.8;
        authored.ConformanceStrength = 0.25;
        CustomModelRigConformance settings = authored.CreateSettings()!;

        FbxModelAuthoringImportResult model = CreateModel();
        model = model with
        {
            Package = model.Package with
            {
                Document = model.Package.Document with { RigConformance = settings },
            },
        };

        var restored = new RigConformanceWizardViewModel(
            (profile, _) => Task.FromResult(CreateResolution(profile)),
            static _ => { });
        restored.SetModel(model);

        Assert.False(restored.KeepExtraBones);
        Assert.Equal(CustomModelConformanceScaleMode.Manual, restored.ScaleMode);
        Assert.Equal(0.8, restored.ManualScale, 9);
        Assert.Equal(0.25, restored.ConformanceStrength, 9);
        Assert.Contains("Restored", restored.SolveStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSolvedAgainstADifferentModelAreFlaggedNotSilentlyReused()
    {
        RigConformanceWizardViewModel authored = CreateResolvedWizard();
        CustomModelRigConformance settings = authored.CreateSettings()! with
        {
            SourceFbxSha256 = new string('a', 64),
        };

        FbxModelAuthoringImportResult model = CreateModel();
        model = model with
        {
            Package = model.Package with
            {
                Document = model.Package.Document with { RigConformance = settings },
            },
        };

        var restored = new RigConformanceWizardViewModel(
            (profile, _) => Task.FromResult(CreateResolution(profile)),
            static _ => { });
        restored.SetModel(model);

        Assert.Contains("different source model", restored.SolveStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingTheModelDiscardsTheSolvedFit()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        Assert.NotNull(wizard.Fit);

        wizard.SetModel(null);

        Assert.Null(wizard.Fit);
        Assert.Empty(wizard.Mappings);
        Assert.False(wizard.CanApply);
    }

    [Fact]
    public void ApplyingTheWizardsFitProducesADl1NamedRigAndPersistsItsSettings()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        FbxModelAuthoringImportResult model = CreateModel();

        FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
            model,
            wizard.Fit!,
            wizard.CreateSettings());
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(conformed);

        Assert.Empty(prepared.Diagnostics);
        HashSet<string> names = prepared.Contract.Nodes
            .Select(static node => node.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("bip01", names);
        Assert.Contains("l_hand", names);
        Assert.NotNull(conformed.Package.Document.RigConformance);
        conformed.Package.Document.Validate();
    }


    [Fact]
    public void GizmoDragMovesTheSelectedJointAndCommitKeepsIt()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.MirrorEdits = false;
        int elbow = IndexOf(wizard, "l_forearm");
        Vector3D start = wizard.TryGetBonePosition("l_forearm")!.Value;
        IRenderTranslationGizmoTarget target = wizard.GizmoTarget;

        Assert.True(target.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(
                new TranslationGizmoBinding(elbow, TranslationGizmoAxis.Y, RenderGizmoSpace.Global),
                new Vector3(0, 1, 0))));
        Assert.True(target.UpdateTranslationGizmoDrag(
            new RenderTranslationGizmoDragUpdate(
                new TranslationGizmoBinding(elbow, TranslationGizmoAxis.Y, RenderGizmoSpace.Global),
                new Vector3(0.0f, 0.05f, 0.0f),
                0.05f)));
        target.CompleteTranslationGizmoDrag(commit: true);

        Vector3D moved = wizard.TryGetBonePosition("l_forearm")!.Value;
        Assert.Equal(start.Y + 0.05, moved.Y, 5);
        Assert.True(wizard.HasOverride("l_forearm"));
    }

    [Fact]
    public void CancellingAGizmoDragRestoresThePreDragPlacement()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.MirrorEdits = false;
        int elbow = IndexOf(wizard, "l_forearm");
        Vector3D start = wizard.TryGetBonePosition("l_forearm")!.Value;
        IRenderTranslationGizmoTarget target = wizard.GizmoTarget;
        var binding = new TranslationGizmoBinding(elbow, TranslationGizmoAxis.Y, RenderGizmoSpace.Global);

        target.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(binding, new Vector3(0, 1, 0)));
        target.UpdateTranslationGizmoDrag(
            new RenderTranslationGizmoDragUpdate(binding, new Vector3(0.0f, 0.2f, 0.0f), 0.2f));
        target.CompleteTranslationGizmoDrag(commit: false);

        Assert.Equal(0.0, Vector3D.Distance(start, wizard.TryGetBonePosition("l_forearm")!.Value), 9);
        Assert.False(wizard.HasOverride("l_forearm"));
    }

    [Fact]
    public void GizmoRefusesSynthesizedDl1HelpersSoTemplateStructureCannotDrift()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        int camera = IndexOf(wizard, "eyecamera");
        Assert.True(camera >= 0);

        bool began = wizard.GizmoTarget.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(
                new TranslationGizmoBinding(camera, TranslationGizmoAxis.Y, RenderGizmoSpace.Global),
                new Vector3(0, 1, 0)));

        Assert.False(began);
    }

    [Fact]
    public void GizmoDragMirrorsWhenMirroringIsEnabled()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.MirrorEdits = true;
        int elbow = IndexOf(wizard, "l_forearm");
        var binding = new TranslationGizmoBinding(elbow, TranslationGizmoAxis.Y, RenderGizmoSpace.Global);
        IRenderTranslationGizmoTarget target = wizard.GizmoTarget;

        target.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(binding, new Vector3(0, 1, 0)));
        target.UpdateTranslationGizmoDrag(
            new RenderTranslationGizmoDragUpdate(binding, new Vector3(0.0f, 0.07f, 0.0f), 0.07f));
        target.CompleteTranslationGizmoDrag(commit: true);

        Vector3D left = wizard.TryGetBonePosition("l_forearm")!.Value;
        Vector3D right = wizard.TryGetBonePosition("r_forearm")!.Value;
        Assert.Equal(-left.X, right.X, 5);
        Assert.Equal(left.Y, right.Y, 5);
    }

    [Fact]
    public void SelectedBoneIndexTracksTheGuidedStep()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        Assert.Equal(-1, wizard.SelectedBoneIndex);

        wizard.SelectedLandmark = wizard.Landmarks.Single(row => row.BoneName == "l_hand");

        Assert.Equal(IndexOf(wizard, "l_hand"), wizard.SelectedBoneIndex);
    }

    [Fact]
    public void VerificationIsUnavailableWithoutARetailClipPickerButApplyStillWorks()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();

        Assert.False(wizard.CanVerifyWithRetailClip);
        Assert.False(wizard.VerifyWithRetailClipCommand.CanExecute(null));
        Assert.True(wizard.CanApply);

        wizard.SetRetailClipPicker((_) => Task.FromResult<Dl1RetailAnimationPayload?>(null));
        Assert.True(wizard.CanVerifyWithRetailClip);
    }

    private static int IndexOf(RigConformanceWizardViewModel wizard, string boneName) =>
        wizard.Fit!.Bones
            .FirstOrDefault(bone => string.Equals(bone.Name, boneName, StringComparison.OrdinalIgnoreCase))
            ?.Index ?? -1;


    /// <summary>
    /// A skin palette indexes the skeleton it was bound against, so a preview
    /// must pair the conformed mesh with the conformed skeleton. Pairing the
    /// imported mesh with the conformed skeleton skins it through unrelated
    /// bones - silently wrong where the row counts happen to fit, and rejected
    /// outright where they do not.
    /// </summary>
    [Fact]
    public void ConformedMeshAndSkeletonValidateTogether()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
            CreateModel(),
            wizard.Fit!,
            wizard.CreateSettings());

        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            conformed,
            CustomModelPreviewMode.Dl1Output);
        RigDefinition rig = Dl1RigConformanceApplier.CreateRigDefinition(wizard.Fit!);
        SkeletonRenderData skeleton =
            CorePreviewAdapter.ToRenderSkeleton(rig.CreateBindPose());

        Assert.Equal(wizard.Fit!.Bones.Length, skeleton.Bones.Count);
        Assert.NotEmpty(session.Meshes);
        foreach (MeshRenderData mesh in session.Meshes)
        {
            Assert.True(
                RenderMeshValidation.TryValidate(mesh, skeleton, out string? error),
                $"'{mesh.Id}' must validate against the conformed skeleton: {error}");
        }
    }

    /// <summary>
    /// Dropping extra bones shrinks the emitted rig below the imported one, so
    /// the mismatch this guards against is not hypothetical.
    /// </summary>
    [Fact]
    public void DroppingExtraBonesStillValidatesBecauseTheMeshIsReboundToo()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        wizard.KeepExtraBones = false;

        FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
            CreateModel(),
            wizard.Fit!,
            wizard.CreateSettings());
        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            conformed,
            CustomModelPreviewMode.Dl1Output);
        RigDefinition rig = Dl1RigConformanceApplier.CreateRigDefinition(wizard.Fit!);
        SkeletonRenderData skeleton =
            CorePreviewAdapter.ToRenderSkeleton(rig.CreateBindPose());

        Assert.True(skeleton.Bones.Count < CreateSourceRig().BoneCount);
        foreach (MeshRenderData mesh in session.Meshes)
        {
            Assert.True(
                RenderMeshValidation.TryValidate(mesh, skeleton, out string? error),
                $"'{mesh.Id}' must validate after dropping extra bones: {error}");
        }
    }

    /// <summary>
    /// Moving a joint must not change the emitted bone table, which is what
    /// lets the workspace reuse a built preview session while dragging.
    /// </summary>
    [Fact]
    public void PlacingAJointLeavesTheEmittedBoneTableUnchanged()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        string[] before = wizard.Fit!.Bones
            .Select(bone => $"{bone.Name}/{bone.ParentIndex}/{(int)bone.Kind}")
            .ToArray();

        wizard.MirrorEdits = false;
        wizard.SetBonePosition("l_forearm", new Vector3D(0.44, 1.31, 0.02));
        wizard.ConformanceStrength = 0.4;

        string[] after = wizard.Fit!.Bones
            .Select(bone => $"{bone.Name}/{bone.ParentIndex}/{(int)bone.Kind}")
            .ToArray();
        Assert.Equal(before, after);
    }

    /// <summary>
    /// Changing the correspondence does change the table, so the cached preview
    /// session must not be reused across it.
    /// </summary>
    [Fact]
    public void DroppingExtraBonesChangesTheEmittedBoneTable()
    {
        RigConformanceWizardViewModel wizard = CreateResolvedWizard();
        int before = wizard.Fit!.Bones.Length;

        wizard.KeepExtraBones = false;

        Assert.NotEqual(before, wizard.Fit!.Bones.Length);
    }

    private static RigConformanceWizardViewModel CreateResolvedWizard()
    {
        var wizard = new RigConformanceWizardViewModel(
            (profile, _) => Task.FromResult(CreateResolution(profile)),
            static _ => { });
        wizard.SetModel(CreateModel());
        wizard.ResolveTemplateCommand.Execute(null);
        return wizard;
    }

    private static Dl1RigTemplateResolution CreateResolution(string profile) =>
        new(CreateTemplate(), profile, "player_1_tpp", new string('b', 64), "ok");

    private static Dl1RigTemplate CreateTemplate()
    {
        var entities = new List<(string Name, int Parent, Vector3D Offset, BoneKind Kind, bool Deform)>
        {
            ("bip01", -1, new Vector3D(0.0, 0.95, 0.0), BoneKind.Root, true),
            ("pelvis", 0, Vector3D.Zero, BoneKind.Deform, true),
            ("spine", 1, new Vector3D(0.0, 0.10, 0.0), BoneKind.Deform, true),
            ("spine1", 2, new Vector3D(0.0, 0.12, 0.0), BoneKind.Deform, true),
            ("spine2", 3, new Vector3D(0.0, 0.12, 0.0), BoneKind.Deform, true),
            ("neck", 4, new Vector3D(0.0, 0.16, 0.0), BoneKind.Deform, true),
            ("head", 5, new Vector3D(0.0, 0.10, 0.0), BoneKind.Deform, true),
            ("eyecamera", 6, new Vector3D(0.0, 0.05, 0.10), BoneKind.Camera, false),
            ("l_clavicle", 4, new Vector3D(0.05, 0.14, 0.0), BoneKind.Deform, true),
            ("l_upperarm", 8, new Vector3D(0.15, 0.0, 0.0), BoneKind.Deform, true),
            ("l_forearm", 9, new Vector3D(0.28, 0.0, 0.0), BoneKind.Deform, true),
            ("l_hand", 10, new Vector3D(0.25, 0.0, 0.0), BoneKind.Deform, true),
            ("r_clavicle", 4, new Vector3D(-0.05, 0.14, 0.0), BoneKind.Deform, true),
            ("r_upperarm", 12, new Vector3D(-0.15, 0.0, 0.0), BoneKind.Deform, true),
            ("r_forearm", 13, new Vector3D(-0.28, 0.0, 0.0), BoneKind.Deform, true),
            ("r_hand", 14, new Vector3D(-0.25, 0.0, 0.0), BoneKind.Deform, true),
            ("l_thigh", 1, new Vector3D(0.10, -0.02, 0.0), BoneKind.Deform, true),
            ("l_calf", 16, new Vector3D(0.0, -0.42, 0.0), BoneKind.Deform, true),
            ("l_foot", 17, new Vector3D(0.0, -0.43, 0.0), BoneKind.Deform, true),
            ("r_thigh", 1, new Vector3D(-0.10, -0.02, 0.0), BoneKind.Deform, true),
            ("r_calf", 19, new Vector3D(0.0, -0.42, 0.0), BoneKind.Deform, true),
            ("r_foot", 20, new Vector3D(0.0, -0.43, 0.0), BoneKind.Deform, true),
        };

        var rows = ImmutableArray.CreateBuilder<Dl1RigTemplateEntity>(entities.Count);
        var globals = new TransformMatrix[entities.Count];
        for (int index = 0; index < entities.Count; index++)
        {
            (string name, int parent, Vector3D offset, BoneKind kind, bool deform) = entities[index];
            TransformMatrix local = TransformMatrix.CreateTranslation(offset);
            globals[index] = parent < 0 ? local : globals[parent] * local;
            rows.Add(new Dl1RigTemplateEntity
            {
                Index = index,
                Name = name,
                ParentIndex = parent,
                Kind = kind,
                IsDeform = deform,
                LocalRestMatrix = local,
                GlobalRestMatrix = globals[index],
                SemanticRole = Dl1RigDefinitionFactory.TryResolveSemanticRole(name),
            });
        }

        return new Dl1RigTemplate("player", "player_1_tpp", new string('b', 64), rows.MoveToImmutable());
    }

    private static RigDefinition CreateSourceRig()
    {
        var rows = new List<(string Name, int Parent, Vector3D Offset)>
        {
            ("RL_BoneRoot", -1, Vector3D.Zero),
            ("CC_Base_Hip", 0, new Vector3D(0.0, 0.95, 0.0)),
            ("CC_Base_Pelvis", 1, Vector3D.Zero),
            ("CC_Base_Waist", 2, new Vector3D(0.0, 0.10, 0.0)),
            ("CC_Base_Spine01", 3, new Vector3D(0.0, 0.12, 0.0)),
            ("CC_Base_Spine02", 4, new Vector3D(0.0, 0.12, 0.0)),
            ("CC_Base_NeckTwist01", 5, new Vector3D(0.0, 0.16, 0.0)),
            ("CC_Base_Head", 6, new Vector3D(0.0, 0.10, 0.0)),
            ("CC_Base_L_Clavicle", 5, new Vector3D(0.05, 0.14, 0.0)),
            ("CC_Base_L_Upperarm", 8, new Vector3D(0.15, 0.0, 0.0)),
            ("CC_Base_L_Forearm", 9, new Vector3D(0.28, 0.0, 0.0)),
            ("CC_Base_L_Hand", 10, new Vector3D(0.25, 0.0, 0.0)),
            ("CC_Base_L_ForearmTwist01", 10, new Vector3D(0.10, 0.0, 0.0)),
            ("CC_Base_R_Clavicle", 5, new Vector3D(-0.05, 0.14, 0.0)),
            ("CC_Base_R_Upperarm", 13, new Vector3D(-0.15, 0.0, 0.0)),
            ("CC_Base_R_Forearm", 14, new Vector3D(-0.28, 0.0, 0.0)),
            ("CC_Base_R_Hand", 15, new Vector3D(-0.25, 0.0, 0.0)),
            ("CC_Base_L_Thigh", 1, new Vector3D(0.10, -0.02, 0.0)),
            ("CC_Base_L_Calf", 17, new Vector3D(0.0, -0.42, 0.0)),
            ("CC_Base_L_Foot", 18, new Vector3D(0.0, -0.43, 0.0)),
            ("CC_Base_R_Thigh", 1, new Vector3D(-0.10, -0.02, 0.0)),
            ("CC_Base_R_Calf", 20, new Vector3D(0.0, -0.42, 0.0)),
            ("CC_Base_R_Foot", 21, new Vector3D(0.0, -0.43, 0.0)),
        };

        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            (string name, int parent, Vector3D offset) = rows[index];
            bones.Add(new BoneDefinition(
                index,
                name,
                parent,
                new TransformTRS(offset, QuaternionD.Identity, Vector3D.One),
                parent < 0 ? BoneKind.Root : BoneKind.Deform));
        }

        return new RigDefinition("source:test", "synthetic", bones.MoveToImmutable());
    }

    private static FbxModelAuthoringImportResult CreateModel()
    {
        RigDefinition source = CreateSourceRig();
        byte[] fbx = "Kaydara FBX Binary  synthetic-wizard-source"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(fbx)).ToLowerInvariant();

        var bones = ImmutableArray.CreateBuilder<CustomModelBone>(source.BoneCount);
        foreach (BoneDefinition bone in source.Bones)
        {
            bones.Add(new CustomModelBone
            {
                Index = bone.Index,
                FbxObjectId = 200 + bone.Index,
                Name = bone.Name,
                ParentIndex = bone.ParentIndex,
                LocalBindTransform = bone.LocalBindPose,
                ExactLocalBindMatrix = bone.LocalBindPose.ToMatrix(),
                Kind = bone.Kind,
                IsWeighted = true,
            });
        }

        var document = new CustomModelDocument
        {
            ModelId = new Guid("7a1f3f0e-4b21-4a4f-9c4b-2f5d6e7a8b90"),
            Name = "Synthetic wizard model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            Bones = bones.MoveToImmutable(),
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "Body",
                    ControlPointCount = 3,
                    PolygonCount = 1,
                    TriangleCount = 1,
                    ExpandedVertexCount = 3,
                    MaterialSlotCount = 1,
                },
            ],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = new Guid("22c852ee-2cbd-579a-8a08-73a9335738fd"),
                    Name = "Default",
                },
            ],
        };
        document = document with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(document.Bones),
        };
        document.Validate();

        var surface = new FbxModelSurface(
            "body",
            "Body",
            new Guid("22c852ee-2cbd-579a-8a08-73a9335738fd"),
            [
                new FbxModelVertex(
                    new Vector3D(0.3, 1.4, 0.0),
                    Vector3D.UnitY,
                    0.0,
                    0.0,
                    [0, 1],
                    [0.7, 0.3]),
            ],
            [0, 0, 0],
            [
                source.GetBoneIndex("CC_Base_L_Forearm"),
                source.GetBoneIndex("CC_Base_L_Hand"),
            ],
            [TransformMatrix.Identity, TransformMatrix.Identity],
            IsSkinned: true);

        return new FbxModelAuthoringImportResult(
            new CustomModelPackage(
                document,
                fbx.ToImmutableArray(),
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty),
            source,
            [surface],
            ImmutableDictionary<Guid, AnimationClip>.Empty,
            new FbxStrictExportInspection(
                [],
                ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
                ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, long?>.Empty,
                [],
                0,
                0,
                ImmutableHashSet<string>.Empty,
                ImmutableHashSet<string>.Empty,
                ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
                0,
                0,
                [],
                [],
                ImmutableHashSet<string>.Empty));
    }
}
