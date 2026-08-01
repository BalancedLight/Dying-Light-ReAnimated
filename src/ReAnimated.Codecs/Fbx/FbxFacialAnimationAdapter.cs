using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Declares how raw FBX BlendShapeChannel DeformPercent values are converted
/// into authored morph weights. The importer never infers this unit from value
/// ranges because a value such as 1 can mean either 1% or a normalized weight
/// of 1.
/// </summary>
public enum FbxFacialSourceValueUnit
{
    Unspecified = 0,
    Normalized = 1,
    Percent = 2,
}

/// <summary>
/// Bounded options for importing scalar FBX BlendShapeChannel animation.
/// DeformPercent values remain available in their raw FBX representation while
/// the produced Core clip uses authored morph weights (normally 0..1).
/// </summary>
public sealed record FbxFacialAnimationImportOptions
{
    public string? AnimationStackName { get; init; }

    public FrameRate? SamplingFrameRate { get; init; }

    /// <summary>
    /// Explicit unit applied to channels without a per-channel override.
    /// Leaving this unspecified is valid only when every channel is covered by
    /// <see cref="ChannelSourceValueUnits"/>.
    /// </summary>
    public FbxFacialSourceValueUnit DefaultSourceValueUnit { get; init; }

    /// <summary>
    /// Optional explicit unit overrides keyed by canonical BlendShapeChannel
    /// name. Keys are matched case-insensitively and unknown keys are rejected.
    /// </summary>
    public IReadOnlyDictionary<string, FbxFacialSourceValueUnit>
        ChannelSourceValueUnits
    { get; init; } =
            ImmutableDictionary<string, FbxFacialSourceValueUnit>.Empty;

    public int MaximumChannels { get; init; } = 4_096;

    public int MaximumRawCurveKeys { get; init; } = 1_000_000;

    public int MaximumSampleFrames { get; init; } = 1_000_000;

    public int MaximumSampledScalarKeys { get; init; } = 1_000_000;
}

/// <summary>
/// The single validated scalar curve connected to one BlendShapeChannel in the
/// selected animation stack.
/// </summary>
public sealed record FbxFacialCurveBinding
{
    internal FbxFacialCurveBinding(
        long channelId,
        long curveNodeId,
        string targetPropertyName,
        string curvePropertyName,
        FbxAnimationCurve curve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPropertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(curvePropertyName);
        ArgumentNullException.ThrowIfNull(curve);

        ChannelId = channelId;
        CurveNodeId = curveNodeId;
        TargetPropertyName = targetPropertyName;
        CurvePropertyName = curvePropertyName;
        Curve = curve;
    }

    public long ChannelId { get; }

    public long CurveNodeId { get; }

    public string TargetPropertyName { get; }

    public string CurvePropertyName { get; }

    /// <summary>
    /// Exact finite FBX DeformPercent keys before the authored-value scale is
    /// applied.
    /// </summary>
    public FbxAnimationCurve Curve { get; }
}

/// <summary>
/// Source identity and conversion metadata for one FBX BlendShapeChannel.
/// </summary>
public sealed record FbxFacialChannel
{
    internal FbxFacialChannel(
        long objectId,
        string name,
        ImmutableArray<string> aliases,
        double defaultDeformPercent,
        FbxFacialSourceValueUnit sourceValueUnit,
        bool animated,
        FbxFacialCurveBinding? binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (aliases.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A facial channel requires at least its source name as an alias.",
                nameof(aliases));
        }

        if (!double.IsFinite(defaultDeformPercent) ||
            sourceValueUnit is not (
                FbxFacialSourceValueUnit.Normalized or
                FbxFacialSourceValueUnit.Percent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultDeformPercent),
                "Facial channel values must be finite and use an explicit unit.");
        }

        ObjectId = objectId;
        Name = name;
        Aliases = aliases;
        DefaultDeformPercent = defaultDeformPercent;
        SourceValueUnit = sourceValueUnit;
        SourceToAuthoredScale =
            sourceValueUnit == FbxFacialSourceValueUnit.Percent
                ? 0.01
                : 1.0;
        Animated = animated;
        Binding = binding;
    }

    public long ObjectId { get; }

    public string Name { get; }

    public ImmutableArray<string> Aliases { get; }

    /// <summary>
    /// Exact default value stored by the FBX BlendShapeChannel.
    /// </summary>
    public double DefaultDeformPercent { get; }

    /// <summary>
    /// Explicit unit selected by the import request for this channel.
    /// </summary>
    public FbxFacialSourceValueUnit SourceValueUnit { get; }

    /// <summary>
    /// Multiplier used to convert FBX source values to the authored scalar
    /// values in <see cref="FbxFacialAnimationImportResult.Clip"/>.
    /// Conventional 0..100 DeformPercent data uses 0.01; already-normalized
    /// source data uses 1.0. Values are never clamped.
    /// </summary>
    public double SourceToAuthoredScale { get; }

    public double DefaultAuthoredValue =>
        DefaultDeformPercent * SourceToAuthoredScale;

    public bool Animated { get; }

    public FbxFacialCurveBinding? Binding { get; }
}

public sealed record FbxFacialAnimationImportResult(
    FbxSemanticScene Scene,
    FbxAnimationStackInfo AnimationStack,
    FbxDeclaredTimebase DeclaredTimebase,
    ImmutableArray<long> SampleTicks,
    ImmutableArray<FbxFacialChannel> Channels,
    AnimationClip Clip)
{
    public long SourceStartTick =>
        SampleTicks.IsDefaultOrEmpty
            ? AnimationStack.StartTick
            : SampleTicks[0];

    public long SourceStopTick =>
        SampleTicks.IsDefaultOrEmpty
            ? AnimationStack.StopTick
            : SampleTicks[^1];

    public double SourceDurationSeconds =>
        (double)(((decimal)SourceStopTick - SourceStartTick) /
                 FbxBinaryDocument.TicksPerSecond);

    public ImmutableArray<FbxFacialChannel> AnimatedChannels =>
        Channels.Where(static channel => channel.Animated).ToImmutableArray();

    public bool HasFacialAnimation =>
        Channels.Any(static channel => channel.Animated);
}

/// <summary>
/// Imports BlendShapeChannel DeformPercent curves without loading mesh or shape
/// delta payloads. Only curve nodes owned by the selected one-layer stack are
/// parsed, so malformed curves in unrelated takes remain isolated.
/// </summary>
public static class FbxFacialAnimationAdapter
{
    private const double AnimatedSpreadThreshold = 1.0e-6;
    private const uint InterpolationTypeMask = 0x0000000E;
    private const uint LinearInterpolation = 0x00000004;

    public static FbxFacialAnimationImportResult Import(
        FbxBinaryDocument document,
        FbxFacialAnimationImportOptions? options = null) =>
        Import(document, options, CancellationToken.None);

    public static FbxFacialAnimationImportResult Import(
        FbxBinaryDocument document,
        FbxFacialAnimationImportOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new FbxFacialAnimationImportOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        FbxSemanticScene scene =
            FbxSemanticScene.Parse(document, cancellationToken);
        FbxAnimationStackInfo stack =
            scene.SelectAnimationStack(options.AnimationStackName);
        if (stack.LayerIds.Length != 1)
        {
            throw new InvalidDataException(
                $"FBX facial animation stack '{stack.Name}' contains " +
                $"{stack.LayerIds.Length} layers; bake or flatten it to one " +
                "layer before import.");
        }

        ImmutableDictionary<long, FbxNode> objects =
            ReadObjectIndex(document, cancellationToken);
        ImmutableArray<ChannelDraft> channels = ReadChannels(
            scene,
            objects,
            options.MaximumChannels,
            cancellationToken);
        ImmutableDictionary<long, FbxFacialCurveBinding> bindings =
            ReadSelectedStackBindings(
                scene,
                stack,
                objects,
                options.MaximumRawCurveKeys,
                cancellationToken);

        foreach (long channelId in bindings.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!channels.Any(channel => channel.ObjectId == channelId))
            {
                throw new InvalidDataException(
                    $"FBX facial animation stack '{stack.Name}' targets " +
                    $"unknown BlendShapeChannel {channelId}.");
            }
        }

        FbxDeclaredTimebase timebase = scene.ResolveDeclaredTimebase(
            bindings.Values.Select(
                static binding =>
                    new FbxAnimationCurveBinding(
                        binding.ChannelId,
                        "DeformPercent",
                        'X',
                        binding.Curve)),
            cancellationToken);
        FrameRate frameRate =
            options.SamplingFrameRate ?? timebase.FrameRate;
        ImmutableArray<long> ticks = BuildSampleTicks(
            stack,
            bindings.Values.Select(static binding => binding.Curve),
            frameRate,
            options.MaximumSampleFrames,
            cancellationToken);

        long sampledScalarKeyCount = checked(
            (long)channels.Length * ticks.Length);
        if (sampledScalarKeyCount > options.MaximumSampledScalarKeys)
        {
            throw new InvalidDataException(
                $"FBX facial animation stack '{stack.Name}' would require " +
                $"{sampledScalarKeyCount:N0} sampled scalar keys " +
                $"({channels.Length:N0} channels x {ticks.Length:N0} frames); " +
                $"the configured limit is " +
                $"{options.MaximumSampledScalarKeys:N0}.");
        }

        if (channels
            .Select(static channel => channel.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != channels.Length)
        {
            throw new InvalidDataException(
                "FBX BlendShapeChannel names must be unique " +
                "(case-insensitive) for facial import.");
        }

        ImmutableDictionary<string, FbxFacialSourceValueUnit> sourceValueUnits =
            ResolveSourceValueUnits(channels, options);
        var importedChannels =
            ImmutableArray.CreateBuilder<FbxFacialChannel>(channels.Length);
        var tracks = ImmutableArray.CreateBuilder<ScalarTrack>(channels.Length);
        foreach (ChannelDraft channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bindings.TryGetValue(
                channel.ObjectId,
                out FbxFacialCurveBinding? binding);
            FbxFacialSourceValueUnit sourceValueUnit =
                sourceValueUnits[channel.Name];
            double scale = SourceToAuthoredScale(sourceValueUnit);
            var keyframes =
                ImmutableArray.CreateBuilder<ScalarKeyframe>(ticks.Length);
            double minimum = double.PositiveInfinity;
            double maximum = double.NegativeInfinity;
            for (int frameIndex = 0;
                 frameIndex < ticks.Length;
                 frameIndex++)
            {
                if ((frameIndex & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                double rawValue =
                    binding?.Curve.Sample(ticks[frameIndex]) ??
                    channel.DefaultDeformPercent;
                double value = rawValue * scale;
                if (!double.IsFinite(value))
                {
                    throw new InvalidDataException(
                        $"FBX BlendShapeChannel '{channel.Name}' samples to " +
                        "a non-finite authored value.");
                }

                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                keyframes.Add(new ScalarKeyframe(frameIndex, value));
            }

            bool animated =
                maximum - minimum > AnimatedSpreadThreshold;
            importedChannels.Add(
                new FbxFacialChannel(
                    channel.ObjectId,
                    channel.Name,
                    channel.Aliases,
                    channel.DefaultDeformPercent,
                    sourceValueUnit,
                    animated,
                    binding));
            tracks.Add(
                new ScalarTrack(
                    channel.Name,
                    keyframes.MoveToImmutable()));
        }

        var clip = new AnimationClip(
            stack.Name,
            frameRate,
            ticks.Length,
            scalarTracks: tracks.MoveToImmutable());
        return new FbxFacialAnimationImportResult(
            scene,
            stack,
            timebase,
            ticks,
            importedChannels.MoveToImmutable(),
            clip);
    }

    private static void ValidateOptions(
        FbxFacialAnimationImportOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumRawCurveKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumSampleFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumSampledScalarKeys);
        if (!Enum.IsDefined(options.DefaultSourceValueUnit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The default facial source value unit is invalid.");
        }

        if (options.ChannelSourceValueUnits is null)
        {
            throw new ArgumentException(
                "The facial channel source-value-unit map cannot be null.",
                nameof(options));
        }

        if (options.SamplingFrameRate is FrameRate frameRate &&
            (frameRate.Numerator <= 0 || frameRate.Denominator <= 0))
        {
            throw new ArgumentException(
                "The facial sampling frame rate is invalid.",
                nameof(options));
        }
    }

    private static ImmutableDictionary<long, FbxNode> ReadObjectIndex(
        FbxBinaryDocument document,
        CancellationToken cancellationToken)
    {
        FbxNode objectsNode = document.FindTopLevel("Objects") ??
            throw new InvalidDataException("FBX is missing the Objects section.");
        var objects = ImmutableDictionary.CreateBuilder<long, FbxNode>();
        foreach (FbxNode node in objectsNode.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Properties.IsEmpty ||
                !FbxSemanticValues.TryConvertInt64(
                    node.Properties[0].Value,
                    out long objectId))
            {
                continue;
            }

            if (!objects.TryAdd(objectId, node))
            {
                throw new InvalidDataException(
                    $"FBX object id {objectId} is duplicated.");
            }
        }

        return objects.ToImmutable();
    }

    private static ImmutableArray<ChannelDraft> ReadChannels(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        int maximumChannels,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ChannelDraft>();
        foreach ((long objectId, FbxNode node) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsBlendShapeChannel(node))
            {
                continue;
            }

            if (result.Count >= maximumChannels)
            {
                throw new InvalidDataException(
                    $"FBX contains more than {maximumChannels:N0} " +
                    "BlendShapeChannel objects.");
            }

            if (node.Properties.Length < 2)
            {
                throw new InvalidDataException(
                    $"FBX BlendShapeChannel {objectId} has no name.");
            }

            string name = CleanFacialName(
                FbxSemanticValues.ConvertString(
                    node.Properties[1].Value));
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException(
                    $"FBX BlendShapeChannel {objectId} has an empty name.");
            }

            double defaultValue = ReadDefaultDeformPercent(
                objectId,
                name,
                node);
            var aliases = ImmutableArray.CreateBuilder<string>();
            var aliasSet = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            AddAlias(name);
            foreach (FbxConnection child in scene.GetChildren(objectId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        child.Kind,
                        "OO",
                        StringComparison.Ordinal) ||
                    !objects.TryGetValue(
                        child.ChildId,
                        out FbxNode? shape) ||
                    !string.Equals(
                        shape.Name,
                        "Geometry",
                        StringComparison.Ordinal) ||
                    !HasSubtype(shape, "Shape") ||
                    shape.Properties.Length < 2)
                {
                    continue;
                }

                AddAlias(
                    CleanFacialName(
                        FbxSemanticValues.ConvertString(
                            shape.Properties[1].Value)));
            }

            result.Add(
                new ChannelDraft(
                    objectId,
                    name,
                    aliases.ToImmutable(),
                    defaultValue));

            void AddAlias(string alias)
            {
                if (!string.IsNullOrWhiteSpace(alias) &&
                    aliasSet.Add(alias))
                {
                    aliases.Add(alias);
                }
            }
        }

        return result
            .OrderBy(
                static channel => channel.Name,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static channel => channel.ObjectId)
            .ToImmutableArray();
    }

    private static ImmutableDictionary<long, FbxFacialCurveBinding>
        ReadSelectedStackBindings(
            FbxSemanticScene scene,
            FbxAnimationStackInfo stack,
            ImmutableDictionary<long, FbxNode> objects,
            int maximumRawCurveKeys,
            CancellationToken cancellationToken)
    {
        long layerId = stack.LayerIds[0];
        long rawCurveKeyCount = 0;
        var result =
            ImmutableDictionary.CreateBuilder<long, FbxFacialCurveBinding>();
        foreach (FbxConnection layerConnection in scene.GetChildren(layerId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    layerConnection.Kind,
                    "OO",
                    StringComparison.Ordinal) ||
                !objects.TryGetValue(
                    layerConnection.ChildId,
                    out FbxNode? curveNode) ||
                !string.Equals(
                    curveNode.Name,
                    "AnimationCurveNode",
                    StringComparison.Ordinal))
            {
                continue;
            }

            FbxConnection[] channelLinks = scene
                .GetParents(layerConnection.ChildId)
                .Where(connection =>
                    objects.TryGetValue(
                        connection.ParentId,
                        out FbxNode? parent) &&
                    IsBlendShapeChannel(parent))
                .ToArray();
            if (channelLinks.Length == 0)
            {
                continue;
            }

            if (channelLinks.Length != 1)
            {
                throw new InvalidDataException(
                    $"FBX facial AnimationCurveNode " +
                    $"{layerConnection.ChildId} has " +
                    $"{channelLinks.Length} BlendShapeChannel bindings; " +
                    "exactly one is required.");
            }

            FbxConnection channelLink = channelLinks[0];
            FbxConnection[] propertyTargets = scene
                .GetParents(layerConnection.ChildId)
                .Where(connection =>
                    string.Equals(
                        connection.Kind,
                        "OP",
                        StringComparison.Ordinal) &&
                    objects.ContainsKey(connection.ParentId))
                .ToArray();
            if (!string.Equals(
                    channelLink.Kind,
                    "OP",
                    StringComparison.Ordinal) ||
                propertyTargets.Length != 1 ||
                channelLink.Metadata.Length != 1)
            {
                throw new InvalidDataException(
                    $"FBX facial AnimationCurveNode " +
                    $"{layerConnection.ChildId} must have exactly one OP " +
                    "property binding to its BlendShapeChannel.");
            }

            string propertyName = channelLink.PropertyName;
            if (!IsDeformPercentProperty(propertyName))
            {
                throw new InvalidDataException(
                    $"FBX facial AnimationCurveNode " +
                    $"{layerConnection.ChildId} targets unsupported " +
                    $"BlendShapeChannel property '{propertyName}'; " +
                    "expected DeformPercent.");
            }

            FbxConnection[] curveLinks = scene
                .GetChildren(layerConnection.ChildId)
                .Where(connection =>
                    objects.TryGetValue(
                        connection.ChildId,
                        out FbxNode? child) &&
                    string.Equals(
                        child.Name,
                        "AnimationCurve",
                        StringComparison.Ordinal))
                .ToArray();
            if (curveLinks.Length != 1)
            {
                throw new InvalidDataException(
                    $"FBX facial AnimationCurveNode " +
                    $"{layerConnection.ChildId} owns {curveLinks.Length} " +
                    "AnimationCurve objects; exactly one scalar curve is " +
                    "required.");
            }

            FbxConnection curveLink = curveLinks[0];
            if (!string.Equals(
                    curveLink.Kind,
                    "OP",
                    StringComparison.Ordinal) ||
                curveLink.Metadata.Length != 1)
            {
                throw new InvalidDataException(
                    $"FBX facial animation curve {curveLink.ChildId} must " +
                    "use one OP scalar-axis property.");
            }

            string curveProperty = curveLink.PropertyName;
            if (!IsScalarDeformPercentAxis(curveProperty))
            {
                throw new InvalidDataException(
                    $"FBX facial animation curve {curveLink.ChildId} uses " +
                    $"unsupported scalar axis '{curveProperty}'; expected " +
                    "d|DeformPercent, d|X, or d.");
            }

            FbxAnimationCurve curve = ReadCurve(
                curveLink.ChildId,
                objects[curveLink.ChildId],
                cancellationToken);
            rawCurveKeyCount = checked(
                rawCurveKeyCount + curve.KeyTimes.Length);
            if (rawCurveKeyCount > maximumRawCurveKeys)
            {
                throw new InvalidDataException(
                    $"FBX facial animation stack '{stack.Name}' contains " +
                    $"{rawCurveKeyCount:N0} raw DeformPercent keys; the " +
                    $"configured limit is {maximumRawCurveKeys:N0}.");
            }

            var binding = new FbxFacialCurveBinding(
                channelLink.ParentId,
                layerConnection.ChildId,
                propertyName,
                curveProperty,
                curve);
            if (!result.TryAdd(binding.ChannelId, binding))
            {
                throw new InvalidDataException(
                    $"FBX facial animation stack '{stack.Name}' binds more " +
                    "than one curve node to BlendShapeChannel " +
                    $"{binding.ChannelId}.");
            }
        }

        return result.ToImmutable();
    }

    private static FbxAnimationCurve ReadCurve(
        long objectId,
        FbxNode node,
        CancellationToken cancellationToken)
    {
        try
        {
            ImmutableArray<long> times = FbxSemanticValues.ReadInt64Array(
                node.FindChild("KeyTime"),
                "KeyTime");
            ImmutableArray<double> values =
                FbxSemanticValues.ReadDoubleArray(
                    node.FindChild("KeyValueFloat"),
                    "KeyValueFloat");
            ValidateLinearInterpolation(
                objectId,
                node,
                times.Length);
            return new FbxAnimationCurve(
                objectId,
                times,
                values,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"FBX selected facial animation curve {objectId} is " +
                $"unusable: {exception.Message}",
                exception);
        }
    }

    private static double ReadDefaultDeformPercent(
        long objectId,
        string channelName,
        FbxNode node)
    {
        FbxNode? direct = node.FindChild("DeformPercent");
        if (direct is not null &&
            !direct.Properties.IsEmpty &&
            TryConvertDouble(
                direct.Properties[0].Value,
                out double directValue))
        {
            return RequireFiniteDefault(
                directValue,
                objectId,
                channelName);
        }

        ImmutableDictionary<string, ImmutableArray<object>> properties =
            FbxSemanticValues.ReadProperties70(node);
        if (!properties.TryGetValue(
                "DeformPercent",
                out ImmutableArray<object> values) &&
            !properties.TryGetValue(
                "Deform Percent",
                out values))
        {
            return 0.0;
        }

        if (values.IsEmpty ||
            !TryConvertDouble(values[0], out double value))
        {
            throw new InvalidDataException(
                $"FBX BlendShapeChannel '{channelName}' ({objectId}) has " +
                "a non-numeric default DeformPercent.");
        }

        return RequireFiniteDefault(value, objectId, channelName);
    }

    private static double RequireFiniteDefault(
        double value,
        long objectId,
        string channelName)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException(
                $"FBX BlendShapeChannel '{channelName}' ({objectId}) has " +
                "a non-finite default DeformPercent.");
        }

        return value;
    }

    private static bool TryConvertDouble(
        object value,
        out double result)
    {
        try
        {
            result = Convert.ToDouble(
                value,
                CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or
            InvalidCastException or
            OverflowException)
        {
            result = 0.0;
            return false;
        }
    }

    private static ImmutableDictionary<string, FbxFacialSourceValueUnit>
        ResolveSourceValueUnits(
            ImmutableArray<ChannelDraft> channels,
            FbxFacialAnimationImportOptions options)
    {
        var units =
            ImmutableDictionary.CreateBuilder<string, FbxFacialSourceValueUnit>(
                StringComparer.OrdinalIgnoreCase);
        var knownChannels = channels
            .Select(static channel => channel.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (
            KeyValuePair<string, FbxFacialSourceValueUnit> entry in
            options.ChannelSourceValueUnits)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new InvalidDataException(
                    "FBX facial channel source-value-unit overrides cannot " +
                    "use an empty channel name.");
            }

            if (entry.Value is not (
                    FbxFacialSourceValueUnit.Normalized or
                    FbxFacialSourceValueUnit.Percent))
            {
                throw new InvalidDataException(
                    $"FBX facial channel '{entry.Key}' has an unspecified or " +
                    "invalid source value unit.");
            }

            if (!knownChannels.Contains(entry.Key))
            {
                throw new InvalidDataException(
                    $"FBX facial source-value-unit override '{entry.Key}' " +
                    "does not match a canonical BlendShapeChannel name.");
            }

            if (!units.TryAdd(entry.Key, entry.Value))
            {
                throw new InvalidDataException(
                    $"FBX facial source-value-unit override '{entry.Key}' is " +
                    "duplicated case-insensitively.");
            }
        }

        foreach (ChannelDraft channel in channels)
        {
            if (units.ContainsKey(channel.Name))
            {
                continue;
            }

            if (options.DefaultSourceValueUnit is not (
                    FbxFacialSourceValueUnit.Normalized or
                    FbxFacialSourceValueUnit.Percent))
            {
                throw new InvalidDataException(
                    $"FBX BlendShapeChannel '{channel.Name}' has no explicit " +
                    "source value unit. Set DefaultSourceValueUnit or add a " +
                    "ChannelSourceValueUnits override; DeformPercent units " +
                    "are not inferred from value ranges.");
            }

            units.Add(channel.Name, options.DefaultSourceValueUnit);
        }

        return units.ToImmutable();
    }

    private static double SourceToAuthoredScale(
        FbxFacialSourceValueUnit unit) =>
        unit switch
        {
            FbxFacialSourceValueUnit.Normalized => 1.0,
            FbxFacialSourceValueUnit.Percent => 0.01,
            _ => throw new InvalidOperationException(
                "A facial source value unit must be explicit."),
        };

    private static void ValidateLinearInterpolation(
        long objectId,
        FbxNode node,
        int keyCount)
    {
        ImmutableArray<long> flags = FbxSemanticValues.ReadInt64Array(
            node.FindChild("KeyAttrFlags"),
            "KeyAttrFlags");
        if (flags.IsDefaultOrEmpty)
        {
            throw new InvalidDataException(
                $"FBX animation curve {objectId} has no KeyAttrFlags " +
                "interpolation metadata. Bake or linearize the facial curve " +
                "before import.");
        }

        ImmutableArray<long> referenceCounts =
            FbxSemanticValues.ReadInt64Array(
                node.FindChild("KeyAttrRefCount"),
                "KeyAttrRefCount");
        if (referenceCounts.IsDefaultOrEmpty)
        {
            if (flags.Length != keyCount)
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} has {flags.Length:N0} " +
                    $"interpolation flags for {keyCount:N0} keys without " +
                    "KeyAttrRefCount metadata.");
            }
        }
        else
        {
            if (referenceCounts.Length != flags.Length)
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} has mismatched " +
                    "KeyAttrFlags and KeyAttrRefCount arrays.");
            }

            long coveredKeys = 0;
            foreach (long referenceCount in referenceCounts)
            {
                if (referenceCount <= 0 ||
                    referenceCount > keyCount - coveredKeys)
                {
                    throw new InvalidDataException(
                        $"FBX animation curve {objectId} has invalid " +
                        "KeyAttrRefCount coverage.");
                }

                coveredKeys += referenceCount;
            }

            if (coveredKeys != keyCount)
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} interpolation metadata " +
                    $"covers {coveredKeys:N0} of {keyCount:N0} keys.");
            }
        }

        ImmutableArray<double> attributeData =
            FbxSemanticValues.ReadDoubleArray(
                node.FindChild("KeyAttrDataFloat"),
                "KeyAttrDataFloat");
        if (!attributeData.IsDefaultOrEmpty &&
            attributeData.Length != (long)flags.Length * 4)
        {
            throw new InvalidDataException(
                $"FBX animation curve {objectId} has malformed " +
                "KeyAttrDataFloat metadata.");
        }

        foreach (long flag in flags)
        {
            if (flag < int.MinValue || flag > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} has an out-of-range " +
                    "KeyAttrFlags value.");
            }

            uint bits = flag < 0
                ? unchecked((uint)(int)flag)
                : (uint)flag;
            uint interpolation = bits & InterpolationTypeMask;
            if (interpolation != LinearInterpolation)
            {
                string kind = interpolation switch
                {
                    0x00000002 => "constant",
                    0x00000008 => "cubic",
                    _ => "unknown",
                };
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} uses {kind} " +
                    $"interpolation (KeyAttrFlags 0x{bits:X8}). Facial import " +
                    "currently accepts only baked linear curves; bake or " +
                    "linearize this take before import.");
            }
        }
    }

    private static ImmutableArray<long> BuildSampleTicks(
        FbxAnimationStackInfo stack,
        IEnumerable<FbxAnimationCurve> curves,
        FrameRate frameRate,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long start = stack.StartTick;
        long stop = stack.StopTick;
        bool hasCurveTime = false;
        long minimumCurveTime = long.MaxValue;
        long maximumCurveTime = long.MinValue;
        foreach (FbxAnimationCurve curve in curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (curve.KeyTimes.IsDefaultOrEmpty)
            {
                continue;
            }

            hasCurveTime = true;
            minimumCurveTime = Math.Min(
                minimumCurveTime,
                curve.KeyTimes[0]);
            maximumCurveTime = Math.Max(
                maximumCurveTime,
                curve.KeyTimes[^1]);
        }

        if (hasCurveTime)
        {
            if (start == 0 && stop == 0)
            {
                start = minimumCurveTime;
            }

            stop = Math.Max(stop, maximumCurveTime);
        }

        if (stop < start)
        {
            throw new InvalidDataException(
                $"FBX facial animation stack '{stack.Name}' stops before " +
                "it starts.");
        }

        decimal durationTicks = (decimal)stop - start;
        decimal frameCountValue = decimal.Ceiling(
            durationTicks *
            frameRate.Numerator /
            ((decimal)FbxBinaryDocument.TicksPerSecond *
             frameRate.Denominator)) + 1m;
        if (frameCountValue < 1m ||
            frameCountValue > maximumFrames)
        {
            throw new InvalidDataException(
                $"FBX facial animation stack '{stack.Name}' would require " +
                $"{frameCountValue:N0} samples; the configured limit is " +
                $"{maximumFrames:N0}.");
        }

        int frameCount = checked((int)frameCountValue);
        var result = ImmutableArray.CreateBuilder<long>(frameCount);
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            if ((frameIndex & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            decimal tickOffset = decimal.Round(
                (decimal)frameIndex *
                FbxBinaryDocument.TicksPerSecond *
                frameRate.Denominator /
                frameRate.Numerator,
                0,
                MidpointRounding.ToEven);
            long tick = decimal.ToInt64(
                Math.Min((decimal)stop, start + tickOffset));
            result.Add(Math.Min(stop, tick));
        }

        return result.MoveToImmutable();
    }

    private static bool IsBlendShapeChannel(FbxNode node) =>
        string.Equals(
            node.Name,
            "Deformer",
            StringComparison.Ordinal) &&
        HasSubtype(node, "BlendShapeChannel");

    private static bool HasSubtype(FbxNode node, string subtype) =>
        node.Properties.Length >= 3 &&
        string.Equals(
            FbxSemanticValues.ConvertString(
                node.Properties[2].Value),
            subtype,
            StringComparison.Ordinal);

    private static bool IsDeformPercentProperty(string propertyName) =>
        string.Equals(
            propertyName.Replace(" ", string.Empty, StringComparison.Ordinal),
            "DeformPercent",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsScalarDeformPercentAxis(string propertyName) =>
        string.Equals(
            propertyName,
            "d|DeformPercent",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            propertyName,
            "d|X",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            propertyName,
            "d",
            StringComparison.OrdinalIgnoreCase);

    private static string CleanFacialName(string value)
    {
        string clean = FbxBinaryDocument.CleanObjectName(value);
        int separator = clean.LastIndexOf('|');
        return separator >= 0 ? clean[(separator + 1)..] : clean;
    }

    private sealed record ChannelDraft(
        long ObjectId,
        string Name,
        ImmutableArray<string> Aliases,
        double DefaultDeformPercent);
}
