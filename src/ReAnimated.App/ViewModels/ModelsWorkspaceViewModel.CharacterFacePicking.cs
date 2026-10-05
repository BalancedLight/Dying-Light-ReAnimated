using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private sealed record FaceSelection(FbxModelAuthoringImportResult Model, string SurfaceId, int Vertex, int Triangle);
    private FaceSelection? _pickedReferenceFace, _pickedTargetFace;
    private FacePickInput? _facePickInput;
    private string _facePickStatus = "Pick a point on the original reference and its matching target point to add a landmark or triangle pair.";
    private RelayCommand? _pickReferenceFaceCommand, _pickTargetFaceCommand, _stopFacePickingCommand,
        _addPickedLandmarkCommand, _addPickedTriangleCommand, _addPickedRegionCommand, _addPickedLockCommand;
    public string FacePickStatus { get => _facePickStatus; private set => SetProperty(ref _facePickStatus, value); }
    public RelayCommand PickReferenceFaceCommand => _pickReferenceFaceCommand ??= new(() => BeginFacePicking(reference: true));
    public RelayCommand PickTargetFaceCommand => _pickTargetFaceCommand ??= new(() => BeginFacePicking(reference: false));
    public RelayCommand StopFacePickingCommand => _stopFacePickingCommand ??= new(() => { StopFacePicking(); RefreshPreview(); ExpressionPreviewSubject = "Neutral"; });
    public RelayCommand AddPickedLandmarkCommand => _addPickedLandmarkCommand ??= new(() => AddPickedPair(triangle: false));
    public RelayCommand AddPickedTriangleCommand => _addPickedTriangleCommand ??= new(() => AddPickedPair(triangle: true));
    public RelayCommand AddPickedRegionCommand => _addPickedRegionCommand ??= new(() => AddPickedTargetVertex(locked: false));
    public RelayCommand AddPickedLockCommand => _addPickedLockCommand ??= new(() => AddPickedTargetVertex(locked: true));

    private void BeginFacePicking(bool reference)
    {
        var model = reference ? _morphReference : _model;
        string? surfaceId = reference ? SelectedReferenceSurface : SelectedCharacterSurface;
        if (model is null || surfaceId is null)
        {
            FacePickStatus = reference ? "Load an original character reference and select its face surface first." : "Load the target model and select its face surface first.";
            return;
        }
        try
        {
            StopFacePicking();
            Timeline.StopCommand.Execute(null);
            FbxModelSurface surface = model.Surfaces.Single(s => s.Id == surfaceId);
            MorphSurfacePicking picker = MorphSurfacePicking.Build(surface);
            var isolated = model with { Surfaces = [surface] };
            var session = CustomModelPreviewAdapter.CreateSession(isolated);
            Viewport.SceneSource.SetExternalPreviewScene(null);
            Viewport.SceneSource.SetScene(session.Meshes.Select(m => m with { IsSkinned = false, InverseBindMatrices = ReadOnlyMemory<Matrix4x4>.Empty, SkinBoneIndices = ReadOnlyMemory<int>.Empty }).ToArray(), null, [],
                generation: Interlocked.Increment(ref _previewGeneration));
            Viewport.SceneSource.SetMorphWeights([]);
            Viewport.SceneSource.SetMeshVisibility(true);
            Viewport.SceneSource.SetTranslationGizmoTarget(null);
            Viewport.SceneSource.SetTransformGizmoTarget(null);
            _cameraCoordinator.SetTargetPreviewCameraOverride(null);
            _facePickInput = new(this, model, surface, picker, reference);
            Viewport.SceneSource.SetBrushTarget(_facePickInput);
            ExpressionPreviewSubject = reference ? "Original neutral" : "Neutral";
            FacePickStatus = (reference ? "Original reference" : "Target") + ": click the neutral face to select its nearest triangle corner. Dragging is not required. Use middle drag or the wheel to adjust the view.";
            FrameModel();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        { StopFacePicking(); FacePickStatus = "Face picking rejected: " + error.Message; }
    }

    private bool IsFacePickCurrent(FacePickInput input) => !_disposed && IsCharacterTabSelected &&
        ReferenceEquals(input.Model, input.Reference ? _morphReference : _model) &&
        input.Surface.Id == (input.Reference ? SelectedReferenceSurface : SelectedCharacterSurface) && ReferenceEquals(input, _facePickInput);

    private void StopFacePicking()
    {
        if (_facePickInput is not null) _previewSession = null;
        _facePickInput = null;
        Viewport.SceneSource.SetBrushTarget(null);
    }
    private void InvalidateFaceSelections()
    {
        StopFacePicking();
        _pickedReferenceFace = null; _pickedTargetFace = null;
        FacePickStatus = "Face or expression selection changed. Pick corresponding points again.";
    }
    private bool PreserveFacePickingPreview()
    {
        if (_facePickInput is null) return false;
        if (IsFacePickCurrent(_facePickInput)) return true;
        StopFacePicking(); return false;
    }
    private void CommitFacePick(FacePickInput input, MorphSurfacePickHit hit)
    {
        if (!IsFacePickCurrent(input)) return;
        var selection = new FaceSelection(input.Model, input.Surface.Id, hit.VertexIndex, hit.TriangleIndex);
        if (input.Reference) _pickedReferenceFace = selection; else _pickedTargetFace = selection;
        FacePickStatus = $"{(input.Reference ? "Original reference" : "Target")} selected vertex {hit.VertexIndex}, triangle {hit.TriangleIndex}. Pick the matching point on the other face, then add the pair.";
        var vertices = input.Surface.Vertices;
        int offset = checked(hit.TriangleIndex * 3);
        int[] triangle = Enumerable.Range(0, 3).Select(i => checked((int)input.Surface.Indices[offset + i])).ToArray();
        var lines = new List<GizmoRenderData>();
        for (int i = 0; i < 3; i++) lines.Add(new(GizmoKind.Line, ToRender(vertices[triangle[i]].Position),
            ToRender(vertices[triangle[(i + 1) % 3]].Position), new(1, .75f, .1f, 1), 3));
        Vector3 center = ToRender(vertices[hit.VertexIndex].Position);
        float radius = Math.Max(0.0005f, triangle.Select(i => Vector3.Distance(center, ToRender(vertices[i].Position))).Max() * .03f);
        foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            lines.Add(new(GizmoKind.Line, center - axis * radius, center + axis * radius, new(0.2f, 1, .3f, 1), 3));
        Viewport.SceneSource.SetGizmos(lines);
    }
    private bool TryGetPickedPair(out FaceSelection reference, out FaceSelection target)
    {
        reference = _pickedReferenceFace!; target = _pickedTargetFace!;
        if (reference is null || target is null || !ReferenceEquals(reference.Model, _morphReference) || !ReferenceEquals(target.Model, _model) ||
            reference.SurfaceId != SelectedReferenceSurface || target.SurfaceId != SelectedCharacterSurface)
        { FacePickStatus = "Pick a current original-reference point and its corresponding target point first."; return false; }
        return true;
    }
    private void AddPickedPair(bool triangle)
    {
        if (!TryGetPickedPair(out var reference, out var target)) return;
        string pair = string.Create(CultureInfo.InvariantCulture, $"{(triangle ? reference.Triangle : reference.Vertex)} {(triangle ? target.Triangle : target.Vertex)}");
        if (triangle) ExpressionCorrespondences = AddUniqueLine(ExpressionCorrespondences, pair);
        else ExpressionLandmarks = AddUniqueLine(ExpressionLandmarks, pair + " 1");
        FacePickStatus = triangle ? "Triangle pair added. Check the correspondence before proposing transfer." : "Landmark pair added. Check its correspondence and weight before proposing transfer.";
    }
    private void AddPickedTargetVertex(bool locked)
    {
        var target = _pickedTargetFace;
        if (target is null || !ReferenceEquals(target.Model, _model) || target.SurfaceId != SelectedCharacterSurface)
        { FacePickStatus = "Pick a point on the current target face first."; return; }
        string index = target.Vertex.ToString(CultureInfo.InvariantCulture);
        string current = locked ? ExpressionLockedVertices : ExpressionTargetRegion;
        string revised = string.Join(" ", current.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Append(index).Distinct(StringComparer.Ordinal));
        if (locked) ExpressionLockedVertices = revised; else ExpressionTargetRegion = revised;
        FacePickStatus = locked ? "Selected target vertex locked to neutral for transfer." : "Selected target vertex added to the reviewed face region.";
    }
    private static string AddUniqueLine(string current, string line) => string.Join(Environment.NewLine,
        current.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Append(line).Distinct(StringComparer.Ordinal));
    private static Vector3 ToRender(Vector3D position) => new((float)position.X, (float)position.Y, (float)position.Z);

    private sealed class FacePickInput(ModelsWorkspaceViewModel owner, FbxModelAuthoringImportResult model,
        FbxModelSurface surface, MorphSurfacePicking picker, bool reference) : IRenderBrushTarget
    {
        private MorphSurfacePickHit? _pending;
        public FbxModelAuthoringImportResult Model { get; } = model;
        public FbxModelSurface Surface { get; } = surface;
        public bool Reference { get; } = reference;
        public bool IsBrushEnabled => owner.IsFacePickCurrent(this);
        public bool TryBeginBrush(RenderBrushPointerRay ray)
        {
            if (!IsBrushEnabled) return false;
            _pending = picker.Pick(new(ray.Origin.X, ray.Origin.Y, ray.Origin.Z), new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
            return _pending is not null;
        }
        public bool UpdateBrush(RenderBrushPointerRay ray) => IsBrushEnabled;
        public void CompleteBrush(bool commit)
        {
            MorphSurfacePickHit? picked = _pending; _pending = null;
            if (commit && picked is not null && IsBrushEnabled) owner.CommitFacePick(this, picked);
        }
    }
}
