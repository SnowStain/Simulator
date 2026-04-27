#pragma once

#include <pybind11/pybind11.h>

namespace rm26_native {

void register_renderer_bindings(pybind11::module_& module);

}  // namespace rm26_native