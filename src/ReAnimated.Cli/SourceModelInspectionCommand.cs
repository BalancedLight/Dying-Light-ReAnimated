using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Cli;

internal static class SourceModelInspectionCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options, CancellationToken token)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: DLReAnimated inspect-source-msh <source.msh>");
        string path = Path.GetFullPath(args[0]);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("The source MSH exceeds the supported 256 MiB inspection limit.");
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.Length != bytes.Length)
            throw new IOException("The source MSH changed while it was being read.");
        var document = Dl1SourceMeshReader.Parse(bytes, token);
        token.ThrowIfCancellationRequested();
        var report = new
        {
            format = "dl-reanimated-source-msh-inspection-v1",
            evidenceLayer = "Chrome source MSH emitted by the C# writer",
            sourceFile = Path.GetFileName(path),
            sourceBytes = bytes.Length,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            sourceStructureValidated = true,
            compiledResourceValidated = false,
            runtimeValidated = false,
            materialNames = document.MaterialNames,
            surfaceNames = document.SurfaceNames,
            physicalNodeCount = document.Nodes.Length,
            rigNodeCount = document.Nodes.Count(node => node.Type is 4 or 8),
            geometryNodeCount = document.Nodes.Count(node => node.Type is 1 or 2),
            vertexCount = document.Nodes.Sum(node => node.Lods.Sum(lod => (long)lod.Positions.Length)),
            indexCount = document.Nodes.Sum(node => node.Lods.Sum(lod => (long)lod.Indices.Length)),
            nodes = document.Nodes.Select((node, index) => new
            {
                index, node.Name, node.Type, node.ParentIndex, node.DescendantCount, node.Flags,
                node.LocalMatrix, node.ReferenceMatrix, node.BoundsCenter, node.BoundsHalfExtents,
                lods = node.Lods.Select((lod, lodIndex) => new
                {
                    lodIndex,
                    vertexCount = lod.Positions.Length,
                    indexCount = lod.Indices.Length,
                    triangleCount = lod.Indices.Length / 3,
                    skinVertexCount = lod.Skin.Length,
                    maximumStoredInfluences = lod.Skin.IsEmpty ? 0 : lod.Skin.Max(row => row.PaletteIndices.Length),
                    positiveQuantizedInfluenceCount = lod.Skin.Sum(row => (long)row.QuantizedWeights.Count(weight => weight > 0)),
                    vertexFormatDeclaration = lod.VertexFormatDeclaration,
                    subsets = lod.Subsets.Select(subset => new
                    {
                        subset.MaterialIndex, subset.FirstIndex, subset.IndexCount, subset.Palette,
                    }),
                    morphTargets = lod.MorphTargets.Select(morph => new
                    {
                        morph.Name, vertexCount = morph.PositionDeltas.Length,
                    }),
                }),
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, options));
        return 0;
    }
}
