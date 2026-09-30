using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.Infrastructure;

public static partial class DeveloperToolsAnimationRefreshService
{
    // Editor compiles can rewrite unrelated compiled/shared outputs in the same project.
    // Keep deployment-owned inputs and animation outputs strict; only tolerate drift in
    // compiled artifacts outside the animation object directory and the shared material DB.
    private static bool HasBlockingStaleArtifacts(Dl1DeveloperToolsDeploymentReceipt receipt)
    {
        if (receipt.StaleArtifactPaths.IsDefaultOrEmpty)
        {
            return false;
        }

        var stalePaths = new HashSet<string>(receipt.StaleArtifactPaths, StringComparer.OrdinalIgnoreCase);
        foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in receipt.Artifacts)
        {
            if (!stalePaths.Contains(artifact.RelativePath))
            {
                continue;
            }

            if (artifact.Role == Dl1DeploymentArtifactRole.Compiled &&
                !artifact.RelativePath.StartsWith("assets_pc/characters/animations/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (artifact.Role == Dl1DeploymentArtifactRole.Shared &&
                string.Equals(artifact.RelativePath, "assets_pc/local_dx11.mp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        // A stale path absent from the authenticated receipt is malformed and fails closed.
        return stalePaths.Any(path => !receipt.Artifacts.Any(artifact =>
            string.Equals(artifact.RelativePath, path, StringComparison.OrdinalIgnoreCase)));
    }

    public static DeveloperToolsAnimationRefreshRequestResult WriteRequest(
        string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt,
        DeveloperToolsAnimationRefreshHost targetHost,
        DeveloperToolsAnimationRefreshRoute route,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (HasBlockingStaleArtifacts(receipt) ||
            receipt.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(receipt.AnimationContentManifestRelativePath) ||
            string.IsNullOrWhiteSpace(receipt.EffectiveAnimationRuntimePackRelativePath) ||
            !Regex.IsMatch(
                receipt.AnimationContentManifestSha256 ?? string.Empty,
                "^[0-9a-f]{64}$",
                RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(
                receipt.EffectiveAnimationRuntimePackSha256 ?? string.Empty,
                "^[0-9a-f]{64}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException(
                "The active deployment is stale or does not contain a schema-2 animation content manifest receipt.");
        }

        string expectedRelativePath =
            $"{RefreshRootRelativePath}/manifests/{receipt.DeploymentId}.json";
        if (!string.Equals(
                receipt.AnimationContentManifestRelativePath,
                expectedRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The deployment receipt does not identify the canonical animation content manifest path.");
        }

        string root = ValidateProjectRoot(projectRoot);
        string manifestPath = ResolveRegularProjectFile(
            root,
            expectedRelativePath,
            MaximumManifestBytes,
            "animation content manifest");
        byte[] manifestBytes = ReadBoundedFile(
            manifestPath,
            MaximumManifestBytes,
            "animation content manifest");
        string actualSha256 = ComputeSha256(manifestBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256),
                Convert.FromHexString(receipt.AnimationContentManifestSha256!)))
        {
            throw new InvalidDataException(
                "The animation content manifest changed after its deployment receipt was written.");
        }

        string expectedRuntimePackRelativePath =
            $"{RefreshRootRelativePath}/packages/{receipt.EffectiveAnimationRuntimePackSha256}.rpack";
        if (!string.Equals(
                receipt.EffectiveAnimationRuntimePackRelativePath,
                expectedRuntimePackRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The deployment receipt does not identify the canonical animation runtime-pack path.");
        }

        string runtimePackPath = ResolveRegularProjectFile(
            root,
            expectedRuntimePackRelativePath,
            int.MaxValue,
            "animation runtime RPack");
        string actualRuntimePackSha256;
        using (FileStream stream = File.OpenRead(runtimePackPath))
        {
            actualRuntimePackSha256 =
                Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualRuntimePackSha256),
                Convert.FromHexString(receipt.EffectiveAnimationRuntimePackSha256!)))
        {
            throw new InvalidDataException(
                "The animation runtime RPack changed after its deployment receipt was written.");
        }

        ValidateDeployedModelAnimationAlias(root, receipt, manifestBytes);

        return WriteRequest(
            root,
            receipt.DeploymentId,
            targetHost,
            route,
            utcNow);
    }

    private static void ValidateDeployedModelAnimationAlias(
        string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt,
        byte[] manifestBytes)
    {
        using JsonDocument document = JsonDocument.Parse(
            manifestBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        JsonElement manifest = document.RootElement;
        if (!string.Equals(ReadRequiredString(manifest, "characterId"), receipt.CharacterId, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(manifest, "modelResourceName"), receipt.ModelResourceName, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(manifest, "animationLibraryName"), receipt.AnimationLibraryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The animation content manifest identities differ from the active deployment receipt.");
        }

        string aliasRelativePath =
            $"data/characters/{receipt.CharacterId}/{receipt.ModelResourceName}.ascr";
        if (!manifest.TryGetProperty("artifacts", out JsonElement artifacts) ||
            artifacts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The animation content manifest has no artifact inventory.");
        }

        string? expectedSourceSha256 = null;
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadRequiredString(artifact, "role"),
                    nameof(Dl1AnimationContentArtifactRole.AliasScript), StringComparison.Ordinal))
            {
                continue;
            }

            if (expectedSourceSha256 is not null ||
                !string.Equals(ReadRequiredString(artifact, "relativePath"), aliasRelativePath, StringComparison.Ordinal) ||
                !string.Equals(ReadRequiredString(artifact, "resourceIdentity"), receipt.ModelResourceName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The animation content manifest has a mismatched model ASCR artifact.");
            }

            expectedSourceSha256 = ReadRequiredString(artifact, "sha256");
        }

        if (expectedSourceSha256 is null ||
            !Regex.IsMatch(expectedSourceSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("The animation content manifest has no valid model ASCR fingerprint.");
        }

        const int maximumAliasScriptBytes = 8 * 1024 * 1024;
        string sourcePath = ResolveRegularProjectFile(
            projectRoot, aliasRelativePath, maximumAliasScriptBytes, "deployed model ASCR");
        byte[] sourceBytes = ReadBoundedFile(sourcePath, maximumAliasScriptBytes, "deployed model ASCR");
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ComputeSha256(sourceBytes)),
                Convert.FromHexString(expectedSourceSha256)))
        {
            throw new InvalidDataException(
                "The deployed model ASCR changed after its animation content manifest was written.");
        }

        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(sourceBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The deployed model ASCR is not valid UTF-8.", exception);
        }

        AnimationSourceDependency[] aliases = AnimationScriptDependencyReader.Read(source)
            .Where(static dependency => dependency.Kind == AnimationSourceDependencyKind.AnimationScriptAlias)
            .ToArray();
        string expectedAlias = receipt.AnimationLibraryName + ".scr";
        if (aliases.Length != 1 ||
            !string.Equals(aliases[0].LiteralName, expectedAlias, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The deployed model ASCR must redirect exactly once to '{expectedAlias}'.");
        }

        string meshRelativePath =
            $"assets_pc/characters/{receipt.CharacterId}/{receipt.ModelResourceName}.msh_obj";
        string meshPath = ResolveRegularProjectFile(
            projectRoot, meshRelativePath, int.MaxValue, "compiled model mesh");
        Dl1RuntimeMeshObjectValidator.ValidateAsync(
            meshPath, receipt.ModelResourceName, receipt.AnimationLibraryName)
            .GetAwaiter().GetResult();
    }
}
