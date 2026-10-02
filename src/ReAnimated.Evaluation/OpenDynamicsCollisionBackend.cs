using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

public readonly record struct SecondaryCollisionContact(Vector3D Normal, double PenetrationDepth);

public interface ISecondaryCollisionBackend
{
    string Identity { get; }
    bool IsNative { get; }
    long NativeContactQueryCount { get; }
    bool TryContact(
        Vector3D particleCenter,
        double particleRadius,
        Vector3D colliderStart,
        Vector3D colliderEnd,
        double colliderRadius,
        Vector3D degenerateNormal,
        out SecondaryCollisionContact contact);
}

/// <summary>
/// Uses the pinned ODE collision API through a small C ABI. ODE types and handles
/// stay inside the native wrapper; the managed boundary carries only finite scalars.
/// </summary>
public sealed class OpenDynamicsCollisionBackend : ISecondaryCollisionBackend
{
    private readonly NativeApi _api;
    private long _queryCount;

    private OpenDynamicsCollisionBackend(NativeApi api) => _api = api;

    public string Identity => _api.Identity;

    public bool IsNative => true;

    /// <summary>Number of actual dCollide calls made through this backend.</summary>
    public long NativeContactQueryCount => Interlocked.Read(ref _queryCount);

    public static bool TryCreate(out OpenDynamicsCollisionBackend? backend, out string failureReason)
    {
        backend = null;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            failureReason = "The bundled ODE contact backend is available only in Windows x64 builds.";
            return false;
        }

        if (!NativeApi.TryGet(out NativeApi? api, out failureReason))
            return false;
        backend = new(api!);
        failureReason = string.Empty;
        return true;
    }

    /// <summary>Creates the shipped native backend or fails; use this for release/contact smoke tests.</summary>
    public static OpenDynamicsCollisionBackend CreateRequired()
    {
        if (!TryCreate(out OpenDynamicsCollisionBackend? backend, out string failureReason))
            throw new DllNotFoundException("The required Open Dynamics Engine collision backend is unavailable: " + failureReason);
        return backend!;
    }

    public bool TryContact(
        Vector3D particleCenter,
        double particleRadius,
        Vector3D colliderStart,
        Vector3D colliderEnd,
        double colliderRadius,
        Vector3D degenerateNormal,
        out SecondaryCollisionContact contact)
    {
        ValidateInput(particleCenter, particleRadius, colliderStart, colliderEnd, colliderRadius, degenerateNormal);
        int status;
        double nx, ny, nz, depth;
        Vector3D axis = colliderEnd - colliderStart;
        if (axis.LengthSquared <= 1e-16)
        {
            status = _api.Sphere(
                particleCenter.X, particleCenter.Y, particleCenter.Z, particleRadius,
                colliderStart.X, colliderStart.Y, colliderStart.Z, colliderRadius,
                out nx, out ny, out nz, out depth);
        }
        else
        {
            status = _api.Capsule(
                particleCenter.X, particleCenter.Y, particleCenter.Z, particleRadius,
                colliderStart.X, colliderStart.Y, colliderStart.Z,
                colliderEnd.X, colliderEnd.Y, colliderEnd.Z,
                colliderRadius,
                out nx, out ny, out nz, out depth);
        }

        if (status < 0)
            throw new InvalidOperationException($"ODE dCollide failed with native status {status}.");
        Interlocked.Increment(ref _queryCount);
        if (status == 0)
        {
            contact = default;
            return false;
        }

        Vector3D normal = new(nx, ny, nz);
        if (!normal.IsFinite || !double.IsFinite(depth) || depth <= 0 || !normal.TryNormalize(out normal))
            throw new InvalidOperationException("ODE dCollide returned a non-finite or invalid contact.");

        // ODE chooses a deterministic axis when two centers coincide. Preserve the
        // preview's authored-side hint for that geometrically ambiguous case.
        if (axis.LengthSquared <= 1e-16 &&
            (particleCenter - colliderStart).LengthSquared <= 1e-20 &&
            degenerateNormal.TryNormalize(out Vector3D sphereHint))
        {
            normal = sphereHint;
        }
        else if (axis.LengthSquared > 1e-16 &&
            DistanceSquaredToSegment(particleCenter, colliderStart, colliderEnd) <= 1e-20 &&
            degenerateNormal.TryNormalize(out Vector3D capsuleHint))
        {
            // A point on the shaft centerline must leave through a radial
            // surface. A parallel authored hint cannot replace ODE's contact
            // normal; it would push the point along the capsule's interior.
            Vector3D radialHint = capsuleHint - axis * (Vector3D.Dot(capsuleHint, axis) / axis.LengthSquared);
            if (radialHint.TryNormalize(out Vector3D normalizedHint)) normal = normalizedHint;
        }

        contact = new(normal, depth);
        return true;
    }

    private static void ValidateInput(
        Vector3D particleCenter,
        double particleRadius,
        Vector3D colliderStart,
        Vector3D colliderEnd,
        double colliderRadius,
        Vector3D degenerateNormal)
    {
        if (!particleCenter.IsFinite || !colliderStart.IsFinite || !colliderEnd.IsFinite || !degenerateNormal.IsFinite ||
            !double.IsFinite(particleRadius) || particleRadius < 0 ||
            !double.IsFinite(colliderRadius) || colliderRadius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(particleCenter), "Collision inputs must be finite with nonnegative particle and positive collider radii.");
        }
    }

    private static double DistanceSquaredToSegment(Vector3D point, Vector3D start, Vector3D end)
    {
        Vector3D axis = end - start;
        double lengthSquared = axis.LengthSquared;
        double amount = lengthSquared <= 1e-16 ? 0 : Math.Clamp(Vector3D.Dot(point - start, axis) / lengthSquared, 0, 1);
        return (point - (start + axis * amount)).LengthSquared;
    }

    private sealed class NativeApi
    {
        private const string ResourceName = "ReAnimated.Evaluation.Native.win-x64.ReAnimated.OdeCollision.dll";
        private const uint RequiredAbiVersion = 1;
        private const string CacheVendorDirectory = "ReAnimated";
        private const string CacheComponentDirectory = "Native";
        private const string CacheLibraryDirectory = "ODE";

        private static readonly Lazy<(NativeApi? Api, string Error)> Shared = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly IntPtr _libraryHandle;
        private readonly SphereContactDelegate _sphere;
        private readonly CapsuleContactDelegate _capsule;

        public string Identity { get; }

        private NativeApi(IntPtr libraryHandle, string identity)
        {
            _libraryHandle = libraryHandle;
            Identity = identity;
            _sphere = Marshal.GetDelegateForFunctionPointer<SphereContactDelegate>(
                NativeLibrary.GetExport(_libraryHandle, "reanimated_ode_sphere_contact"));
            _capsule = Marshal.GetDelegateForFunctionPointer<CapsuleContactDelegate>(
                NativeLibrary.GetExport(_libraryHandle, "reanimated_ode_capsule_contact"));
        }

        public static bool TryGet(out NativeApi? api, out string error)
        {
            (api, error) = Shared.Value;
            return api is not null;
        }

        public int Sphere(
            double particleX, double particleY, double particleZ, double particleRadius,
            double sphereX, double sphereY, double sphereZ, double sphereRadius,
            out double normalX, out double normalY, out double normalZ, out double penetrationDepth) =>
            _sphere(particleX, particleY, particleZ, particleRadius,
                sphereX, sphereY, sphereZ, sphereRadius,
                out normalX, out normalY, out normalZ, out penetrationDepth);

        public int Capsule(
            double particleX, double particleY, double particleZ, double particleRadius,
            double startX, double startY, double startZ,
            double endX, double endY, double endZ,
            double capsuleRadius,
            out double normalX, out double normalY, out double normalZ, out double penetrationDepth) =>
            _capsule(particleX, particleY, particleZ, particleRadius,
                startX, startY, startZ, endX, endY, endZ, capsuleRadius,
                out normalX, out normalY, out normalZ, out penetrationDepth);

        private static (NativeApi? Api, string Error) Load()
        {
            try
            {
                return LoadEmbeddedLibrary();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or TypeInitializationException or SecurityException)
            {
                return (null, "The embedded ODE payload could not be loaded: " + error.Message);
            }
        }

        private static (NativeApi? Api, string Error) LoadEmbeddedLibrary()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return (null, "The native payload targets Windows x64.");

            Assembly assembly = typeof(NativeApi).Assembly;
            using Stream? resource = assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
                return (null, $"Embedded native resource '{ResourceName}' is missing.");

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            byte[] image = buffer.ToArray();
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(image));
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                return (null, "A local application cache directory is unavailable.");

            string cacheDirectory = Path.Combine(localAppData, CacheVendorDirectory, CacheComponentDirectory, CacheLibraryDirectory, sha256);
            Directory.CreateDirectory(cacheDirectory);
            string nativePath = Path.Combine(cacheDirectory, "ReAnimated.OdeCollision.dll");
            if (!FileMatchesSha256(nativePath, sha256))
            {
                string temporaryPath = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                    {
                        output.Write(image);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporaryPath, nativePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }

            IntPtr library = IntPtr.Zero;
            try
            {
                library = NativeLibrary.Load(nativePath);
                var abi = Marshal.GetDelegateForFunctionPointer<AbiVersionDelegate>(
                    NativeLibrary.GetExport(library, "reanimated_ode_collision_abi_version"));
                var identity = Marshal.GetDelegateForFunctionPointer<IdentityDelegate>(
                    NativeLibrary.GetExport(library, "reanimated_ode_collision_identity"));
                uint actualAbi = abi();
                string version = Marshal.PtrToStringUTF8(identity()) ?? "unknown native backend";
                if (actualAbi != RequiredAbiVersion || !version.Contains("ODE 0.16.6", StringComparison.Ordinal))
                {
                    NativeLibrary.Free(library);
                    library = IntPtr.Zero;
                    return (null, $"Embedded collision payload identity is '{version}' with ABI {actualAbi}; expected ODE 0.16.6 ABI {RequiredAbiVersion}.");
                }
                var api = new NativeApi(library, version);
                library = IntPtr.Zero; // The process-lifetime shared API now owns the module.
                return (api, string.Empty);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                return (null, "The embedded ODE payload could not load: " + error.Message);
            }
            finally
            {
                if (library != IntPtr.Zero) NativeLibrary.Free(library);
            }
        }

        private static bool FileMatchesSha256(string path, string expectedHash)
        {
            if (!File.Exists(path)) return false;
            using FileStream file = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(file)) == expectedHash;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint AbiVersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr IdentityDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SphereContactDelegate(
            double particleX, double particleY, double particleZ, double particleRadius,
            double sphereX, double sphereY, double sphereZ, double sphereRadius,
            out double normalX, out double normalY, out double normalZ, out double penetrationDepth);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CapsuleContactDelegate(
            double particleX, double particleY, double particleZ, double particleRadius,
            double startX, double startY, double startZ,
            double endX, double endY, double endZ,
            double capsuleRadius,
            out double normalX, out double normalY, out double normalZ, out double penetrationDepth);
    }
}

internal sealed class AnalyticSecondaryCollisionBackend(string unavailableReason) : ISecondaryCollisionBackend
{
    public string Identity => "Analytic development fallback; ODE unavailable: " + unavailableReason;

    public bool IsNative => false;

    public long NativeContactQueryCount => 0;

    public bool TryContact(
        Vector3D particleCenter,
        double particleRadius,
        Vector3D colliderStart,
        Vector3D colliderEnd,
        double colliderRadius,
        Vector3D degenerateNormal,
        out SecondaryCollisionContact contact)
    {
        Vector3D axis = colliderEnd - colliderStart;
        double amount = axis.LengthSquared <= 1e-16 ? 0 : Math.Clamp(Vector3D.Dot(particleCenter - colliderStart, axis) / axis.LengthSquared, 0, 1);
        Vector3D center = colliderStart + axis * amount;
        Vector3D offset = particleCenter - center;
        double radius = colliderRadius + particleRadius;
        double distance = offset.Length;
        if (distance >= radius)
        {
            contact = default;
            return false;
        }

        Vector3D normal = offset.TryNormalize(out Vector3D normalized)
            ? normalized
            : degenerateNormal.TryNormalize(out Vector3D hint)
                ? hint
                : Vector3D.UnitX;
        contact = new(normal, radius - distance);
        return true;
    }
}
