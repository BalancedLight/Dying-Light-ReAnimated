using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Model authoring identity recorded at deployment; never engine-load evidence.</summary>
public sealed record Dl1DeploymentAuthoringIdentity
{
    public int Version { get; init; } = 1;
    public Guid ModelId { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public string RigSignature { get; init; } = string.Empty;
    public string MorphSignature { get; init; } = string.Empty;
    public string ModelInputFingerprint { get; init; } = string.Empty;
    public string? StudioInputFingerprint { get; init; }
    public RigProfileReference? Profile { get; init; }
    public string ToolFingerprint { get; init; } = string.Empty;

    public void Validate()
    {
        if (Version != 1 || ModelId == Guid.Empty) throw new InvalidDataException("Unsupported or incomplete deployment authoring identity.");
        foreach (string hash in new[] { SourceSha256, RigSignature, MorphSignature, ModelInputFingerprint, ToolFingerprint })
            ValidateHash(hash);
        if (StudioInputFingerprint is not null) ValidateHash(StudioInputFingerprint);
        Profile?.Validate();
    }

    private static void ValidateHash(string value)
    {
        if(value is null || value.Length!=64 || value.Any(static c=>!char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Deployment identity requires a complete SHA-256 value.");
    }

    public static Dl1DeploymentAuthoringIdentity Capture(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var document=model.Package.Document;
        return new()
        {
            ModelId=document.ModelId,SourceSha256=document.Source.ContentSha256,RigSignature=document.RigSignature,MorphSignature=document.MorphSignature,
            // This is the input model, not the requested output resource naming or an external animation variant.
            ModelInputFingerprint=Dl1OfficialModelCompiler.CalculateInputFingerprint(model,"authoring_snapshot","default",null),
            StudioInputFingerprint=document.RiggingSession?.ComputeInputFingerprint(),Profile=document.RiggingSession?.Recipe.Profile,
            ToolFingerprint=Dl1OfficialModelCompiler.CurrentToolFingerprint,
        };
    }
}

public sealed record Dl1DeploymentInspection(
    string DeploymentId, RigValidationStatus ModelAuthoringStatus, RigValidationStatus InstalledFilesStatus,
    ImmutableArray<string> ChangedArtifactPaths, ImmutableArray<string> LocalResourceWarnings,
    ImmutableArray<string> Notes)
{
    public Dl1ProjectArchiveScan? ArchiveScan { get; init; }
    // A filesystem receipt cannot satisfy either native facet.
    public RigValidationStatus LoadedResourceStatus { get; } = RigValidationStatus.Unverified;
    public RigValidationStatus GameplayStatus { get; } = RigValidationStatus.Unverified;
}

public static partial class Dl1DeveloperToolsProjectDeployer
{
    public static async Task<Dl1DeploymentInspection> InspectDeploymentAsync(
        Dl1DeveloperToolsDeploymentReceipt receipt,string projectRoot,FbxModelAuthoringImportResult currentModel,
        Dl1ProjectArchiveScanLimits? archiveLimits=null,CancellationToken cancellationToken=default)
    {
        var inspection=InspectDeployment(receipt,projectRoot,currentModel,cancellationToken);
        var archives=await Dl1ProjectResourceCopies.InspectAsync(projectRoot,receipt,archiveLimits,cancellationToken).ConfigureAwait(false);
        var warnings=inspection.LocalResourceWarnings.AddRange(archives.Copies.Where(static copy=>!copy.RecordedByReceipt)
            .Select(static copy=>"Additional RPack resource copy: "+copy.Summary)).AddRange(archives.Diagnostics);
        return inspection with {ArchiveScan=archives,LocalResourceWarnings=warnings,
            Notes=inspection.Notes.Where(static note=>!note.StartsWith("Duplicate checks cover loose",StringComparison.Ordinal)).ToImmutableArray()
                .Add(archives.Scope).Add(archives.HashDomain)
                .Add("Archive name/type inventory does not establish mounts, precedence, cached instances or active Player binding.")};
    }

    public static Dl1DeploymentInspection InspectDeployment(
        Dl1DeveloperToolsDeploymentReceipt receipt, string projectRoot,
        FbxModelAuthoringImportResult currentModel, CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(receipt);ArgumentNullException.ThrowIfNull(currentModel);
        cancellationToken.ThrowIfCancellationRequested();
        string root=Path.GetFullPath(projectRoot);
        ValidateReceipt(receipt,Path.Combine(root,".dl-reanimated","deployments",receipt.DeploymentId+".json"),root);
        var notes=ImmutableArray.CreateBuilder<string>();
        var authoring=RigValidationStatus.Unverified;
        if(receipt.AuthoringIdentity is not { } recorded)
            notes.Add("This older receipt has no model authoring identity. Matching installed files cannot establish a match to the current recipe.");
        else
        {
            var current=Dl1DeploymentAuthoringIdentity.Capture(currentModel);
            authoring=recorded==current?RigValidationStatus.Passed:RigValidationStatus.Failed;
            notes.Add(authoring==RigValidationStatus.Passed?"The model authoring snapshot matches this deployment.":
                "The source, model, rig, morphs, recipe/profile or exporter contract changed after this deployment. Rebuild and deploy the reviewed revision.");
        }
        var freshness=InspectDeploymentReceiptFreshness(receipt,root,cancellationToken);
        var files=receipt.Artifacts.IsEmpty?RigValidationStatus.Unverified:freshness.IsStale?RigValidationStatus.Failed:RigValidationStatus.Passed;
        if(receipt.RolledBackUtc is not null)
        { files=RigValidationStatus.Failed;notes.Add("This deployment was rolled back; its installation is not active."); }
        var canonical=receipt.Artifacts.Select(static a=>NormalizeRelativePath(a.RelativePath)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings=FindDuplicateResources(root,canonical,cancellationToken)
            .AddRange(FindLegacyOutputPaths(root).Select(static path=>"Legacy or unmounted output needs review: "+path));
        notes.Add("Duplicate checks cover loose project files by filename. Archive contents, mount precedence, cached variants and the active Player provider have not been verified.");
        notes.Add(receipt.ReferenceExistingAnimationLibrary?
            "The stock bank is referenced, not owned by this receipt. Its current resolution and runtime animation binding remain unverified.":
            "Installed animation artifacts were checked as files. External animation-variant authoring and runtime binding require their own evidence.");
        notes.Add("After installation or resource reload, freshly spawn or reinitialize the actor before contact/IK calibration and gameplay testing. A matching file check does not prove that this happened.");
        return new(receipt.DeploymentId,authoring,files,freshness.StaleArtifactPaths,warnings,notes.ToImmutable());
    }
}
