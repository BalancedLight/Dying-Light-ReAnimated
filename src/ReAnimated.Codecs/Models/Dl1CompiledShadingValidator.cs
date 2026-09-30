using System.IO;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

/// <summary>Checks the encoded base shading frame before decoded defaults can hide zero byte vectors.</summary>
public static class Dl1CompiledShadingValidator
{
    public static int Validate(int authoredSurfaceCount, IReadOnlyList<CompiledMeshSurface> surfaces,
        ReadOnlySpan<byte> vertexData)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (authoredSurfaceCount < 0 || authoredSurfaceCount > surfaces.Count)
            throw new InvalidDataException("The compiled mesh omitted an authored draw surface.");
        int checkedVertices = 0;
        for (int surfaceIndex = 0; surfaceIndex < authoredSurfaceCount; surfaceIndex++)
        {
            CompiledMeshSurface surface = surfaces[surfaceIndex];
            CompiledVertexLayout layout = surface.VertexLayout ??
                throw new InvalidDataException("Compiled shading layout is missing.");
            CompiledVertexElement? normalElement = layout.Elements.SingleOrDefault(e =>
                e.Semantic == CompiledVertexSemantic.Normal && e.Channel == 0);
            CompiledVertexElement? tangentElement = layout.Elements.SingleOrDefault(e =>
                e.Semantic == CompiledVertexSemantic.Tangent && e.Channel == 0);
            if (normalElement is not { Format: CompiledVertexFormat.SignedNormalizedByte4, ByteSize: >= 4 } ||
                tangentElement is not { Format: CompiledVertexFormat.SignedNormalizedByte4, ByteSize: >= 4 })
                throw new InvalidDataException($"Compiled draw surface '{surface.Name}' lacks a supported base normal/tangent layout.");
            for (int vertexIndex = 0; vertexIndex < surface.Vertices.Count; vertexIndex++)
            {
                long baseOffset = surface.VertexByteOffset + (long)vertexIndex * layout.Stride;
                CheckEncoded(vertexData, baseOffset, normalElement, "normal", surface.Name, vertexIndex);
                CheckEncoded(vertexData, baseOffset, tangentElement, "tangent", surface.Name, vertexIndex);
                CompiledVertex vertex = surface.Vertices[vertexIndex];
                Vector3 tangent = new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
                if (!IsFinite(vertex.Normal) || vertex.Normal.LengthSquared() < .25f ||
                    !IsFinite(tangent) || tangent.LengthSquared() < .25f ||
                    Math.Abs(Vector3.Dot(vertex.Normal, tangent)) > .2f)
                    throw new InvalidDataException($"Compiled draw surface '{surface.Name}' vertex {vertexIndex} has an invalid normal/tangent frame.");
                checkedVertices++;
            }
        }
        return checkedVertices;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void CheckEncoded(
        ReadOnlySpan<byte> vertexData,
        long baseOffset,
        CompiledVertexElement element,
        string channel,
        string surfaceName,
        int vertexIndex)
    {
        long offset = baseOffset + element.ByteOffset;
        if (offset < 0 || offset > vertexData.Length - 4 ||
            vertexData[(int)offset] == 0 && vertexData[(int)offset + 1] == 0 && vertexData[(int)offset + 2] == 0)
            throw new InvalidDataException($"Compiled draw surface '{surfaceName}' vertex {vertexIndex} has a missing or zero encoded {channel}.");
    }
}
