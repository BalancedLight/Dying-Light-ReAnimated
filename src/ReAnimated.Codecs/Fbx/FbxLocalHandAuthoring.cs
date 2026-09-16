using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Immutable source observation and local hand proposals; it never owns an emitted rig.</summary>
public sealed record FbxHandDetectionWork
{
    internal FbxModelAuthoringImportResult Model { get; }
    public RiggingSession Session { get; }
    public RiggingJobToken Token { get; }
    public LocalHandDetectionResult Detection { get; }
    public SourceVolumeGrid Grid { get; }
    public string ComponentId { get; }
    public Vector3D Wrist { get; }

    internal FbxHandDetectionWork(FbxModelAuthoringImportResult model, RiggingSession session,
        RiggingJobToken token, LocalHandDetectionResult detection, SourceVolumeGrid grid,
        string componentId, Vector3D wrist)
    {
        Model = model; Session = session; Token = token; Detection = detection; Grid = grid;
        ComponentId = componentId; Wrist = wrist;
    }
}

/// <summary>
/// Adapts one preserved FBX draw component into a local, bind-deformed volume for
/// reviewable hand proposals. The source mesh, skin weights and rig are never changed.
/// </summary>
public static class FbxLocalHandAuthoring
{
    public static FbxHandDetectionWork Detect(FbxModelAuthoringImportResult model, string componentId,
        Vector3D minimum, Vector3D maximum, LocalHandDetectionOptions options,
        SourceVolumeGridOptions gridOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gridOptions);
        cancellationToken.ThrowIfCancellationRequested();

        CustomModelDocument sourceDocument = model.Package.Document;
        sourceDocument.Validate();
        RigStudioEntryPath expectedEntry = sourceDocument.Bones.IsEmpty
            ? RigStudioEntryPath.AutoRigBiped
            : RigStudioEntryPath.AdaptExistingRig;
        RiggingSession session = sourceDocument.RiggingSession ?? RiggingSessions.Create(sourceDocument, expectedEntry);
        bool compatible = sourceDocument.Bones.IsEmpty ? session.EntryPath == RigStudioEntryPath.AutoRigBiped :
            session.EntryPath is RigStudioEntryPath.AdaptExistingRig or RigStudioEntryPath.RepairExistingRig ||
            GeneratedBodyRig.IsGenerated(sourceDocument with { RiggingSession = session });
        if (!compatible || !session.MatchesSource(sourceDocument.Source.ContentSha256))
            throw new InvalidOperationException("Hand detection requires a current AutoRigBiped or AdaptExistingRig source session.");

        FbxModelAuthoringImportResult observedModel = ReferenceEquals(sourceDocument.RiggingSession, session)
            ? model
            : model with
            {
                Package = model.Package with
                {
                    Document = sourceDocument with { RiggingSession = session },
                },
            };
        RiggingJobToken token = session.CreateJobToken();
        LocalHandDetectionOptions effectiveOptions = PreserveLockedGuides(session, options);
        SourceGeometryComponentAnalysis component = BuildComponent(observedModel, componentId, cancellationToken);
        SourceGeometryAnalysis analysis = new(sourceDocument.Source.ContentSha256, [component]);
        SourceGeometryVolume volume = SourceGeometryVolume.Build(analysis, cancellationToken: cancellationToken);
        SourceVolumeGrid grid = SourceVolumeGrid.BuildRegion(volume, minimum, maximum, gridOptions,
            cancellationToken: cancellationToken);
        LocalHandDetectionResult detection = LocalHandDetector.Detect(grid, effectiveOptions, cancellationToken);
        return new(observedModel, session, token, detection, grid, componentId, effectiveOptions.Wrist);
    }

    /// <summary>
    /// Refreshes a work item after workflow navigation. Source, surfaces, bones,
    /// rig and the captured session token must still identify the same observation.
    /// </summary>
    public static FbxHandDetectionWork? RefreshMetadata(FbxHandDetectionWork work,
        FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(current);
        FbxModelAuthoringImportResult previous = work.Model;
        CustomModelDocument before = previous.Package.Document;
        CustomModelDocument after = current.Package.Document;
        if (previous.Surfaces != current.Surfaces || !ReferenceEquals(previous.Rig, current.Rig) ||
            before.ModelId != after.ModelId || before.Source.ContentSha256 != after.Source.ContentSha256 ||
            previous.Package.SourceFbx != current.Package.SourceFbx || before.RigSignature != after.RigSignature ||
            !before.Bones.SequenceEqual(after.Bones) || !before.AuthoredHelpers.SequenceEqual(after.AuthoredHelpers) || after.RiggingSession?.Matches(work.Token) != true)
            return null;
        return new(current, after.RiggingSession!, work.Token, work.Detection, work.Grid, work.ComponentId, work.Wrist);
    }

    private static LocalHandDetectionOptions PreserveLockedGuides(RiggingSession session,
        LocalHandDetectionOptions options)
    {
        string side = options.Side;
        if (side is not ("left" or "right")) return options;
        RigLandmark[] locked = session.Landmarks
            .Where(landmark => landmark.Locked && IsSideRole(landmark.RoleId, side))
            .ToArray();
        if (locked.Length == 0) return options;
        var byRole = locked.ToDictionary(static landmark => landmark.RoleId, StringComparer.Ordinal);
        Vector3D wrist = byRole.TryGetValue($"hand.{side}", out RigLandmark? wristGuide)
            ? wristGuide.Position
            : options.Wrist;
        var guides = options.Guides.ToDictionary(static guide => guide.Role, StringComparer.Ordinal);
        foreach (RigLandmark guide in locked)
            guides[guide.RoleId] = new AnatomicalGuide(guide.RoleId, guide.Position, true);
        return options with { Wrist = wrist, Guides = guides.Values.OrderBy(static guide => guide.Role, StringComparer.Ordinal).ToImmutableArray() };
    }

    private static bool IsSideRole(string role, string side) =>
        role == $"hand.{side}" || role.StartsWith($"finger.{side}.", StringComparison.Ordinal);

    internal static SourceGeometryComponentAnalysis BuildComponent(FbxModelAuthoringImportResult model,
        string componentId, CancellationToken cancellationToken)
    {
        CustomModelDocument document = model.Package.Document;
        FbxModelSurface[] surfaces = model.Surfaces.Where(surface => surface.SourceGeometry?.Id == componentId).ToArray();
        if (surfaces.Length == 0) throw new ArgumentException("The selected source component is missing.", nameof(componentId));
        GeometrySourceComponent source = surfaces[0].SourceGeometry!;
        if (source.ControlPoints.IsDefault || source.ControlPoints.Length == 0 || source.ControlPoints.Any(static point => !point.IsFinite) ||
            source.Coordinates is not { } coordinates || !double.IsFinite(coordinates.MetersPerSourceUnit) ||
            coordinates.MetersPerSourceUnit <= 0 || !coordinates.SourceToAuthoring.IsFinite || source.Skinning is not { } skinning)
            throw new InvalidDataException("Hand detection requires complete normalized source geometry and skin provenance.");
        skinning.Validate(source.ControlPoints.Length, cancellationToken);
        foreach (FbxModelSurface surface in surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(source, surface.SourceGeometry) &&
                (!source.ControlPoints.SequenceEqual(surface.SourceGeometry!.ControlPoints) || source.Coordinates != surface.SourceGeometry.Coordinates ||
                 !SameSkinning(source.Skinning, surface.SourceGeometry.Skinning, cancellationToken)))
                throw new InvalidDataException("Render partitions disagree about source geometry or skin provenance.");
        }

        ImmutableArray<CustomModelBone> bones = document.CreateEffectiveBones();
        TransformMatrix[] globals = new TransformMatrix[bones.Length];
        foreach (CustomModelBone bone in bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var positions = new Vector3D?[source.ControlPoints.Length];
        var used = new bool[source.ControlPoints.Length];
        var triangles = new Dictionary<GeometrySourceTriangle, SourceGeometryAnalysisTriangle>();
        foreach (FbxModelSurface surface in surfaces)
        {
            if (surface.SourceGeometry is null || surface.SourceCorners.IsDefault || surface.SourceTriangles.IsDefault ||
                surface.SourceCorners.Length != surface.Vertices.Length || surface.Indices.IsDefault || surface.Indices.Length % 3 != 0 ||
                surface.SourceTriangles.Length * 3 != surface.Indices.Length || surface.Indices.Any(index => index >= surface.Vertices.Length) ||
                surface.IsSkinned && surface.InverseBindMatrices.Length != surface.PaletteBoneIndices.Length)
                throw new InvalidDataException("The selected draw is missing source corner, triangle or skin correspondence.");
            Vector3D[] drawPositions = new Vector3D[surface.Vertices.Length];
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                drawPositions[i] = CurrentPosition(surface, i, globals);
                if (!drawPositions[i].IsFinite) throw new InvalidDataException("The selected draw contains a non-finite bind-deformed position.");
                GeometrySourceCorner corner = surface.SourceCorners[i];
                if ((uint)corner.ControlPointIndex >= (uint)source.ControlPoints.Length || corner.PolygonVertexIndex < 0)
                    throw new InvalidDataException("The selected draw contains an invalid source corner identity.");
                int point = corner.ControlPointIndex;
                used[point] = true;
                if (positions[point] is { } prior && (drawPositions[i] - prior).Length > 1e-8 * Math.Max(1, drawPositions[i].Length))
                    throw new InvalidDataException("A source point has conflicting bind-deformed positions across draw seams.");
                positions[point] = drawPositions[i];
            }
            for (int i = 0; i < surface.SourceTriangles.Length; i++)
            {
                GeometrySourceTriangle identity = surface.SourceTriangles[i];
                if (identity.PolygonIndex < 0 || identity.TriangleInPolygon < 0 || !triangles.TryAdd(identity,
                    new(identity, Point(i * 3), Point(i * 3 + 1), Point(i * 3 + 2))))
                    throw new InvalidDataException("Source triangle identities are missing or repeated across draws.");
                int Point(int index)
                {
                    uint vertex = surface.Indices[index];
                    return surface.SourceCorners[(int)vertex].ControlPointIndex;
                }
            }
        }
        if (triangles.Count == 0)
            throw new InvalidDataException("The selected source component has no referenced control-point surface.");
        // Unreferenced source points keep their original identity/position. They
        // have no triangles and therefore contribute no invented volume surface.
        ImmutableArray<Vector3D> currentPoints = positions.Select((point, i) => point ?? source.ControlPoints[i]).ToImmutableArray();
        ImmutableArray<SourceGeometryAnalysisTriangle> ordered = triangles.Values
            .OrderBy(static triangle => triangle.Source.PolygonIndex)
            .ThenBy(static triangle => triangle.Source.TriangleInPolygon)
            .ToImmutableArray();
        SourceMeshTopologyResult topology = SourceMeshTopology.Build(currentPoints.Length,
            ordered.Select(static triangle => (triangle.A, triangle.B, triangle.C)).ToArray(), cancellationToken);
        GeometrySourceComponent geometry = new(source.Id, currentPoints)
        {
            Coordinates = source.Coordinates,
            Skinning = source.Skinning,
        };
        return new(geometry, ordered, topology);
    }

    private static Vector3D CurrentPosition(FbxModelSurface surface, int vertexIndex, TransformMatrix[] globals)
    {
        FbxModelVertex vertex = surface.Vertices[vertexIndex];
        if (!surface.IsSkinned) return vertex.Position;
        if (vertex.BoneIndices.IsDefault || vertex.BoneWeights.IsDefault || vertex.BoneIndices.Length != vertex.BoneWeights.Length)
            throw new InvalidDataException("The selected skinned draw contains mismatched original weights.");
        Vector3D position = Vector3D.Zero;
        double total = 0;
        for (int i = 0; i < vertex.BoneIndices.Length; i++)
        {
            int slot = vertex.BoneIndices[i];
            double weight = vertex.BoneWeights[i];
            if ((uint)slot >= (uint)surface.PaletteBoneIndices.Length || !double.IsFinite(weight) || weight < 0 ||
                (uint)slot >= (uint)surface.InverseBindMatrices.Length)
                throw new InvalidDataException("The selected skinned draw contains an invalid palette or weight.");
            int bone = surface.PaletteBoneIndices[slot];
            if ((uint)bone >= (uint)globals.Length || !surface.InverseBindMatrices[slot].IsFinite)
                throw new InvalidDataException("The selected skinned draw references an invalid bind transform.");
            if (weight == 0) continue;
            TransformMatrix palette = globals[bone] * surface.InverseBindMatrices[slot];
            if (!palette.IsFinite) throw new InvalidDataException("The selected skinned draw has a non-finite skin palette.");
            position += palette.TransformPoint(vertex.Position) * weight;
            total += weight;
        }
        if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("A skinned source point has no positive normalized influence.");
        return position / total;
    }

    private static bool SameSkinning(GeometrySourceSkinning? first, GeometrySourceSkinning? second,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null || first.HasSkinDeformer != second.HasSkinDeformer ||
            first.ControlPoints.IsDefault || second.ControlPoints.IsDefault || first.ControlPoints.Length != second.ControlPoints.Length)
            return false;
        for (int i = 0; i < first.ControlPoints.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeometrySourceControlPointWeights left = first.ControlPoints[i];
            GeometrySourceControlPointWeights right = second.ControlPoints[i];
            if (left.RetainedWeight != right.RetainedWeight || left.DiscardedWeight != right.DiscardedWeight ||
                !left.Influences.SequenceEqual(right.Influences)) return false;
        }
        return true;
    }
}
