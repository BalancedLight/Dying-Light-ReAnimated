using System.Security.Cryptography;
using System.Text.Json;

namespace ReAnimated.Tests;

/// <summary>
/// Reads optional, user-owned test inputs that cannot be published with the
/// repository. The default location is ignored; an explicit environment
/// override makes configuration failures actionable instead of silently
/// skipping an intended local acceptance gate.
/// </summary>
internal sealed class ExternalCorpusManifest
{
    internal const string Format = "dl-reanimated-external-corpora-v1";
    internal const string ManifestEnvironmentVariable =
        "DLR_EXTERNAL_CORPORA_MANIFEST";

    private readonly Dictionary<string, ExternalCorpusControl> _controls;

    private ExternalCorpusManifest(
        string sourcePath,
        IEnumerable<ExternalCorpusControl> controls)
    {
        SourcePath = sourcePath;
        _controls = controls.ToDictionary(
            static control => control.Id,
            StringComparer.Ordinal);
    }

    public string SourcePath { get; }

    public static ExternalCorpusManifest? LoadOptional() =>
        LoadOptional(
            FindRepositoryRoot(),
            Environment.GetEnvironmentVariable(
                ManifestEnvironmentVariable));

    internal static ExternalCorpusManifest? LoadOptional(
        string repositoryRoot,
        string? configuredManifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        bool explicitlyConfigured = !string.IsNullOrWhiteSpace(
            configuredManifestPath);
        string manifestPath = explicitlyConfigured
            ? Path.GetFullPath(configuredManifestPath!.Trim().Trim('"'))
            : Path.Combine(
                Path.GetFullPath(repositoryRoot),
                "tests",
                "local-external-corpora.json");
        if (!File.Exists(manifestPath))
        {
            if (explicitlyConfigured)
            {
                throw new FileNotFoundException(
                    $"{ManifestEnvironmentVariable} names a manifest that does not exist.",
                    manifestPath);
            }

            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(manifestPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    RequiredString(root, "format", manifestPath),
                    Format,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"External corpus manifest '{manifestPath}' must declare format '{Format}'.");
            }

            if (!root.TryGetProperty("controls", out JsonElement controls) ||
                controls.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"External corpus manifest '{manifestPath}' must contain a controls array.");
            }

            var parsed = new List<ExternalCorpusControl>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement element in controls.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"External corpus manifest '{manifestPath}' contains a non-object control.");
                }

                string id = RequiredString(element, "id", manifestPath);
                if (!ids.Add(id))
                {
                    throw new InvalidDataException(
                        $"External corpus manifest '{manifestPath}' declares duplicate control '{id}'.");
                }

                string sha256 = RequiredString(element, "sha256", manifestPath)
                    .ToLowerInvariant();
                if (sha256.Length != 64 ||
                    sha256.Any(static character =>
                        !((character is >= '0' and <= '9') ||
                          (character is >= 'a' and <= 'f'))))
                {
                    throw new InvalidDataException(
                        $"External corpus control '{id}' in '{manifestPath}' has an invalid lowercase SHA-256.");
                }

                if (!element.TryGetProperty("expectations", out JsonElement expectations) ||
                    expectations.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"External corpus control '{id}' in '{manifestPath}' must contain an expectations object.");
                }

                var parsedExpectations = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (JsonProperty expectation in expectations.EnumerateObject())
                {
                    string value = expectation.Value.ValueKind switch
                    {
                        JsonValueKind.String => expectation.Value.GetString()
                            ?? throw new InvalidDataException(
                                $"External corpus control '{id}' has a null expectation '{expectation.Name}'."),
                        JsonValueKind.Number when expectation.Value.TryGetInt64(out long number) =>
                            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException(
                            $"External corpus control '{id}' has a non-scalar expectation '{expectation.Name}'."),
                    };
                    parsedExpectations.Add(expectation.Name, value);
                }

                parsed.Add(new ExternalCorpusControl(
                    id,
                    RequiredString(element, "kind", manifestPath),
                    RequiredString(element, "filePath", manifestPath),
                    sha256,
                    parsedExpectations,
                    manifestPath));
            }

            return new ExternalCorpusManifest(manifestPath, parsed);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"External corpus manifest '{manifestPath}' is not valid JSON.",
                exception);
        }
    }

    public ExternalCorpusControl RequireControl(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _controls.TryGetValue(id, out ExternalCorpusControl? control)
            ? control
            : throw new InvalidDataException(
                $"External corpus manifest '{SourcePath}' does not declare required control '{id}'.");
    }

    public IReadOnlyList<ExternalCorpusControl> RequireControls(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ExternalCorpusControl[] controls = _controls.Values
            .Where(control => string.Equals(
                control.Kind,
                kind,
                StringComparison.Ordinal))
            .OrderBy(static control => control.Id, StringComparer.Ordinal)
            .ToArray();
        if (controls.Length == 0)
        {
            throw new InvalidDataException(
                $"External corpus manifest '{SourcePath}' does not declare any '{kind}' controls.");
        }

        return controls;
    }

    private static string RequiredString(
        JsonElement element,
        string property,
        string manifestPath)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"External corpus manifest '{manifestPath}' requires non-empty string '{property}'.");
        }

        return value.GetString()!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root for external corpus configuration.");
    }
}

internal sealed record ExternalCorpusControl(
    string Id,
    string Kind,
    string FilePath,
    string Sha256,
    IReadOnlyDictionary<string, string> Expectations,
    string ManifestPath)
{
    public string RequireExistingFile()
    {
        string path = Path.IsPathFullyQualified(FilePath)
            ? Path.GetFullPath(FilePath)
            : Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(ManifestPath)!,
                FilePath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"External corpus control '{Id}' names a file that does not exist.",
                path);
        }

        return path;
    }

    public long RequireInt64(string name)
    {
        if (!Expectations.TryGetValue(name, out string? value) ||
            !long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long result))
        {
            throw new InvalidDataException(
                $"External corpus control '{Id}' requires integral expectation '{name}'.");
        }

        return result;
    }

    public int RequireInt32(string name)
    {
        long result = RequireInt64(name);
        return result is >= int.MinValue and <= int.MaxValue
            ? (int)result
            : throw new InvalidDataException(
                $"External corpus control '{Id}' expectation '{name}' is outside Int32 range.");
    }

    public string RequireString(string name) =>
        Expectations.TryGetValue(name, out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"External corpus control '{Id}' requires string expectation '{name}'.");

    public async Task VerifySha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External corpus control '{Id}' does not match its configured SHA-256.");
        }
    }
}

internal sealed class ExternalCorpusFactAttribute : FactAttribute
{
    public ExternalCorpusFactAttribute()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                ExternalCorpusManifest.ManifestEnvironmentVariable)))
        {
            return;
        }

        try
        {
            if (ExternalCorpusManifest.LoadOptional() is null)
            {
                Skip =
                    "External controls are not configured. Copy tests/local-external-corpora.example.json to tests/local-external-corpora.json or set DLR_EXTERNAL_CORPORA_MANIFEST.";
            }
        }
        catch (Exception)
        {
            // Let the test body expose malformed local configuration as a test failure.
        }
    }
}
