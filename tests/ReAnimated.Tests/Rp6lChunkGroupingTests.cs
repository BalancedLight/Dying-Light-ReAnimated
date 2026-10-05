using System.Globalization;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Rp6lChunkGroupingTests
{
    [Fact]
    public async Task CompatibleUncompressedUnitsOverNativeChunkLimitKeepEveryItem()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var paths = new List<string>();
            for (int index = 0; index < 300; index++)
            {
                string name = "generic_" + index.ToString(CultureInfo.InvariantCulture);
                string path = Path.Combine(directory, name + ".rpack");
                byte[] bytes = RpackTestData.BuildArchive(name, index == 0 ? unchecked((short)0x8110) : Rp6lResourceTypes.Texture,
                    [new(1, [(byte)index, (byte)(index >> 8), 0x41])], RpackTestCompression.None);
                bytes[57] = 0xA1;
                await File.WriteAllBytesAsync(path, bytes);
                paths.Add(path);
            }
            string output = Path.Combine(directory, "linked.rpack");
            await Rp6lCompilerObjectNormalizer.LinkAtomicAsync(paths, output);
            var archive = await Rp6lArchive.OpenAsync(output);
            Assert.True(archive.Chunks.Count <= 256); Assert.Equal(300, archive.Resources.Count);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            for (int index = 0; index < 300; index++)
            {
                var resource = Assert.Single(archive.Resources, r => r.Name == "generic_" + index.ToString(CultureInfo.InvariantCulture));
                var item = Assert.Single(resource.Items);
                Assert.Equal(new byte[] { (byte)index, (byte)(index >> 8), 0x41 }, await archive.ReadItemBytesAsync(item, cache, 100));
                Assert.Equal(index == 0 ? (byte)0xA0 : (byte)0xA1, item.Flags);
            }
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
}



