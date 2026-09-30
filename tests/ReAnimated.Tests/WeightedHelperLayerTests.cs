using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class WeightedHelperLayerTests
{
    internal static FbxModelAuthoringImportResult WeightedSource()
    {
        var model = StructuralHelperAuthoringTests.Source();
        int helper = model.Package.Document.Bones.Length;
        var inverse = FbxRestPoseAuthoringTests.ExactGlobals(model.Package.Document)[helper].InvertedAffine();
        return model with { Surfaces = model.Surfaces.Select(surface => surface with
        {
            IsSkinned = true, PaletteBoneIndices = [helper], InverseBindMatrices = [inverse],
            Vertices = surface.Vertices.Select(v => v with { BoneIndices = [0], BoneWeights = [1] }).ToImmutableArray(),
        }).ToImmutableArray() };
    }

    [Fact]
    public void WeightedHelpersBindByPersistentIdAndReplayWithTheirExactInverse()
    {
        var source = WeightedSource();
        var captured = FbxAuthoredModelLayer.Capture(source);
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(captured.Package)!;
        Assert.Equal(2, layer.Version);
        var helper = source.Package.Document.AuthoredHelpers[0];
        var declared = Assert.Single(layer.Bones, b => b.Name == helper.Name);
        Assert.Equal(helper.Id, declared.Id);
        Assert.DoesNotContain(layer.Bones, b => b.Name == source.Package.Document.AuthoredHelpers[1].Name);
        var reopened = FbxModelAuthoringImporter.ImportPackage(captured.Package);
        for (int i = 0; i < reopened.Surfaces.Length; i++)
        {
            var actual = reopened.Surfaces[i];
            int palette = Assert.Single(actual.PaletteBoneIndices);
            Assert.Equal(helper.Name, reopened.Package.Document.CreateEffectiveBones()[palette].Name);
            Assert.Equal(source.Surfaces[i].InverseBindMatrices.Single(), actual.InverseBindMatrices.Single());
            Assert.All(actual.Vertices, v => Assert.Equal<double>([1d], v.BoneWeights));
        }
        Assert.Equal<byte>(source.Package.SourceFbx, reopened.Package.SourceFbx);
    }

    [Fact]
    public void SameNamedReplacementHelperCannotTakeOverSavedWeights()
    {
        var model = FbxAuthoredModelLayer.Capture(WeightedSource());
        var doc = model.Package.Document;
        doc = doc with { AuthoredHelpers = doc.AuthoredHelpers.SetItem(0, doc.AuthoredHelpers[0] with { Id = Guid.NewGuid() }) };
        doc = doc with { RigSignature = CustomModelContractSignatures.ComputeRig(doc.CreateEffectiveBones()) };
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.ValidateAuthoredLayer(model.Package with { Document = doc }));
    }

    [Fact]
    public void LegacyBaseOnlyLayerReplaysAndUpgradesWithoutLosingSource()
    {
        var captured = FbxAuthoredModelLayer.Capture(StructuralHelperAuthoringTests.Source());
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(captured.Package)!;
        var legacy = ReplaceLayer(captured, layer with { Version = 1 });
        Assert.Equal(1, CustomModelPackageSerializer.ValidateAuthoredLayer(legacy.Package)!.Version);
        var reopened = FbxModelAuthoringImporter.ImportPackage(legacy.Package);
        var upgraded = FbxAuthoredModelLayer.Capture(reopened);
        Assert.Equal(2, CustomModelPackageSerializer.ValidateAuthoredLayer(upgraded.Package)!.Version);
        Assert.Equal<byte>(legacy.Package.SourceFbx, upgraded.Package.SourceFbx);
        Assert.Equal<CustomModelAuthoredHelper>(legacy.Package.Document.AuthoredHelpers, upgraded.Package.Document.AuthoredHelpers);
        Assert.Equal(layer.Components.Length, CustomModelPackageSerializer.ValidateAuthoredLayer(upgraded.Package)!.Components.Length);
    }

    [Fact]
    public void LegacyLayerCannotClaimHelperInfluences()
    {
        var model = FbxAuthoredModelLayer.Capture(WeightedSource());
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(model.Package)!;
        var legacy = ReplaceLayer(model, layer with { Version = 1 });
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.ValidateAuthoredLayer(legacy.Package));
    }

    private static FbxModelAuthoringImportResult ReplaceLayer(FbxModelAuthoringImportResult model, AuthoredModelLayer layer)
    {
        var payload = AuthoredModelLayerCodec.Serialize(layer);
        var reference = model.Package.Document.AuthoredLayer! with { PayloadLength = payload.Length,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())) };
        return model with { Package = model.Package with { AuthoredLayerPayload = payload,
            Document = model.Package.Document with { AuthoredLayer = reference } } };
    }
}
