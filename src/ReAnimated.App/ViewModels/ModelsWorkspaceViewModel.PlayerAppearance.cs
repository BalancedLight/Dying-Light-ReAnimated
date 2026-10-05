using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private Dl1PlayerAppearanceDocument? _playerAppearanceSource;
    private string? _playerAppearanceSourcePath;
    private string? _playerAppearanceSourceHash;
    private bool _playerAppearanceSourceBom;
    private Dl1PlayerAppearance? _selectedPlayerAppearance;
    private string _playerAppearanceSourceStatus =
        "Open the current Player appearance script to inspect available appearances.";
    private RelayCommand? _openPlayerAppearanceCommand;
    private RelayCommand? _exportPlayerAppearanceCommand;

    public ObservableCollection<Dl1PlayerAppearance> PlayerAppearanceChoices { get; } = [];
    public string PlayerAppearanceSourceStatus
    {
        get => _playerAppearanceSourceStatus;
        private set => SetProperty(ref _playerAppearanceSourceStatus, value);
    }
    public Dl1PlayerAppearance? SelectedPlayerAppearance
    {
        get => _selectedPlayerAppearance;
        set
        {
            if (SetProperty(ref _selectedPlayerAppearance, value))
            {
                _exportPlayerAppearanceCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PlayerAppearanceBindingSummary));
            }
        }
    }

    public RelayCommand OpenPlayerAppearanceCommand => _openPlayerAppearanceCommand ??=
        new(OpenPlayerAppearance, () => HasModel && !IsBusy);
    public RelayCommand ExportPlayerAppearanceCommand => _exportPlayerAppearanceCommand ??=
        new(ExportPlayerAppearance, () => HasModel && !IsBusy &&
            _playerAppearanceSource is not null && SelectedPlayerAppearance is not null);
    public string PlayerAppearanceBindingSummary => SelectedPlayerAppearance is { } selected
        ? $"{selected.CharacterId} / {selected.AppearanceId}: use {PerspectiveResourceBase}_fpp.msh and {PerspectiveResourceBase}_tpp.msh, skin {SurfaceName}. {DescribeAvailability(selected.Availability)} This assigns the three source bindings; it does not select or equip an outfit. Build and deploy both packages before using this appearance."
        : "Open the current Player appearance script and choose one appearance. The other outfits and selection flags stay intact.";
    private string PerspectiveResourceBase => Dl1SourceModelWriter.SanitizeName(ResourceName, 51);

    private static string DescribeAvailability(
        Dl1PlayerAppearanceAvailability availability)
    {
        List<string> conditions = [];
        if (availability.AvailableOnStart)
            conditions.Add("AvailableOnStart");
        if (availability.AvailableOnPrologue)
            conditions.Add("AvailableOnPrologue");
        if (availability.IsDefault)
            conditions.Add("Default");
        string conditionText = conditions.Count == 0
            ? "No recognized availability flags found."
            : "Source availability: " + string.Join(", ", conditions) + ".";
        if (availability.Unlocks.Length == 0)
            return conditionText;
        string unlockText = string.Join(
            "; ",
            availability.Unlocks.Select(FormatUnlockCondition));
        return $"{conditionText} Matching unlock conditions: {unlockText}.";
    }

    private static string FormatUnlockCondition(
        Dl1PlayerAppearanceUnlock unlock)
    {
        if (string.Equals(
                unlock.Name,
                "PlayerLevel",
                StringComparison.OrdinalIgnoreCase) &&
            unlock.Arguments.Length >= 2 &&
            string.Equals(
                unlock.Arguments[0].Trim('"'),
                "Status",
                StringComparison.OrdinalIgnoreCase))
        {
            string remaining = string.Join(", ", unlock.Arguments.Skip(2));
            return remaining.Length == 0
                ? $"{unlock.Name}(Status level {unlock.Arguments[1]})"
                : $"{unlock.Name}(Status level {unlock.Arguments[1]}, {remaining})";
        }
        return $"{unlock.Name}({string.Join(", ", unlock.Arguments)})";
    }

    private void RefreshPlayerAppearanceAvailability()
    {
        if (!HasModel)
        {
            _playerAppearanceSource = null;
            _playerAppearanceSourcePath = null;
            _playerAppearanceSourceHash = null;
            PlayerAppearanceChoices.Clear();
            SelectedPlayerAppearance = null;
            PlayerAppearanceSourceStatus =
                "Open the current Player appearance script to inspect available appearances.";
        }
        _openPlayerAppearanceCommand?.NotifyCanExecuteChanged();
        _exportPlayerAppearanceCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlayerAppearanceBindingSummary));
    }

    private void OpenPlayerAppearance()
    {
        if (!HasModel || IsBusy) return;
        if (!TryShowModelPicker("Choose the current Player appearance script",
            () => _fileDialogs.ShowOpenPlayerAppearanceScriptDialog(_playerAppearanceSourcePath), out string? path))
        {
            PlayerAppearanceSourceStatus =
                "Player appearance script picker could not be opened; current choices were retained.";
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            PlayerAppearanceSourceStatus =
                "Player appearance script selection canceled; current choices were retained.";
            return;
        }
        try
        {
            byte[] bytes = ReadBoundedPlayerAppearance(path);
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            string text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            Dl1PlayerAppearanceDocument source = Dl1PlayerAppearanceCodec.Read(text);
            _playerAppearanceSource = source;
            _playerAppearanceSourcePath = Path.GetFullPath(path);
            _playerAppearanceSourceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _playerAppearanceSourceBom = bom;
            SelectedPlayerAppearance = null;
            PlayerAppearanceChoices.Clear();
            foreach (Dl1PlayerAppearance appearance in source.Appearances) PlayerAppearanceChoices.Add(appearance);
            RefreshPlayerAppearanceAvailability();
            PlayerAppearanceSourceStatus =
                $"Loaded {PlayerAppearanceChoices.Count:N0} appearance(s) from {Path.GetFileName(path)}.";
            BuildStatus = $"Loaded {PlayerAppearanceChoices.Count:N0} Player appearance(s). Choose the outfit to assign.";
            _setStatus(BuildStatus);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            PlayerAppearanceSourceStatus =
                "Player appearance script could not be opened: " + error.Message;
            BuildStatus = "Player appearances could not be opened: " + error.Message;
            _setStatus(BuildStatus);
        }
    }

    private void ExportPlayerAppearance()
    {
        if (!ExportPlayerAppearanceCommand.CanExecute(null) || SelectedPlayerAppearance is not { } selected) return;
        if (!TryShowModelPicker("Choose a new file for the assigned Player appearance script",
            () => _fileDialogs.ShowSavePlayerAppearanceScriptDialog(PerspectiveResourceBase + "-playerappearances", _playerAppearanceSourcePath), out string? path) ||
            string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string destination = Path.GetFullPath(path);
            if (string.Equals(destination, _playerAppearanceSourcePath, StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
                throw new IOException("Choose a new output file so the current appearance script stays intact.");
            string currentHash = Convert.ToHexStringLower(SHA256.HashData(ReadBoundedPlayerAppearance(_playerAppearanceSourcePath!)));
            if (currentHash != _playerAppearanceSourceHash)
                throw new InvalidDataException("The source appearance script changed. Open its current version before exporting.");
            string text = _playerAppearanceSource!.Bind(selected.CharacterId, selected.AppearanceId,
                PerspectiveResourceBase + "_fpp.msh", PerspectiveResourceBase + "_tpp.msh", SurfaceName).Syntax.Write();
            byte[] content = new UTF8Encoding(false).GetBytes(text);
            byte[] output = _playerAppearanceSourceBom ? Encoding.UTF8.Preamble.ToArray().Concat(content).ToArray() : content;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, output);
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            BuildStatus = $"Player appearance script saved to {destination}. Deploy it with both model packages, then verify the selected outfit in Player.";
            _setStatus(BuildStatus);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            BuildStatus = "Player appearance export could not finish: " + error.Message;
            _setStatus(BuildStatus);
        }
    }

    private static byte[] ReadBoundedPlayerAppearance(string path)
    {
        if (new FileInfo(path).Length > NativeCharacterScriptCodec.MaximumCharacters * 4L + 3)
            throw new InvalidDataException("The Player appearance script exceeds the supported size limit.");
        return File.ReadAllBytes(path);
    }
}
