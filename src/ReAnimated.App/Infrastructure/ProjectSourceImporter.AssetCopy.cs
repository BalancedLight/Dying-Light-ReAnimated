using System.IO;

namespace ReAnimated.App.Infrastructure;

public static partial class ProjectSourceImporter
{
    public static async Task<bool> CopyVerifiedAssetAsync(string sourcePath, string destinationPath,
        string expectedSha256, CancellationToken cancellationToken)
    {
        ValidateAssetFileSize(sourcePath);
        if (File.Exists(destinationPath))
        {
            string existing = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A destination asset contains different bytes.");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await CopyAtomicAsync(sourcePath, destinationPath, expectedSha256, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static void ValidateAssetFileSize(string path)
    {
        var source = new FileInfo(path);
        if (!source.Exists || source.Length <= 0 || source.Length > MaximumSourceBytes)
            throw new InvalidDataException("The source asset is missing or has an unsupported size.");
    }
}
