using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxConnection(
    string Kind,
    long ChildId,
    long ParentId,
    ImmutableArray<object> Metadata)
{
    public string PropertyName =>
        Metadata.Length > 0 ? Convert.ToString(Metadata[0], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
}

public sealed class FbxModelObject
{
    internal FbxModelObject(
        long objectId,
        string name,
        string subtype,
        FbxNode sourceNode,
        ImmutableDictionary<string, ImmutableArray<object>> properties)
    {
        ObjectId = objectId;
        Name = name;
        Subtype = subtype;
        SourceNode = sourceNode;
        Properties = properties;
    }

    public long ObjectId { get; }

    public string Name { get; }

    public string Subtype { get; }

    public bool IsLimb => string.Equals(Subtype, "LimbNode", StringComparison.Ordinal);

    public FbxNode SourceNode { get; }

    public ImmutableDictionary<string, ImmutableArray<object>> Properties { get; }
}

public sealed record FbxAnimationStackInfo(
    long ObjectId,
    string Name,
    ImmutableArray<long> LayerIds,
    ImmutableArray<string> LayerNames,
    long StartTick,
    long StopTick);

public sealed record FbxAnimationStackActivity(
    FbxAnimationStackInfo Stack,
    bool Usable,
    string UnavailableReason,
    int SkeletalBindingCount,
    int ChangingSkeletalBindingCount);

public sealed class FbxAnimationCurve
{
    internal FbxAnimationCurve(
        long objectId,
        ImmutableArray<long> keyTimes,
        ImmutableArray<double> keyValues,
        CancellationToken cancellationToken)
    {
        if (keyTimes.IsDefaultOrEmpty || keyTimes.Length != keyValues.Length)
        {
            throw new InvalidDataException(
                $"FBX animation curve {objectId} must have equal non-empty KeyTime and KeyValueFloat arrays.");
        }

        for (int index = 0; index < keyTimes.Length; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!double.IsFinite(keyValues[index]))
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} contains a non-finite value.");
            }

            if (index > 0 && keyTimes[index] < keyTimes[index - 1])
            {
                throw new InvalidDataException(
                    $"FBX animation curve {objectId} has decreasing KeyTime values.");
            }
        }

        ObjectId = objectId;
        KeyTimes = keyTimes;
        KeyValues = keyValues;
    }

    public long ObjectId { get; }

    public ImmutableArray<long> KeyTimes { get; }

    public ImmutableArray<double> KeyValues { get; }

    public double Sample(long tick)
    {
        if (tick <= KeyTimes[0])
        {
            return KeyValues[0];
        }

        if (tick >= KeyTimes[^1])
        {
            return KeyValues[^1];
        }

        int low = 0;
        int high = KeyTimes.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (KeyTimes[middle] <= tick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int firstIndex = low - 1;
        int secondIndex = low;
        long firstTick = KeyTimes[firstIndex];
        long secondTick = KeyTimes[secondIndex];
        if (secondTick == firstTick)
        {
            return KeyValues[firstIndex];
        }

        double amount = (double)(tick - firstTick) / (secondTick - firstTick);
        return KeyValues[firstIndex] +
            ((KeyValues[secondIndex] - KeyValues[firstIndex]) * amount);
    }
}

public sealed record FbxAnimationCurveBinding(
    long ModelId,
    string PropertyName,
    char Axis,
    FbxAnimationCurve Curve);

public enum FbxTimebaseSource
{
    GlobalSettings,
    AnimationCurveKeySpacing,
    Fallback30Fps,
}

public enum FbxTimebaseConfidence
{
    Declared,
    InferredLow,
    FallbackLow,
}

public sealed record FbxDeclaredTimebase(
    int? TimeMode,
    FrameRate FrameRate,
    double? CustomFrameRate,
    FbxTimebaseSource Source,
    FbxTimebaseConfidence Confidence)
{
    public double FramesPerSecond => FrameRate.FramesPerSecond;
}

/// <summary>
/// Validated semantic view over the low-level binary FBX AST.
/// </summary>
public sealed class FbxSemanticScene
{
    private static readonly ImmutableDictionary<int, FrameRate> TimeModeFrameRates =
        new Dictionary<int, FrameRate>
        {
            [0] = new(30, 1),
            [1] = new(120, 1),
            [2] = new(100, 1),
            [3] = new(60, 1),
            [4] = new(50, 1),
            [5] = new(48, 1),
            [6] = new(30, 1),
            [7] = new(30, 1),
            [8] = new(30_000, 1_001),
            [9] = new(30_000, 1_001),
            [10] = new(25, 1),
            [11] = new(24, 1),
            [12] = new(1_000, 1),
            [13] = new(24_000, 1_001),
            [15] = new(96, 1),
            [16] = new(72, 1),
            [17] = new(60_000, 1_001),
            [18] = new(120_000, 1_001),
        }.ToImmutableDictionary();

    private readonly ImmutableDictionary<long, FbxNode> _objectsById;
    private readonly ImmutableDictionary<long, ImmutableArray<FbxConnection>> _parents;
    private readonly ImmutableDictionary<long, ImmutableArray<FbxConnection>> _children;
    private readonly ImmutableDictionary<long, FbxAnimationCurve> _curves;
    private readonly ImmutableDictionary<long, string> _curveErrors;

    private FbxSemanticScene(
        FbxBinaryDocument document,
        ImmutableDictionary<long, FbxNode> objectsById,
        ImmutableDictionary<long, FbxModelObject> models,
        ImmutableArray<long> modelOrder,
        ImmutableArray<FbxConnection> connections,
        ImmutableDictionary<long, ImmutableArray<FbxConnection>> parents,
        ImmutableDictionary<long, ImmutableArray<FbxConnection>> children,
        ImmutableDictionary<long, FbxAnimationCurve> curves,
        ImmutableDictionary<long, string> curveErrors,
        ImmutableArray<FbxAnimationStackInfo> animationStacks,
        ImmutableDictionary<string, ImmutableArray<object>> globalSettings)
    {
        Document = document;
        _objectsById = objectsById;
        Models = models;
        ModelOrder = modelOrder;
        Connections = connections;
        _parents = parents;
        _children = children;
        _curves = curves;
        _curveErrors = curveErrors;
        AnimationStacks = animationStacks;
        GlobalSettings = globalSettings;
    }

    public FbxBinaryDocument Document { get; }

    public ImmutableDictionary<long, FbxModelObject> Models { get; }

    public ImmutableArray<long> ModelOrder { get; }

    public ImmutableArray<FbxConnection> Connections { get; }

    public ImmutableArray<FbxAnimationStackInfo> AnimationStacks { get; }

    public ImmutableDictionary<string, ImmutableArray<object>> GlobalSettings { get; }

    public double MetersPerUnit
    {
        get
        {
            double unitScaleFactor = FbxSemanticValues.GetDouble(
                GlobalSettings,
                "UnitScaleFactor",
                1.0);
            if (!double.IsFinite(unitScaleFactor) || unitScaleFactor <= 0.0)
            {
                throw new InvalidDataException(
                    $"FBX GlobalSettings UnitScaleFactor '{unitScaleFactor}' is invalid.");
            }

            return unitScaleFactor / 100.0;
        }
    }

    public static FbxSemanticScene Parse(FbxBinaryDocument document) =>
        Parse(document, CancellationToken.None);

    public static FbxSemanticScene Parse(
        FbxBinaryDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        FbxNode objects = document.FindTopLevel("Objects") ??
            throw new InvalidDataException("FBX is missing the Objects section.");
        FbxNode connectionsNode = document.FindTopLevel("Connections") ??
            throw new InvalidDataException("FBX is missing the Connections section.");

        var objectBuilder = ImmutableDictionary.CreateBuilder<long, FbxNode>();
        var modelBuilder = ImmutableDictionary.CreateBuilder<long, FbxModelObject>();
        var modelOrder = ImmutableArray.CreateBuilder<long>();
        var layerBuilder = ImmutableDictionary.CreateBuilder<long, string>();
        var curveBuilder = ImmutableDictionary.CreateBuilder<long, FbxAnimationCurve>();
        var curveErrorBuilder = ImmutableDictionary.CreateBuilder<long, string>();

        foreach (FbxNode node in objects.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Properties.IsEmpty ||
                !FbxSemanticValues.TryConvertInt64(node.Properties[0].Value, out long objectId))
            {
                continue;
            }

            if (!objectBuilder.TryAdd(objectId, node))
            {
                throw new InvalidDataException($"FBX object id {objectId} is duplicated.");
            }

            if (string.Equals(node.Name, "Model", StringComparison.Ordinal))
            {
                if (node.Properties.Length < 3)
                {
                    throw new InvalidDataException($"FBX Model {objectId} is missing name or subtype.");
                }

                string name = FbxBinaryDocument.CleanObjectName(
                    FbxSemanticValues.ConvertString(node.Properties[1].Value));
                string subtype = FbxSemanticValues.ConvertString(node.Properties[2].Value);
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidDataException($"FBX Model {objectId} has an empty name.");
                }

                modelBuilder.Add(
                    objectId,
                    new FbxModelObject(
                        objectId,
                        name,
                        subtype,
                        node,
                        FbxSemanticValues.ReadProperties70(node)));
                modelOrder.Add(objectId);
            }
            else if (string.Equals(node.Name, "AnimationLayer", StringComparison.Ordinal) &&
                     node.Properties.Length >= 2)
            {
                layerBuilder.Add(
                    objectId,
                    FbxBinaryDocument.CleanObjectName(
                        FbxSemanticValues.ConvertString(node.Properties[1].Value)));
            }
            else if (string.Equals(node.Name, "AnimationCurve", StringComparison.Ordinal))
            {
                try
                {
                    FbxAnimationCurve? curve = ReadCurve(
                        objectId,
                        node,
                        cancellationToken);
                    if (curve is not null)
                    {
                        curveBuilder.Add(objectId, curve);
                    }
                }
                catch (InvalidDataException exception)
                {
                    curveErrorBuilder.Add(objectId, exception.Message);
                }
            }
        }

        ImmutableArray<FbxConnection> connections =
            ReadConnections(connectionsNode, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableDictionary<long, ImmutableArray<FbxConnection>> parents =
            connections
                .GroupBy(static connection => connection.ChildId)
                .ToImmutableDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray());
        ImmutableDictionary<long, ImmutableArray<FbxConnection>> children =
            connections
                .GroupBy(static connection => connection.ParentId)
                .ToImmutableDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray());
        ImmutableDictionary<string, ImmutableArray<object>> globalSettings =
            FbxSemanticValues.ReadProperties70(document.FindTopLevel("GlobalSettings"));
        ImmutableArray<FbxAnimationStackInfo> stacks = ReadAnimationStacks(
            document,
            objects,
            layerBuilder.ToImmutable(),
            children,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return new FbxSemanticScene(
            document,
            objectBuilder.ToImmutable(),
            modelBuilder.ToImmutable(),
            modelOrder.ToImmutable(),
            connections,
            parents,
            children,
            curveBuilder.ToImmutable(),
            curveErrorBuilder.ToImmutable(),
            stacks,
            globalSettings);
    }

    public long? GetModelParentId(long modelId)
    {
        long[] candidates = GetParents(modelId)
            .Where(connection =>
                string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                Models.ContainsKey(connection.ParentId))
            .Select(static connection => connection.ParentId)
            .Distinct()
            .ToArray();
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidDataException(
                $"FBX Model '{Models[modelId].Name}' has multiple Model parents."),
        };
    }

    public long? GetNearestLimbParentId(long modelId)
    {
        var visited = new HashSet<long> { modelId };
        long? parent = GetModelParentId(modelId);
        while (parent.HasValue)
        {
            if (!visited.Add(parent.Value))
            {
                throw new InvalidDataException(
                    $"FBX Model hierarchy contains a cycle at object {parent.Value}.");
            }

            if (Models[parent.Value].IsLimb)
            {
                return parent.Value;
            }

            parent = GetModelParentId(parent.Value);
        }

        return null;
    }

    public ImmutableArray<FbxConnection> GetParents(long objectId) =>
        _parents.TryGetValue(objectId, out ImmutableArray<FbxConnection> rows) ? rows : [];

    public ImmutableArray<FbxConnection> GetChildren(long objectId) =>
        _children.TryGetValue(objectId, out ImmutableArray<FbxConnection> rows) ? rows : [];

    /// <summary>
    /// Reads authoritative Pose::BindPose globals in the FBX row-vector layout
    /// and converts them into the Core column-vector convention. Multiple
    /// identical pose rows are accepted; conflicting authoritative rows fail
    /// closed rather than silently selecting one.
    /// </summary>
    public ImmutableDictionary<long, TransformMatrix> ReadBindPoseGlobals() =>
        ReadBindPoseGlobals(CancellationToken.None);

    public ImmutableDictionary<long, TransformMatrix> ReadBindPoseGlobals(
        CancellationToken cancellationToken)
    {
        var result = ImmutableDictionary.CreateBuilder<long, TransformMatrix>();
        FbxNode objects = Document.FindTopLevel("Objects") ??
            throw new InvalidDataException("FBX is missing the Objects section.");
        foreach (FbxNode pose in objects.FindChildren("Pose"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsBindPose(pose))
            {
                continue;
            }

            bool hasPoseNode = false;
            foreach (FbxNode poseNode in pose.FindChildren("PoseNode"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasPoseNode = true;
                FbxNode? node = poseNode.FindChild("Node");
                if (node is null ||
                    node.Properties.IsEmpty ||
                    !FbxSemanticValues.TryConvertInt64(
                        node.Properties[0].Value,
                        out long objectId))
                {
                    throw new InvalidDataException(
                        "FBX BindPose PoseNode has a missing or invalid Node id.");
                }

                TransformMatrix matrix = ReadBindPoseMatrix(
                    poseNode.FindChild("Matrix"),
                    objectId);
                if (result.TryGetValue(
                        objectId,
                        out TransformMatrix existing))
                {
                    if (!existing.NearlyEquals(matrix, 5.0e-5))
                    {
                        throw new InvalidDataException(
                            $"FBX BindPose contains conflicting matrices for object {objectId}.");
                    }

                    continue;
                }

                result.Add(objectId, matrix);
            }

            if (!hasPoseNode)
            {
                throw new InvalidDataException(
                    "FBX BindPose contains no PoseNode matrices.");
            }
        }

        return result.ToImmutable();
    }

    public FbxAnimationStackInfo SelectAnimationStack(string? name)
    {
        if (AnimationStacks.IsEmpty)
        {
            throw new InvalidDataException("FBX contains no AnimationStack or unclaimed AnimationLayer.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (AnimationStacks.Length != 1)
            {
                throw new InvalidDataException(
                    "FBX contains multiple animation stacks; select one explicitly: " +
                    string.Join(", ", AnimationStacks.Select(static stack => stack.Name)));
            }

            return AnimationStacks[0];
        }

        FbxAnimationStackInfo[] matches = AnimationStacks
            .Where(stack => string.Equals(stack.Name, name, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"FBX animation stack '{name}' was not found."),
            _ => throw new InvalidDataException(
                $"FBX contains duplicate animation stack names '{name}'."),
        };
    }

    /// <summary>
    /// Selects the sole usable skeletal stack for normal animation import.
    /// Explicit names remain authoritative. With multiple stacks, automatic
    /// selection is permitted only when exactly one one-layer stack owns
    /// changing limb channels, or, for a static rest pose, exactly one owns
    /// any limb channels. Ambiguous authored takes still require review.
    /// </summary>
    public FbxAnimationStackInfo SelectAnimationStackForImport(
        string? name) =>
        SelectAnimationStackForImport(name, CancellationToken.None);

    public FbxAnimationStackInfo SelectAnimationStackForImport(
        string? name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(name) ||
            AnimationStacks.Length <= 1)
        {
            return SelectAnimationStack(name);
        }

        ImmutableArray<FbxAnimationStackActivity> activities =
            AnalyzeAnimationStacks(cancellationToken);
        FbxAnimationStackActivity[] changingCandidates = activities
                .Where(static activity =>
                    activity.Usable &&
                    activity.ChangingSkeletalBindingCount > 0)
                .ToArray();
        if (changingCandidates.Length == 1)
        {
            return changingCandidates[0].Stack;
        }

        if (changingCandidates.Length == 0)
        {
            FbxAnimationStackActivity[] staticCandidates = activities
                    .Where(static activity =>
                        activity.Usable &&
                        activity.SkeletalBindingCount > 0)
                    .ToArray();
            if (staticCandidates.Length == 1)
            {
                return staticCandidates[0].Stack;
            }
        }

        throw new InvalidDataException(
            "FBX contains multiple animation stacks without one unambiguous " +
            "changing skeletal take; select one explicitly: " +
            string.Join(
                ", ",
                AnimationStacks.Select(static stack => stack.Name)));
    }

    public ImmutableArray<FbxAnimationStackActivity>
        AnalyzeAnimationStacks() =>
            AnalyzeAnimationStacks(CancellationToken.None);

    public ImmutableArray<FbxAnimationStackActivity>
        AnalyzeAnimationStacks(CancellationToken cancellationToken)
    {
        var result =
            ImmutableArray.CreateBuilder<FbxAnimationStackActivity>(
                AnimationStacks.Length);
        foreach (FbxAnimationStackInfo stack in AnimationStacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stack.LayerIds.Length != 1)
            {
                result.Add(
                    new(
                        stack,
                        false,
                        $"The stack owns {stack.LayerIds.Length} layers; bake or flatten it to one layer.",
                        0,
                        0));
                continue;
            }

            ImmutableArray<FbxAnimationCurveBinding> bindings;
            try
            {
                bindings = ReadAnimationBindings(stack, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                result.Add(
                    new(
                        stack,
                        false,
                        exception.Message,
                        0,
                        0));
                continue;
            }

            FbxAnimationCurveBinding[] limbBindings = bindings
                .Where(binding =>
                    Models.TryGetValue(
                        binding.ModelId,
                        out FbxModelObject? model) &&
                    model.IsLimb &&
                    IsEvaluatedTransformProperty(binding.PropertyName))
                .ToArray();
            int changing = limbBindings.Count(
                static binding =>
                    binding.Curve.KeyValues.Length > 1 &&
                    binding.Curve.KeyValues.Max() -
                    binding.Curve.KeyValues.Min() > 1.0e-8);
            result.Add(
                new(
                    stack,
                    true,
                    string.Empty,
                    limbBindings.Length,
                    changing));
        }

        return result.MoveToImmutable();
    }

    public ImmutableArray<FbxAnimationCurveBinding> ReadAnimationBindings(
        FbxAnimationStackInfo stack) =>
        ReadAnimationBindings(stack, CancellationToken.None);

    public ImmutableArray<FbxAnimationCurveBinding> ReadAnimationBindings(
        FbxAnimationStackInfo stack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        cancellationToken.ThrowIfCancellationRequested();
        if (stack.LayerIds.Length != 1)
        {
            throw new InvalidDataException(
                $"FBX animation stack '{stack.Name}' contains {stack.LayerIds.Length} layers; " +
                "bake or flatten it to one layer before import.");
        }

        long layerId = stack.LayerIds[0];
        var result = ImmutableArray.CreateBuilder<FbxAnimationCurveBinding>();
        foreach (FbxConnection layerConnection in GetChildren(layerId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(layerConnection.Kind, "OO", StringComparison.Ordinal) ||
                !_objectsById.TryGetValue(layerConnection.ChildId, out FbxNode? curveNode) ||
                !string.Equals(curveNode.Name, "AnimationCurveNode", StringComparison.Ordinal))
            {
                continue;
            }

            FbxConnection[] modelLinks = GetParents(layerConnection.ChildId)
                .Where(connection =>
                    string.Equals(connection.Kind, "OP", StringComparison.Ordinal) &&
                    Models.ContainsKey(connection.ParentId))
                .ToArray();
            if (modelLinks.Length != 1)
            {
                if (modelLinks.Length == 0)
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"FBX AnimationCurveNode {layerConnection.ChildId} has multiple Model bindings.");
            }

            FbxConnection modelLink = modelLinks[0];
            string propertyName = modelLink.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                continue;
            }

            foreach (FbxConnection curveLink in GetChildren(layerConnection.ChildId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(curveLink.Kind, "OP", StringComparison.Ordinal))
                {
                    continue;
                }

                if (_curveErrors.TryGetValue(
                        curveLink.ChildId,
                        out string? curveError))
                {
                    throw new InvalidDataException(
                        $"FBX animation stack '{stack.Name}' contains unusable animation curve {curveLink.ChildId}: {curveError}");
                }

                if (!_curves.TryGetValue(
                        curveLink.ChildId,
                        out FbxAnimationCurve? curve))
                {
                    continue;
                }

                string axisName = curveLink.PropertyName;
                char axis = axisName.Length > 0
                    ? char.ToUpperInvariant(axisName[^1])
                    : '\0';
                if (axis is not ('X' or 'Y' or 'Z'))
                {
                    continue;
                }

                result.Add(
                    new FbxAnimationCurveBinding(
                        modelLink.ParentId,
                        propertyName,
                        axis,
                        curve));
            }
        }

        if (result
            .GroupBy(
                static binding =>
                    (binding.ModelId, binding.PropertyName, binding.Axis))
            .Any(static group => group.Count() > 1))
        {
            throw new InvalidDataException(
                $"FBX animation stack '{stack.Name}' binds more than one curve to the same channel.");
        }

        return result.ToImmutable();
    }

    private static bool IsEvaluatedTransformProperty(string propertyName) =>
        propertyName is
            "Lcl Translation" or
            "Lcl Rotation" or
            "Lcl Scaling";

    public FbxDeclaredTimebase ResolveDeclaredTimebase(
        IEnumerable<FbxAnimationCurveBinding> bindings) =>
        ResolveDeclaredTimebase(bindings, CancellationToken.None);

    public FbxDeclaredTimebase ResolveDeclaredTimebase(
        IEnumerable<FbxAnimationCurveBinding> bindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        int? timeMode = FbxSemanticValues.TryGetInt32(
            GlobalSettings,
            "TimeMode");
        double? custom = FbxSemanticValues.TryGetDouble(
            GlobalSettings,
            "CustomFrameRate");
        if (custom.HasValue &&
            (!double.IsFinite(custom.Value) || custom.Value <= 0.0))
        {
            custom = null;
        }

        if (timeMode == 14 && custom.HasValue)
        {
            return new(
                timeMode,
                RationalizeFrameRate(custom.Value),
                custom,
                FbxTimebaseSource.GlobalSettings,
                FbxTimebaseConfidence.Declared);
        }

        if (timeMode.HasValue &&
            TimeModeFrameRates.TryGetValue(timeMode.Value, out FrameRate declared))
        {
            return new(
                timeMode,
                declared,
                custom,
                FbxTimebaseSource.GlobalSettings,
                FbxTimebaseConfidence.Declared);
        }

        var deltaCounts = new Dictionary<long, long>();
        foreach (FbxAnimationCurveBinding binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<long> keyTimes = binding.Curve.KeyTimes;
            for (int index = 1; index < keyTimes.Length; index++)
            {
                if ((index & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                long delta = keyTimes[index] - keyTimes[index - 1];
                if (delta > 0)
                {
                    deltaCounts[delta] =
                        deltaCounts.GetValueOrDefault(delta) + 1;
                }
            }
        }

        if (deltaCounts.Count > 0)
        {
            long interval = deltaCounts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .First()
                .Key;
            double inferred = (double)FbxBinaryDocument.TicksPerSecond / interval;
            if (double.IsFinite(inferred) && inferred is >= 1.0 and <= 240.0)
            {
                return new(
                    timeMode,
                    RationalizeFrameRate(inferred),
                    custom,
                    FbxTimebaseSource.AnimationCurveKeySpacing,
                    FbxTimebaseConfidence.InferredLow);
            }
        }

        return new(
            timeMode,
            new FrameRate(30, 1),
            custom,
            FbxTimebaseSource.Fallback30Fps,
            FbxTimebaseConfidence.FallbackLow);
    }

    private static ImmutableArray<FbxConnection> ReadConnections(
        FbxNode node,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<FbxConnection>();
        foreach (FbxNode connection in node.FindChildren("C"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.Properties.Length < 3 ||
                !FbxSemanticValues.TryConvertInt64(connection.Properties[1].Value, out long childId) ||
                !FbxSemanticValues.TryConvertInt64(connection.Properties[2].Value, out long parentId))
            {
                throw new InvalidDataException("FBX Connection row is malformed.");
            }

            result.Add(
                new(
                    FbxSemanticValues.ConvertString(connection.Properties[0].Value),
                    childId,
                    parentId,
                    connection.Properties
                        .Skip(3)
                        .Select(static property => property.Value)
                        .ToImmutableArray()));
        }

        return result.ToImmutable();
    }

    private static FbxAnimationCurve? ReadCurve(
        long objectId,
        FbxNode node,
        CancellationToken cancellationToken)
    {
        ImmutableArray<long> times = FbxSemanticValues.ReadInt64Array(
            node.FindChild("KeyTime"),
            "KeyTime");
        ImmutableArray<double> values = FbxSemanticValues.ReadDoubleArray(
            node.FindChild("KeyValueFloat"),
            "KeyValueFloat");
        if (times.IsEmpty && values.IsEmpty)
        {
            return null;
        }

        return new FbxAnimationCurve(
            objectId,
            times,
            values,
            cancellationToken);
    }

    private static bool IsBindPose(FbxNode pose)
    {
        bool propertyTyped = pose.Properties
            .Skip(1)
            .Any(
                static property =>
                    property.Value is string value &&
                    string.Equals(
                        FbxBinaryDocument.CleanObjectName(value),
                        "BindPose",
                        StringComparison.Ordinal));
        string? childType = pose.FindChild("Type")?.FirstString();
        return propertyTyped ||
            string.Equals(
                childType is null
                    ? null
                    : FbxBinaryDocument.CleanObjectName(childType),
                "BindPose",
                StringComparison.Ordinal);
    }

    private static TransformMatrix ReadBindPoseMatrix(
        FbxNode? matrixNode,
        long objectId)
    {
        ImmutableArray<double> values = FbxSemanticValues.ReadDoubleArray(
            matrixNode,
            $"BindPose PoseNode {objectId} Matrix");
        if (values.Length != 16 ||
            values.Any(static value => !double.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"FBX BindPose PoseNode {objectId} Matrix must contain 16 finite values.");
        }

        // FBX matrix arrays use the row-vector convention used by common
        // exporters (translation in elements 12..14). Transpose once at this
        // boundary so the rest of the application remains column-vector-only.
        var matrix = new TransformMatrix(
            values[0], values[4], values[8], values[12],
            values[1], values[5], values[9], values[13],
            values[2], values[6], values[10], values[14],
            values[3], values[7], values[11], values[15]);
        if (Math.Abs(matrix.M41) > 1.0e-9 ||
            Math.Abs(matrix.M42) > 1.0e-9 ||
            Math.Abs(matrix.M43) > 1.0e-9 ||
            Math.Abs(matrix.M44 - 1.0) > 1.0e-9 ||
            Math.Abs(matrix.LinearDeterminant) <= 1.0e-12)
        {
            throw new InvalidDataException(
                $"FBX BindPose PoseNode {objectId} Matrix must be non-singular and affine.");
        }

        return matrix;
    }

    private static ImmutableArray<FbxAnimationStackInfo> ReadAnimationStacks(
        FbxBinaryDocument document,
        FbxNode objects,
        ImmutableDictionary<long, string> layerNames,
        ImmutableDictionary<long, ImmutableArray<FbxConnection>> children,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var takes = new Dictionary<string, (long Start, long Stop)>(StringComparer.Ordinal);
        FbxNode? takesNode = document.FindTopLevel("Takes");
        if (takesNode is not null)
        {
            foreach (FbxNode take in takesNode.FindChildren("Take"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FbxNode? localTime = take.FindChild("LocalTime");
                if (take.Properties.IsEmpty ||
                    localTime is null ||
                    localTime.Properties.Length < 2 ||
                    !FbxSemanticValues.TryConvertInt64(localTime.Properties[0].Value, out long start) ||
                    !FbxSemanticValues.TryConvertInt64(localTime.Properties[1].Value, out long stop))
                {
                    continue;
                }

                takes[FbxBinaryDocument.CleanObjectName(
                    FbxSemanticValues.ConvertString(take.Properties[0].Value))] = (start, stop);
            }
        }

        var claimedLayers = new HashSet<long>();
        var result = ImmutableArray.CreateBuilder<FbxAnimationStackInfo>();
        foreach (FbxNode node in objects.FindChildren("AnimationStack"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Properties.Length < 2 ||
                !FbxSemanticValues.TryConvertInt64(node.Properties[0].Value, out long stackId))
            {
                throw new InvalidDataException("FBX AnimationStack is malformed.");
            }

            string name = FbxBinaryDocument.CleanObjectName(
                FbxSemanticValues.ConvertString(node.Properties[1].Value));
            ImmutableArray<long> layerIds = children.TryGetValue(
                    stackId,
                    out ImmutableArray<FbxConnection> rows)
                ? rows
                    .Where(connection =>
                        string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                        layerNames.ContainsKey(connection.ChildId))
                    .Select(static connection => connection.ChildId)
                    .Distinct()
                    .ToImmutableArray()
                : [];
            claimedLayers.UnionWith(layerIds);
            ImmutableDictionary<string, ImmutableArray<object>> properties =
                FbxSemanticValues.ReadProperties70(node);
            long start = FbxSemanticValues.GetInt64(properties, "LocalStart", 0);
            long stop = FbxSemanticValues.GetInt64(properties, "LocalStop", start);
            if (takes.TryGetValue(name, out (long Start, long Stop) takeRange))
            {
                (start, stop) = takeRange;
            }

            result.Add(
                new(
                    stackId,
                    name,
                    layerIds,
                    layerIds.Select(layerId => layerNames[layerId]).ToImmutableArray(),
                    start,
                    stop));
        }

        foreach ((long layerId, string layerName) in layerNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claimedLayers.Contains(layerId))
            {
                continue;
            }

            result.Add(
                new(
                    layerId,
                    layerName,
                    [layerId],
                    [layerName],
                    0,
                    0));
        }

        return result.ToImmutable();
    }

    private static FrameRate RationalizeFrameRate(double value)
    {
        const int maximumDenominator = 100_000;
        if (!double.IsFinite(value) || value <= 0.0 || value > int.MaxValue)
        {
            throw new InvalidDataException($"FBX frame rate '{value}' is invalid.");
        }

        long previousNumerator = 0;
        long numerator = 1;
        long previousDenominator = 1;
        long denominator = 0;
        double remaining = value;
        while (true)
        {
            long whole = checked((long)Math.Floor(remaining));
            long nextNumerator = checked((whole * numerator) + previousNumerator);
            long nextDenominator = checked((whole * denominator) + previousDenominator);
            if (nextDenominator > maximumDenominator ||
                nextNumerator > int.MaxValue)
            {
                break;
            }

            previousNumerator = numerator;
            numerator = nextNumerator;
            previousDenominator = denominator;
            denominator = nextDenominator;
            double fraction = remaining - whole;
            if (fraction < 1e-12)
            {
                break;
            }

            remaining = 1.0 / fraction;
        }

        if (denominator <= 0 || numerator <= 0)
        {
            return new FrameRate(checked((int)Math.Round(value)), 1);
        }

        return new FrameRate(checked((int)numerator), checked((int)denominator));
    }
}

internal static class FbxSemanticValues
{
    public static ImmutableDictionary<string, ImmutableArray<object>> ReadProperties70(
        FbxNode? node)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<object>>(
            StringComparer.Ordinal);
        FbxNode? container = node?.FindChild("Properties70");
        if (container is null)
        {
            return result.ToImmutable();
        }

        foreach (FbxNode row in container.FindChildren("P"))
        {
            if (row.Properties.IsEmpty)
            {
                continue;
            }

            string name = ConvertString(row.Properties[0].Value);
            result[name] = row.Properties
                .Skip(Math.Min(4, row.Properties.Length))
                .Select(static property => property.Value)
                .ToImmutableArray();
        }

        return result.ToImmutable();
    }

    public static string ConvertString(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ??
        throw new InvalidDataException("FBX string property is null.");

    public static bool TryConvertInt64(object value, out long result)
    {
        try
        {
            result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }

    public static int? TryGetInt32(
        IReadOnlyDictionary<string, ImmutableArray<object>> properties,
        string name)
    {
        if (!properties.TryGetValue(name, out ImmutableArray<object> values) ||
            values.IsEmpty ||
            !TryConvertInt64(values[0], out long value) ||
            value is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        return (int)value;
    }

    public static double? TryGetDouble(
        IReadOnlyDictionary<string, ImmutableArray<object>> properties,
        string name)
    {
        if (!properties.TryGetValue(name, out ImmutableArray<object> values) ||
            values.IsEmpty)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(values[0], CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static double GetDouble(
        IReadOnlyDictionary<string, ImmutableArray<object>> properties,
        string name,
        double fallback) =>
        TryGetDouble(properties, name) ?? fallback;

    public static long GetInt64(
        IReadOnlyDictionary<string, ImmutableArray<object>> properties,
        string name,
        long fallback)
    {
        if (!properties.TryGetValue(name, out ImmutableArray<object> values) ||
            values.IsEmpty ||
            !TryConvertInt64(values[0], out long value))
        {
            return fallback;
        }

        return value;
    }

    public static ImmutableArray<long> ReadInt64Array(
        FbxNode? node,
        string fieldName)
    {
        if (node is null || node.Properties.IsEmpty)
        {
            return [];
        }

        object value = node.Properties[0].Value;
        return value switch
        {
            ImmutableArray<long> rows => rows,
            ImmutableArray<int> rows => rows.Select(static row => (long)row).ToImmutableArray(),
            long[] rows => rows.ToImmutableArray(),
            int[] rows => rows.Select(static row => (long)row).ToImmutableArray(),
            _ when TryConvertInt64(value, out long scalar) => [scalar],
            _ => throw new InvalidDataException($"FBX {fieldName} is not an integer array."),
        };
    }

    public static ImmutableArray<double> ReadDoubleArray(
        FbxNode? node,
        string fieldName)
    {
        if (node is null || node.Properties.IsEmpty)
        {
            return [];
        }

        object value = node.Properties[0].Value;
        return value switch
        {
            ImmutableArray<double> rows => rows,
            ImmutableArray<float> rows => rows.Select(static row => (double)row).ToImmutableArray(),
            double[] rows => rows.ToImmutableArray(),
            float[] rows => rows.Select(static row => (double)row).ToImmutableArray(),
            _ => ReadScalarDouble(value, fieldName),
        };
    }

    private static ImmutableArray<double> ReadScalarDouble(
        object value,
        string fieldName)
    {
        try
        {
            return [Convert.ToDouble(value, CultureInfo.InvariantCulture)];
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException(
                $"FBX {fieldName} is not a numeric array.",
                exception);
        }
    }
}
