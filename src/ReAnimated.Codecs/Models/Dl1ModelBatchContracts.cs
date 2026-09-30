using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReAnimated.Codecs.Models;

public enum Dl1ModelBatchItemState { Pending, Running, CompilerValidated, NeedsReview, Failed, Cancelled }

public sealed record Dl1ModelBatchFile(string Path, string Sha256);
public sealed record Dl1ModelBatchItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public required Dl1ModelBatchFile Package { get; init; }
    public bool Approved { get; init; }
    // Omit absent setups to preserve existing v1 manifest hashes across upgrades.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dl1ModelBatchSetup? Setup { get; init; }
}

public sealed record Dl1ModelBatchSetupBinding(string Key, Guid DestinationEntityId);

public sealed record Dl1ModelBatchSetup
{
    public required Dl1ModelBatchFile Preset { get; init; }
    public ImmutableArray<Dl1ModelBatchSetupBinding> Bindings { get; init; } = [];
}

/// <summary>Reviewed package inputs. The saved package owns its rig, profile and animation selections.</summary>
public sealed record Dl1ModelBatchManifest
{
    public string Format { get; init; } = "dl-reanimated-model-batch";
    public int Version { get; init; } = 1;
    public Guid Id { get; init; }
    public required Dl1ModelBatchFile Compiler { get; init; }
    public Dl1ModelBatchFile? RetailData0 { get; init; }
    public string? CompilerWorkingDirectory { get; init; }
    public ImmutableArray<Dl1ModelBatchFile> AdditionalToolInputs { get; init; } = [];
    public ImmutableArray<Dl1ModelBatchItem> Items { get; init; } = [];
}

public sealed record Dl1ModelBatchItemReceipt
{
    public Guid Id { get; init; }
    public Dl1ModelBatchItemState State { get; init; }
    public int Attempts { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? PackageDirectory { get; init; }
    public ImmutableDictionary<string, string> Outputs { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<string> Warnings { get; init; } = [];
    public DateTimeOffset? UpdatedUtc { get; init; }
}

public sealed record Dl1ModelBatchReceipt
{
    public string Format { get; init; } = "dl-reanimated-model-batch-receipt";
    public int Version { get; init; } = 1;
    public Guid Id { get; init; }
    public string ManifestSha256 { get; init; } = string.Empty;
    public string ToolFingerprint { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public ImmutableArray<Dl1ModelBatchItemReceipt> Items { get; init; } = [];
    public bool Interrupted { get; init; }
    public bool RuntimeVerified { get; }
    public string ProvenanceScope { get; } = "Approved package and optional setup bytes, destination bindings, declared tool inputs and output hashes. Full native toolchain closure and actual-load/runtime behavior remain separate checks.";
}

public sealed record Dl1ModelBatchRequest
{
    public required Dl1ModelBatchManifest Manifest { get; init; }
    public required string OutputDirectory { get; init; }
    public TimeSpan CompilerTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public IProgress<Dl1ModelBatchItemReceipt>? Progress { get; init; }
    internal Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>>? BuildOverride { get; init; }
}

internal sealed record Dl1ModelBatchBuildOutput(string PackageDirectory, ImmutableArray<string> Warnings);
public sealed record Dl1ModelBatchResult(string Directory, string ReceiptPath, Dl1ModelBatchReceipt Receipt);

public static class Dl1ModelBatchJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Dl1ModelBatchManifest LoadManifest(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (new FileInfo(full).Length > 1024 * 1024) throw new InvalidDataException("Batch manifests are limited to 1 MiB.");
        var manifest = JsonSerializer.Deserialize<Dl1ModelBatchManifest>(File.ReadAllBytes(full), Options)
            ?? throw new InvalidDataException("Batch manifest cannot be null.");
        if (manifest.Compiler is null || manifest.Items.IsDefaultOrEmpty || manifest.AdditionalToolInputs.IsDefault ||
            manifest.Items.Any(i => i is null || i.Package is null || i.Setup is { Preset: null }) || manifest.AdditionalToolInputs.Any(i => i is null))
            throw new InvalidDataException("Batch manifest contains missing file or item declarations.");
        string root = System.IO.Path.GetDirectoryName(full)!;
        Dl1ModelBatchFile Resolve(Dl1ModelBatchFile file) => file with { Path = System.IO.Path.GetFullPath(file.Path, root) };
        return manifest with
        {
            Compiler = Resolve(manifest.Compiler), RetailData0 = manifest.RetailData0 is { } data ? Resolve(data) : null,
            CompilerWorkingDirectory = manifest.CompilerWorkingDirectory is { } work ? System.IO.Path.GetFullPath(work, root) : null,
            AdditionalToolInputs = manifest.AdditionalToolInputs.Select(Resolve).ToImmutableArray(),
            Items = manifest.Items.Select(item => item with { Package = Resolve(item.Package), Setup = item.Setup is { } setup ? setup with { Preset = Resolve(setup.Preset) } : null }).ToImmutableArray(),
        };
    }
}
