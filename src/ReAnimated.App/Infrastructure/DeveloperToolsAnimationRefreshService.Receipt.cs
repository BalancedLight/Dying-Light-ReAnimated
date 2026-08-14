using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.Infrastructure;

public static partial class DeveloperToolsAnimationRefreshService
{
    public static DeveloperToolsAnimationRefreshRequestResult WriteRequest(
        string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt,
        DeveloperToolsAnimationRefreshHost targetHost,
        DeveloperToolsAnimationRefreshRoute route,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.IsStale ||
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

        return WriteRequest(
            root,
            receipt.DeploymentId,
            targetHost,
            route,
            utcNow);
    }
}
