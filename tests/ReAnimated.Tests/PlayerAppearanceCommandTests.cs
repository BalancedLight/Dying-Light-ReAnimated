using System.Text;
using ReAnimated.Cli;

namespace ReAnimated.Tests;

public sealed class PlayerAppearanceCommandTests
{
    [Fact]
    public async Task CommandPreservesSourceBomAndRejectsExistingOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "appearance-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.scr");
            string output = Path.Combine(directory, "bound.scr");
            const string text = "sub appearances() { Character(\"hero\") { Appearance(\"h\", \"b\", \"look\") { MeshFpp(\"old_fpp.msh\"); MeshTpp(\"old_tpp.msh\"); Skin(\"old\"); Default(); } } }\r\n";
            await File.WriteAllTextAsync(source, text, new UTF8Encoding(true));
            byte[] before = await File.ReadAllBytesAsync(source);
            string[] args = ["bind-player-appearance", source, output, "hero", "look", "new_fpp.msh", "new_tpp.msh", "default"];
            Assert.Equal(0, await CliApplication.RunAsync(args));
            byte[] created = await File.ReadAllBytesAsync(output);
            Assert.True(created.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
            Assert.Contains("MeshFpp(\"new_fpp.msh\")", await File.ReadAllTextAsync(output), StringComparison.Ordinal);
            Assert.Equal(2, await CliApplication.RunAsync(args));
            Assert.Equal(created, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
