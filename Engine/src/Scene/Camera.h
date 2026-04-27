#pragma once

#include <array>

namespace rm26::engine::scene {

class Camera {
public:
    void set_pose(const std::array<float, 3>& eye, const std::array<float, 3>& target, const std::array<float, 3>& up);
    void set_projection(float fov_deg, float aspect, float near_plane, float far_plane);
    const std::array<float, 3>& eye() const;
    const std::array<float, 3>& target() const;
    const std::array<float, 16>& projection() const;

private:
    std::array<float, 3> eye_{0.0f, 0.0f, 0.0f};
    std::array<float, 3> target_{0.0f, 0.0f, -1.0f};
    std::array<float, 3> up_{0.0f, 1.0f, 0.0f};
    std::array<float, 16> projection_{
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    };
};

}  // namespace rm26::engine::scene
