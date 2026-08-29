using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxExternalTextureResolutionTests
{
    [Fact]
    public void ResolvesRootAndFbmSidecarInDeterministicOrder()
    {
        string root = Directory.CreateTempSubdirectory("dlr-texture-root-").FullName;
        try
        {
            string rootTexture = Path.Combine(root, "body.tga");
            File.WriteAllBytes(rootTexture, [1, 2, 3]);
            var direct = FbxModelAuthoringImporter.ResolveExternalTexture(
                root,
                "character.fbx",
                @"source\body.tga",
                1024,
                CancellationToken.None);
            Assert.Equal(Path.GetFullPath(rootTexture), direct.ResolvedPath);
            Assert.Equal(new byte[] { 1, 2, 3 }, direct.Content.ToArray());

            File.Delete(rootTexture);
            string sidecar = Path.Combine(root, "character.fbm");
            Directory.CreateDirectory(sidecar);
            string sidecarTexture = Path.Combine(sidecar, "body.tga");
            File.WriteAllBytes(sidecarTexture, [4, 5, 6]);
            var fbm = FbxModelAuthoringImporter.ResolveExternalTexture(
                root,
                "character.fbx",
                "body.tga",
                1024,
                CancellationToken.None);
            Assert.Equal(Path.GetFullPath(sidecarTexture), fbm.ResolvedPath);
            Assert.Equal(new byte[] { 4, 5, 6 }, fbm.Content.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RootedTraversalUnsupportedAndOversizedReferencesAreRefused()
    {
        string parent = Directory.CreateTempSubdirectory("dlr-texture-parent-").FullName;
        string root = Path.Combine(parent, "model");
        Directory.CreateDirectory(root);
        try
        {
            string outside = Path.Combine(parent, "outside.tga");
            File.WriteAllBytes(outside, [1, 2, 3]);
            Assert.Null(FbxModelAuthoringImporter.ResolveExternalTexture(
                root, "model.fbx", @"..\outside.tga", 1024, CancellationToken.None).ResolvedPath);
            Assert.Null(FbxModelAuthoringImporter.ResolveExternalTexture(
                root, "model.fbx", outside, 1024, CancellationToken.None).ResolvedPath);
            Assert.Null(FbxModelAuthoringImporter.ResolveExternalTexture(
                root, "model.fbx", "outside.tiff", 1024, CancellationToken.None).ResolvedPath);

            string oversized = Path.Combine(root, "large.tga");
            File.WriteAllBytes(oversized, new byte[32]);
            Assert.Null(FbxModelAuthoringImporter.ResolveExternalTexture(
                root, "model.fbx", "large.tga", 16, CancellationToken.None).ResolvedPath);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void SavedPayloadSuppressesStaleExternalTextureWarning()
    {
        string entry = "textures/saved.tga";
        var binding = new CustomModelTextureBinding
        {
            Semantic = CustomModelTextureSemantic.BaseColor,
            SourceKind = CustomModelTextureSourceKind.ExternalFbx,
            DisplayName = "saved.tga",
            PackageEntryPath = entry,
            ContentSha256 = new string('a', 64),
            MediaType = "image/x-tga",
        };
        var material = new CustomModelMaterial
        {
            Name = "body",
            Textures = [binding],
        };
        CustomModelImportDiagnostic stale = new()
        {
            Code = "model_external_texture_not_embedded",
            Severity = CustomModelImportSeverity.Warning,
            Subject = "body",
            Message = "stale",
        };

        ImmutableArray<CustomModelImportDiagnostic> diagnostics =
            FbxModelAuthoringImporter.BuildTextureBindingDiagnostics(
                [material],
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(
                    entry,
                    ImmutableArray.Create<byte>(1, 2, 3)),
                [stale]);

        Assert.Empty(diagnostics);
    }
}
