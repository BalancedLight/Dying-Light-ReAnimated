using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

public enum FbxEulerOrder
{
    Xyz = 0,
    Xzy = 1,
    Yzx = 2,
    Yxz = 3,
    Zxy = 4,
    Zyx = 5,
}

/// <summary>
/// Evaluates FBX transforms directly into the Core column-vector convention.
/// No System.Numerics row-vector boundary is involved.
/// </summary>
public static class FbxTransformEvaluator
{
    private static readonly string[] AnimatedVectorProperties =
    [
        "Lcl Translation",
        "Lcl Rotation",
        "Lcl Scaling",
    ];

    public static TransformMatrix EvaluateModelLocal(
        FbxModelObject model,
        IReadOnlyDictionary<string, Vector3D>? animatedOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Vector3D translation = AnimatedValue(
            model,
            animatedOverrides,
            "Lcl Translation",
            Vector3D.Zero);
        Vector3D rotation = AnimatedValue(
            model,
            animatedOverrides,
            "Lcl Rotation",
            Vector3D.Zero);
        Vector3D scaling = AnimatedValue(
            model,
            animatedOverrides,
            "Lcl Scaling",
            Vector3D.One);
        Vector3D preRotation = ReadVector(
            model,
            "PreRotation",
            Vector3D.Zero);
        Vector3D postRotation = ReadVector(
            model,
            "PostRotation",
            Vector3D.Zero);
        Vector3D rotationOffset = ReadVector(
            model,
            "RotationOffset",
            Vector3D.Zero);
        Vector3D rotationPivot = ReadVector(
            model,
            "RotationPivot",
            Vector3D.Zero);
        Vector3D scalingOffset = ReadVector(
            model,
            "ScalingOffset",
            Vector3D.Zero);
        Vector3D scalingPivot = ReadVector(
            model,
            "ScalingPivot",
            Vector3D.Zero);
        FbxEulerOrder order = ReadRotationOrder(model);

        return
            TransformMatrix.CreateTranslation(translation) *
            TransformMatrix.CreateTranslation(rotationOffset) *
            TransformMatrix.CreateTranslation(rotationPivot) *
            EvaluateEuler(preRotation, order) *
            EvaluateEuler(rotation, order) *
            EvaluateEuler(postRotation, order).InvertedAffine() *
            TransformMatrix.CreateTranslation(-rotationPivot) *
            TransformMatrix.CreateTranslation(scalingOffset) *
            TransformMatrix.CreateTranslation(scalingPivot) *
            TransformMatrix.CreateScale(scaling) *
            TransformMatrix.CreateTranslation(-scalingPivot);
    }

    public static TransformMatrix EvaluateEuler(
        Vector3D degrees,
        FbxEulerOrder order)
    {
        ReadOnlySpan<char> axes = order switch
        {
            FbxEulerOrder.Xyz => "XYZ",
            FbxEulerOrder.Xzy => "XZY",
            FbxEulerOrder.Yzx => "YZX",
            FbxEulerOrder.Yxz => "YXZ",
            FbxEulerOrder.Zxy => "ZXY",
            FbxEulerOrder.Zyx => "ZYX",
            _ => "XYZ",
        };

        TransformMatrix result = TransformMatrix.Identity;
        foreach (char axis in axes)
        {
            double value = axis switch
            {
                'X' => degrees.X,
                'Y' => degrees.Y,
                _ => degrees.Z,
            };
            result = AxisRotation(axis, value) * result;
        }

        return result;
    }

    public static ImmutableDictionary<long, TransformMatrix> EvaluateModelGlobals(
        FbxSemanticScene scene,
        long tick,
        IEnumerable<FbxAnimationCurveBinding>? bindings = null,
        bool useAnimation = true) =>
        EvaluateModelGlobals(
            scene,
            tick,
            bindings,
            useAnimation,
            CancellationToken.None);

    public static ImmutableDictionary<long, TransformMatrix> EvaluateModelGlobals(
        FbxSemanticScene scene,
        long tick,
        IEnumerable<FbxAnimationCurveBinding>? bindings,
        bool useAnimation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<FbxAnimationCurveBinding> bindingArray =
            bindings?.ToImmutableArray() ?? [];
        ImmutableDictionary<long, ImmutableDictionary<string, Vector3D>> overrides =
            useAnimation
                ? EvaluateAnimatedOverrides(
                    scene,
                    bindingArray,
                    tick,
                    cancellationToken)
                : ImmutableDictionary<long, ImmutableDictionary<string, Vector3D>>.Empty;

        var cache = new Dictionary<long, TransformMatrix>();
        var visiting = new HashSet<long>();

        TransformMatrix Resolve(long modelId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.TryGetValue(modelId, out TransformMatrix cached))
            {
                return cached;
            }

            if (!visiting.Add(modelId))
            {
                throw new InvalidDataException(
                    $"FBX Model hierarchy contains a cycle at '{scene.Models[modelId].Name}'.");
            }

            FbxModelObject model = scene.Models[modelId];
            TransformMatrix local = EvaluateModelLocal(
                model,
                overrides.TryGetValue(
                    modelId,
                    out ImmutableDictionary<string, Vector3D>? modelOverrides)
                    ? modelOverrides
                    : null);
            long? parentId = scene.GetModelParentId(modelId);
            TransformMatrix global = parentId.HasValue
                ? Resolve(parentId.Value) * local
                : local;
            visiting.Remove(modelId);
            if (!global.IsFinite)
            {
                throw new InvalidDataException(
                    $"FBX Model '{model.Name}' evaluated to a non-finite transform.");
            }

            cache.Add(modelId, global);
            return global;
        }

        foreach (long modelId in scene.ModelOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Resolve(modelId);
        }

        return cache.ToImmutableDictionary();
    }

    private static ImmutableDictionary<long, ImmutableDictionary<string, Vector3D>>
        EvaluateAnimatedOverrides(
            FbxSemanticScene scene,
            ImmutableArray<FbxAnimationCurveBinding> bindings,
            long tick,
            CancellationToken cancellationToken)
    {
        var result =
            new Dictionary<long, Dictionary<string, Vector3D>>();
        foreach (FbxAnimationCurveBinding binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scene.Models.TryGetValue(binding.ModelId, out FbxModelObject? model) ||
                !AnimatedVectorProperties.Contains(
                    binding.PropertyName,
                    StringComparer.Ordinal))
            {
                continue;
            }

            Dictionary<string, Vector3D> modelOverrides =
                result.GetValueOrDefault(binding.ModelId) ??
                new Dictionary<string, Vector3D>(StringComparer.Ordinal);
            result[binding.ModelId] = modelOverrides;
            Vector3D fallback = string.Equals(
                binding.PropertyName,
                "Lcl Scaling",
                StringComparison.Ordinal)
                ? Vector3D.One
                : Vector3D.Zero;
            Vector3D value = modelOverrides.GetValueOrDefault(
                binding.PropertyName,
                ReadVector(model, binding.PropertyName, fallback));
            double sampled = binding.Curve.Sample(tick);
            value = binding.Axis switch
            {
                'X' => value with { X = sampled },
                'Y' => value with { Y = sampled },
                'Z' => value with { Z = sampled },
                _ => value,
            };
            modelOverrides[binding.PropertyName] = value;
        }

        return result.ToImmutableDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static TransformMatrix AxisRotation(char axis, double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            throw new InvalidDataException("FBX Euler rotation contains a non-finite angle.");
        }

        Vector3D rotationAxis = axis switch
        {
            'X' => Vector3D.UnitX,
            'Y' => Vector3D.UnitY,
            _ => Vector3D.UnitZ,
        };
        return TransformMatrix.CreateRotation(
            QuaternionD.FromAxisAngle(
                rotationAxis,
                degrees * (Math.PI / 180.0)));
    }

    private static Vector3D AnimatedValue(
        FbxModelObject model,
        IReadOnlyDictionary<string, Vector3D>? overrides,
        string name,
        Vector3D fallback)
    {
        if (overrides is not null &&
            overrides.TryGetValue(name, out Vector3D value))
        {
            if (!value.IsFinite)
            {
                throw new InvalidDataException(
                    $"FBX Model '{model.Name}' has a non-finite animated {name} value.");
            }

            return value;
        }

        return ReadVector(model, name, fallback);
    }

    private static Vector3D ReadVector(
        FbxModelObject model,
        string name,
        Vector3D fallback)
    {
        if (!model.Properties.TryGetValue(
                name,
                out ImmutableArray<object> values) ||
            values.IsEmpty)
        {
            return fallback;
        }

        if (values.Length < 3)
        {
            throw new InvalidDataException(
                $"FBX Model '{model.Name}' property '{name}' has fewer than three components.");
        }

        try
        {
            var result = new Vector3D(
                Convert.ToDouble(values[0], CultureInfo.InvariantCulture),
                Convert.ToDouble(values[1], CultureInfo.InvariantCulture),
                Convert.ToDouble(values[2], CultureInfo.InvariantCulture));
            if (!result.IsFinite)
            {
                throw new InvalidDataException(
                    $"FBX Model '{model.Name}' property '{name}' is non-finite.");
            }

            return result;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException(
                $"FBX Model '{model.Name}' property '{name}' is not numeric.",
                exception);
        }
    }

    private static FbxEulerOrder ReadRotationOrder(FbxModelObject model)
    {
        int raw = FbxSemanticValues.TryGetInt32(
            model.Properties,
            "RotationOrder") ?? 0;
        return raw is >= 0 and <= 5
            ? (FbxEulerOrder)raw
            : FbxEulerOrder.Xyz;
    }
}
