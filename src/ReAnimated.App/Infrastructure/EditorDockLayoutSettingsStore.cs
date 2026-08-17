using System.IO;

namespace ReAnimated.App.Infrastructure;

public enum EditorDockWorkflow
{
    Models,
    Animations,
    Playback,
    RetargetEdit,
    Export,
}

/// <summary>
/// Stores editor chrome locally. Dock positions are intentionally excluded
/// from portable project documents because they describe one workstation,
/// monitor arrangement, and authoring preference rather than game content.
/// </summary>
public sealed class EditorDockLayoutSettingsStore
{
    public const int LayoutSchemaVersion = 1;
    private const int MaximumLayoutBytes = 1024 * 1024;
    private readonly string _layoutDirectory;

    public EditorDockLayoutSettingsStore(string layoutDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutDirectory);
        _layoutDirectory = Path.GetFullPath(layoutDirectory);
    }

    public bool TryLoad(
        EditorDockWorkflow workflow,
        out string? serializedLayout)
    {
        serializedLayout = null;
        string path = GetLayoutPath(workflow);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumLayoutBytes)
            {
                return false;
            }

            string candidate = File.ReadAllText(path);
            if (candidate.Contains(
                    "<!DOCTYPE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            serializedLayout = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }

    public void Save(
        EditorDockWorkflow workflow,
        string serializedLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedLayout);
        if (serializedLayout.Length > MaximumLayoutBytes)
        {
            throw new InvalidOperationException(
                "The editor layout is too large to persist safely.");
        }

        Directory.CreateDirectory(_layoutDirectory);
        AtomicFileWriter.WriteAllText(
            GetLayoutPath(workflow),
            serializedLayout);
    }

    public static EditorDockLayoutSettingsStore CreateDefault() =>
        new(Path.Combine(
            AppPaths.CreateDefault().RootDirectory,
            "Settings",
            "Layouts"));

    internal string GetLayoutPath(EditorDockWorkflow workflow) =>
        Path.Combine(
            _layoutDirectory,
            $"{ToFileStem(workflow)}.v{LayoutSchemaVersion}.xml");

    internal static string ToFileStem(EditorDockWorkflow workflow) =>
        workflow switch
        {
            EditorDockWorkflow.Models => "models",
            EditorDockWorkflow.Animations => "animations",
            EditorDockWorkflow.Playback => "playback",
            EditorDockWorkflow.RetargetEdit => "retarget-edit",
            EditorDockWorkflow.Export => "export",
            _ => throw new ArgumentOutOfRangeException(
                nameof(workflow),
                workflow,
                "Unknown editor dock workflow."),
        };
}
