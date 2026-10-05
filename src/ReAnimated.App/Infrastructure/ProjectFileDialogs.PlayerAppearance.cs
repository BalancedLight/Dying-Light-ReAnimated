using Microsoft.Win32;

namespace ReAnimated.App.Infrastructure;

public sealed partial class WindowsProjectFileDialogService
{
    public string? ShowOpenPlayerAppearanceScriptDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true, Multiselect = false,
            Filter = "Player appearance script (*.scr)|*.scr",
            Title = "Open the current Player appearance script",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSavePlayerAppearanceScriptDialog(string suggestedName, string? initialPath)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true, CheckPathExists = true, DefaultExt = ".scr",
            FileName = MakeSafeFileName(suggestedName) + ".scr",
            Filter = "Player appearance script (*.scr)|*.scr",
            Title = "Save a new Player appearance script",
            OverwritePrompt = false,
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }
}
