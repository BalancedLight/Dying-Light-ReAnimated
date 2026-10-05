using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Cli;

internal static class PlayerAppearanceCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options, CancellationToken token)
    {
        if (args.Length != 7)
            throw new ArgumentException("Usage: DLReAnimated bind-player-appearance <source.scr> <new-output.scr> <character-id> <appearance-id> <fpp.msh> <tpp.msh> <skin>");
        string source = Path.GetFullPath(args[0]);
        string destination = Path.GetFullPath(args[1]);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
            throw new IOException("Choose a new output file; the appearance source and existing outputs are preserved.");
        if (new FileInfo(source).Length > NativeCharacterScriptCodec.MaximumCharacters * 4L + 3)
            throw new InvalidDataException("The Player appearance script exceeds the supported size limit.");
        byte[] input = await File.ReadAllBytesAsync(source, token).ConfigureAwait(false);
        bool bom = input.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string text = new UTF8Encoding(false, true).GetString(input, bom ? 3 : 0, input.Length - (bom ? 3 : 0));
        var changed = Dl1PlayerAppearanceCodec.Read(text).Bind(args[2], args[3], args[4], args[5], args[6]);
        byte[] content = new UTF8Encoding(bom).GetBytes(changed.Syntax.Write());
        byte[] output = bom ? Encoding.UTF8.Preamble.ToArray().Concat(content).ToArray() : content;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        token.ThrowIfCancellationRequested();
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Move without overwrite also protects a destination created after preflight.
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            format = "dl-reanimated-player-appearance-binding-v1", source, destination,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(input)),
            outputSha256 = Convert.ToHexStringLower(SHA256.HashData(output)),
            characterId = args[2], appearanceId = args[3], firstPersonMesh = args[4], thirdPersonMesh = args[5], skin = args[6],
            runtimeValidated = false,
        }, options));
        return 0;
    }
}
