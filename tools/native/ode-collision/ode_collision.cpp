#include "ode_collision.h"

#include <ode/ode.h>

#include <atomic>
#include <cmath>
#include <cstddef>
#include <memory>
#include <mutex>

static_assert(sizeof(dReal) == sizeof(double), "ODE collision wrapper requires double precision");

namespace
{
    constexpr uint32_t AbiVersion = 1;
    constexpr double MinimumSphereRadius = 1e-12;

    std::once_flag OdeInitialization;
    bool OdeInitialized = false;
    std::atomic<uint64_t> QueryCount{0};

    bool EnsureOdeInitialized()
    {
        std::call_once(OdeInitialization, []
        {
            OdeInitialized = dInitODE2(dInitFlagManualThreadCleanup) != 0;
        });
        return OdeInitialized;
    }

    bool IsFinite(double value)
    {
        return std::isfinite(value);
    }

    bool IsFinite3(double x, double y, double z)
    {
        return IsFinite(x) && IsFinite(y) && IsFinite(z);
    }

    struct ThreadWorkspace
    {
        dSpaceID Space = nullptr;
        dGeomID Particle = nullptr;
        dGeomID SolidSphere = nullptr;
        dGeomID SolidCapsule = nullptr;
        bool HasThreadData = false;
        bool Ready = false;

        ThreadWorkspace()
        {
            if (!EnsureOdeInitialized() || dAllocateODEDataForThread(dAllocateFlagCollisionData) == 0)
                return;

            HasThreadData = true;
            Space = dSimpleSpaceCreate(nullptr);
            if (Space == nullptr)
                return;

            // Manual cleanup is required by ODE when manual TLS cleanup is enabled.
            dSpaceSetManualCleanup(Space, 1);
            Particle = dCreateSphere(Space, MinimumSphereRadius);
            SolidSphere = dCreateSphere(Space, MinimumSphereRadius);
            SolidCapsule = dCreateCapsule(Space, MinimumSphereRadius, 0);
            Ready = Particle != nullptr && SolidSphere != nullptr && SolidCapsule != nullptr;
        }

        ~ThreadWorkspace()
        {
            // The space is in manual cleanup mode, so destroy its reusable geoms explicitly.
            if (Particle != nullptr) dGeomDestroy(Particle);
            if (SolidSphere != nullptr) dGeomDestroy(SolidSphere);
            if (SolidCapsule != nullptr) dGeomDestroy(SolidCapsule);
            if (Space != nullptr) dSpaceDestroy(Space);
            if (HasThreadData) dCleanupODEAllDataForThread();
        }
    };

    struct ThreadWorkspaceHolder
    {
        std::unique_ptr<ThreadWorkspace> Value;

        ThreadWorkspace& Get()
        {
            if (!Value) Value = std::make_unique<ThreadWorkspace>();
            return *Value;
        }
    };

    // Keep TLS initialization trivial; ODE startup occurs lazily on first use.
    thread_local ThreadWorkspaceHolder WorkspaceHolder;

    ThreadWorkspace& GetWorkspace()
    {
        return WorkspaceHolder.Get();
    }

    int Query(dGeomID solid,
              double particleX, double particleY, double particleZ, double particleRadius,
              double solidX, double solidY, double solidZ,
              double* normalX, double* normalY, double* normalZ, double* penetrationDepth)
    {
        ThreadWorkspace& workspace = GetWorkspace();
        if (!workspace.Ready || solid == nullptr ||
            normalX == nullptr || normalY == nullptr || normalZ == nullptr || penetrationDepth == nullptr)
            return REANIMATED_ODE_ERROR;

        dGeomSphereSetRadius(workspace.Particle, particleRadius);
        dGeomSetPosition(workspace.Particle, particleX, particleY, particleZ);
        dGeomSetPosition(solid, solidX, solidY, solidZ);

        dContactGeom contact{};
        QueryCount.fetch_add(1, std::memory_order_relaxed);
        const int count = dCollide(workspace.Particle, solid, 1, &contact, sizeof(contact));
        if (count == 0 || contact.depth <= 0)
            return REANIMATED_ODE_NO_CONTACT;

        const double normalLength = std::sqrt(
            contact.normal[0] * contact.normal[0] +
            contact.normal[1] * contact.normal[1] +
            contact.normal[2] * contact.normal[2]);
        if (!std::isfinite(normalLength) || normalLength <= 1e-12 || !std::isfinite(contact.depth))
            return REANIMATED_ODE_ERROR;

        *normalX = contact.normal[0] / normalLength;
        *normalY = contact.normal[1] / normalLength;
        *normalZ = contact.normal[2] / normalLength;
        *penetrationDepth = contact.depth;
        return REANIMATED_ODE_CONTACT;
    }
}

uint32_t REANIMATED_ODE_CALL reanimated_ode_collision_abi_version(void)
{
    return AbiVersion;
}

const char* REANIMATED_ODE_CALL reanimated_ode_collision_identity(void)
{
    return "ODE 0.16.6; double precision; ReAnimated collision ABI 1";
}

uint64_t REANIMATED_ODE_CALL reanimated_ode_collision_query_count(void)
{
    return QueryCount.load(std::memory_order_relaxed);
}

int REANIMATED_ODE_CALL reanimated_ode_sphere_contact(
    double particleX, double particleY, double particleZ, double particleRadius,
    double sphereX, double sphereY, double sphereZ, double sphereRadius,
    double* normalX, double* normalY, double* normalZ, double* penetrationDepth)
{
    if (!IsFinite3(particleX, particleY, particleZ) || !IsFinite3(sphereX, sphereY, sphereZ) ||
        !IsFinite(particleRadius) || particleRadius < 0 || !IsFinite(sphereRadius) || sphereRadius <= 0)
        return REANIMATED_ODE_ERROR;
    ThreadWorkspace& workspace = GetWorkspace();
    if (!workspace.Ready) return REANIMATED_ODE_ERROR;

    dGeomSphereSetRadius(workspace.SolidSphere, sphereRadius);
    return Query(workspace.SolidSphere, particleX, particleY, particleZ, particleRadius,
        sphereX, sphereY, sphereZ, normalX, normalY, normalZ, penetrationDepth);
}

int REANIMATED_ODE_CALL reanimated_ode_capsule_contact(
    double particleX, double particleY, double particleZ, double particleRadius,
    double startX, double startY, double startZ,
    double endX, double endY, double endZ,
    double capsuleRadius,
    double* normalX, double* normalY, double* normalZ, double* penetrationDepth)
{
    if (!IsFinite3(particleX, particleY, particleZ) || !IsFinite3(startX, startY, startZ) ||
        !IsFinite3(endX, endY, endZ) || !IsFinite(particleRadius) || particleRadius < 0 ||
        !IsFinite(capsuleRadius) || capsuleRadius <= 0)
        return REANIMATED_ODE_ERROR;
    ThreadWorkspace& workspace = GetWorkspace();
    if (!workspace.Ready) return REANIMATED_ODE_ERROR;

    const double axisX = endX - startX;
    const double axisY = endY - startY;
    const double axisZ = endZ - startZ;
    const double length = std::sqrt(axisX * axisX + axisY * axisY + axisZ * axisZ);
    if (!std::isfinite(length)) return REANIMATED_ODE_ERROR;
    if (length <= 1e-12)
    {
        dGeomSphereSetRadius(workspace.SolidSphere, capsuleRadius);
        return Query(workspace.SolidSphere, particleX, particleY, particleZ, particleRadius,
            startX, startY, startZ, normalX, normalY, normalZ, penetrationDepth);
    }

    dGeomCapsuleSetParams(workspace.SolidCapsule, capsuleRadius, length);
    dMatrix3 rotation;
    dRFromZAxis(rotation, axisX / length, axisY / length, axisZ / length);
    dGeomSetRotation(workspace.SolidCapsule, rotation);
    return Query(workspace.SolidCapsule, particleX, particleY, particleZ, particleRadius,
        (startX + endX) * 0.5, (startY + endY) * 0.5, (startZ + endZ) * 0.5,
        normalX, normalY, normalZ, penetrationDepth);
}
