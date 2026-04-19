#include "ModuleRuntime.h"

#include "Core/NativeRuntimeShared.h"
#include "Physics/NativePhysics.h"
#include "Renderer/NativeRenderer.h"

namespace rm26_native {

void register_module(py::module_& module) {
    module.doc() = "RM26 native OpenGL renderer and Bullet bridge";
    module.def("build_info", &build_info);
    register_renderer_bindings(module);
    register_physics_bindings(module);
}

}  // namespace rm26_native