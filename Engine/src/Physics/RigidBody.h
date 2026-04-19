#pragma once

#include <array>
#include <string>

namespace rm26::engine::physics {

struct RigidBodyDesc {
    std::string id;
    double mass = 0.0;
    std::array<double, 3> half_extents{0.4, 0.3, 0.1};
    std::array<double, 3> position{0.0, 0.0, 0.0};
    std::array<double, 3> velocity{0.0, 0.0, 0.0};
    double yaw_deg = 0.0;
};

}  // namespace rm26::engine::physics
