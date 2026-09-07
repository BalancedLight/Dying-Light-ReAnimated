using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelFeatureImportPersistenceTests
{
    [Fact]
    public void ActualPackageImportAndCompatibleReimportRetainAuthoredFeatures()
    {
        var source = BlenderFbxStrictValidationTests.CreateValidModelFixture();
        var imported = FbxModelAuthoringImporter.Import(source, "generic.fbx");
        string bone = imported.Package.Document.Bones[0].Name;
        var secondary = new SecondaryMotionDefinition
        {
            Groups = [new SecondaryMotionGroup
            {
                Name = "Accessory",
                Particles = [new SecondaryParticle { ReferenceBoneName = bone, Fixed = true },
                    new SecondaryParticle { ReferenceBoneName = bone, LocalPosition = new Vector3D(0, -0.1, 0) }],
                Constraints = [new SecondaryDistanceConstraint { First = 0, Second = 1 }],
            }],
        };
        var facial = new FacialPresetLibrary
        {
            Presets = [new FacialPresetDefinition { Name = "Neutral" }],
        };
        var package = imported.Package with
        {
            Document = imported.Package.Document with { SecondaryMotion = secondary, FacialPresets = facial },
        };
        var decoded = FbxModelAuthoringImporter.ImportPackage(package);
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(secondary),
            SecondaryMotionSetupSerializer.Serialize(decoded.Package.Document.SecondaryMotion));
        Assert.Equal("Neutral", Assert.Single(decoded.Package.Document.FacialPresets.Presets).Name);
        var reimport = FbxModelAuthoringImporter.PreviewReimport(package, source, "generic.fbx");
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(secondary),
            SecondaryMotionSetupSerializer.Serialize(reimport.Replacement.Package.Document.SecondaryMotion));
        Assert.Equal("Neutral", Assert.Single(reimport.Replacement.Package.Document.FacialPresets.Presets).Name);
    }
}
