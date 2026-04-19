#include "../../Engine/src/Python/ModuleRuntime.h"

PYBIND11_MODULE(rm26_native, module) {
    rm26_native::register_module(module);
}