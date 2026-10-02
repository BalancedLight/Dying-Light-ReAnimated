cmake_minimum_required(VERSION 3.21)
project(ReAnimatedOdeCollision LANGUAGES C CXX)

set(ODE_SOURCE_DIR "" CACHE PATH "Pinned ODE 0.16.6 source directory")
if(NOT ODE_SOURCE_DIR OR NOT EXISTS "${ODE_SOURCE_DIR}/CMakeLists.txt")
  message(FATAL_ERROR "Set ODE_SOURCE_DIR to the verified ODE 0.16.6 source root.")
endif()

option(REANIMATED_ODE_COLLISION_BUILD_TESTS "Build the native ODE contact smoke tests" ON)

# Keep ODE private and static; only this wrapper DLL crosses the .NET boundary.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(ODE_DOUBLE_PRECISION ON CACHE BOOL "" FORCE)
set(ODE_WITH_OU ON CACHE BOOL "" FORCE)
set(ODE_WITH_DEMOS OFF CACHE BOOL "" FORCE)
set(ODE_WITH_TESTS OFF CACHE BOOL "" FORCE)
set(ODE_WITH_OPCODE OFF CACHE BOOL "" FORCE)
set(ODE_WITH_GIMPACT OFF CACHE BOOL "" FORCE)
set(ODE_WITH_LIBCCD OFF CACHE BOOL "" FORCE)

add_subdirectory("${ODE_SOURCE_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/ode" EXCLUDE_FROM_ALL)

add_library(ReAnimated.OdeCollision SHARED ode_collision.cpp)
target_include_directories(ReAnimated.OdeCollision PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(ReAnimated.OdeCollision PRIVATE ODE)
target_compile_features(ReAnimated.OdeCollision PRIVATE cxx_std_17)
set_target_properties(ReAnimated.OdeCollision PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/native"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/native"
)

  if(MSVC)
  target_compile_options(ReAnimated.OdeCollision PRIVATE
    /W4
    /experimental:deterministic
    "/pathmap:${ODE_SOURCE_DIR}=vendor/ode-0.16.6"
    "/pathmap:${CMAKE_CURRENT_SOURCE_DIR}=src/ode-collision"
  )
endif()

if(REANIMATED_ODE_COLLISION_BUILD_TESTS)
  enable_testing()
  add_executable(ode_collision_native_tests ode_collision_native_tests.cpp)
  target_link_libraries(ode_collision_native_tests PRIVATE ReAnimated.OdeCollision)
  target_compile_features(ode_collision_native_tests PRIVATE cxx_std_17)
  set_target_properties(ode_collision_native_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/native"
  )
  if(MSVC)
    target_compile_options(ode_collision_native_tests PRIVATE /W4 /WX /experimental:deterministic)
  endif()
  add_test(NAME ode_collision_native_tests COMMAND ode_collision_native_tests)
endif()
