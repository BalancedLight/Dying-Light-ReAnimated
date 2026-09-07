using System.IO.Compression;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class StockAnimationReferenceTests
{
    [Fact]
    public void SourceOnlyBankAndEmptyOrAmbiguousCompiledBanksAreRejected()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string archive = Path.Combine(root, "data.pak");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("data/characters/animations/animscripts/bank.scr").Open()))
                writer.Write("SeqTrack(\"idle\",\"stock_idle\",0,2,30,1,0)");
            InvalidDataException missing = Assert.Throws<InvalidDataException>(() =>
                Dl1StockAnimationReferenceValidator.Validate(archive, "bank"));
            Assert.Contains("0 compiled type-322", missing.Message, StringComparison.Ordinal);
            string compiled = WriteCompiledBank(archive, "bank");
            Dl1StockAnimationReference result = Dl1StockAnimationReferenceValidator.Validate(archive, "bank");
            Assert.Equal(1, result.CompiledBank.SequenceCount);
            Assert.Equal(64, result.CompiledBank.ArchiveSha256.Length);
            Assert.Equal(64, result.CompiledBank.RecordsSha256.Length);
            string duplicate = Path.Combine(root, "Data", "duplicate.rpack");
            File.Copy(compiled, duplicate);
            Assert.Throws<InvalidDataException>(() => Dl1StockAnimationReferenceValidator.Validate(archive, "bank"));
            File.Delete(duplicate);
            WriteCompiledBank(archive, "bank", empty: true);
            Assert.Throws<InvalidDataException>(() => Dl1StockAnimationReferenceValidator.Validate(archive, "bank"));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsIncludeCyclesButDeduplicatesCompletedSharedDependencies(bool cyclic)
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string archive = Path.Combine(root, "data.pak");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                Write("bank", "!include(\"left.scr\")\n!include(\"right.scr\")");
                Write("left", "!include(\"common.scr\")");
                Write("right", "!include(\"common.scr\")");
                Write("common", cyclic
                    ? "!include(\"BANK.scr\")"
                    : "SeqTrack(\"idle\",\"stock_idle\",0,2,30,1,0)");

                void Write(string name, string content)
                {
                    using var writer = new StreamWriter(zip.CreateEntry($"data/characters/animations/animscripts/{name}.scr").Open());
                    writer.Write(content);
                }
            }

            WriteCompiledBank(archive, "bank");
            if (cyclic)
            {
                InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                    Dl1StockAnimationReferenceValidator.Validate(archive, "bank"));
                Assert.Contains("include cycle", error.Message, StringComparison.Ordinal);
            }
            else
            {
                Dl1StockAnimationReference result = Dl1StockAnimationReferenceValidator.Validate(archive, "bank");
                Assert.Equal(4, result.Sources.Length);
                Assert.Equal(1, result.SequenceCount);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ResolvesIncludedStockSequencesAndRejectsLocalShadow()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string archive = Path.Combine(root,"data.pak");
            const string bankPath = "data/characters/animations/animscripts/bank.scr";
            using (var zip = ZipFile.Open(archive,ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry(bankPath).Open())) writer.Write("!include(\"child.scr\")\n// !include(\"missing.scr\")");
                using (var writer = new StreamWriter(zip.CreateEntry("data/characters/animations/animscripts/child.scr").Open())) writer.Write("SeqTrack(\"idle\",\"stock_idle\",0,2,30,1,0)");
            }
            WriteCompiledBank(archive, "bank");
            var result = Dl1StockAnimationReferenceValidator.Validate(archive,"bank",root);
            Assert.Equal(2,result.Sources.Length);
            Assert.Equal(1,result.SequenceCount);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root,bankPath))!);
            File.WriteAllText(Path.Combine(root,bankPath),"// empty local bank");
            Assert.Throws<InvalidDataException>(() => Dl1StockAnimationReferenceValidator.Validate(archive,"bank",root));
            Assert.Throws<InvalidDataException>(() => Dl1StockAnimationReferenceValidator.Validate(archive,"absent"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    public void ResolvesMissingBaseDependencyFromSiblingDlcArchive()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"DW"));
            Directory.CreateDirectory(Path.Combine(root,"DW_DLC1"));
            string archive = Path.Combine(root,"DW","Data0.pak");
            using (var zip = ZipFile.Open(archive,ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("data/characters/animations/animscripts/bank.scr").Open()))
                writer.Write("!include(\"included.scr\")");
            using (var zip = ZipFile.Open(Path.Combine(root,"DW_DLC1","DataDLC1_0.pak"),ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("data/sequence/included.scr").Open()))
                writer.Write("SeqTrack(\"idle\",\"stock_idle\",0,2,30,1,0)");
            WriteCompiledBank(archive, "bank");
            var reference = Dl1StockAnimationReferenceValidator.Validate(archive,"bank");
            Assert.Equal(1,reference.SequenceCount);
            Assert.Contains(reference.Sources,s=>s.ArchiveName=="DataDLC1_0.pak");
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    internal static string WriteCompiledBank(string sourceArchive, string bank, bool empty = false)
    {
        string directory = Path.Combine(Path.GetDirectoryName(sourceArchive)!, "Data");
        Directory.CreateDirectory(directory);
        AnimationScrSections sections = AnimationScrCodec.Build(empty
            ? []
            : [new AnimationScrSequence("idle", "stock_idle", 0, 2, 30)]);
        string path = Path.Combine(directory, "stock.rpack");
        File.WriteAllBytes(path, RpackTestData.BuildArchive(bank, Rp6lResourceTypes.AnimationScript,
            [new(0, sections.RecordsAndNames), new(0, sections.IndexAndNames)], RpackTestCompression.None));
        return path;
    }
}
