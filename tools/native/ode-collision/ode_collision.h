#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  define REANIMATED_ODE_API __declspec(dllexport)
#  define REANIMATED_ODE_CALL __cdecl
#else
#  define REANIMATED_ODE_API __attribute__((visibility("default")))
#  define REANIMATED_ODE_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum ReAnimatedOdeCollisionStatus
{
    REANIMATED_ODE_ERROR = -1,
    REANIMATED_ODE_NO_CONTACT = 0,
    REANIMATED_ODE_CONTACT = 1
};

REANIMATED_ODE_API uint32_t REANIMATED_ODE_CALL reanimated_ode_collision_abi_version(void);
REANIMATED_ODE_API const char* REANIMATED_ODE_CALL reanimated_ode_collision_identity(void);
REANIMATED_ODE_API uint64_t REANIMATED_ODE_CALL reanimated_ode_collision_query_count(void);

// The ODE ABI never crosses this boundary: inputs and contacts are scalar doubles.
REANIMATED_ODE_API int REANIMATED_ODE_CALL reanimated_ode_sphere_contact(
    double particle_x, double particle_y, double particle_z, double particle_radius,
    double sphere_x, double sphere_y, double sphere_z, double sphere_radius,
    double* normal_x, double* normal_y, double* normal_z, double* penetration_depth);

REANIMATED_ODE_API int REANIMATED_ODE_CALL reanimated_ode_capsule_contact(
    double particle_x, double particle_y, double particle_z, double particle_radius,
    double capsule_start_x, double capsule_start_y, double capsule_start_z,
    double capsule_end_x, double capsule_end_y, double capsule_end_z,
    double capsule_radius,
    double* normal_x, double* normal_y, double* normal_z, double* penetration_depth);

#ifdef __cplusplus
}
#endif
