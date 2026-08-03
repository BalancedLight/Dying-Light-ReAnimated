using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class RetailMorphValidationTests
{
    [Fact]
    public async Task InstalledPlayerTppDecodesRetailMorphDeltasWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string pack = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(pack))
        {
            return;
        }

        Rp6lArchive archive = await Rp6lArchive.OpenAsync(pack);
        Rp6lResourceDescriptor? resource = archive.FindResource(
            Rp6lResourceTypes.Mesh,
            "player_1_tpp");
        if (resource is null)
        {
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 4L * 1024 * 1024 * 1024,
            });
            Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);

            Assert.True(
                mesh.IsStructurallyValid,
                string.Join(
                    Environment.NewLine,
                    mesh.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
            Dl1MorphTarget[] decoded = mesh.MorphTargets
                .Where(static target =>
                    target.PayloadStatus ==
                        Dl1MorphPayloadStatus.VertexDeltasDecoded)
                .ToArray();
            Assert.NotEmpty(decoded);
            Assert.DoesNotContain(
                mesh.MorphTargets,
                static target =>
                    target.PayloadStatus ==
                        Dl1MorphPayloadStatus.VertexDeltasUnresolved);

            Dl1MorphBinding[] bindings = decoded
                .SelectMany(static target => target.Bindings)
                .DistinctBy(static binding =>
                    (binding.EntityIndex, binding.LodIndex))
                .ToArray();
            Assert.Contains(bindings, static binding =>
                binding.VertexCount == 3_068 &&
                binding.DeltaByteStride == 8 &&
                binding.DeltaEncoding ==
                    Dl1MorphDeltaEncoding.SignedShort4Scale16384);
            Assert.Contains(bindings, static binding =>
                binding.VertexCount == 2_006 &&
                binding.DeltaByteStride == 8 &&
                binding.DeltaEncoding ==
                    Dl1MorphDeltaEncoding.SignedShort4Scale16384);

            Dl1MorphTarget jaw = Assert.Single(
                mesh.MorphTargets,
                static target =>
                    string.Equals(
                        target.Name,
                        "morph_jaw_open",
                        StringComparison.OrdinalIgnoreCase));
            Vector3[] jawDeltas = jaw.Bindings
                .SelectMany(static binding =>
                    binding.PositionDeltaSets)
                .SelectMany(static set => set.PositionDeltas)
                .ToArray();
            Assert.Contains(
                jawDeltas,
                static delta => delta != Vector3.Zero);
            Assert.All(
                jawDeltas.SelectMany(static delta =>
                    new[] { delta.X, delta.Y, delta.Z }),
                static component =>
                    Assert.InRange(
                        Math.Abs(
                            component * 16_384.0f -
                            MathF.Round(component * 16_384.0f)),
                        0,
                        1.0e-5f));

            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            foreach (ReAnimated.Renderer.D3D11.MeshRenderData renderMesh in
                     preview.Meshes)
            {
                Vector3[] positions = renderMesh.Vertices.Span
                    .ToArray()
                    .Select(static vertex => vertex.Position)
                    .ToArray();
                if (positions.Length == 0)
                {
                    continue;
                }

                float surfaceDiagonal = Vector3.Distance(
                    positions.Aggregate(Vector3.Min),
                    positions.Aggregate(Vector3.Max));
                foreach (ReAnimated.Renderer.D3D11.MorphTargetRenderData target in
                         renderMesh.MorphTargets)
                {
                    Assert.All(
                        target.PositionDeltas.Span.ToArray(),
                        delta => Assert.InRange(
                            delta.Length(),
                            0.0f,
                            surfaceDiagonal + 1.0e-5f));
                }
            }
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "morph_jaw_open",
                        StringComparison.OrdinalIgnoreCase) &&
                    diagnostic.Contains(
                        "unsafe target was preserved by the codec but omitted from preview",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
