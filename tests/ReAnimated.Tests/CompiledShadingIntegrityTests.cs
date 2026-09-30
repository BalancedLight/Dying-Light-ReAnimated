using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class CompiledShadingIntegrityTests
{
    [Fact]
    public void ZeroEncodedNormalOrTangentCannotHideBehindDecoderDefaults()
    {
        CompiledMeshSurface surface = Surface();
        byte[] valid = [0, 0, 127, 0, 127, 0, 0, 127];
        Assert.Equal(1, Dl1CompiledShadingValidator.Validate(1, [surface], valid));
        byte[] missingNormal = (byte[])valid.Clone();
        Array.Clear(missingNormal, 0, 3);
        Assert.Contains("normal", Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledShadingValidator.Validate(1, [surface], missingNormal)).Message, StringComparison.OrdinalIgnoreCase);
        byte[] missingTangent = (byte[])valid.Clone();
        Array.Clear(missingTangent, 4, 3);
        Assert.Contains("tangent", Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledShadingValidator.Validate(1, [surface], missingTangent)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompiledShadingChecksLayoutBoundsAndFrameOrthogonality()
    {
        CompiledMeshSurface surface = Surface();
        byte[] data = [0, 0, 127, 0, 127, 0, 0, 127];
        Assert.Throws<InvalidDataException>(() => Dl1CompiledShadingValidator.Validate(1, [surface], data.AsSpan(0, 7)));
        var noNormal = surface with { VertexLayout = surface.VertexLayout with { Elements = [surface.VertexLayout.Elements[1]] } };
        Assert.Contains("layout", Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledShadingValidator.Validate(1, [noNormal], data)).Message, StringComparison.OrdinalIgnoreCase);
        var parallel = surface with { Vertices = [surface.Vertices[0] with { Tangent = new(0, 0, 1, 1) }] };
        Assert.Contains("frame", Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledShadingValidator.Validate(1, [parallel], data)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachCompiledSurfaceUsesItsOwnVertexByteOffset()
    {
        CompiledMeshSurface first = Surface();
        CompiledMeshSurface second = first with
        {
            Name = "generic-second-surface",
            VertexByteOffset = 8,
        };
        byte[] data =
        [
            0, 0, 127, 0, 127, 0, 0, 127,
            0, 0, 127, 0, 127, 0, 0, 127,
        ];
        Assert.Equal(2, Dl1CompiledShadingValidator.Validate(2, [first, second], data));
        Array.Clear(data, 8, 3);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledShadingValidator.Validate(2, [first, second], data));
        Assert.Contains("generic-second-surface", error.Message, StringComparison.Ordinal);
        Assert.Contains("normal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledMeshSurface Surface()
    {
        var layout = new CompiledVertexLayout(0, 8,
        [
            new((byte)CompiledVertexFormat.SignedNormalizedByte4, (byte)CompiledVertexSemantic.Normal, 0, 0, 4),
            new((byte)CompiledVertexFormat.SignedNormalizedByte4, (byte)CompiledVertexSemantic.Tangent, 0, 4, 4),
        ]);
        var vertex = new CompiledVertex(Vector3.Zero, Vector3.UnitZ, new(1, 0, 0, 1),
            Vector2.Zero, Vector2.Zero, Vector4.One, new(1, 0, 0, 0), new(0, 0, 0, 0));
        return new(0, "generic-surface", 0, 0, 0, 0, layout, [vertex], [], []);
    }
}
