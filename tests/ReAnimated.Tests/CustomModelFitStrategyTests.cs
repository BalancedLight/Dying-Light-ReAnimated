using System.Text.Json;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CustomModelFitStrategyTests
{
    [Fact]
    public void Schema10KeepsLegacyFitDecisionsAndSchema11PersistsStockChoice()
    {
        var previous = new CustomModelRigConformance
        {
            TemplateId = "synthetic-reference", TemplateProfileName = "player",
            TemplateSourceResourceName = "synthetic-player", TemplateFingerprint = new string('a', 64),
            SourceFbxSha256 = new string('b', 64), ScaleMode = CustomModelConformanceScaleMode.Manual,
            ManualScale = .75, ConformanceStrength = .375,
            PositionOverrides = [new() { BoneName = "joint", Position = new Vector3D(1, 2, 3) }],
        };
        CustomModelDocument legacy = CustomModelPreviewSessionTests.CreateModel(false).Package.Document with
        { SchemaVersion = 10, RigConformance = previous };
        CustomModelDocument migrated = CustomModelPackageSerializer.MigrateToCurrent(legacy);
        Assert.Equal(11, migrated.SchemaVersion);
        Assert.Equal(CustomModelRigFitStrategy.Legacy, migrated.RigConformance!.FitStrategy);
        Assert.Equal(previous, migrated.RigConformance);
        Assert.Equal(0, (int)CustomModelRigFitStrategy.Legacy);
        Assert.Equal(1, (int)CustomModelRigFitStrategy.StockFppRegional);

        CustomModelRigConformance stock = previous with
        {
            FitStrategy = CustomModelRigFitStrategy.StockFppRegional,
            StockFppNativeBoneCount = 87, StockFppNativeFrameSignature = new string('c', 64),
        };
        var options = CustomModelPackageSerializer.CreateSerializerOptions();
        CustomModelRigConformance restored = JsonSerializer.Deserialize<CustomModelRigConformance>(
            JsonSerializer.Serialize(stock, options), options)!;
        restored.Validate(nameof(restored));
        Assert.Equal(CustomModelRigFitStrategy.StockFppRegional, restored.FitStrategy);
        Assert.Equal(stock.StockFppNativeBoneCount, restored.StockFppNativeBoneCount);
        Assert.Equal(stock.StockFppNativeFrameSignature, restored.StockFppNativeFrameSignature);
    }
}
