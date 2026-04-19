#include "Application.h"

namespace rm26::engine {

bool Application::initialize() {
    initialized_ = true;
    last_dt_ = 0.0;
    return true;
}

void Application::update(double dt) {
    last_dt_ = dt;
}

void Application::render() {
}

bool Application::initialized() const {
    return initialized_;
}

}  // namespace rm26::engine
