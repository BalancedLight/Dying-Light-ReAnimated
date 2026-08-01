using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReAnimated.Cli;

namespace ReAnimated.App.Infrastructure;

public static class PackageSelfTest
{
    public const string Switch =
        "--package-self-test";
    public const string ResultFileName =
        "DL_REANIMATED_PACKAGE_SELF_TEST.json";
    public const string Format =
        "dl-reanimated-package-self-test";
    public const int SchemaVersion = 2;

    private const string CandidateSha256Metadata =
        "DLReAnimatedCandidateSourceSha256";
    private const string CandidateInputCountMetadata =
        "DLReAnimatedCandidateInputCount";
    private const string GitHeadMetadata =
        "DLReAnimatedGitHead";
    private const string GitStateMetadata =
        "DLReAnimatedGitState";
    private const string SourceIdentityMetadata =
        "DLReAnimatedSourceIdentity";

    private static readonly string[] RequiredResourceSuffixes =
    [
        "Embedded.LICENSE",
        "Embedded.README.md",
        "Embedded.Schemas.dlraproj.schema.json",
        "Embedded.Schemas.animation-library-build.schema.json",
        "Embedded.Docs.CSHARP_REWRITE.md",
        "Embedded.Docs.DL1_FIRST_RELEASE_SUPPORT_MATRIX.md",
        "Embedded.Docs.DL1_BLENDER_RETAIL_HANDOFF.md",
        "Embedded.Docs.DL1_WPF_STARTUP_ACCEPTANCE.md",
        BlenderHelperResource.ResourceSuffix,
    ];
    private static readonly string[] RequiredHelperMarkers =
    [
        "child_pivot_display_v1",
        "bake_anim_use_all_actions=True",
        "DLR_ACTION_STACKS:",
        "DLR_BIND_POSE:",
        "DLR_ROOT_PARITY:",
        "DLR_EXPORT_COMPLETE:",
    ];
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true,
        };

    public static bool IsRequested(
        IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        string.Equals(
            arguments[0],
            Switch,
            StringComparison.Ordinal);

    public static async Task RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if ((arguments.Count != 2 &&
             arguments.Count != 8) ||
            !string.Equals(
                arguments[0],
                Switch,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(arguments[1]))
        {
            throw new ArgumentException(
                $"Usage: {Switch} <empty-output-directory> [<candidate-sha256> <candidate-input-count> <git-head> <clean|dirty> <source-identity> <informational-version>]",
                nameof(arguments));
        }

        PackageProvenanceExpectation? provenanceExpectation =
            arguments.Count == 8
                ? ParseProvenanceExpectation(arguments)
                : null;
        if (!Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture !=
                Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "The packaged DL ReAnimated application must run as an x64 process.");
        }

        string outputDirectory =
            Path.GetFullPath(arguments[1]);
        if (Directory.Exists(outputDirectory) &&
            Directory.EnumerateFileSystemEntries(
                    outputDirectory)
                .Any())
        {
            throw new IOException(
                "The package self-test output directory must be empty.");
        }

        Directory.CreateDirectory(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string[] cliCommands =
            CliApplication.SupportedCommands.ToArray();
        if (cliCommands.Length == 0 ||
            cliCommands.Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count() != cliCommands.Length ||
            cliCommands.Any(command =>
                !CliApplication.IsInvocation([command])) ||
            !CliApplication.IsInvocation(["--help"]) ||
            CliApplication.IsInvocation([]) ||
            CliApplication.IsInvocation([Switch]))
        {
            throw new InvalidDataException(
                "The packaged CLI dispatch contract is unavailable or inconsistent.");
        }

        Assembly assembly =
            typeof(PackageSelfTest).Assembly;
        PackageBuildProvenance provenance =
            ReadBuildProvenance(assembly);
        if (provenanceExpectation is not null)
        {
            ValidateExpectedProvenance(
                provenance,
                provenanceExpectation);
        }

        string[] resources =
            assembly.GetManifestResourceNames();
        foreach (string suffix in
                 RequiredResourceSuffixes)
        {
            int count = resources.Count(name =>
                name.EndsWith(
                    suffix,
                    StringComparison.Ordinal));
            if (count != 1)
            {
                throw new InvalidDataException(
                    $"The packaged application must contain exactly one embedded resource ending in '{suffix}'; found {count:N0}.");
            }
        }

        var helperResource =
            new BlenderHelperResource(assembly);
        string helperResourceName =
            helperResource.ResolveResourceName();
        string helperPath =
            await helperResource.ExtractAsync(
                    outputDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        byte[] helperBytes =
            await File.ReadAllBytesAsync(
                    helperPath,
                    cancellationToken)
                .ConfigureAwait(false);
        string helperText =
            System.Text.Encoding.UTF8.GetString(
                helperBytes);
        foreach (string marker in
                 RequiredHelperMarkers)
        {
            if (!helperText.Contains(
                    marker,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The embedded Blender helper is missing required marker '{marker}'.");
            }
        }

        string helperSha256 =
            Convert.ToHexString(
                    SHA256.HashData(helperBytes))
                .ToLowerInvariant();
        var result = new PackageSelfTestResult(
            Format,
            SchemaVersion,
            RuntimeInformation.ProcessArchitecture
                .ToString(),
            helperResourceName,
            helperSha256,
            helperBytes.LongLength,
            CliApplication.DispatchContract,
            cliCommands,
            provenance.IsVerified,
            provenance.AssemblyInformationalVersion,
            provenance.CandidateSourceSha256,
            provenance.CandidateInputCount,
            provenance.GitHead,
            provenance.GitState,
            provenance.SourceIdentity,
            resources
                .Where(name =>
                    RequiredResourceSuffixes.Any(suffix =>
                        name.EndsWith(
                            suffix,
                            StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray());
        string resultPath =
            Path.Combine(
                outputDirectory,
                ResultFileName);
        string temporaryResultPath =
            resultPath + ".tmp";
        await using (FileStream stream = new(
                         temporaryResultPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    result,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(
            temporaryResultPath,
            resultPath);
    }

    public sealed record PackageSelfTestResult(
        [property: JsonPropertyName("format")]
        string Format,
        [property: JsonPropertyName("schemaVersion")]
        int SchemaVersion,
        [property: JsonPropertyName("processArchitecture")]
        string ProcessArchitecture,
        [property: JsonPropertyName("helperResourceName")]
        string HelperResourceName,
        [property: JsonPropertyName("helperSha256")]
        string HelperSha256,
        [property: JsonPropertyName("helperLength")]
        long HelperLength,
        [property: JsonPropertyName("cliDispatchContract")]
        string CliDispatchContract,
        [property: JsonPropertyName("cliCommands")]
        IReadOnlyList<string> CliCommands,
        [property: JsonPropertyName("provenanceVerified")]
        bool ProvenanceVerified,
        [property: JsonPropertyName("assemblyInformationalVersion")]
        string AssemblyInformationalVersion,
        [property: JsonPropertyName("candidateSourceSha256")]
        string? CandidateSourceSha256,
        [property: JsonPropertyName("candidateInputCount")]
        int? CandidateInputCount,
        [property: JsonPropertyName("gitHead")]
        string? GitHead,
        [property: JsonPropertyName("gitState")]
        string? GitState,
        [property: JsonPropertyName("sourceIdentity")]
        string? SourceIdentity,
        [property: JsonPropertyName("requiredResources")]
        IReadOnlyList<string> RequiredResources);

    private static PackageProvenanceExpectation
        ParseProvenanceExpectation(
            IReadOnlyList<string> arguments)
    {
        if (!int.TryParse(
                arguments[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int inputCount))
        {
            throw new ArgumentException(
                "The expected candidate input count is invalid.",
                nameof(arguments));
        }

        var expectation = new PackageProvenanceExpectation(
            arguments[2],
            inputCount,
            arguments[4],
            arguments[5],
            arguments[6],
            arguments[7]);
        ValidateCanonicalProvenance(
            expectation.CandidateSourceSha256,
            expectation.CandidateInputCount,
            expectation.GitHead,
            expectation.GitState,
            expectation.SourceIdentity,
            expectation.AssemblyInformationalVersion);
        return expectation;
    }

    private static PackageBuildProvenance ReadBuildProvenance(
        Assembly assembly)
    {
        string informationalVersion =
            assembly.GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? throw new InvalidDataException(
                "The package assembly has no informational version.");
        AssemblyMetadataAttribute[] metadata =
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToArray();
        string? candidateSha256 = ReadSingleMetadata(
            metadata,
            CandidateSha256Metadata);
        string? candidateInputCountText = ReadSingleMetadata(
            metadata,
            CandidateInputCountMetadata);
        string? gitHead = ReadSingleMetadata(
            metadata,
            GitHeadMetadata);
        string? gitState = ReadSingleMetadata(
            metadata,
            GitStateMetadata);
        string? sourceIdentity = ReadSingleMetadata(
            metadata,
            SourceIdentityMetadata);
        bool anyProvenance =
            candidateSha256 is not null ||
            candidateInputCountText is not null ||
            gitHead is not null ||
            gitState is not null ||
            sourceIdentity is not null;
        if (!anyProvenance)
        {
            return new PackageBuildProvenance(
                false,
                informationalVersion,
                null,
                null,
                null,
                null,
                null);
        }

        if (candidateSha256 is null ||
            candidateInputCountText is null ||
            gitHead is null ||
            gitState is null ||
            sourceIdentity is null ||
            !int.TryParse(
                candidateInputCountText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int candidateInputCount))
        {
            throw new InvalidDataException(
                "The package assembly contains incomplete provenance metadata.");
        }

        ValidateCanonicalProvenance(
            candidateSha256,
            candidateInputCount,
            gitHead,
            gitState,
            sourceIdentity,
            informationalVersion);
        return new PackageBuildProvenance(
            true,
            informationalVersion,
            candidateSha256,
            candidateInputCount,
            gitHead,
            gitState,
            sourceIdentity);
    }

    private static string? ReadSingleMetadata(
        IEnumerable<AssemblyMetadataAttribute> metadata,
        string key)
    {
        string?[] matches = metadata
            .Where(attribute => string.Equals(
                attribute.Key,
                key,
                StringComparison.Ordinal))
            .Select(static attribute => attribute.Value)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 when !string.IsNullOrWhiteSpace(matches[0]) =>
                matches[0],
            _ => throw new InvalidDataException(
                $"The package assembly metadata '{key}' is duplicated or empty."),
        };
    }

    private static void ValidateCanonicalProvenance(
        string candidateSha256,
        int candidateInputCount,
        string gitHead,
        string gitState,
        string sourceIdentity,
        string informationalVersion)
    {
        if (!Regex.IsMatch(
                candidateSha256,
                "^[0-9a-f]{64}$",
                RegexOptions.CultureInvariant) ||
            candidateInputCount <= 0 ||
            !Regex.IsMatch(
                gitHead,
                "^[0-9a-f]{40}$",
                RegexOptions.CultureInvariant) ||
            gitState is not ("clean" or "dirty"))
        {
            throw new InvalidDataException(
                "The package assembly provenance fields are malformed.");
        }

        string expectedSourceIdentity =
            $"dl-reanimated-csharp-source-v1.git-{gitHead}.state-{gitState}.inputs-{candidateInputCount}.sha256-{candidateSha256}";
        string expectedVersionSuffix =
            $"+git.{gitHead}.candidate.{candidateSha256}.inputs.{candidateInputCount}.{gitState}";
        if (!string.Equals(
                sourceIdentity,
                expectedSourceIdentity,
                StringComparison.Ordinal) ||
            !informationalVersion.EndsWith(
                expectedVersionSuffix,
                StringComparison.Ordinal) ||
            informationalVersion.Length ==
                expectedVersionSuffix.Length)
        {
            throw new InvalidDataException(
                "The package assembly source identity and informational version do not match its provenance fields.");
        }
    }

    private static void ValidateExpectedProvenance(
        PackageBuildProvenance actual,
        PackageProvenanceExpectation expected)
    {
        if (!actual.IsVerified ||
            !string.Equals(
                actual.CandidateSourceSha256,
                expected.CandidateSourceSha256,
                StringComparison.Ordinal) ||
            actual.CandidateInputCount !=
                expected.CandidateInputCount ||
            !string.Equals(
                actual.GitHead,
                expected.GitHead,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.GitState,
                expected.GitState,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.SourceIdentity,
                expected.SourceIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.AssemblyInformationalVersion,
                expected.AssemblyInformationalVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The packaged executable provenance does not match the release inputs.");
        }
    }

    private sealed record PackageBuildProvenance(
        bool IsVerified,
        string AssemblyInformationalVersion,
        string? CandidateSourceSha256,
        int? CandidateInputCount,
        string? GitHead,
        string? GitState,
        string? SourceIdentity);

    private sealed record PackageProvenanceExpectation(
        string CandidateSourceSha256,
        int CandidateInputCount,
        string GitHead,
        string GitState,
        string SourceIdentity,
        string AssemblyInformationalVersion);
}
