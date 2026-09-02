using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Cli;

/// <summary>
/// Converts an arbitrarily rigged FBX into a DL1-capable model by conforming
/// its skeleton to a target template extracted from a decoded retail mesh.
/// </summary>
/// <remarks>
/// The template resource must be supplied by the caller from their own game
/// data. Nothing retail is bundled, and the command never writes retail bytes
/// into its output beyond the skeleton shape the conversion targets.
/// </remarks>
public static class ConformModelCommand
{
    public const string Usage =
        """
        Usage: DLReAnimated conform-model --fbx <model.fbx> --template-mesh <resource>
                                          [--out <model.dlrmodel>]
                                          [--strength <0..1>] [--scale <value>]
                                          [--scale-region leg|torso|arm]
                                          [--drop-extras] [--ignore-morphs]

          --fbx            The rigged source model to convert.
          --template-mesh  A decoded retail type-272 mesh resource whose skeleton
                           is the conversion target, for example an extracted
                           player_1_tpp payload.
          --out            Optional .dlrmodel destination for the conformed model.
          --strength       Conformance strength. 1 (default) imposes DL1 rest
                           proportions so stock clips play as authored; 0 keeps
                           the source model's own segment lengths.
          --scale          Overrides the solved uniform scale.
          --scale-region   Restricts the scale solve to one anatomical region.
                           'leg' is useful for locomotion, which plants feet at
                           template proportions.
          --drop-extras    Removes source bones with no DL1 counterpart and folds
                           their weights into the nearest surviving ancestor.
          --ignore-morphs  Skips FBX blend shapes. Required for exports whose
                           morph channels are unusable, such as Character
                           Creator's duplicate empty 'V_None' channels.
        """;

    public static async Task<int> RunAsync(
        string[] args,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        Options options = Options.Parse(args);
        Dl1RigTemplate template = LoadTemplate(options.TemplateMeshPath);

        FbxModelAuthoringImportResult model =
            await FbxModelAuthoringImporter.ImportFileAsync(
                options.FbxPath,
                new FbxModelAuthoringImportOptions
                {
                    IgnoreMorphChannels = options.IgnoreMorphChannels,
                    DecodeAnimationClips = false,
                },
                cancellationToken).ConfigureAwait(false);
        RigDefinition source = model.Rig ?? throw new InvalidDataException(
            "The source FBX has no skinned rig to conform.");

        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(
            template,
            source,
            new RigCorrespondenceOptions { DropExtraBones = options.DropExtras },
            cancellationToken);
        RigLandmarkSolution landmark = RigLandmarkSolver.Solve(
            template,
            source,
            correspondence,
            new RigLandmarkOptions
            {
                ScaleOverride = options.ScaleOverride,
                RestrictScaleToRegion = options.ScaleRegion,
            },
            cancellationToken);
        RigConformanceResult fit = RigConformanceSolver.Solve(
            template,
            source,
            correspondence,
            landmark,
            new RigConformanceOptions { ConformanceStrength = options.Strength },
            cancellationToken);

        // Record the decisions so a package written here can be reopened and
        // adjusted in the wizard instead of being a dead end.
        var settings = new CustomModelRigConformance
        {
            TemplateId = template.TemplateId,
            TemplateProfileName = template.ProfileName,
            TemplateSourceResourceName = template.SourceResourceName,
            TemplateFingerprint = template.SourceFingerprint,
            SourceFbxSha256 = model.Package.Document.Source.ContentSha256,
            DropExtraBones = options.DropExtras,
            ScaleMode = options.ScaleOverride is not null
                ? CustomModelConformanceScaleMode.Manual
                : options.ScaleRegion switch
                {
                    RigScaleRegion.Leg => CustomModelConformanceScaleMode.Leg,
                    RigScaleRegion.Torso => CustomModelConformanceScaleMode.Torso,
                    RigScaleRegion.Arm => CustomModelConformanceScaleMode.Arm,
                    _ => CustomModelConformanceScaleMode.Automatic,
                },
            ManualScale = options.ScaleOverride,
            ConformanceStrength = options.Strength,
        };
        FbxModelAuthoringImportResult conformed =
            Dl1RigConformanceApplier.Apply(model, fit, settings, cancellationToken);
        Dl1PreparedAuthoredRig prepared =
            Dl1CustomModelRigPreparer.Prepare(conformed, cancellationToken);

        string? written = null;
        if (options.OutputPath is { } destination)
        {
            CustomModelPackageSerializer.SaveAtomic(conformed.Package, destination);
            written = Path.GetFullPath(destination);
        }

        var report = new
        {
            format = "dl-reanimated-rig-conformance-v1",
            source = new
            {
                fbx = Path.GetFullPath(options.FbxPath),
                bones = source.BoneCount,
                surfaces = model.Surfaces.Length,
            },
            template = new
            {
                template.TemplateId,
                template.ProfileName,
                template.SourceResourceName,
                entities = template.EntityCount,
                deformBones = template.DeformCount,
                referenceHeightMeters = Round(template.ReferenceHeight),
            },
            correspondence = new
            {
                mapped = correspondence.MappedCount,
                synthesized = correspondence.SynthesizedCount,
                extra = correspondence.ExtraCount,
                dropped = correspondence.DroppedCount,
                unmatchedRoles = correspondence.UnmatchedRoles,
                ambiguities = correspondence.Ambiguities.Select(static row => new
                {
                    row.Role,
                    row.TemplateName,
                    candidates = row.CandidateSourceNames,
                    row.ChosenSourceName,
                    row.Reason,
                }),
            },
            scale = new
            {
                uniform = Round(landmark.UniformScale),
                landmark.IsScaleOverridden,
                proportionResidual = Round(landmark.ProportionResidual),
                worstSampleDeviation = Round(landmark.WorstSampleDeviation),
                landmark.Evidence,
                regions = landmark.RegionFits.Select(static row => new
                {
                    region = row.Region.ToString(),
                    row.SampleCount,
                    ratio = Round(row.Ratio),
                    offBy = Round(row.RelativeErrorAtSolvedScale),
                }),
            },
            fit = new
            {
                conformanceStrength = Round(fit.ConformanceStrength),
                emittedBones = fit.Bones.Length,
                maximumJointDisplacementCm = Round(fit.MaximumJointDisplacement * 100.0),
                warnings = fit.Warnings
                    .OrderBy(static row => row.SegmentRatio)
                    .Select(static row => new
                    {
                        row.BoneName,
                        segmentRatio = Round(row.SegmentRatio),
                        movedCm = Round(row.OffsetFromSourceJoint * 100.0),
                        row.Message,
                    }),
            },
            emitted = new
            {
                authoredNodes = prepared.Contract.Nodes.Length,
                prepared.Contract.ContractId,
                surfaces = prepared.Surfaces.Length,
                diagnostics = prepared.Diagnostics.Select(static row => new
                {
                    row.Code,
                    row.BoneName,
                    row.Message,
                }),
            },
            output = written,
        };

        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
        return 0;
    }

    private static Dl1RigTemplate LoadTemplate(string path)
    {
        byte[] payload = File.ReadAllBytes(path);
        CompactMeshDocument hierarchy;
        try
        {
            hierarchy = CompactMeshDecoder.Decode(payload);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"'{path}' is not a decodable DL1 compact mesh resource: {exception.Message}",
                exception);
        }

        return Dl1RigTemplateFactory.TryCreate(
                Dl1RigTemplateFactory.PlayerProfileName,
                Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name
                    ? name
                    : Dl1RigTemplateFactory.PlayerSourceResourceName,
                ComputeFingerprint(payload),
                hierarchy)
            ?? throw new InvalidDataException(
                $"'{path}' decoded, but carries no usable animation skeleton to use as a template.");
    }

    private static string ComputeFingerprint(byte[] payload) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
            .ToLowerInvariant();

    private static double Round(double value) => Math.Round(value, 6);

    private sealed record Options
    {
        public required string FbxPath { get; init; }

        public required string TemplateMeshPath { get; init; }

        public string? OutputPath { get; init; }

        public double Strength { get; init; } = 1.0;

        public double? ScaleOverride { get; init; }

        public RigScaleRegion? ScaleRegion { get; init; }

        public bool DropExtras { get; init; }

        public bool IgnoreMorphChannels { get; init; }

        public static Options Parse(string[] args)
        {
            string? fbx = null;
            string? templateMesh = null;
            string? output = null;
            double strength = 1.0;
            double? scale = null;
            RigScaleRegion? region = null;
            bool dropExtras = false;
            bool ignoreMorphs = false;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "--fbx":
                        fbx = RequireValue(args, ref index, argument);
                        break;
                    case "--template-mesh":
                        templateMesh = RequireValue(args, ref index, argument);
                        break;
                    case "--out":
                        output = RequireValue(args, ref index, argument);
                        break;
                    case "--strength":
                        strength = ParseDouble(RequireValue(args, ref index, argument), argument);
                        break;
                    case "--scale":
                        scale = ParseDouble(RequireValue(args, ref index, argument), argument);
                        break;
                    case "--scale-region":
                        region = ParseRegion(RequireValue(args, ref index, argument));
                        break;
                    case "--drop-extras":
                        dropExtras = true;
                        break;
                    case "--ignore-morphs":
                        ignoreMorphs = true;
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unrecognized option '{argument}'.{Environment.NewLine}{Usage}");
                }
            }

            if (fbx is null || templateMesh is null)
            {
                throw new ArgumentException(
                    $"--fbx and --template-mesh are both required.{Environment.NewLine}{Usage}");
            }

            if (strength is < 0.0 or > 1.0 || !double.IsFinite(strength))
            {
                throw new ArgumentException("--strength must be between 0 and 1.");
            }

            if (scale is { } value && (!double.IsFinite(value) || value <= 0.0))
            {
                throw new ArgumentException("--scale must be a positive finite number.");
            }

            string fullFbx = Path.GetFullPath(fbx);
            string fullTemplate = Path.GetFullPath(templateMesh);
            if (!File.Exists(fullFbx))
            {
                throw new FileNotFoundException("The source FBX was not found.", fullFbx);
            }

            if (!File.Exists(fullTemplate))
            {
                throw new FileNotFoundException(
                    "The template mesh resource was not found.",
                    fullTemplate);
            }

            return new Options
            {
                FbxPath = fullFbx,
                TemplateMeshPath = fullTemplate,
                OutputPath = output is null ? null : Path.GetFullPath(output),
                Strength = strength,
                ScaleOverride = scale,
                ScaleRegion = region,
                DropExtras = dropExtras,
                IgnoreMorphChannels = ignoreMorphs,
            };
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }

            return args[++index];
        }

        private static double ParseDouble(string value, string option) =>
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed)
                ? parsed
                : throw new ArgumentException($"Option '{option}' requires a number.");

        private static RigScaleRegion ParseRegion(string value) =>
            value.ToLowerInvariant() switch
            {
                "leg" => RigScaleRegion.Leg,
                "torso" => RigScaleRegion.Torso,
                "arm" => RigScaleRegion.Arm,
                _ => throw new ArgumentException(
                    "--scale-region must be one of: leg, torso, arm."),
            };
    }
}
