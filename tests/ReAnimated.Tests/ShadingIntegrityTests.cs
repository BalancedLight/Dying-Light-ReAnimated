using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class ShadingIntegrityTests
{
    [Fact]
    public async Task MorphBearingSurfaceWithCancelledBaseNormalIsRejectedBeforeOutput()
    {
        FbxModelAuthoringImportResult source = CustomModelSchema2MorphTests.CreateMorphModel();
        var surface = Assert.Single(source.Surfaces);
        Assert.Single(surface.MorphTargets);
        var vertices = surface.Vertices.SetItem(0, surface.Vertices[0] with { Normal = Vector3D.Zero });
        var candidate = source with { Surfaces = [surface with { Vertices = vertices }] };
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = candidate, OutputDirectory = Path.Combine(directory, "output"),
                ResourceName = "generic_shading_control",
            }));
            Assert.Contains("normal", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("vertex 0", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(directory, "output")) &&
                Directory.EnumerateFiles(Path.Combine(directory, "output")).Any());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task FiniteNonUnitBaseNormalsAreNormalizedWithoutChangingMorphPositions()
    {
        FbxModelAuthoringImportResult source = CustomModelSchema2MorphTests.CreateMorphModel();
        var surface = Assert.Single(source.Surfaces);
        var vertices = surface.Vertices.SetItem(0, surface.Vertices[0] with { Normal = Vector3D.UnitZ * 2 });
        var candidate = source with { Surfaces = [surface with { Vertices = vertices }] };
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var result = await Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = candidate, OutputDirectory = Path.Combine(directory, "output"),
                ResourceName = "generic_shading_control",
            });
            Assert.True(File.Exists(result.SourceMshPath));
            Assert.Equal<Vector3D>(surface.MorphTargets[0].PositionDeltas,
                candidate.Surfaces[0].MorphTargets[0].PositionDeltas);
            Assert.Equal(Vector3D.UnitZ * 2, candidate.Surfaces[0].Vertices[0].Normal);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
}
