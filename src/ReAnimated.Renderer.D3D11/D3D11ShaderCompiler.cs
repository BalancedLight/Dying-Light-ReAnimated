using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ReAnimated.Renderer.D3D11;

internal static class D3D11ShaderCompiler
{
    public static byte[] Compile(
        string shaderSource,
        string entryPoint,
        string profile,
        string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        Result result = Compiler.Compile(
            shaderSource: shaderSource,
            defines: Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: sourceName,
            profile: profile,
            shaderFlags: ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3,
            effectFlags: EffectFlags.None,
            out Blob? code,
            out Blob? errors);
        using (code)
        using (errors)
        {
            if (result.Failure || code is null)
            {
                string detail = errors is null
                    ? $"HRESULT 0x{result.Code:X8}"
                    : Marshal.PtrToStringAnsi(errors.BufferPointer)
                        ?? $"HRESULT 0x{result.Code:X8}";
                throw new InvalidOperationException(
                    $"Unable to compile {sourceName} ({profile}): {detail.Trim()}");
            }

            int byteCount = checked((int)(nuint)code.BufferSize);
            byte[] bytecode = new byte[byteCount];
            Marshal.Copy(code.BufferPointer, bytecode, 0, byteCount);
            return bytecode;
        }
    }
}

public static class RenderCameraMath
{
    public static Matrix4x4 CreateViewProjection(
        RenderCamera camera,
        int width,
        int height,
        RenderProjectionParameters? projectionOverride = null)
    {
        Vector3 forward = camera.Target - camera.Eye;
        if (forward.LengthSquared() <= 1.0e-8f)
        {
            forward = -Vector3.UnitZ;
        }

        Vector3 up = camera.Up;
        if (up.LengthSquared() <= 1.0e-8f)
        {
            up = Vector3.UnitY;
        }

        forward = Vector3.Normalize(forward);
        up = Vector3.Normalize(up);
        if (MathF.Abs(Vector3.Dot(forward, up)) >= 0.999f)
        {
            up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) < 0.999f
                ? Vector3.UnitY
                : Vector3.UnitX;
        }

        Matrix4x4 view = Matrix4x4.CreateLookAt(
            camera.Eye,
            camera.Eye + forward,
            up);
        Matrix4x4 projection = projectionOverride is { } explicitProjection
            ? CreateProjection(explicitProjection)
            : CreateSceneProjection(camera, width, height);
        return view * projection;
    }

    public static RenderViewportRectangle CreateSceneViewport(
        RenderCamera camera,
        int width,
        int height)
    {
        float safeWidth = Math.Max(1, width);
        float safeHeight = Math.Max(1, height);
        float viewportAspect = safeWidth / safeHeight;
        if (camera.ProjectionAspectRatio is not float capturedAspect ||
            !float.IsFinite(capturedAspect) ||
            capturedAspect <= 0.0f)
        {
            return new RenderViewportRectangle(
                0.0f,
                0.0f,
                safeWidth,
                safeHeight);
        }

        if (viewportAspect > capturedAspect)
        {
            float contentWidth = safeHeight * capturedAspect;
            return new RenderViewportRectangle(
                (safeWidth - contentWidth) * 0.5f,
                0.0f,
                contentWidth,
                safeHeight);
        }

        float contentHeight = safeWidth / capturedAspect;
        return new RenderViewportRectangle(
            0.0f,
            (safeHeight - contentHeight) * 0.5f,
            safeWidth,
            contentHeight);
    }

    public static Matrix4x4 CreateProjection(
        RenderProjectionParameters projection)
    {
        float fieldOfViewDegrees = ValidateFieldOfView(
            projection.FieldOfViewDegrees);
        float aspectRatio = ValidateAspectRatio(
            projection.AspectRatio);
        float nearPlane = ValidateNearPlane(
            projection.NearPlane);
        float verticalFieldOfView = projection.FieldOfViewAxis switch
        {
            RenderProjectionFovAxis.Vertical =>
                fieldOfViewDegrees * (MathF.PI / 180.0f),
            RenderProjectionFovAxis.Horizontal =>
                2.0f * MathF.Atan(
                    MathF.Tan(
                        fieldOfViewDegrees *
                        (MathF.PI / 360.0f)) /
                    aspectRatio),
            _ => throw new ArgumentOutOfRangeException(
                nameof(projection),
                "The projection FOV axis is unknown."),
        };

        if (projection.FarPlane == RenderProjectionFarPlane.Infinite)
        {
            if (projection.FarClip is not null)
            {
                throw new ArgumentException(
                    "An infinite projection cannot declare a finite far clip.",
                    nameof(projection));
            }

            float yScale = 1.0f /
                MathF.Tan(verticalFieldOfView * 0.5f);
            float xScale = yScale / aspectRatio;
            return new Matrix4x4(
                xScale, 0.0f, 0.0f, 0.0f,
                0.0f, yScale, 0.0f, 0.0f,
                0.0f, 0.0f, -1.0f, -1.0f,
                0.0f, 0.0f, -nearPlane, 0.0f);
        }

        if (projection.FarPlane != RenderProjectionFarPlane.Finite ||
            projection.FarClip is not float farPlane ||
            !float.IsFinite(farPlane) ||
            farPlane <= nearPlane)
        {
            throw new ArgumentException(
                "A finite projection requires a far clip beyond the near clip.",
                nameof(projection));
        }

        return Matrix4x4.CreatePerspectiveFieldOfView(
            verticalFieldOfView,
            aspectRatio,
            nearPlane,
            farPlane);
    }

    private static Matrix4x4 CreateSceneProjection(
        RenderCamera camera,
        int width,
        int height)
    {
        float aspectRatio =
            camera.ProjectionAspectRatio is float capturedAspect &&
            float.IsFinite(capturedAspect) &&
            capturedAspect > 0.0f
                ? capturedAspect
                : Math.Max(1, width) /
                  (float)Math.Max(1, height);
        float fieldOfView = float.IsFinite(
            camera.VerticalFieldOfViewDegrees)
                ? Math.Clamp(
                    camera.VerticalFieldOfViewDegrees,
                    1.0f,
                    179.0f)
                : 60.0f;
        float nearPlane =
            float.IsFinite(camera.NearPlane)
                ? Math.Max(0.001f, camera.NearPlane)
                : 0.001f;
        float farPlane =
            float.IsFinite(camera.FarPlane)
                ? Math.Max(nearPlane + 0.01f, camera.FarPlane)
                : nearPlane + 0.01f;
        return Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView * (MathF.PI / 180.0f),
            aspectRatio,
            nearPlane,
            farPlane);
    }

    private static float ValidateFieldOfView(float value)
    {
        if (!float.IsFinite(value) ||
            value <= 1.0f ||
            value >= 179.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Projection FOV must be finite and between 1 and 179 degrees.");
        }

        return value;
    }

    private static float ValidateAspectRatio(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Projection aspect ratio must be finite and positive.");
        }

        return value;
    }

    private static float ValidateNearPlane(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Projection near clip must be finite and positive.");
        }

        return value;
    }
}

public readonly record struct RenderViewportRectangle(
    float X,
    float Y,
    float Width,
    float Height);
