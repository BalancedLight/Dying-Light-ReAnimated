using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum Dl1CustomModelExportStatus { CompilerValidated, BestEffort, Failed }

/// <summary>Standalone export outcome. Only Package carries compiler-validated evidence.</summary>
public sealed record Dl1CustomModelExportOutcome(
    Dl1CustomModelExportStatus Status,
    Dl1CustomModelPackageResult? Package,
    string ArtifactDirectory,
    string DiagnosticsPath,
    ImmutableArray<string> Warnings);

internal static class Dl1CustomModelExportRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal const string ArtifactKey = "Dl1ExportArtifactDirectory";
    internal const string DiagnosticsKey = "Dl1ExportDiagnosticsPath";
    internal const string JobKey = "Dl1CompilerJobDirectory";
    internal const string CandidatesKey = "Dl1CompilerCandidatePaths";
    internal const string StatusKey = "Dl1ExportStatus";

    internal static bool IsRecoverable(Exception error) => error is
        InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or FormatException or NotSupportedException or TimeoutException or Win32Exception;

    internal static (string Parent, string Staging) CreateRecoveryStaging(string preferredParent, string resource, Exception error)
    {
        string staging = Path.Combine(preferredParent, $".{resource}.recovery.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            return (preferredParent, staging);
        }
        catch (Exception pathError) when (pathError is IOException or UnauthorizedAccessException)
        {
            error.Data["Dl1RequestedRecoveryPathError"] = pathError.Message;
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLReAnimated", "ExportRecovery");
            staging = Path.Combine(fallback, $".{resource}.recovery.{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            return (fallback, staging);
        }
    }

    internal static string Preserve(Exception error, string parent, string resource, string? staging, CustomModelPackage editableInput)
    {
        Directory.CreateDirectory(parent);
        string temporary = staging ?? Path.Combine(parent, $".{resource}.recovery.{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var copyWarnings = new List<string>();
        string? editableInputSaveError = null;
        try { CustomModelPackageSerializer.SaveAtomic(editableInput, Path.Combine(temporary, "editable-input.dlrmodel")); }
        catch (Exception saveError) when (IsRecoverable(saveError))
        {
            editableInputSaveError = saveError.Message;
        }
        if (error.Data[CandidatesKey] is string[] candidates)
        {
            string candidateDirectory = Path.Combine(temporary, "compiled-candidates");
            int candidateIndex = 0;
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(candidate) || new FileInfo(candidate).Length == 0) continue;
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                    string container = Path.Combine(candidateDirectory, $"{candidateIndex++:000}");
                    Directory.CreateDirectory(container);
                    string destination = Path.Combine(container, Path.GetFileName(candidate));
                    File.Copy(candidate, destination);
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    copyWarnings.Add($"Could not copy compiler candidate '{candidate}': {copyError.Message}");
                }
            }
        }
        bool hasCandidate = Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)
            .Any(path => (path.EndsWith(".rpack", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".msh_obj", StringComparison.OrdinalIgnoreCase)) && new FileInfo(path).Length > 0);
        Dl1CustomModelExportStatus status = hasCandidate ? Dl1CustomModelExportStatus.BestEffort : Dl1CustomModelExportStatus.Failed;
        string recovery = Path.Combine(parent, $"{resource}_dl1_{(hasCandidate ? "best_effort" : "failed")}_{Guid.NewGuid():N}");
        Directory.Move(temporary, recovery);
        string diagnostics = Path.Combine(recovery, "export-diagnostics.json");
        File.WriteAllText(diagnostics, JsonSerializer.Serialize(new
        {
            format = "dl-reanimated-custom-model-export-diagnostics", schemaVersion = 1,
            status = status.ToString(), compilerValidated = false, runtimeVerified = false,
            warning = hasCandidate
                ? "Best-effort compiler output: validation did not complete. Isolated for review; not approved for validated deployment."
                : "Export failed to produce a compiled model. Editable source and diagnostics are retained.",
            exceptionType = error.GetType().FullName, error = error.Message, stackTrace = error.StackTrace,
            compilerJobDirectory = error.Data[JobKey] as string, copyWarnings,
            requestedRecoveryPathError = error.Data["Dl1RequestedRecoveryPathError"] as string,
            editableInputSaveError,
        }, JsonOptions), new UTF8Encoding(false));
        error.Data[ArtifactKey] = recovery;
        error.Data[DiagnosticsKey] = diagnostics;
        error.Data[StatusKey] = status;
        return recovery;
    }
}
