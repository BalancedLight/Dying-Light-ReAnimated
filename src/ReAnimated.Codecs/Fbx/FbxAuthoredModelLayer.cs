using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Captures/replays source-linked authoring edits without storing another base mesh.</summary>
public static class FbxAuthoredModelLayer
{
    public static FbxModelAuthoringImportResult Capture(FbxModelAuthoringImportResult model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Package.Document.Validate();
        var reference = model.Package.Document.AuthoredLayer ?? new AuthoredModelLayerReference {
            SourceRigMode = CustomModelRigMode.Auto, SourceIgnoreMorphChannels = model.Package.Document.IgnoreMorphChannels,
        };
        var source = DecodeSource(model.Package, reference, cancellationToken);
        var original = Inventory.Build(source, cancellationToken);
        var current = Inventory.Build(model, cancellationToken);
        original.RequireSameTopology(current);
        Dictionary<string, Guid>? previousIds = null;
        if (model.Package.Document.AuthoredLayer is { } oldReference)
        {
            if (model.Package.AuthoredLayerPayload.Length != oldReference.PayloadLength || !string.Equals(oldReference.ContentSha256,
                Convert.ToHexStringLower(SHA256.HashData(model.Package.AuthoredLayerPayload.AsSpan())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The previous authored layer payload is missing or changed.");
            var previous = AuthoredModelLayerCodec.Deserialize(model.Package.AuthoredLayerPayload.AsSpan());
            if (!string.Equals(previous.SourceSha256, model.Package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The previous authored layer belongs to another source.");
            previousIds = previous.Bones.ToDictionary(static b => b.Name, static b => b.Id, StringComparer.Ordinal);
        }
        var boneIds = model.Package.Document.Bones.Select(b => new AuthoredBoneIdentity(
            previousIds is not null && previousIds.TryGetValue(b.Name, out var id) ? id : StableBoneId(model.Package.Document.ModelId, b.Name), b.Name)).ToImmutableArray();
        var idByName = boneIds.ToDictionary(static b => b.Name, static b => b.Id, StringComparer.Ordinal);
        var components = ImmutableArray.CreateBuilder<AuthoredComponentEdits>();
        foreach (var (id, component) in current.Components.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = original.Components[id];
            var points = ImmutableArray.CreateBuilder<AuthoredPointEdit>();
            foreach (var (index, point) in component.Points.OrderBy(static p => p.Key))
            {
                var originalPoint = baseline.Points[index];
                Vector3D delta = point.Position - originalPoint.Position;
                bool replace = !point.Weights.SequenceEqual(originalPoint.Weights);
                if (delta != Vector3D.Zero || replace)
                    points.Add(new(index, delta, replace, replace ? point.Weights.Select(w => new AuthoredSkinInfluence(idByName[w.Name], w.Weight)).ToImmutableArray() : []));
            }
            var normals = Differences(component.Normals, baseline.Normals);
            var morphs = ImmutableArray.CreateBuilder<AuthoredMorphEdits>();
            if (!component.Morphs.Keys.Order().SequenceEqual(baseline.Morphs.Keys.Order()))
                throw new InvalidDataException("Source-linked edits cannot invent or remove a source morph channel.");
            foreach (var (descriptor, morph) in component.Morphs.OrderBy(static m => m.Key))
            {
                var originalMorph = baseline.Morphs[descriptor];
                var positions = Differences(morph.Positions, originalMorph.Positions);
                var normalChanges = Differences(morph.Normals, originalMorph.Normals);
                bool? presence = morph.HasNormals == originalMorph.HasNormals ? null : morph.HasNormals;
                if (!positions.IsEmpty || !normalChanges.IsEmpty || presence is not null)
                    morphs.Add(new(descriptor, positions, morph.HasNormals ? normalChanges : [], presence));
            }
            if (points.Count > 0 || !normals.IsEmpty || morphs.Count > 0 || component.InverseBinds.Count > 0)
                components.Add(new(id, component.ControlPointCount, component.PolygonVertexCount, points.ToImmutable(), normals, morphs.ToImmutable()) {
                    InverseBinds = component.InverseBinds.Select(p => new AuthoredInverseBind(idByName[p.Key], p.Value)).ToImmutableArray(),
                });
        }
        var layer = new AuthoredModelLayer {
            SourceSha256 = model.Package.Document.Source.ContentSha256, SourceGeometryFingerprint = original.Fingerprint(source.Package.Document.RigSignature),
            TargetRigSignature = CustomModelContractSignatures.ComputeRig(model.Package.Document.Bones), Bones = boneIds, Components = components.ToImmutable(),
        };
        var payload = AuthoredModelLayerCodec.Serialize(layer);
        var document = model.Package.Document with {
            AuthoredLayer = reference with { PayloadLength = payload.Length, ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())) },
            LastBuildReceipt = null,
        };
        var package = model.Package with { Document = document, AuthoredLayerPayload = payload };
        CustomModelPackageSerializer.ValidateAuthoredLayer(package);
        return model with { Package = package };
    }

    public static FbxModelAuthoringImportResult DecodeSource(CustomModelPackage package, AuthoredModelLayerReference reference, CancellationToken cancellationToken = default) =>
        FbxModelAuthoringImporter.Import(package.SourceFbx.AsSpan(), package.Document.Source.OriginalFileName,
            new() { RigMode = reference.SourceRigMode, IgnoreMorphChannels = reference.SourceIgnoreMorphChannels, DecodeAnimationClips = false }, cancellationToken);

    /// <summary>Replays onto a fresh source decode after validating both source and target contracts.</summary>
    public static FbxModelAuthoringImportResult Replay(FbxModelAuthoringImportResult source, CustomModelPackage saved,
        CancellationToken cancellationToken = default)
    {
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(saved) ?? throw new InvalidDataException("No authored model layer was supplied.");
        var inventory = Inventory.Build(source, cancellationToken);
        if (!string.Equals(source.Package.Document.Source.ContentSha256, layer.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(inventory.Fingerprint(source.Package.Document.RigSignature), layer.SourceGeometryFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The decoded source geometry, normals, morphs or bindings differ from the authored layer's source contract.");
        var edits = layer.Components.ToDictionary(static c => c.ComponentId, StringComparer.Ordinal);
        foreach (var edit in layer.Components)
        {
            if (!inventory.Components.TryGetValue(edit.ComponentId, out var component) || component.ControlPointCount != edit.ControlPointCount || component.PolygonVertexCount != edit.PolygonVertexCount)
                throw new InvalidDataException("An authored edit references a changed or missing source component.");
            if (edit.Points.Any(p => !component.Points.ContainsKey(p.ControlPointIndex)) || edit.Normals.Any(n => !component.Normals.ContainsKey(n.Index)) ||
                edit.Morphs.Any(m => !component.Morphs.ContainsKey(m.DescriptorHash) || m.PositionDeltas.Any(d => !component.Points.ContainsKey(d.Index)) || m.NormalDeltas.Any(d => !component.Normals.ContainsKey(d.Index))))
                throw new InvalidDataException("An authored edit references a missing source point, corner or morph.");
        }
        var targetByName = saved.Document.Bones.ToDictionary(static b => b.Name, static b => b.Index, StringComparer.Ordinal);
        var targetById = layer.Bones.ToDictionary(static b => b.Id, b => targetByName[b.Name]);
        var globals = new TransformMatrix[saved.Document.Bones.Length];
        foreach (var bone in saved.Document.Bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var inverse = globals.Select(static g => g.InvertedAffine()).ToImmutableArray();
        var surfaces = ImmutableArray.CreateBuilder<FbxModelSurface>();
        foreach (var surface in source.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var componentId = surface.SourceGeometry!.Id;
            edits.TryGetValue(componentId, out var component);
            var pointEdits = component?.Points.ToDictionary(static p => p.ControlPointIndex) ?? [];
            var normalEdits = component?.Normals.ToDictionary(static p => p.Index, static p => p.Delta) ?? [];
            var morphEdits = component?.Morphs.ToDictionary(static m => m.DescriptorHash) ?? [];
            var componentBinds = component?.InverseBinds.ToDictionary(b => targetById[b.BoneId], static b => b.Matrix) ?? [];
            var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>(surface.Vertices.Length);
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vertex = surface.Vertices[i]; var corner = surface.SourceCorners[i];
                pointEdits.TryGetValue(corner.ControlPointIndex, out var edit);
                var weights = edit is { ReplaceWeights: true }
                    ? edit.Weights.Select(w => (Index: targetById[w.BoneId], w.Weight)).ToArray()
                    : inventory.Components[componentId].Points[corner.ControlPointIndex].Weights.Select(w =>
                        (Index: targetByName.TryGetValue(w.Name, out int index) ? index : throw new InvalidDataException("An unchanged source influence is absent from the target rig."), w.Weight)).ToArray();
                vertices.Add(vertex with { Position = AddFinite(vertex.Position, edit?.PositionDelta ?? Vector3D.Zero),
                    Normal = AddFinite(vertex.Normal, normalEdits.GetValueOrDefault(corner.PolygonVertexIndex)),
                    BoneIndices = weights.Select(static w => w.Index).ToImmutableArray(), BoneWeights = weights.Select(static w => w.Weight).ToImmutableArray() });
            }
            var morphs = surface.MorphTargets.Select(morph => {
                if (!morphEdits.TryGetValue(morph.DescriptorHash, out var edit)) return morph;
                var positions = edit.PositionDeltas.ToDictionary(static d => d.Index, static d => d.Delta);
                var normals = edit.NormalDeltas.ToDictionary(static d => d.Index, static d => d.Delta);
                bool hasNormals = edit.HasNormalDeltas ?? !morph.NormalDeltas.IsDefaultOrEmpty;
                return morph with {
                    PositionDeltas = morph.PositionDeltas.Select((p, i) => AddFinite(p, positions.GetValueOrDefault(surface.SourceCorners[i].ControlPointIndex))).ToImmutableArray(),
                    NormalDeltas = hasNormals ? surface.SourceCorners.Select((c, i) =>
                        AddFinite(morph.NormalDeltas.IsDefaultOrEmpty ? Vector3D.Zero : morph.NormalDeltas[i], normals.GetValueOrDefault(c.PolygonVertexIndex))).ToImmutableArray() : [],
                };
            }).ToImmutableArray();
            var authoredVertices = vertices.MoveToImmutable();
            var authored = surface with { Vertices = authoredVertices, MorphTargets = morphs,
                PaletteBoneIndices = Enumerable.Range(0, globals.Length).ToImmutableArray(), InverseBindMatrices = inverse,
                IsSkinned = authoredVertices.Any(static v => !v.BoneWeights.IsEmpty) };
            if (componentBinds.Count > 0) authored = authored with {
                InverseBindMatrices = inverse.Select((matrix, index) => componentBinds.GetValueOrDefault(index, matrix)).ToImmutableArray(),
            };
            surfaces.AddRange(FbxAuthoredSurfacePartitioner.Partition(authored, cancellationToken));
        }
        var clips = saved.Document.RiggingSession?.ParentDecisions.IsEmpty == false
            ? FbxAnimationTrackReindexer.ReindexByName(source.AnimationClips, source.Package.Document.CreateEffectiveBones(), saved.Document.CreateEffectiveBones(), cancellationToken)
            : source.AnimationClips;
        return source with { Package = saved, Rig = saved.Document.Bones.IsEmpty ? null : saved.Document.CreateRigDefinition(), Surfaces = surfaces.ToImmutable(), AnimationClips = clips };
    }

    private static ImmutableArray<AuthoredVectorDelta> Differences(Dictionary<int, Vector3D> current, Dictionary<int, Vector3D> original) =>
        current.Keys.Concat(original.Keys).Distinct().Order().Select(i => new AuthoredVectorDelta(i, current.GetValueOrDefault(i) - original.GetValueOrDefault(i)))
            .Where(static d => d.Delta != Vector3D.Zero).ToImmutableArray();

    private static Vector3D AddFinite(Vector3D original, Vector3D delta)
    {
        var result = original + delta;
        if (!result.IsFinite) throw new InvalidDataException("An authored geometry or morph edit overflows its finite coordinate range.");
        return result;
    }

    private static Guid StableBoneId(Guid modelId, string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{modelId:N}:authored-bone:{name}")).AsSpan(0, 16));

    private sealed record NamedInfluence(string Name, double Weight);
    private sealed record Point(Vector3D Position, ImmutableArray<NamedInfluence> Weights);
    private sealed class MorphData(bool hasNormals)
    {
        public bool HasNormals { get; } = hasNormals;
        public Dictionary<int, Vector3D> Positions { get; } = [];
        public Dictionary<int, Vector3D> Normals { get; } = [];
    }
    private sealed class ComponentData(ImmutableArray<Vector3D> sourceControlPoints, int polygonVertexCount)
    {
        public ImmutableArray<Vector3D> SourceControlPoints { get; } = sourceControlPoints;
        public int ControlPointCount => SourceControlPoints.Length;
        public int PolygonVertexCount { get; set; } = polygonVertexCount;
        public Dictionary<int, Point> Points { get; } = [];
        public Dictionary<int, Vector3D> Normals { get; } = [];
        public Dictionary<int, (double U, double V)> TextureCoordinates { get; } = [];
        public Dictionary<string, TransformMatrix> InverseBinds { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, MorphData> Morphs { get; } = [];
        public Dictionary<GeometrySourceTriangle, string> Triangles { get; } = [];
    }
    private sealed class Inventory
    {
        public Dictionary<string, ComponentData> Components { get; } = new(StringComparer.Ordinal);

        public static Inventory Build(FbxModelAuthoringImportResult model, CancellationToken cancellationToken)
        {
            var result = new Inventory();
            foreach (var surface in model.Surfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometry = surface.SourceGeometry ?? throw new InvalidDataException("Source-linked authoring requires original component and corner identities.");
                if (surface.SourceCorners.Length != surface.Vertices.Length || surface.SourceTriangles.Length * 3 != surface.Indices.Length)
                    throw new InvalidDataException("Source corner/triangle identities do not cover the render surface.");
                if (!result.Components.TryGetValue(geometry.Id, out var component))
                    result.Components.Add(geometry.Id, component = new(geometry.ControlPoints, 0));
                if (!component.SourceControlPoints.SequenceEqual(geometry.ControlPoints) || geometry.ControlPoints.Any(static p => !p.IsFinite))
                    throw new InvalidDataException("A source component has inconsistent or non-finite original control points.");
                for (int i = 0; i < surface.Vertices.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var vertex = surface.Vertices[i]; var corner = surface.SourceCorners[i];
                    if ((uint)corner.ControlPointIndex >= (uint)component.ControlPointCount || corner.PolygonVertexIndex < 0 || !vertex.Position.IsFinite || !vertex.Normal.IsFinite ||
                        vertex.BoneIndices.Length != vertex.BoneWeights.Length || surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length)
                        throw new InvalidDataException("A source-linked vertex or palette is invalid.");
                    component.PolygonVertexCount = Math.Max(component.PolygonVertexCount, checked(corner.PolygonVertexIndex + 1));
                    var weights = new List<NamedInfluence>();
                    for (int w = 0; w < vertex.BoneIndices.Length; w++)
                    {
                        int slot = vertex.BoneIndices[w]; double weight = vertex.BoneWeights[w];
                        if (!double.IsFinite(weight) || weight < 0 || (uint)slot >= (uint)surface.PaletteBoneIndices.Length ||
                            (uint)surface.PaletteBoneIndices[slot] >= (uint)model.Package.Document.Bones.Length)
                            throw new InvalidDataException("An authored weight or bone reference is invalid.");
                        if (weight > 0)
                        {
                            string name = model.Package.Document.Bones[surface.PaletteBoneIndices[slot]].Name;
                            var matrix = surface.InverseBindMatrices[slot];
                            if (!matrix.IsFinite || component.InverseBinds.TryGetValue(name, out var previousMatrix) && previousMatrix != matrix)
                                throw new InvalidDataException("A source component has conflicting or non-finite inverse bind references.");
                            component.InverseBinds[name] = matrix;
                            weights.Add(new(name, weight));
                        }
                    }
                    var row = new Point(vertex.Position, weights.OrderBy(static w => w.Name, StringComparer.Ordinal).ToImmutableArray());
                    if (component.Points.TryGetValue(corner.ControlPointIndex, out var existing) && (existing.Position != row.Position || !existing.Weights.SequenceEqual(row.Weights)))
                        throw new InvalidDataException("Render copies of a source control point have conflicting positions or weights.");
                    component.Points[corner.ControlPointIndex] = row;
                    Put(component.Normals, corner.PolygonVertexIndex, vertex.Normal);
                    var uv = (vertex.TextureCoordinateU, vertex.TextureCoordinateV);
                    if (!double.IsFinite(uv.TextureCoordinateU) || !double.IsFinite(uv.TextureCoordinateV) ||
                        component.TextureCoordinates.TryGetValue(corner.PolygonVertexIndex, out var previousUv) && previousUv != uv)
                        throw new InvalidDataException("Source corner texture coordinates are inconsistent or non-finite.");
                    component.TextureCoordinates[corner.PolygonVertexIndex] = uv;
                }
                for (int t = 0; t < surface.SourceTriangles.Length; t++)
                {
                    string triangle = string.Join("/", Enumerable.Range(t * 3, 3).Select(i => {
                        int vertex = checked((int)surface.Indices[i]);
                        if ((uint)vertex >= (uint)surface.SourceCorners.Length) throw new InvalidDataException("A render triangle references a missing source corner.");
                        var corner = surface.SourceCorners[vertex]; return $"{corner.ControlPointIndex}:{corner.PolygonVertexIndex}";
                    }));
                    if (!component.Triangles.TryAdd(surface.SourceTriangles[t], triangle + ":" + surface.MaterialId.ToString("N"))) throw new InvalidDataException("A source triangle is duplicated across render parts.");
                }
                foreach (var morph in surface.MorphTargets)
                {
                    bool hasNormals = !morph.NormalDeltas.IsDefaultOrEmpty;
                    if (morph.PositionDeltas.Length != surface.Vertices.Length || hasNormals && morph.NormalDeltas.Length != surface.Vertices.Length)
                        throw new InvalidDataException("A morph does not cover its source corners.");
                    if (!component.Morphs.TryGetValue(morph.DescriptorHash, out var values)) component.Morphs.Add(morph.DescriptorHash, values = new(hasNormals));
                    if (values.HasNormals != hasNormals) throw new InvalidDataException("A morph has inconsistent normal-array presence across source parts.");
                    for (int i = 0; i < surface.Vertices.Length; i++)
                    {
                        Put(values.Positions, surface.SourceCorners[i].ControlPointIndex, morph.PositionDeltas[i]);
                        if (hasNormals) Put(values.Normals, surface.SourceCorners[i].PolygonVertexIndex, morph.NormalDeltas[i]);
                    }
                }
            }
            return result;
        }

        private static void Put(Dictionary<int, Vector3D> values, int index, Vector3D value)
        {
            if (!value.IsFinite || values.TryGetValue(index, out var previous) && previous != value)
                throw new InvalidDataException("Source copies have conflicting or non-finite vector values.");
            values[index] = value;
        }
        public void RequireSameTopology(Inventory current)
        {
            if (!Components.Keys.Order(StringComparer.Ordinal).SequenceEqual(current.Components.Keys.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Source-linked edits cannot add or discard source components.");
            foreach (var (id, original) in Components)
            {
                var authored = current.Components[id];
                if (original.ControlPointCount != authored.ControlPointCount || original.PolygonVertexCount != authored.PolygonVertexCount ||
                    !original.SourceControlPoints.SequenceEqual(authored.SourceControlPoints) ||
                    !original.Points.Keys.Order().SequenceEqual(authored.Points.Keys.Order()) || !original.Normals.Keys.Order().SequenceEqual(authored.Normals.Keys.Order()) ||
                    !original.TextureCoordinates.OrderBy(static t => t.Key).SequenceEqual(authored.TextureCoordinates.OrderBy(static t => t.Key)) ||
                    !original.Triangles.OrderBy(static t => t.Key.PolygonIndex).ThenBy(static t => t.Key.TriangleInPolygon)
                        .SequenceEqual(authored.Triangles.OrderBy(static t => t.Key.PolygonIndex).ThenBy(static t => t.Key.TriangleInPolygon)))
                    throw new InvalidDataException("Source-linked edits cannot change render/source topology.");
            }
        }
        public string Fingerprint(string rigSignature)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes); }
            void Number(double value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(bytes, value); hash.AppendData(bytes); }
            void Vector(Vector3D value) { Number(value.X); Number(value.Y); Number(value.Z); }
            Text("source-authoring-contract-v1"); Text(rigSignature);
            foreach (var (id, component) in Components.OrderBy(static c => c.Key, StringComparer.Ordinal))
            {
                Text(id); Number(component.ControlPointCount); Number(component.PolygonVertexCount);
                foreach (var point in component.SourceControlPoints) Vector(point);
                Number(component.Points.Count);
                foreach (var (index, point) in component.Points.OrderBy(static p => p.Key))
                { Number(index); Vector(point.Position); Number(point.Weights.Length); foreach (var weight in point.Weights) { Text(weight.Name); Number(weight.Weight); } }
                Number(component.Normals.Count);
                foreach (var (index, normal) in component.Normals.OrderBy(static n => n.Key)) { Number(index); Vector(normal); }
                Number(component.TextureCoordinates.Count);
                foreach (var (index, uv) in component.TextureCoordinates.OrderBy(static n => n.Key)) { Number(index); Number(uv.U); Number(uv.V); }
                Number(component.InverseBinds.Count);
                foreach (var (name, matrix) in component.InverseBinds.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    Text(name); foreach (double value in MatrixElements(matrix)) Number(value);
                }
                Number(component.Triangles.Count); foreach (var (idTriangle, triangle) in component.Triangles.OrderBy(static t => t.Key.PolygonIndex).ThenBy(static t => t.Key.TriangleInPolygon))
                { Number(idTriangle.PolygonIndex); Number(idTriangle.TriangleInPolygon); Text(triangle); }
                Number(component.Morphs.Count);
                foreach (var (descriptor, morph) in component.Morphs.OrderBy(static m => m.Key))
                {
                    Number(descriptor); Number(morph.HasNormals ? 1 : 0); Number(morph.Positions.Count);
                    foreach (var (index, delta) in morph.Positions.OrderBy(static d => d.Key)) { Number(index); Vector(delta); }
                    Number(morph.Normals.Count); foreach (var (index, delta) in morph.Normals.OrderBy(static d => d.Key)) { Number(index); Vector(delta); }
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private static double[] MatrixElements(TransformMatrix m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
}
