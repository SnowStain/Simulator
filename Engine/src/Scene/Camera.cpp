#include "Camera.h"

#include <cmath>

namespace rm26::engine::scene {

void Camera::set_pose(const std::array<float, 3>& eye, const std::array<float, 3>& target, const std::array<float, 3>& up) {
    eye_ = eye;
    target_ = target;
    up_ = up;
}

void Camera::set_projection(float fov_deg, float aspect, float near_plane, float far_plane) {
    const float half_fov_rad = fov_deg * 0.5f * 3.14159265358979323846f / 180.0f;
    const float y_scale = 1.0f / std::tan(half_fov_rad);
    const float x_scale = y_scale / (aspect <= 1e-6f ? 1.0f : aspect);
    projection_ = {
        x_scale, 0.0f, 0.0f, 0.0f,
        0.0f, y_scale, 0.0f, 0.0f,
        0.0f, 0.0f, far_plane / (near_plane - far_plane), (far_plane * near_plane) / (near_plane - far_plane),
        0.0f, 0.0f, -1.0f, 0.0f
    };
}

const std::array<float, 3>& Camera::eye() const {
    return eye_;
}

const std::array<float, 3>& Camera::target() const {
    return target_;
}

const std::array<float, 16>& Camera::projection() const {
    return projection_;
}

}  // namespace rm26::engine::scene
