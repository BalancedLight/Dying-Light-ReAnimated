using System.IO;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public static class ProjectRetailRootResolver
{
    public static IReadOnlyList<string> ResolveAdditionalRpackRoots(
        DlraProject project,
        string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Dl1Settings.AdditionalRpackRoots.IsDefaultOrEmpty)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException(
                "Save the project before using project-relative additional RPack roots.");
        }

        string projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException(
                "The project path has no parent directory.");
        string requiredPrefix = projectDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string[] resolved = new string[
            project.Dl1Settings.AdditionalRpackRoots.Length];
        for (int index = 0; index < resolved.Length; index++)
        {
            string relativePath =
                project.Dl1Settings.AdditionalRpackRoots[index];
            string fullPath = Path.GetFullPath(
                Path.Combine(
                    projectDirectory,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(
                    requiredPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Additional RPack root '{relativePath}' escapes the project directory.");
            }

            resolved[index] = fullPath;
        }

        return resolved;
    }
}
