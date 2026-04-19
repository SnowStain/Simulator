#include "Window.h"

namespace rm26::engine::core {

Window::Window(WindowDesc desc) : desc_(std::move(desc)) {
}

bool Window::initialize() {
    initialized_ = true;
    return true;
}

bool Window::initialized() const {
    return initialized_;
}

const WindowDesc& Window::desc() const {
    return desc_;
}

}  // namespace rm26::engine::core
