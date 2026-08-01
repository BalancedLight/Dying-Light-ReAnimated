using System.IO;
using System.Reflection;

namespace ReAnimated.App.Infrastructure;

public sealed class BlenderHelperResource
{
    public const string ResourceSuffix =
        "Blender.export_dl1_retail_anm2_fbx.py";

    private readonly Assembly _assembly;

    public BlenderHelperResource(Assembly? assembly = null)
    {
        _assembly = assembly ?? typeof(BlenderHelperResource).Assembly;
    }

    public string ResolveResourceName()
    {
        string[] matches = _assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(
                ResourceSuffix,
                StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                "The embedded Blender FBX helper is missing from this application build."),
            _ => throw new InvalidOperationException(
                "The application contains more than one Blender FBX helper resource."),
        };
    }

    public async Task<string> ExtractAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        string resourceName = ResolveResourceName();
        await using Stream source =
            _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The embedded Blender FBX helper could not be opened.");
        string outputPath = Path.Combine(
            directory,
            "export_dl1_retail_anm2_fbx.py");
        await using FileStream output = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await source.CopyToAsync(output, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        return outputPath;
    }
}
