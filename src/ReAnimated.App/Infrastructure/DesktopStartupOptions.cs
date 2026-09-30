using System.IO;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Arguments for an interactive editor launch, after CLI and diagnostic modes
/// have been dispatched. Parsing does not read a project or change rendering.
/// </summary>
internal sealed record DesktopStartupOptions(
    string? ProjectPath,
    bool SoftwareUi)
{
    public const string Usage =
        "Usage: DLReAnimated [--project <file.dlraproj> | <file.dlraproj>] [--software-ui | --hardware-ui]";

    public static DesktopStartupOptions Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? projectPath = null;
        // WPF's software compositor is the reliable default for mixed WPF and
        // native D3D viewport hosts. The viewport renderer remains on D3D11.
        bool? requestedSoftwareUi = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "--software-ui" or "--hardware-ui")
            {
                if (requestedSoftwareUi is not null)
                {
                    throw new ArgumentException(
                        "Specify only one UI renderer override.",
                        nameof(arguments));
                }

                requestedSoftwareUi = argument == "--software-ui";
                continue;
            }

            if (string.Equals(
                    argument,
                    "--project",
                    StringComparison.Ordinal))
            {
                if (++index >= arguments.Count ||
                    string.IsNullOrWhiteSpace(arguments[index]) ||
                    arguments[index].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "--project requires a .dlraproj file path.",
                        nameof(arguments));
                }

                argument = arguments[index];
            }
            else if (string.IsNullOrWhiteSpace(argument) ||
                     argument.StartsWith('-'))
            {
                throw new ArgumentException(
                    $"Unknown desktop argument '{argument}'.",
                    nameof(arguments));
            }

            if (projectPath is not null)
            {
                throw new ArgumentException(
                    "Specify only one project to open.",
                    nameof(arguments));
            }

            if (!string.Equals(
                    Path.GetExtension(argument),
                    ".dlraproj",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The project path must name a .dlraproj file.",
                    nameof(arguments));
            }

            // File existence and document validation belong to the editor's
            // normal open transaction so failures use its diagnostics/recovery.
            projectPath = Path.GetFullPath(argument);
        }

        return new DesktopStartupOptions(projectPath, requestedSoftwareUi ?? true);
    }
}
