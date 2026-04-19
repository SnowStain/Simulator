cmake_minimum_required(VERSION 3.22)

project(rm26_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Python3 REQUIRED COMPONENTS Interpreter Development.Module)
find_package(pybind11 CONFIG QUIET)

if(NOT pybind11_FOUND)
    execute_process(
        COMMAND "${Python3_EXECUTABLE}" -m pybind11 --cmakedir
        OUTPUT_VARIABLE PYBIND11_CMAKE_DIR
        ERROR_QUIET
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )
    if(PYBIND11_CMAKE_DIR)
        list(APPEND CMAKE_PREFIX_PATH "${PYBIND11_CMAKE_DIR}")
        find_package(pybind11 CONFIG REQUIRED)
    else()
        message(FATAL_ERROR "pybind11 was not found. Install it with: python -m pip install pybind11")
    endif()
endif()

find_package(OpenGL REQUIRED)

set(RM26_BULLET_TARGETS)
find_package(Bullet QUIET)
if(Bullet_FOUND)
    foreach(target_name IN ITEMS Bullet::Bullet Bullet::BulletDynamics Bullet::BulletCollision Bullet::LinearMath BulletDynamics BulletCollision LinearMath)
        if(TARGET ${target_name})
            list(APPEND RM26_BULLET_TARGETS ${target_name})
        endif()
    endforeach()
endif()

if(NOT RM26_BULLET_TARGETS)
    find_package(bullet3 CONFIG QUIET)
    foreach(target_name IN ITEMS Bullet::Bullet bullet3::BulletDynamics bullet3::BulletCollision bullet3::LinearMath Bullet::BulletDynamics Bullet::BulletCollision Bullet::LinearMath BulletDynamics BulletCollision LinearMath)
        if(TARGET ${target_name})
            list(APPEND RM26_BULLET_TARGETS ${target_name})
        endif()
    endforeach()
endif()

if(RM26_BULLET_TARGETS)
    list(REMOVE_DUPLICATES RM26_BULLET_TARGETS)
endif()

pybind11_add_module(
    rm26_native
    MODULE
    src/module.cpp
    ../Engine/src/Python/ModuleRuntime.cpp
    ../Engine/src/Renderer/NativeRenderer.cpp
    ../Engine/src/Physics/NativePhysics.cpp
)
target_include_directories(rm26_native PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../Engine/src)
target_link_libraries(rm26_native PRIVATE OpenGL::GL)
target_compile_definitions(rm26_native PRIVATE PY_SSIZE_T_CLEAN)

if(RM26_BULLET_TARGETS)
    target_link_libraries(rm26_native PRIVATE ${RM26_BULLET_TARGETS})
    target_compile_definitions(rm26_native PRIVATE RM26_NATIVE_HAS_BULLET=1)
else()
    target_compile_definitions(rm26_native PRIVATE RM26_NATIVE_HAS_BULLET=0)
endif()

if(WIN32)
    target_compile_definitions(rm26_native PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(rm26_native PRIVATE user32 gdi32)
endif()

set_target_properties(rm26_native PROPERTIES OUTPUT_NAME "rm26_native")

install(TARGETS rm26_native LIBRARY DESTINATION . RUNTIME DESTINATION .)
