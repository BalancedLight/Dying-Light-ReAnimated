using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SecondaryMotionSession? _secondarySession;
    private CustomModelDocument? _secondaryDocument;
    private readonly SecondaryMotionModelStateCache _secondaryModelStates = new();
    private ProjectAnimation? _secondaryAnimation;
    private EvaluationRequest? _secondaryRequest;
    private readonly Dictionary<string, TransformMatrix> _secondaryLocalDeltas = new(StringComparer.Ordinal);
    private GizmoRenderData[] _secondaryGizmos = [];
    private GizmoRenderData[] _secondaryExternalGizmos = [];
    private FbxModelAuthoringImportResult? _secondaryFitModel;
    private CustomModelPackage? _secondaryFitPackage;
    private CustomModelDocument? _nativeCollisionDocument;
    private ImmutableArray<NativeClothSource> _nativeCollisionSources = [];
    public SecondaryMotionViewModel SecondaryMotion { get; } = new();

    private void InitializeSecondaryMotionFeature()
    {
        SecondaryMotion.Changed += OnSecondaryMotionChanged;
        SecondaryMotion.ResetCommand = new RelayCommand(() => { ResetSecondaryMotionPreview(); RefreshEditableSkeletonPreview(); });
        SecondaryMotion.LoadSetupCommand = new RelayCommand(LoadSecondarySetup);
        SecondaryMotion.SaveSetupCommand = new RelayCommand(SaveSecondarySetup);
        SecondaryMotion.ImportNativeCommand = new RelayCommand(ImportNativeCloth);
        SecondaryMotion.ExportNativeCommand = new RelayCommand(ExportNativeCloth);
        SecondaryMotion.SaveModelCopyCommand = new RelayCommand(SaveSecondaryModelCopy);
        SecondaryMotion.FitCollisionSphereCommand = new RelayCommand(FitSecondarySphereCollision);
        SecondaryMotion.FitCollisionCapsuleCommand = new RelayCommand(FitSecondaryCapsuleCollision);
        SecondaryMotion.ApplyCollisionProposalCommand = new RelayCommand(ApplySecondaryCollisionProposal);
        SecondaryMotion.CollisionPreviewChanged += OnSecondaryCollisionPreviewChanged;
    }

    private void DisposeSecondaryMotionFeature()
    {
        SecondaryMotion.Changed -= OnSecondaryMotionChanged;
        SecondaryMotion.CollisionPreviewChanged -= OnSecondaryCollisionPreviewChanged;
    }

    private void OnSecondaryMotionChanged(object? sender, EventArgs args)
    {
        NotifySecondaryMotionPendingStateChanged();
        RefreshNativeCollisionOverlays();
        ResetSecondaryMotionPreview();
        RefreshEditableSkeletonPreview();
    }

    private void OnSecondaryCollisionPreviewChanged(object? sender, EventArgs args) => RefreshEditableSkeletonPreview();

    private void ResetSecondaryMotionPreview()
    {
        _secondarySession = null;
        _secondaryRequest = null;
        _secondaryLocalDeltas.Clear();
        _secondaryGizmos = [];
        _secondaryExternalGizmos = [];
    }

    /// <summary>Call when custom-target presentation changes, after its session has been assigned.</summary>
    private void SynchronizeSecondaryMotionModel()
    {
        CustomModelDocument? document = _customTargetPreviewSession?.Document;
        SecondaryMotion.SetCollisionBoneNames(document?.CreateEffectiveBones().Select(bone => bone.Name) ?? []);
        if (ReferenceEquals(document, _secondaryDocument)) return;
        _secondaryDocument = document;
        _secondaryFitModel = null;
        _secondaryFitPackage = null;
        _nativeCollisionDocument = null;
        _nativeCollisionSources = [];
        ResetSecondaryMotionPreview();
        SecondaryMotionModelKey? key = document is null ? null : new(_project.ProjectId, document.ModelId,
            document.Source.ContentSha256, _targetProjectAsset?.ContentSha256 ?? Convert.ToHexStringLower(
                SHA256.HashData(CustomModelPackageSerializer.Serialize(_customTargetPreviewSession!.Package).AsSpan())),
            CustomModelContractSignatures.ComputeRig(document.CreateEffectiveBones()));
        SecondaryMotionModelSelection selection = _secondaryModelStates.Select(key, document?.SecondaryMotion ?? new(), SecondaryMotion.Definition);
        if (!selection.IdentityChanged) return;
        SecondaryMotion.Changed -= OnSecondaryMotionChanged;
        SecondaryMotion.Load(selection.Definition);
        SecondaryMotion.PersistenceStatus = selection.HasPendingEdits
            ? "Restored unsaved settings for this model. Save a model copy to keep them."
            : "Model settings loaded. To keep changes, save a model copy, open it in Models, then save the project.";
        SecondaryMotion.Changed += OnSecondaryMotionChanged;
        RefreshNativeCollisionOverlays(force: true);
    }

    private FbxModelAuthoringImportResult GetSecondaryFitModel()
    {
        CustomModelPreviewSession preview = _customTargetPreviewSession ??
            throw new InvalidOperationException("Select a custom model target before fitting a collision.");
        if (_secondaryFitModel is null || !ReferenceEquals(_secondaryFitPackage, preview.Package))
        {
            _secondaryFitModel = FbxModelAuthoringImporter.ImportPackage(preview.Package);
            _secondaryFitPackage = preview.Package;
        }
        return _secondaryFitModel;
    }

    private void FitSecondarySphereCollision()
    {
        try
        {
            if (SecondaryMotion.StartBoneName is not { } boneName)
                throw new InvalidOperationException("Choose a bone before fitting a sphere.");
            SecondaryMotion.SetCollisionProposal(SecondaryColliderAuthoring.FitSphere(GetSecondaryFitModel(), boneName));
            SecondaryMotion.Status = "Sphere fitted. Resize or move it, then apply.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
        {
            SecondaryMotion.Status = "Sphere fit failed: " + error.Message;
        }
    }

    private void FitSecondaryCapsuleCollision()
    {
        try
        {
            if (SecondaryMotion.StartBoneName is not { } start || SecondaryMotion.EndBoneName is not { } end)
                throw new InvalidOperationException("Choose both bones before fitting a capsule.");
            SecondaryMotion.SetCollisionProposal(SecondaryColliderAuthoring.FitCapsule(GetSecondaryFitModel(), start, end));
            SecondaryMotion.Status = "Capsule fitted. Resize or move it, then apply.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
        {
            SecondaryMotion.Status = "Capsule fit failed: " + error.Message;
        }
    }

    private void ApplySecondaryCollisionProposal()
    {
        try
        {
            if (SecondaryMotion.CurrentColliderProposal is not { } proposal)
                throw new InvalidOperationException("Enter finite offsets and a positive radius before applying.");
            SecondaryMotion.ApplyCollisionProposal(proposal);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            SecondaryMotion.Status = "Collision edit rejected: " + error.Message;
        }
    }

    private void RefreshNativeCollisionOverlays(bool force = false)
    {
        CustomModelDocument? document = _customTargetPreviewSession?.Document;
        ImmutableArray<NativeClothSource> sources = SecondaryMotion.Definition.NativeSources;
        if (!force && ReferenceEquals(document, _nativeCollisionDocument) &&
            _nativeCollisionSources.SequenceEqual(sources)) return;
        _nativeCollisionDocument = document;
        _nativeCollisionSources = sources;
        if (document is null || sources.IsEmpty)
        {
            ImmutableArray<string> details = document is null && !sources.IsEmpty
                ? DescribeNativeSourcesWithoutModel(sources)
                : [];
            SecondaryMotion.SetNativeCollisionOverlays(new([], details));
            return;
        }

        try
        {
            FbxModelAuthoringImportResult model = GetSecondaryFitModel();
            Dl1AuthoredRigContract prepared = Dl1CustomModelRigPreparer.Prepare(model).Contract;
            var bounds = prepared.Nodes.ToDictionary(node => node.Name, node => node.Bounds, StringComparer.Ordinal);
            Dl1NativeClothCollisionOverlayResolution resolution = Dl1NativeClothCollisionOverlayResolver.Resolve(
                SecondaryMotion.Definition, document.CreateEffectiveBones(), bounds);
            SecondaryMotion.SetNativeCollisionOverlays(resolution);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException)
        {
            SecondaryMotion.SetNativeCollisionOverlays(new([], ["Native collision overlays unavailable: " + error.Message]));
        }
    }

    private static ImmutableArray<string> DescribeNativeSourcesWithoutModel(ImmutableArray<NativeClothSource> sources)
    {
        var details = ImmutableArray.CreateBuilder<string>();
        foreach (NativeClothSource source in sources)
        {
            ImmutableArray<NativeClothDiagnostic> diagnostics = source.Kind == NativeClothSourceKind.Phx
                ? Dl1ClothCodec.ReadPhx(source.Text).Diagnostics
                : Dl1ClothCodec.ReadMpCloth(source.Text).Diagnostics;
            details.AddRange(diagnostics.Select(diagnostic => $"{source.ResourceName}: {diagnostic.Message}"));
        }
        details.Add("Select a model to resolve native collision positions from its retained element bounds.");
        return details.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Call after the final display skeleton has been rebased for the custom-model presentation.</summary>
    private SkeletonRenderData ApplySecondaryMotionPreview(ProjectAnimation animation, EvaluationRequest request,
        SkeletonRenderData display)
    {
        SynchronizeSecondaryMotionModel();
        display = ApplySecondaryActorScale(display);
        if (!SecondaryMotion.Enabled || SecondaryMotion.Definition.Groups.IsEmpty)
        {
            _secondarySession = null;
            _secondaryRequest = null;
            _secondaryLocalDeltas.Clear();
            bool showCollisionOverlay = SecondaryMotion.ShowCollisions &&
                (SecondaryMotion.NativeCollisionOverlays.Length > 0 || SecondaryMotion.CurrentColliderProposal is not null ||
                 SecondaryMotion.Definition.Groups.Any(group => !group.Colliders.IsEmpty));
            if (!showCollisionOverlay)
            { _secondaryGizmos = []; _secondaryExternalGizmos = []; return display; }
            try
            {
                bool fpp = request.PreviewProfile.Context == Dl1PreviewContext.Dl1Fpp;
                EvaluationFrame sampled = new AnimationEvaluator().Evaluate(request);
                SkeletonPose physicalPose = SecondaryMotionDomain.SelectPhysicsPose(sampled);
                SkeletonPose bind = request.TargetRig.CreateBindPose();
                SkeletonRenderData physicalSkeleton = CorePreviewAdapter.ToRenderSkeleton(
                    bind, actorWorldTransform: ScaleSecondaryActorTransform(sampled.ActorWorldTransform));
                physicalSkeleton = physicalSkeleton with { Bones = physicalSkeleton.Bones.Select((bone, index) => bone with
                    { WorldTransform = CorePreviewAdapter.ToSystemMatrix(physicalPose.GlobalMatrices[index]) }).ToArray() };
                if (fpp)
                {
                    _secondaryExternalGizmos = BuildSecondaryGizmos(physicalSkeleton, null);
                    _secondaryGizmos = [];
                }
                else
                {
                    _secondaryGizmos = BuildSecondaryGizmos(physicalSkeleton, null);
                    _secondaryExternalGizmos = [];
                }
                SecondaryMotion.Status = "Collision shapes previewed.";
                return display;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                _secondaryGizmos = [];
                _secondaryExternalGizmos = [];
                SecondaryMotion.Status = "Collision preview unavailable: " + exception.Message;
                return display;
            }
        }
        try
        {
            bool fpp = request.PreviewProfile.Context == Dl1PreviewContext.Dl1Fpp;
            bool samePhysicalFppDomain = fpp && _secondaryRequest?.PreviewProfile.Context == Dl1PreviewContext.Dl1Fpp;
            bool inputsChanged = _secondaryRequest is null || _secondaryAnimation != animation ||
                !samePhysicalFppDomain && (!SecondaryPreviewProfilesEquivalent(_secondaryRequest.PreviewProfile, request.PreviewProfile) || _secondaryRequest.Dl1PreviewInputs != request.Dl1PreviewInputs) ||
                _secondaryRequest.Clip != request.Clip || _secondaryRequest.PlaybackMode != request.PlaybackMode ||
                _secondaryRequest.PreviewMotionAccumulationEnabled != request.PreviewMotionAccumulationEnabled ||
                !_secondaryRequest.EditLayers.SequenceEqual(request.EditLayers) || !_secondaryRequest.IkLayers.SequenceEqual(request.IkLayers);
            if (inputsChanged) _secondarySession = null;
            _secondaryAnimation = animation;
            _secondaryRequest = request;
            HashSet<string> drivenNames = SecondaryMotion.Definition.Groups.Where(g => g.Enabled).SelectMany(g => g.Particles)
                .Where(p => !p.Fixed && p.DrivenBoneName is not null).Select(p => p.DrivenBoneName!).ToHashSet(StringComparer.Ordinal);
            var evaluator = new AnimationEvaluator();
            SecondaryMotionFrame SamplePhysics(double seconds)
            {
                var sampledRequest = new EvaluationRequest(request.SourceRig, request.TargetRig, request.Clip, seconds,
                    request.PreviewProfile, request.RetargetMap, request.EditLayers, request.IkConstraints, request.PlaybackMode,
                    request.Purpose, request.Attachments, request.Dl1AuthoringPolicy, request.MorphBindings, request.MorphEditLayers,
                    request.IkLayers, request.Dl1PreviewInputs, request.PreviewMotionAccumulationEnabled, request.DirectRigBinding);
                EvaluationFrame sampled = evaluator.Evaluate(sampledRequest);
                SkeletonPose physicalPose = SecondaryMotionDomain.SelectPhysicsPose(sampled);
                // Model-owned offsets are expressed in the imported/source bone axes. Chrome
                // presentation rebases those axes to +X; feeding its matrices to these offsets
                // would relocate virtual tips and colliders before the solver even starts.
                TransformMatrix actorWorld = ScaleSecondaryActorTransform(sampled.ActorWorldTransform);
                return new(physicalPose.GlobalMatrices, actorWorld);
            }
            SkeletonPose bind = request.TargetRig.CreateBindPose();
            SkeletonPose presentationBind = _customTargetPreviewSession?.CreatePresentationPose(bind) ?? bind;
            _secondarySession ??= new(SecondaryMotion.Definition, bind.Rig.Bones.Select(b => b.Name),
                new SecondaryMotionFrame(bind.GlobalMatrices, SamplePhysics(0).ActorWorldTransform),
                parentIndices: bind.Rig.Bones.Select(b => b.ParentIndex));
            SecondaryMotionResult simulated = _secondarySession.Sample(request.TimeSeconds, SamplePhysics);
            SecondaryMotionFrame physicalAnimated = SamplePhysics(request.TimeSeconds);
            _secondaryLocalDeltas.Clear();
            foreach (string name in drivenNames)
            {
                int sourceIndex = bind.Rig.GetBoneIndex(name);
                if (sourceIndex < 0) throw new ArgumentException($"Secondary output '{name}' is absent from the source rig.");
                int presentedIndex = _customTargetPreviewSession?.GetPresentationBoneIndex(sourceIndex) ?? sourceIndex;
                if ((uint)presentedIndex >= display.Bones.Count || display.Bones[presentedIndex].Role is BoneRenderRole.Camera or BoneRenderRole.Prop)
                    throw new ArgumentException($"Secondary output '{name}' has no valid deform/helper preview counterpart.");
                TransformMatrix sourceDelta = physicalAnimated.Globals[sourceIndex].InvertedAffine() * simulated.Globals[sourceIndex];
                _secondaryLocalDeltas[display.Bones[presentedIndex].Name] = SecondaryMotionRenderAdapter.RebaseBoneLocalDelta(
                    sourceDelta, bind.GlobalMatrices[sourceIndex], presentationBind.GlobalMatrices[presentedIndex]);
            }
            SkeletonRenderData physicalSkeleton = CorePreviewAdapter.ToRenderSkeleton(bind, actorWorldTransform: physicalAnimated.ActorWorldTransform);
            physicalSkeleton = physicalSkeleton with { Bones = physicalSkeleton.Bones.Select((bone, i) => bone with
                { WorldTransform = CorePreviewAdapter.ToSystemMatrix(physicalAnimated.Globals[i]) }).ToArray() };
            if (fpp)
            {
                _secondaryExternalGizmos = BuildSecondaryGizmos(physicalSkeleton, simulated);
                _secondaryGizmos = [];
            }
            else { _secondaryGizmos = BuildSecondaryGizmos(physicalSkeleton, simulated); _secondaryExternalGizmos = []; }
            SecondaryMotion.Status = $"Secondary motion preview · {simulated.Particles.Length} particles. " +
                (fpp ? "Guides appear in external orbit. First-person view adjustments do not affect the motion. " : string.Empty) +
                "Check native cloth and mesh collisions in game.";
            return ApplySecondaryMotionToExternalSkeleton(display);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ResetSecondaryMotionPreview();
            SecondaryMotion.Status = "Secondary preview unavailable: " + exception.Message;
            return display;
        }
    }

    /// <summary>For FPP external orbit, apply only the simulated bone-local deltas to its authored pose.</summary>
    private SkeletonRenderData ApplySecondaryMotionToExternalSkeleton(SkeletonRenderData skeleton)
        => SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(skeleton, _secondaryLocalDeltas);

    private SkeletonRenderData ApplySecondaryActorScale(SkeletonRenderData skeleton)
    {
        if (SecondaryMotion.PreviewActorScale == 1) return skeleton;
        TransformMatrix root = ScaleSecondaryActorTransform(CorePreviewAdapter.ToCoreMatrix(skeleton.RootTransform));
        return skeleton with { RootTransform = CorePreviewAdapter.ToSystemMatrix(root) };
    }

    private TransformMatrix ScaleSecondaryActorTransform(TransformMatrix actorWorld) => actorWorld *
        TransformMatrix.CreateScale(new(SecondaryMotion.PreviewActorScale, SecondaryMotion.PreviewActorScale, SecondaryMotion.PreviewActorScale));

    // Runtime variants and FPP profiles are recreated during ordinary playback. Storage/reference
    // identity would restart the entire bind preroll on every frame even when their values agree.
    internal static bool SecondaryPreviewProfilesEquivalent(PreviewProfile first, PreviewProfile second) =>
        first.Id == second.Id && first.ViewMode == second.ViewMode && first.Fidelity == second.Fidelity &&
        first.VisualStyle == second.VisualStyle && first.CameraBoneName == second.CameraBoneName &&
        first.CameraLens == second.CameraLens && first.CameraOffset == second.CameraOffset &&
        first.FidelityTier == second.FidelityTier && first.Context == second.Context &&
        first.ProfileVersion == second.ProfileVersion && first.BuildFingerprint == second.BuildFingerprint &&
        first.CaptureFingerprint == second.CaptureFingerprint &&
        first.ProceduralToggles.SequenceEqual(second.ProceduralToggles) &&
        first.MorphActivationThreshold == second.MorphActivationThreshold &&
        first.MaximumActiveMorphTargets == second.MaximumActiveMorphTargets &&
        first.ClampMorphWeightsToRigBounds == second.ClampMorphWeightsToRigBounds;

    private GizmoRenderData[] BuildSecondaryGizmos(SkeletonRenderData skeleton, SecondaryMotionResult? result)
    {
        List<GizmoRenderData> lines = [];
        var world = skeleton.Bones.ToDictionary(b => b.Name,
            b => CorePreviewAdapter.ToCoreMatrix(b.WorldTransform * skeleton.RootTransform), StringComparer.Ordinal);
        if (SecondaryMotion.ShowAnchors && result is not null)
        {
            foreach (SecondaryMotionGroup group in SecondaryMotion.Definition.Groups.Where(g => g.Enabled))
            {
                SecondaryMotionParticleState[] particles = result.Particles.Where(p => p.Group == group.Name).ToArray();
                foreach (SecondaryDistanceConstraint link in group.Constraints)
                    Line(particles[link.First].WorldPosition, particles[link.Second].WorldPosition, new(0.1f, 0.8f, 1, 1));
                foreach (SecondaryMotionParticleState p in particles.Where(p => p.Fixed))
                { Circle(p.WorldPosition, 0.012, Vector3D.UnitX, Vector3D.UnitZ, new(1, 0.7f, 0.05f, 1)); }
            }
        }
        if (SecondaryMotion.ShowCollisions)
        {
            foreach (SecondaryMotionGroup group in SecondaryMotion.Definition.Groups.Where(g => g.Enabled))
            {
                for (int index = 0; index < group.Colliders.Length; index++)
                {
                    SecondaryCollider collider = group.Colliders[index];
                    if (group.Name == SecondaryMotion.SelectedGroup && SecondaryMotion.SelectedColliderIndex == index &&
                        SecondaryMotion.CurrentColliderProposal is { } draft)
                        collider = draft;
                    DrawBoneCollider(collider, new(1, 0.35f, 0.2f, 0.8f));
                }
            }
            if (SecondaryMotion.CurrentColliderProposal is { } proposal && SecondaryMotion.SelectedColliderIndex is null)
                DrawBoneCollider(proposal, new(1, 0.8f, 0.1f, 1));

            TransformMatrix root = CorePreviewAdapter.ToCoreMatrix(skeleton.RootTransform);
            double nativeRootScale = (root.TransformDirection(Vector3D.UnitX).Length +
                root.TransformDirection(Vector3D.UnitY).Length + root.TransformDirection(Vector3D.UnitZ).Length) / 3;
            foreach (Dl1NativeClothCollisionOverlay native in SecondaryMotion.NativeCollisionOverlays)
            {
                if (!world.TryGetValue(native.BoneName, out TransformMatrix start)) continue;
                Vector3D a = start.TransformPoint(native.LocalPosition);
                Vector3D b = native.EndBoneName is { } endName && world.TryGetValue(endName, out TransformMatrix end)
                    ? end.TransformPoint(native.EndLocalPosition) : a;
                DrawCapsule(a, b, native.Radius * nativeRootScale, new(0.5f, 0.75f, 1, 0.85f));
            }
        }
        return lines.ToArray();
        void Line(Vector3D a, Vector3D b, Vector4 color) => lines.Add(new(GizmoKind.Line,
            new((float)a.X, (float)a.Y, (float)a.Z), new((float)b.X, (float)b.Y, (float)b.Z), color, 1.2f));
        void Circle(Vector3D center, double radius, Vector3D x, Vector3D y, Vector4 color)
        {
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.Tau / 24, b = (i + 1) * Math.Tau / 24;
                Line(center + radius * (x * Math.Cos(a) + y * Math.Sin(a)), center + radius * (x * Math.Cos(b) + y * Math.Sin(b)), color);
            }
        }
        void DrawBoneCollider(SecondaryCollider collider, Vector4 color)
        {
            if (!world.TryGetValue(collider.BoneName, out TransformMatrix start)) return;
            TransformMatrix end = collider.EndBoneName is { } endName && world.TryGetValue(endName, out TransformMatrix endBone)
                ? endBone : start;
            Vector3D a = start.TransformPoint(collider.LocalPosition);
            Vector3D b = collider.EndBoneName is null ? a : end.TransformPoint(collider.EndLocalPosition);
            double scaleA = MaxBasisScale(start);
            double scaleB = collider.EndBoneName is null ? scaleA : MaxBasisScale(end);
            DrawCapsule(a, b, collider.Radius * Math.Max(scaleA, scaleB), color);
        }
        void DrawCapsule(Vector3D a, Vector3D b, double radius, Vector4 color)
        {
            Vector3D axis = b - a;
            Vector3D direction = axis.TryNormalize(out Vector3D normalized) ? normalized : Vector3D.UnitY;
            Vector3D reference = Math.Abs(direction.X) < 0.8 ? Vector3D.UnitX : Vector3D.UnitY;
            Vector3D first = Vector3D.Cross(direction, reference).Normalized();
            Vector3D second = Vector3D.Cross(direction, first).Normalized();
            foreach (Vector3D point in new[] { a, b })
            {
                Circle(point, radius, first, second, color);
                Circle(point, radius, direction, first, color);
                Circle(point, radius, direction, second, color);
            }
            foreach (Vector3D offset in new[] { first * radius, -first * radius, second * radius, -second * radius })
                Line(a + offset, b + offset, color);
        }
        static double MaxBasisScale(TransformMatrix matrix) => Math.Max(
            matrix.TransformDirection(Vector3D.UnitX).Length,
            Math.Max(matrix.TransformDirection(Vector3D.UnitY).Length, matrix.TransformDirection(Vector3D.UnitZ).Length));
    }

    private void LoadSecondarySetup()
    {
        OpenFileDialog dialog = new() { Title = "Load secondary motion setup", Filter = "Secondary setup (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() =>
        {
            SecondaryMotion.Load(SecondaryMotionSetupSerializer.Deserialize(ReadBounded(dialog.FileName), _targetRig?.Bones.Select(b => b.Name)));
            SecondaryMotion.PersistenceStatus = "Setup loaded. Save a model copy to keep it with the model.";
        });
    }
    private void SaveSecondarySetup()
    {
        SaveFileDialog dialog = new() { Title = "Save secondary motion setup", Filter = "Secondary setup (*.json)|*.json", DefaultExt = ".json" };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() => File.WriteAllText(dialog.FileName, SecondaryMotionSetupSerializer.Serialize(SecondaryMotion.Definition)));
    }
    private void ImportNativeCloth()
    {
        OpenFileDialog dialog = new() { Title = "Import native cloth scripts", Filter = "Native cloth (*.phx;*.mpcloth)|*.phx;*.mpcloth", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() =>
        {
            var sources = SecondaryMotion.Definition.NativeSources.ToBuilder();
            List<string> diagnostics = [];
            foreach (string path in dialog.FileNames)
            {
                string text = ReadBounded(path);
                NativeClothSourceKind kind = Path.GetExtension(path).Equals(".phx", StringComparison.OrdinalIgnoreCase) ? NativeClothSourceKind.Phx : NativeClothSourceKind.MpCloth;
                ImmutableArray<NativeClothDiagnostic> report = kind == NativeClothSourceKind.Phx ? Dl1ClothCodec.ReadPhx(text).Diagnostics : Dl1ClothCodec.ReadMpCloth(text).Diagnostics;
                diagnostics.AddRange(report.Select(d => d.Message));
                var source = new NativeClothSource { Kind = kind, ResourceName = Path.GetFileName(path), Text = text };
                int existing = -1;
                for (int index = 0; index < sources.Count; index++)
                    if (sources[index].ResourceName.Equals(source.ResourceName, StringComparison.OrdinalIgnoreCase)) { existing = index; break; }
                if (existing >= 0) sources[existing] = source;
                else sources.Add(source);
            }
            SecondaryMotion.Load(SecondaryMotion.Definition with { NativeSources = sources.ToImmutable() });
            SecondaryMotion.Status = "Cloth files imported.";
            if (dialog.FileNames.Length > 0 && diagnostics.Count > 0)
                SecondaryMotion.CollisionDetails = string.Join(Environment.NewLine,
                    SecondaryMotion.CollisionDetails.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                        .Concat(diagnostics).Distinct(StringComparer.Ordinal));
            SecondaryMotion.PersistenceStatus = "Save a model copy to keep the imported cloth files.";
        });
    }
    private void ExportNativeCloth()
    {
        OpenFolderDialog dialog = new() { Title = "Export native cloth sources to a new folder" };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() =>
        {
            foreach (NativeClothSource source in SecondaryMotion.Definition.NativeSources)
            {
                string path = Path.Combine(dialog.FolderName, source.ResourceName);
                if (File.Exists(path) && File.ReadAllText(path) != source.Text) throw new IOException("A different native source already exists at " + path);
            }
            foreach (NativeClothSource source in SecondaryMotion.Definition.NativeSources)
            {
                string path = Path.Combine(dialog.FolderName, source.ResourceName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source.Text);
            }
            SecondaryMotion.Status = "Cloth files exported. Check them in Developer Tools and in game.";
        });
    }
    private void SaveSecondaryModelCopy()
    {
        if (_customTargetPreviewSession is not { } preview) { SecondaryMotion.Status = "Select a custom model target first."; return; }
        SaveFileDialog dialog = new() { Title = "Save a new model package with secondary motion", Filter = "Model package (*.dlrmodel)|*.dlrmodel", DefaultExt = ".dlrmodel" };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() =>
        {
            CustomModelPackage package = preview.Package;
            CustomModelDocument document = package.Document with { SecondaryMotion = SecondaryMotion.Definition, FacialPresets = FacialFpp.FacialLibrary, LastBuildReceipt = null };
            document.Validate();
            var updated = package with { Document = document };
            ImmutableArray<byte> bytes = CustomModelPackageSerializer.Serialize(updated);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            string path = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "-" + hash[..16] + ".dlrmodel");
            if (File.Exists(path))
            {
                if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes.AsSpan()))) throw new IOException("Content-addressed model path collision.");
            }
            else CustomModelPackageSerializer.SaveAtomic(updated, path);
            SecondaryMotion.PersistenceStatus = "Saved model copy: " + path + ". Open it in Models and save the project to keep the setup.";
        });
    }
    private void SecondaryOperation(Action operation)
    {
        try { operation(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or FormatException or JsonException)
        { SecondaryMotion.Status = exception.Message; AddDiagnostic("Error", "Secondary motion", "Secondary operation failed", exception.Message); }
    }
    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Secondary source exceeds the text limit.");
        return File.ReadAllText(path);
    }
}
