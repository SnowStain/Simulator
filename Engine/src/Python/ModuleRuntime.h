#pragma once

#include <pybind11/pybind11.h>

namespace rm26_native {

void register_module(pybind11::module_& module);

}  // namespace rm26_native