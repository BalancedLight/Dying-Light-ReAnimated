using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ReAnimated.App.Infrastructure;
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
    }

    private void DisposeSecondaryMotionFeature() => SecondaryMotion.Changed -= OnSecondaryMotionChanged;

    private void OnSecondaryMotionChanged(object? sender, EventArgs args)
    {
        ResetSecondaryMotionPreview();
        RefreshEditableSkeletonPreview();
    }

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
        if (ReferenceEquals(document, _secondaryDocument)) return;
        _secondaryDocument = document;
        ResetSecondaryMotionPreview();
        SecondaryMotionModelKey? key = document is null ? null : new(_project.ProjectId, document.ModelId,
            document.Source.ContentSha256, _targetProjectAsset?.ContentSha256 ?? Convert.ToHexStringLower(
                SHA256.HashData(CustomModelPackageSerializer.Serialize(_customTargetPreviewSession!.Package).AsSpan())));
        SecondaryMotionModelSelection selection = _secondaryModelStates.Select(key, document?.SecondaryMotion ?? new(), SecondaryMotion.Definition);
        if (!selection.IdentityChanged) return;
        SecondaryMotion.Changed -= OnSecondaryMotionChanged;
        SecondaryMotion.Load(selection.Definition);
        SecondaryMotion.PersistenceStatus = selection.HasPendingEdits
            ? "Restored pending settings for this exact model package. Save a model copy to persist them."
            : "Model settings loaded. Save model copy creates a new package; open that copy in Models and save the project to retain edits.";
        SecondaryMotion.Changed += OnSecondaryMotionChanged;
    }

    /// <summary>Call after the final display skeleton has been rebased for the custom-model presentation.</summary>
    private SkeletonRenderData ApplySecondaryMotionPreview(ProjectAnimation animation, EvaluationRequest request,
        SkeletonRenderData display)
    {
        SynchronizeSecondaryMotionModel();
        if (!SecondaryMotion.Enabled || SecondaryMotion.Definition.Groups.IsEmpty)
        { _secondaryLocalDeltas.Clear(); _secondaryGizmos = []; _secondaryExternalGizmos = []; return display; }
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
                return new(physicalPose.GlobalMatrices, sampled.ActorWorldTransform);
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
            SecondaryMotion.Status = $"MPC preview approximation · {simulated.Particles.Length} particles · stable 120 Hz replay. " +
                (fpp ? "Physics uses the authored model; FPP view corrections cannot drive cloth. Overlays are in external orbit. " : string.Empty) +
                "Native and mesh-collision validation remain separate.";
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

    private GizmoRenderData[] BuildSecondaryGizmos(SkeletonRenderData skeleton, SecondaryMotionResult result)
    {
        List<GizmoRenderData> lines = [];
        var world = skeleton.Bones.ToDictionary(b => b.Name,
            b => CorePreviewAdapter.ToCoreMatrix(b.WorldTransform * skeleton.RootTransform), StringComparer.Ordinal);
        if (SecondaryMotion.ShowAnchors)
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
            foreach (SecondaryCollider c in SecondaryMotion.Definition.Groups.Where(g => g.Enabled).SelectMany(g => g.Colliders))
            {
                Vector3D a = world[c.BoneName].TransformPoint(c.LocalPosition);
                Vector3D b = c.EndBoneName is { } end ? world[end].TransformPoint(c.EndLocalPosition) : a;
                double radius = c.Radius * world[c.BoneName].TransformDirection(Vector3D.UnitX).Length;
                foreach (Vector3D p in new[] { a, b })
                {
                    Circle(p, radius, Vector3D.UnitX, Vector3D.UnitY, new(1, 0.35f, 0.2f, 0.8f));
                    Circle(p, radius, Vector3D.UnitX, Vector3D.UnitZ, new(1, 0.35f, 0.2f, 0.8f));
                    Circle(p, radius, Vector3D.UnitY, Vector3D.UnitZ, new(1, 0.35f, 0.2f, 0.8f));
                }
                if (a != b) foreach (Vector3D offset in new[] { Vector3D.UnitX * radius, -Vector3D.UnitX * radius, Vector3D.UnitZ * radius, -Vector3D.UnitZ * radius })
                    Line(a + offset, b + offset, new(1, 0.35f, 0.2f, 0.8f));
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
    }

    private void LoadSecondarySetup()
    {
        OpenFileDialog dialog = new() { Title = "Load secondary motion setup", Filter = "Secondary setup (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        SecondaryOperation(() =>
        {
            SecondaryMotion.Load(SecondaryMotionSetupSerializer.Deserialize(ReadBounded(dialog.FileName), _targetRig?.Bones.Select(b => b.Name)));
            SecondaryMotion.PersistenceStatus = "Setup loaded for preview. Save a model copy to include it in a model package.";
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
            SecondaryMotion.Status = "Native text retained without creating a guessed preview setup. " + string.Join(" ", diagnostics.Distinct());
            SecondaryMotion.PersistenceStatus = "Native sources are pending model edits; save a model copy to retain them.";
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
            SecondaryMotion.Status = "Native sources exported losslessly; this is not compiler or runtime validation.";
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
            var updated = new CustomModelPackage(document, package.SourceFbx, package.TexturePayloads);
            ImmutableArray<byte> bytes = CustomModelPackageSerializer.Serialize(updated);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            string path = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "-" + hash[..16] + ".dlrmodel");
            if (File.Exists(path))
            {
                if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes.AsSpan()))) throw new IOException("Content-addressed model path collision.");
            }
            else CustomModelPackageSerializer.SaveAtomic(updated, path);
            SecondaryMotion.PersistenceStatus = "Saved immutable model copy: " + path + ". Open this copy in Models and save the project to retain the setup.";
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
