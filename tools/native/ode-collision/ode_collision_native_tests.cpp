#include "ode_collision.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <atomic>
#include <limits>
#include <thread>
#include <vector>

namespace
{
    int Fail(const char* message)
    {
        std::fprintf(stderr, "FAIL: %s\n", message);
        return 1;
    }

    bool Near(double actual, double expected, double tolerance = 1e-10)
    {
        return std::abs(actual - expected) <= tolerance;
    }

    bool UnitNormal(double x, double y, double z)
    {
        return Near(std::sqrt(x * x + y * y + z * z), 1.0);
    }
}

int main()
{
    if (reanimated_ode_collision_abi_version() != 1)
        return Fail("Unexpected native collision ABI version.");

    const char* identity = reanimated_ode_collision_identity();
    if (identity == nullptr || std::strstr(identity, "ODE 0.16.6") == nullptr ||
        std::strstr(identity, "double precision") == nullptr)
        return Fail("Native backend identity does not identify the pinned double-precision ODE build.");

    double nx = 0, ny = 0, nz = 0, depth = 0;
    const uint64_t before = reanimated_ode_collision_query_count();
    int status = reanimated_ode_sphere_contact(
        0, 0.9, 0, 0.2,
        0, 0, 0, 1,
        &nx, &ny, &nz, &depth);
    if (status != REANIMATED_ODE_CONTACT || !UnitNormal(nx, ny, nz) ||
        !Near(nx, 0) || !Near(ny, 1) || !Near(nz, 0) || !Near(depth, 0.3))
        return Fail("Sphere contact returned an unexpected normal or penetration depth.");

    status = reanimated_ode_sphere_contact(
        0, 1.3, 0, 0.2,
        0, 0, 0, 1,
        &nx, &ny, &nz, &depth);
    if (status != REANIMATED_ODE_NO_CONTACT)
        return Fail("Separated spheres produced a contact.");

    status = reanimated_ode_capsule_contact(
        0.8, 0, 0, 0.5,
        0, -1, 0,
        0, 1, 0,
        0.5,
        &nx, &ny, &nz, &depth);
    if (status != REANIMATED_ODE_CONTACT || !UnitNormal(nx, ny, nz) ||
        !Near(nx, 1) || !Near(ny, 0) || !Near(nz, 0) || !Near(depth, 0.2))
        return Fail("Capsule shaft contact returned an unexpected normal or depth.");

    status = reanimated_ode_capsule_contact(
        0.5, 1.3, 0, 0.3,
        0, 0, 0,
        0, 1, 0,
        0.4,
        &nx, &ny, &nz, &depth);
    const double expectedDepth = 0.7 - std::sqrt(0.5 * 0.5 + 0.3 * 0.3);
    if (status != REANIMATED_ODE_CONTACT || !UnitNormal(nx, ny, nz) ||
        !Near(nx, 0.5 / std::sqrt(0.34)) || !Near(ny, 0.3 / std::sqrt(0.34)) ||
        !Near(depth, expectedDepth))
        return Fail("Capsule end-cap contact returned an unexpected normal or depth.");

    const double firstDepth = depth;
    status = reanimated_ode_capsule_contact(
        0.5, 1.3, 0, 0.3,
        0, 0, 0,
        0, 1, 0,
        0.4,
        &nx, &ny, &nz, &depth);
    if (status != REANIMATED_ODE_CONTACT || !Near(depth, firstDepth))
        return Fail("Repeated native contacts are not deterministic.");

    status = reanimated_ode_sphere_contact(
        std::numeric_limits<double>::quiet_NaN(), 0, 0, 0.2,
        0, 0, 0, 1,
        &nx, &ny, &nz, &depth);
    if (status != REANIMATED_ODE_ERROR)
        return Fail("Non-finite input was not rejected.");

    std::atomic<int> threadFailures{0};
    std::vector<std::thread> workers;
    for (int worker = 0; worker < 4; ++worker)
    {
        workers.emplace_back([&threadFailures]
        {
            for (int i = 0; i < 64; ++i)
            {
                double threadNx = 0, threadNy = 0, threadNz = 0, threadDepth = 0;
                if (reanimated_ode_sphere_contact(
                        0, 0.9, 0, 0.2, 0, 0, 0, 1,
                        &threadNx, &threadNy, &threadNz, &threadDepth) != REANIMATED_ODE_CONTACT ||
                    !Near(threadNy, 1) || !Near(threadDepth, 0.3))
                    threadFailures.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }
    for (std::thread& worker : workers) worker.join();
    if (threadFailures.load(std::memory_order_relaxed) != 0)
        return Fail("Per-thread ODE collision workspaces were not deterministic.");

    const uint64_t after = reanimated_ode_collision_query_count();
    if (after - before != 5 + 4 * 64)
        return Fail("Native query counter did not count every dCollide call across threads.");

    std::printf("PASS: %s (%llu actual dCollide calls)\n", identity,
        static_cast<unsigned long long>(after - before));
    return 0;
}
