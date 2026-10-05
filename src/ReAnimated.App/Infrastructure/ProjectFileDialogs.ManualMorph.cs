using Microsoft.Win32;

namespace ReAnimated.App.Infrastructure;

public sealed partial class WindowsProjectFileDialogService
{
    public string? ShowOpenManualMorphSculptDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true, Multiselect = false,
            Filter = "Face sculpt (*.obj;*.fbx)|*.obj;*.fbx|Wavefront OBJ (*.obj)|*.obj|Binary FBX (*.fbx)|*.fbx",
            Title = "Import the sculpted target face under its original expression name",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSaveManualMorphNeutralDialog(string suggestedName, string? initialPath)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true, CheckPathExists = true, DefaultExt = ".obj",
            FileName = MakeSafeFileName(suggestedName) + ".obj",
            Filter = "Wavefront OBJ (*.obj)|*.obj",
            Title = "Export the selected neutral face for sculpting",
            OverwritePrompt = true,
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }
}
