using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.Models;

if (args.Length != 2) throw new ArgumentException("ScaleStudyTool <private-request.json> <empty-output-directory>");
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
};
var request = JsonSerializer.Deserialize<ScaleStudyRequest>(await File.ReadAllTextAsync(args[0]), json)
    ?? throw new InvalidDataException("Missing scale-study request.");
if (request.Controls is null || request.Controls.Length is < 1 or > 32)
    throw new InvalidDataException("A study requires between one and 32 explicit control presets.");
string sourcePath = Path.GetFullPath(request.SourcePath);
string output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Scale-study output must be empty; existing files were retained.");
string resultSourcePath = Path.Combine(output, "humanai.pre");
if (string.Equals(sourcePath, resultSourcePath, StringComparison.OrdinalIgnoreCase))
    throw new IOException("Study output must not replace the control source.");
if (new FileInfo(sourcePath).Length > 8 * 1024 * 1024)
    throw new InvalidDataException("Control preset source exceeds the study limit.");
byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);
// Fail on unsupported encoding instead of silently replacing characters in a control.
var utf8 = new UTF8Encoding(false, true);
string source = utf8.GetString(sourceBytes);
if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];
var reports = new List<Dl1ScaleExperimentSource>();
foreach (var control in request.Controls)
{
    var report = Dl1HumanAiScaleExperiments.Build(source, request.DefinitionName,
        control.PresetName, control.Trials);
    reports.Add(report); source = report.Source;
}
Directory.CreateDirectory(output);
await File.WriteAllTextAsync(resultSourcePath, source, utf8);
string sourceDigest = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
string resultDigest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(resultSourcePath)));
var manifest = new
{
    SchemaVersion = 1,
    Experiment = "S1-native-size-source-trials",
    SourcePath = sourcePath,
    SourceBytesSha256 = sourceDigest,
    ResultBytesSha256 = resultDigest,
    Definition = request.DefinitionName,
    Controls = reports.Select(r => new
    {
        r.ControlPresetName,
        r.ControlFields,
        r.Trials,
        r.SourceTextSha256,
        r.ResultTextSha256,
    }),
    ChangedInputs = new[] { "trial preset name", Dl1HumanAiScaleExperiments.MinimumField, Dl1HumanAiScaleExperiments.MaximumField },
    SourceControlRetained = true,
    MeshOrAnimationFilesWritten = false,
    DependencyResolution = "unverified; resolve the selected preset's mesh, CHR, skin, banks and scripts in the target Player provider before acceptance",
    NativeSupportedRange = "unverified; the trial inputs do not establish a universal native range",
    PostSpawnScaleChange = "unverified; these presets configure spawn-time trials",
    NativeAcceptance = "unverified; no runtime actor, binding, contact or collision observations were collected",
    Deployed = false,
};
await File.WriteAllTextAsync(Path.Combine(output, "scale-study.json"), JsonSerializer.Serialize(manifest, json), utf8);
Console.WriteLine($"Prepared {reports.Sum(static r => r.Trials.Length)} fixed-size source trials from {reports.Count} controls. Native behavior remains unverified.");

internal sealed record ScaleStudyRequest(string SourcePath, string DefinitionName, ScaleStudyControl[] Controls);
internal sealed record ScaleStudyControl(string PresetName, Dl1ScaleTrial[] Trials);
