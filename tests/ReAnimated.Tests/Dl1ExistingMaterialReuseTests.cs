using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

/// <summary>Hermetic source and ABDM fixtures exercise the verified material-reuse boundary.</summary>
public sealed class Dl1ExistingMaterialReuseTests
{
    private const string MaterialReference = "surface.mat";
    private const string MaterialFile = "surface.dmt";
    private const string TextureFile = "surface.dds";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AcceptsIdenticalSourcesAndReturnsOnlyCurrentFilesWithExactHashes()
    {
        using var fixture = new Fixture();
        var hashes = fixture.Validate();
        string[] expected = [fixture.Current(MaterialFile), fixture.Current(TextureFile), fixture.Database];
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), hashes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (string path in expected)
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), hashes[path]);
        }
        Dl1OfficialModelCompiler.VerifyMaterialReuseFilesAreCurrent(hashes);
    }

    [Theory]
    [InlineData(MaterialFile)]
    [InlineData(TextureFile)]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsChangedStagedSource(string file)
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.Staged(file), "changed");
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData(MaterialFile)]
    [InlineData(TextureFile)]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsMissingCurrentSource(string file)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Current(file));
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData(MaterialFile)]
    [InlineData(TextureFile)]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsMissingStagedSource(string file)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Staged(file));
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsMissingCompiledMaterialIdentity()
    {
        using var fixture = new Fixture();
        fixture.WriteDatabase("other.mat", TextureFile);
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsWrongCompiledTextureEvenWhenDmtAndDdsMatch()
    {
        using var fixture = new Fixture();
        fixture.WriteDatabase(MaterialReference, "other.dds");
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("../surface.dds")]
    [InlineData("nested/surface.dds")]
    [InlineData("other.dds")]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsForbiddenOrUndeclaredTextureReference(string texture)
    {
        using var fixture = new Fixture();
        fixture.WriteMaterial(texture);
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsAdditionalTextureFieldOutsideTheVerifiedInventory()
    {
        using var fixture = new Fixture();
        fixture.WriteMaterial(TextureFile, "<nrm_0_tex>\"other.dds\"</nrm_0_tex>");
        Assert.Throws<InvalidDataException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData(MaterialFile, false)]
    [InlineData(TextureFile, false)]
    [InlineData("database.mp", false)]
    [InlineData(MaterialFile, true)]
    [InlineData(TextureFile, true)]
    [InlineData("database.mp", true)]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsCurrentFileDriftAfterSuccessfulValidation(string file, bool delete)
    {
        using var fixture = new Fixture();
        var hashes = fixture.Validate();
        string path = file == "database.mp" ? fixture.Database : fixture.Current(file);
        if (delete) File.Delete(path);
        else File.AppendAllText(path, "changed");
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.VerifyMaterialReuseFilesAreCurrent(hashes));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AcceptsChangedGeometryCountsAndTopologyWithIdenticalNamedFeatureSets()
    {
        var previous = Features();
        var updated = Features();
        Array.Reverse(updated[MaterialReference]);
        // Geometry is deliberately outside this shader-feature contract. Both geometry
        // revisions use the same layout, skinning, morph and raw-load requirements.
        var previousGeometry = (VertexCount: 4, Indices: new[] { 0, 1, 2, 0, 2, 3 });
        var updatedGeometry = (VertexCount: 5, Indices: new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4 });
        Assert.NotEqual(previousGeometry.VertexCount, updatedGeometry.VertexCount);
        Assert.NotEqual(previousGeometry.Indices.Length, updatedGeometry.Indices.Length);
        Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated);
    }

    [Theory]
    [InlineData("layout:position-normal-uv")]
    [InlineData("skin:four-weights")]
    [InlineData("morph:position-deltas")]
    [InlineData("raw-load:enabled")]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsChangedShaderRelevantFeature(string feature)
    {
        var previous = Features();
        var updated = Features();
        updated[MaterialReference] = updated[MaterialReference].Select(value => value == feature ? feature + ":changed" : value).ToArray();
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsRemovedFeatureEvenWhenOtherRequirementsMatch()
    {
        var previous = Features();
        var updated = Features();
        updated[MaterialReference] = updated[MaterialReference][..^1];
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsAddedFeatureEvenWhenMaterialNameMatches()
    {
        var previous = Features();
        var updated = Features();
        updated[MaterialReference] = [.. updated[MaterialReference], "tangent:required"];
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsRenamedMaterialWithTheSameFeatureSet()
    {
        var previous = Features();
        var updated = new Dictionary<string, string[]> { ["other.mat"] = previous[MaterialReference] };
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsAdditionalMaterialWithTheSameFeatureSet()
    {
        var previous = Features();
        var updated = Features();
        updated["other.mat"] = previous[MaterialReference];
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateMaterialReuseFeatures(previous, updated));
    }

    [Theory]
    [InlineData("../surface.mat", "surface.dds")]
    [InlineData("nested/surface.mat", "surface.dds")]
    [InlineData("surface.mat", "../surface.dds")]
    [InlineData("surface.mat", "nested/surface.dds")]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsPathBearingSourceInventories(string material, string texture)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Validate(material, texture));
    }
    private static Dictionary<string, string[]> Features() => new()
    {
        [MaterialReference] = ["layout:position-normal-uv", "skin:four-weights", "morph:position-deltas", "raw-load:enabled"],
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = RpackTestData.CreateTemporaryDirectory();
        private readonly string stagedDirectory;
        private readonly string currentDirectory;
        public string Database { get; }

        public Fixture()
        {
            stagedDirectory = Path.Combine(directory, "staged");
            currentDirectory = Path.Combine(directory, "current");
            Directory.CreateDirectory(stagedDirectory);
            Directory.CreateDirectory(currentDirectory);
            Database = Path.Combine(currentDirectory, "database.mp");
            WriteMaterial(TextureFile);
            byte[] texture = [0x44, 0x44, 0x53, 0x20, 0x10, 0x20, 0x30, 0x40];
            File.WriteAllBytes(Staged(TextureFile), texture);
            File.WriteAllBytes(Current(TextureFile), texture);
            WriteDatabase(MaterialReference, TextureFile);
        }

        public string Staged(string file) => Path.Combine(stagedDirectory, file);
        public string Current(string file) => Path.Combine(currentDirectory, file);

        public void WriteMaterial(string texture, string extra = "")
        {
            string xml = $"<MaterialData><TemplateData><template>standard</template><dif_0_tex>\"{texture}\"</dif_0_tex>{extra}</TemplateData></MaterialData>";
            File.WriteAllText(Staged(MaterialFile), xml);
            File.WriteAllText(Current(MaterialFile), xml);
        }

        public void WriteDatabase(string material, string texture) => File.WriteAllBytes(Database, BuildDatabase(material, texture));

        public System.Collections.Immutable.ImmutableDictionary<string, string> Validate(string material = MaterialReference, string texture = TextureFile) =>
            Dl1OfficialModelCompiler.ValidateMaterialReuseSources(stagedDirectory, currentDirectory, [material], [texture], Database);

        public void Dispose() => RpackTestData.DeleteTemporaryDirectory(directory);
    }

    private static byte[] BuildDatabase(string material, string texture)
    {
        const int headerSize = 16;
        const int containerRowSize = 48;
        const int recordRowSize = 16;
        const int payloadSize = 36;
        const int tableOffset = headerSize + containerRowSize;
        const int payloadOffset = tableOffset + recordRowSize;
        byte[] output = new byte[payloadOffset + payloadSize];
        "ABDM"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), headerSize);
        Span<byte> container = output.AsSpan(headerSize, containerRowSize);
        "materials"u8.CopyTo(container);
        BinaryPrimitives.WriteUInt32LittleEndian(container[32..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(container[36..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(container[40..], tableOffset);
        uint materialHash = ResourceHash(material);
        Span<byte> record = output.AsSpan(tableOffset, recordRowSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record, materialHash);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], payloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], payloadSize);
        Span<byte> payload = output.AsSpan(payloadOffset, payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, materialHash);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[16..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[18..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[28..], ResourceHash(texture));
        return output;
    }

    private static uint ResourceHash(string name)
    {
        uint crc = 0x811C9DC5 ^ uint.MaxValue;
        foreach (byte value in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320U & unchecked((uint)-(int)(crc & 1)));
        }
        return crc ^ uint.MaxValue;
    }
}

