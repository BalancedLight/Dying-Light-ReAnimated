using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.Win32;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async void ImportAnimationEventFile(object? sender, EventArgs args)
    {
        if (IsBusy || HasUncommittedAnimationScript()) { Timeline.Events.Status = "Save or discard script edits first"; return; }
        OpenFileDialog picker = new() { Title = "Import animation script", Filter = "Animation scripts|*.scr;*.rpack|Script|*.scr|RPack|*.rpack", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        DlraProject expected = _project;
        try
        {
            var imported = ImmutableArray.CreateBuilder<ProjectAnimationLibrary>();
            if (Path.GetExtension(picker.FileName).Equals(".scr", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(picker.FileName).Length > ProjectAnimationLibrary.MaximumAuthoredScriptLength) throw new InvalidDataException("The script is too large.");
                string text = await File.ReadAllTextAsync(picker.FileName);
                var document = AnimationScriptTextCodec.Read(text);
                imported.Add(CreateImportedEventLibrary(Path.GetFileNameWithoutExtension(picker.FileName), document.Sequences, text, null, expected));
            }
            else
            {
                Rp6lArchive archive = await Rp6lArchive.OpenAsync(picker.FileName);
                await using var cache = new Rp6lChunkCache();
                var choices = archive.Resources.Where(r => r.ResourceType == Rp6lResourceTypes.AnimationScript).ToArray();
                Rp6lResourceDescriptor? chosen = PickAnimationEventResource(choices);
                foreach (Rp6lResourceDescriptor resource in chosen is null ? [] : new[] { chosen })
                {
                    if (resource.Items.Count != 2) throw new InvalidDataException($"Script '{resource.Name}' uses an unsupported section layout.");
                    byte[] records = await archive.ReadItemBytesAsync(resource.Items[0], cache, maximumBytes: 64 * 1024 * 1024);
                    byte[] index = await archive.ReadItemBytesAsync(resource.Items[1], cache, maximumBytes: 64 * 1024 * 1024);
                    if (records.Length > 64 * 1024 * 1024 || index.Length > 64 * 1024 * 1024) throw new InvalidDataException("The compiled script is too large.");
                    var sections = new AnimationScrSections(records, index);
                    ImmutableArray<AnimationSequenceUse> sequences = AnimationScrCodec.ReadSequenceUses(sections, name => name + ".anm2");
                    imported.Add(CreateImportedEventLibrary(resource.Name, sequences, null, new AnimationScriptBinaryBacking { RecordsAndNames = records, IndexAndNames = index }, expected with { AnimationLibraries = expected.AnimationLibraries.AddRange(imported) }));
                }
            }
            if (!ReferenceEquals(_project, expected)) { Timeline.Events.Status = "The project changed; import again"; return; }
            if (imported.Count == 0) { Timeline.Events.Status = "No animation scripts found"; return; }
            ImmutableArray<ProjectAnimationLibrary> libraries = imported.ToImmutable();
            CommitProject(expected with { AnimationLibraries = expected.AnimationLibraries.AddRange(libraries) });
            SelectedScriptLibrary = AnimationScriptLibraries.FirstOrDefault(l => l.Id == libraries[0].Id);
            Timeline.Events.Status = $"Imported {libraries.Sum(l => l.SequenceUses.Sum(s => s.Events.Length))} events";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Timeline.Events.Status = exception.Message;
        }
    }

    private static Rp6lResourceDescriptor? PickAnimationEventResource(Rp6lResourceDescriptor[] resources)
    {
        if (resources.Length == 0) return null;
        if (resources.Length == 1) return resources[0];
        var list = new System.Windows.Controls.ListBox { ItemsSource = resources, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new System.Windows.Thickness(8) };
        var window = new System.Windows.Window { Title = "Animation script", Width = 440, Height = 380, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, Owner = System.Windows.Application.Current?.MainWindow };
        var panel = new System.Windows.Controls.DockPanel();
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var open = new System.Windows.Controls.Button { Content = "Open", IsDefault = true, MinWidth = 80, Margin = new System.Windows.Thickness(4) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new System.Windows.Thickness(4) };
        open.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(open); buttons.Children.Add(cancel);
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(buttons); panel.Children.Add(list); window.Content = panel;
        return window.ShowDialog() == true ? list.SelectedItem as Rp6lResourceDescriptor : null;
    }

    private static ProjectAnimationLibrary CreateImportedEventLibrary(string name, ImmutableArray<AnimationSequenceUse> sequences, string? text, AnimationScriptBinaryBacking? binary, DlraProject project)
    {
        string stem = Dl1SourceModelWriter.SanitizeName(name, 100);
        string resource = stem;
        for (int suffix = 2; project.AnimationLibraries.Any(l => l.ResourceName.Equals(resource, StringComparison.OrdinalIgnoreCase)); suffix++) resource = stem + "_" + suffix;
        return new ProjectAnimationLibrary { ResourceName = resource, DisplayName = resource, AuthoredScriptText = text, SequenceUses = sequences, ImportedBinaryScript = binary };
    }

    private void ExportAnimationEventSource(object? sender, EventArgs args)
    {
        if (HasUncommittedAnimationScript()) { Timeline.Events.Status = "Save or discard script edits first"; return; }
        if (_eventLibraryId is not { } id || ResolveAnimationLibrary(id) is not { } library) return;
        try
        {
            string text = library.AuthoredScriptText ?? AnimationSequenceExport.Source(library.SequenceUses);
            SaveFileDialog picker = new() { Title = "Export animation script", Filter = "Animation script|*.scr", FileName = library.ResourceName + ".scr", OverwritePrompt = true };
            if (picker.ShowDialog() != true) return;
            WriteEventSourceAtomic(picker.FileName, text);
            Timeline.Events.Status = "Script exported";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { Timeline.Events.Status = exception.Message; }
    }

    internal static void WriteEventSourceAtomic(string path, string source)
    {
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, source, new UTF8Encoding(false)); File.Move(temporary, target, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
