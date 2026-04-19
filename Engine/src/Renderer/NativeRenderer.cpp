#include "Renderer/NativeRenderer.h"

#include "Core/NativeRuntimeShared.h"

#include <fstream>
#include <limits>
#include <memory>
#include <sstream>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <GL/gl.h>
#endif

namespace {

using namespace rm26_native;

void blend_pixel(std::vector<std::uint8_t>& rgba, int width, int height, int x, int y, const std::array<std::uint8_t, 4>& color) {
    if (x < 0 || y < 0 || x >= width || y >= height) {
        return;
    }
    const std::size_t offset = (static_cast<std::size_t>(y) * static_cast<std::size_t>(width) + static_cast<std::size_t>(x)) * 4;
    const double src_alpha = static_cast<double>(color[3]) / 255.0;
    const double dst_alpha = 1.0 - src_alpha;
    rgba[offset + 0] = clamp_byte(static_cast<double>(color[0]) * src_alpha + static_cast<double>(rgba[offset + 0]) * dst_alpha);
    rgba[offset + 1] = clamp_byte(static_cast<double>(color[1]) * src_alpha + static_cast<double>(rgba[offset + 1]) * dst_alpha);
    rgba[offset + 2] = clamp_byte(static_cast<double>(color[2]) * src_alpha + static_cast<double>(rgba[offset + 2]) * dst_alpha);
    rgba[offset + 3] = 255;
}

void fill_rect(std::vector<std::uint8_t>& rgba, int width, int height, int x0, int y0, int x1, int y1, const std::array<std::uint8_t, 4>& color) {
    const int min_x = std::max(0, std::min(x0, x1));
    const int max_x = std::min(width, std::max(x0, x1));
    const int min_y = std::max(0, std::min(y0, y1));
    const int max_y = std::min(height, std::max(y0, y1));
    for (int y = min_y; y < max_y; ++y) {
        for (int x = min_x; x < max_x; ++x) {
            blend_pixel(rgba, width, height, x, y, color);
        }
    }
}

void draw_circle(std::vector<std::uint8_t>& rgba, int width, int height, int center_x, int center_y, int radius, const std::array<std::uint8_t, 4>& color) {
    const int radius_sq = radius * radius;
    for (int y = -radius; y <= radius; ++y) {
        for (int x = -radius; x <= radius; ++x) {
            if (x * x + y * y <= radius_sq) {
                blend_pixel(rgba, width, height, center_x + x, center_y + y, color);
            }
        }
    }
}

void draw_line(std::vector<std::uint8_t>& rgba, int width, int height, int x0, int y0, int x1, int y1, const std::array<std::uint8_t, 4>& color) {
    const int dx = std::abs(x1 - x0);
    const int sx = x0 < x1 ? 1 : -1;
    const int dy = -std::abs(y1 - y0);
    const int sy = y0 < y1 ? 1 : -1;
    int error = dx + dy;
    while (true) {
        blend_pixel(rgba, width, height, x0, y0, color);
        if (x0 == x1 && y0 == y1) {
            break;
        }
        const int error2 = 2 * error;
        if (error2 >= dy) {
            error += dy;
            x0 += sx;
        }
        if (error2 <= dx) {
            error += dx;
            y0 += sy;
        }
    }
}

struct RendererEntityModelSpecWheelCenter {
    float x_wu = 0.0f;
    float y_wu = 0.0f;
    float h_offset_m = 0.0f;
};

struct RendererEntityModelSpecLeg {
    bool enabled = false;
    float side_offset_wu = 0.0f;
    float upper_pair_gap_wu = 0.0f;
    float upper_half_width_wu = 0.0f;
    float upper_half_height_m = 0.0f;
    float lower_half_width_wu = 0.0f;
    float lower_half_height_m = 0.0f;
    float hinge_half_forward_wu = 0.0f;
    float hinge_half_height_m = 0.0f;
    float foot_hub_half_right_wu = 0.0f;
    float upper_anchor_x_wu = 0.0f;
    float upper_anchor_h_offset_m = 0.0f;
    float joint_x_wu = 0.0f;
    float joint_h_offset_m = 0.0f;
    float foot_x_wu = 0.0f;
    float foot_h_offset_m = 0.0f;
    std::array<float, 4> upper_color{0.42f, 0.44f, 0.48f, 0.92f};
    std::array<float, 4> lower_color{0.36f, 0.38f, 0.42f, 0.92f};
    std::array<float, 4> hinge_color{0.58f, 0.60f, 0.66f, 0.92f};
};

struct RendererEntityModelSpec {
    std::string detail_mode = "proxy";
    float body_half_length_wu = 0.5f;
    float body_half_width_wu = 0.5f;
    float body_half_height_m = 0.3f;
    std::array<float, 4> body_color{0.75f, 0.75f, 0.78f, 0.94f};

    float armor_half_width_wu = 0.14f;
    float armor_half_height_m = 0.06f;
    float armor_thickness_wu = 0.06f;
    float armor_center_offset_m = 0.0f;
    std::array<float, 4> armor_color{0.84f, 0.86f, 0.90f, 0.94f};

    bool mount_enabled = false;
    float mount_half_length_wu = 0.06f;
    float mount_half_width_wu = 0.05f;
    float mount_half_height_m = 0.04f;
    float mount_center_offset_m = 0.0f;
    std::array<float, 4> mount_color{0.38f, 0.40f, 0.44f, 0.92f};

    bool turret_enabled = false;
    float turret_half_length_wu = 0.16f;
    float turret_half_width_wu = 0.08f;
    float turret_half_height_m = 0.06f;
    float turret_offset_x_wu = 0.0f;
    float turret_offset_y_wu = 0.0f;
    float turret_center_offset_m = 0.0f;
    float turret_yaw_delta_rad = 0.0f;
    std::array<float, 4> turret_color{0.86f, 0.86f, 0.88f, 0.94f};

    bool barrel_enabled = false;
    float barrel_length_wu = 0.2f;
    float barrel_vertical_m = 0.0f;
    float barrel_half_width_wu = 0.012f;
    float barrel_half_height_m = 0.01f;
    std::array<float, 4> barrel_color{0.78f, 0.79f, 0.81f, 0.94f};

    float wheel_half_length_wu = 0.08f;
    float wheel_half_width_wu = 0.03f;
    float wheel_half_height_m = 0.08f;
    std::array<float, 4> wheel_color{0.20f, 0.21f, 0.24f, 0.94f};
    std::vector<RendererEntityModelSpecWheelCenter> wheel_centers;
    RendererEntityModelSpecLeg leg;
};

struct RendererEntityState {
    std::string id;
    std::string type;
    std::string team;
    Vec3 center{};
    Vec3 half_extents{0.5f, 0.5f, 0.5f};
    Vec3 forward_basis{1.0f, 0.0f, 0.0f};
    Vec3 right_basis{0.0f, 0.0f, 1.0f};
    Vec3 up_basis{0.0f, 1.0f, 0.0f};
    float yaw_rad = 0.0f;
    bool alive = true;
    struct Part {
        Mat4 model = Mat4::identity();
        std::array<float, 4> color{0.75f, 0.75f, 0.78f, 0.94f};
    };
    std::vector<Part> parts;
    bool has_model_spec = false;
    RendererEntityModelSpec model_spec{};
};

inline std::array<float, 4> handle_color_rgba(const py::handle& object, const std::array<float, 4>& fallback = {0.75f, 0.75f, 0.78f, 0.94f}) {
    if (object.is_none()) {
        return fallback;
    }
    std::array<float, 4> result = fallback;
    py::sequence sequence = py::reinterpret_borrow<py::sequence>(object);
    if (sequence.size() > 0) {
        result[0] = py::cast<float>(sequence[0]) / 255.0f;
    }
    if (sequence.size() > 1) {
        result[1] = py::cast<float>(sequence[1]) / 255.0f;
    }
    if (sequence.size() > 2) {
        result[2] = py::cast<float>(sequence[2]) / 255.0f;
    }
    if (sequence.size() > 3) {
        result[3] = py::cast<float>(sequence[3]) / 255.0f;
    }
    return result;
}

inline bool dict_bool(const py::dict& payload, const char* key, bool default_value) {
    if (!payload.contains(key)) {
        return default_value;
    }
    return py::cast<bool>(payload[key]);
}

Mat4 matrix_from_axes(const Vec3& center, const Vec3& x_axis, const Vec3& y_axis, const Vec3& z_axis) {
    Mat4 matrix = Mat4::identity();
    matrix.values = {
        x_axis.x, y_axis.x, z_axis.x, center.x,
        x_axis.y, y_axis.y, z_axis.y, center.y,
        x_axis.z, y_axis.z, z_axis.z, center.z,
        0.0f, 0.0f, 0.0f, 1.0f,
    };
    return matrix;
}

Vec3 rotate_horizontal(const Vec3& forward_basis, const Vec3& right_basis, float yaw_rad) {
    const float c = std::cos(yaw_rad);
    const float s = std::sin(yaw_rad);
    return {
        forward_basis.x * c + right_basis.x * s,
        forward_basis.y * c + right_basis.y * s,
        forward_basis.z * c + right_basis.z * s,
    };
}

Vec3 rotate_horizontal_right(const Vec3& forward_basis, const Vec3& right_basis, float yaw_rad) {
    const float c = std::cos(yaw_rad);
    const float s = std::sin(yaw_rad);
    return {
        right_basis.x * c - forward_basis.x * s,
        right_basis.y * c - forward_basis.y * s,
        right_basis.z * c - forward_basis.z * s,
    };
}

Vec3 local_scene_point(const RendererEntityState& entity, float x_wu, float y_wu, float h_offset_m) {
    return entity.center + entity.forward_basis * x_wu + entity.right_basis * y_wu + entity.up_basis * h_offset_m;
}

RendererEntityState::Part build_scene_box_part(
    const Vec3& center,
    const Vec3& forward_axis,
    const Vec3& right_axis,
    const Vec3& up_axis,
    float half_forward_wu,
    float half_up_m,
    float half_right_wu,
    const std::array<float, 4>& color) {
    RendererEntityState::Part part;
    part.model = matrix_from_axes(center, forward_axis * half_forward_wu, up_axis * half_up_m, right_axis * half_right_wu);
    part.color = color;
    return part;
}

void append_local_box_part(
    std::vector<RendererEntityState::Part>* parts,
    const RendererEntityState& entity,
    float local_forward_wu,
    float local_right_wu,
    float local_h_offset_m,
    float half_forward_wu,
    float half_up_m,
    float half_right_wu,
    float yaw_rad,
    const std::array<float, 4>& color) {
    const Vec3 rotated_forward = rotate_horizontal(entity.forward_basis, entity.right_basis, yaw_rad);
    const Vec3 rotated_right = rotate_horizontal_right(entity.forward_basis, entity.right_basis, yaw_rad);
    const Vec3 center = local_scene_point(entity, local_forward_wu, local_right_wu, local_h_offset_m);
    parts->push_back(build_scene_box_part(center, rotated_forward, rotated_right, entity.up_basis, half_forward_wu, half_up_m, half_right_wu, color));
}

void append_segment_part(
    std::vector<RendererEntityState::Part>* parts,
    const Vec3& start,
    const Vec3& end,
    const Vec3& side_basis,
    const Vec3& up_basis,
    float half_width_wu,
    float half_height_m,
    const std::array<float, 4>& color) {
    const Vec3 segment = end - start;
    const float segment_length = length(segment);
    if (segment_length <= 1e-6f) {
        return;
    }
    const Vec3 segment_dir = segment * (1.0f / segment_length);
    const Vec3 side_normalized = normalize(side_basis, {1.0f, 0.0f, 0.0f});
    Vec3 up_dir = cross(side_normalized, segment_dir);
    up_dir = normalize(up_dir, normalize(up_basis, {0.0f, 1.0f, 0.0f}));
    const float up_scale = length(up_basis) * half_height_m;
    RendererEntityState::Part part;
    part.model = matrix_from_axes((start + end) * 0.5f, segment * 0.5f, up_dir * up_scale, side_basis * half_width_wu);
    part.color = color;
    parts->push_back(std::move(part));
}

RendererEntityModelSpec parse_model_spec(const py::dict& payload) {
    RendererEntityModelSpec spec;
    spec.detail_mode = dict_string(payload, "detail_mode", "proxy");
    spec.body_half_length_wu = static_cast<float>(dict_double(payload, "body_half_length_wu", spec.body_half_length_wu));
    spec.body_half_width_wu = static_cast<float>(dict_double(payload, "body_half_width_wu", spec.body_half_width_wu));
    spec.body_half_height_m = static_cast<float>(dict_double(payload, "body_half_height_m", spec.body_half_height_m));
    spec.body_color = handle_color_rgba(payload.contains("body_color_rgba") ? py::reinterpret_borrow<py::object>(payload["body_color_rgba"]) : py::none(), spec.body_color);

    spec.armor_half_width_wu = static_cast<float>(dict_double(payload, "armor_half_width_wu", spec.armor_half_width_wu));
    spec.armor_half_height_m = static_cast<float>(dict_double(payload, "armor_half_height_m", spec.armor_half_height_m));
    spec.armor_thickness_wu = static_cast<float>(dict_double(payload, "armor_thickness_wu", spec.armor_thickness_wu));
    spec.armor_center_offset_m = static_cast<float>(dict_double(payload, "armor_center_offset_m", spec.armor_center_offset_m));
    spec.armor_color = handle_color_rgba(payload.contains("armor_color_rgba") ? py::reinterpret_borrow<py::object>(payload["armor_color_rgba"]) : py::none(), spec.armor_color);

    spec.mount_enabled = dict_bool(payload, "mount_enabled", false);
    spec.mount_half_length_wu = static_cast<float>(dict_double(payload, "mount_half_length_wu", spec.mount_half_length_wu));
    spec.mount_half_width_wu = static_cast<float>(dict_double(payload, "mount_half_width_wu", spec.mount_half_width_wu));
    spec.mount_half_height_m = static_cast<float>(dict_double(payload, "mount_half_height_m", spec.mount_half_height_m));
    spec.mount_center_offset_m = static_cast<float>(dict_double(payload, "mount_center_offset_m", spec.mount_center_offset_m));
    spec.mount_color = handle_color_rgba(payload.contains("mount_color_rgba") ? py::reinterpret_borrow<py::object>(payload["mount_color_rgba"]) : py::none(), spec.mount_color);

    spec.turret_enabled = dict_bool(payload, "turret_enabled", false);
    spec.turret_half_length_wu = static_cast<float>(dict_double(payload, "turret_half_length_wu", spec.turret_half_length_wu));
    spec.turret_half_width_wu = static_cast<float>(dict_double(payload, "turret_half_width_wu", spec.turret_half_width_wu));
    spec.turret_half_height_m = static_cast<float>(dict_double(payload, "turret_half_height_m", spec.turret_half_height_m));
    spec.turret_offset_x_wu = static_cast<float>(dict_double(payload, "turret_offset_x_wu", spec.turret_offset_x_wu));
    spec.turret_offset_y_wu = static_cast<float>(dict_double(payload, "turret_offset_y_wu", spec.turret_offset_y_wu));
    spec.turret_center_offset_m = static_cast<float>(dict_double(payload, "turret_center_offset_m", spec.turret_center_offset_m));
    spec.turret_yaw_delta_rad = static_cast<float>(dict_double(payload, "turret_yaw_delta_deg", 0.0) * kPi / 180.0);
    spec.turret_color = handle_color_rgba(payload.contains("turret_color_rgba") ? py::reinterpret_borrow<py::object>(payload["turret_color_rgba"]) : py::none(), spec.turret_color);

    spec.barrel_enabled = dict_bool(payload, "barrel_enabled", false);
    spec.barrel_length_wu = static_cast<float>(dict_double(payload, "barrel_length_wu", spec.barrel_length_wu));
    spec.barrel_vertical_m = static_cast<float>(dict_double(payload, "barrel_vertical_m", spec.barrel_vertical_m));
    spec.barrel_half_width_wu = static_cast<float>(dict_double(payload, "barrel_half_width_wu", spec.barrel_half_width_wu));
    spec.barrel_half_height_m = static_cast<float>(dict_double(payload, "barrel_half_height_m", spec.barrel_half_height_m));
    spec.barrel_color = handle_color_rgba(payload.contains("barrel_color_rgba") ? py::reinterpret_borrow<py::object>(payload["barrel_color_rgba"]) : py::none(), spec.barrel_color);

    spec.wheel_half_length_wu = static_cast<float>(dict_double(payload, "wheel_half_length_wu", spec.wheel_half_length_wu));
    spec.wheel_half_width_wu = static_cast<float>(dict_double(payload, "wheel_half_width_wu", spec.wheel_half_width_wu));
    spec.wheel_half_height_m = static_cast<float>(dict_double(payload, "wheel_half_height_m", spec.wheel_half_height_m));
    spec.wheel_color = handle_color_rgba(payload.contains("wheel_color_rgba") ? py::reinterpret_borrow<py::object>(payload["wheel_color_rgba"]) : py::none(), spec.wheel_color);
    if (payload.contains("wheel_centers")) {
        for (const py::handle& item : py::reinterpret_borrow<py::sequence>(payload["wheel_centers"])) {
            if (!py::isinstance<py::dict>(item)) {
                continue;
            }
            py::dict wheel = py::reinterpret_borrow<py::dict>(item);
            RendererEntityModelSpecWheelCenter center;
            center.x_wu = static_cast<float>(dict_double(wheel, "x_wu", 0.0));
            center.y_wu = static_cast<float>(dict_double(wheel, "y_wu", 0.0));
            center.h_offset_m = static_cast<float>(dict_double(wheel, "h_offset_m", 0.0));
            spec.wheel_centers.push_back(center);
        }
    }

    if (payload.contains("leg")) {
        py::dict leg_payload = py::reinterpret_borrow<py::dict>(payload["leg"]);
        spec.leg.enabled = dict_bool(leg_payload, "enabled", false);
        spec.leg.side_offset_wu = static_cast<float>(dict_double(leg_payload, "side_offset_wu", 0.0));
        spec.leg.upper_pair_gap_wu = static_cast<float>(dict_double(leg_payload, "upper_pair_gap_wu", 0.0));
        spec.leg.upper_half_width_wu = static_cast<float>(dict_double(leg_payload, "upper_half_width_wu", 0.0));
        spec.leg.upper_half_height_m = static_cast<float>(dict_double(leg_payload, "upper_half_height_m", 0.0));
        spec.leg.lower_half_width_wu = static_cast<float>(dict_double(leg_payload, "lower_half_width_wu", 0.0));
        spec.leg.lower_half_height_m = static_cast<float>(dict_double(leg_payload, "lower_half_height_m", 0.0));
        spec.leg.hinge_half_forward_wu = static_cast<float>(dict_double(leg_payload, "hinge_half_forward_wu", 0.0));
        spec.leg.hinge_half_height_m = static_cast<float>(dict_double(leg_payload, "hinge_half_height_m", 0.0));
        spec.leg.foot_hub_half_right_wu = static_cast<float>(dict_double(leg_payload, "foot_hub_half_right_wu", 0.0));
        spec.leg.upper_anchor_x_wu = static_cast<float>(dict_double(leg_payload, "upper_anchor_x_wu", 0.0));
        spec.leg.upper_anchor_h_offset_m = static_cast<float>(dict_double(leg_payload, "upper_anchor_h_offset_m", 0.0));
        spec.leg.joint_x_wu = static_cast<float>(dict_double(leg_payload, "joint_x_wu", 0.0));
        spec.leg.joint_h_offset_m = static_cast<float>(dict_double(leg_payload, "joint_h_offset_m", 0.0));
        spec.leg.foot_x_wu = static_cast<float>(dict_double(leg_payload, "foot_x_wu", 0.0));
        spec.leg.foot_h_offset_m = static_cast<float>(dict_double(leg_payload, "foot_h_offset_m", 0.0));
        spec.leg.upper_color = handle_color_rgba(leg_payload.contains("upper_color_rgba") ? py::reinterpret_borrow<py::object>(leg_payload["upper_color_rgba"]) : py::none(), spec.leg.upper_color);
        spec.leg.lower_color = handle_color_rgba(leg_payload.contains("lower_color_rgba") ? py::reinterpret_borrow<py::object>(leg_payload["lower_color_rgba"]) : py::none(), spec.leg.lower_color);
        spec.leg.hinge_color = handle_color_rgba(leg_payload.contains("hinge_color_rgba") ? py::reinterpret_borrow<py::object>(leg_payload["hinge_color_rgba"]) : py::none(), spec.leg.hinge_color);
    }
    return spec;
}

void build_parts_from_model_spec(RendererEntityState* entity) {
    entity->parts.clear();
    if (!entity->has_model_spec) {
        return;
    }
    const RendererEntityModelSpec& spec = entity->model_spec;
    const std::string detail_mode = spec.detail_mode;
    if (detail_mode == "proxy") {
        return;
    }

    append_local_box_part(
        &entity->parts,
        *entity,
        0.0f,
        0.0f,
        0.0f,
        spec.body_half_length_wu,
        spec.body_half_height_m,
        spec.body_half_width_wu,
        0.0f,
        spec.body_color);

    const std::array<std::tuple<float, float, float>, 4> armor_layout = {
        std::make_tuple(spec.body_half_length_wu + spec.armor_thickness_wu * 1.05f, 0.0f, 0.0f),
        std::make_tuple(-spec.body_half_length_wu - spec.armor_thickness_wu * 1.05f, 0.0f, static_cast<float>(kPi)),
        std::make_tuple(0.0f, spec.body_half_width_wu + spec.armor_thickness_wu * 1.05f, static_cast<float>(kPi * 0.5)),
        std::make_tuple(0.0f, -spec.body_half_width_wu - spec.armor_thickness_wu * 1.05f, static_cast<float>(-kPi * 0.5)),
    };
    for (const auto& [forward, right, yaw] : armor_layout) {
        append_local_box_part(
            &entity->parts,
            *entity,
            forward,
            right,
            spec.armor_center_offset_m,
            spec.armor_thickness_wu,
            spec.armor_half_height_m,
            spec.armor_half_width_wu,
            yaw,
            spec.armor_color);
    }

    if (spec.mount_enabled) {
        append_local_box_part(
            &entity->parts,
            *entity,
            spec.turret_offset_x_wu,
            spec.turret_offset_y_wu,
            spec.mount_center_offset_m,
            spec.mount_half_length_wu,
            spec.mount_half_height_m,
            spec.mount_half_width_wu,
            0.0f,
            spec.mount_color);
    }

    if (spec.turret_enabled) {
        append_local_box_part(
            &entity->parts,
            *entity,
            spec.turret_offset_x_wu,
            spec.turret_offset_y_wu,
            spec.turret_center_offset_m,
            spec.turret_half_length_wu,
            spec.turret_half_height_m,
            spec.turret_half_width_wu,
            spec.turret_yaw_delta_rad,
            spec.turret_color);
        if (spec.barrel_enabled) {
            const Vec3 turret_forward = rotate_horizontal(entity->forward_basis, entity->right_basis, spec.turret_yaw_delta_rad);
            const Vec3 turret_right = rotate_horizontal_right(entity->forward_basis, entity->right_basis, spec.turret_yaw_delta_rad);
            const Vec3 barrel_start = local_scene_point(*entity, spec.turret_offset_x_wu, spec.turret_offset_y_wu, spec.turret_center_offset_m);
            const Vec3 barrel_end = barrel_start + turret_forward * spec.barrel_length_wu + entity->up_basis * spec.barrel_vertical_m;
            append_segment_part(
                &entity->parts,
                barrel_start,
                barrel_end,
                turret_right,
                entity->up_basis,
                spec.barrel_half_width_wu,
                spec.barrel_half_height_m,
                spec.barrel_color);
        }
    }

    if (detail_mode == "medium") {
        return;
    }

    for (const RendererEntityModelSpecWheelCenter& wheel : spec.wheel_centers) {
        append_local_box_part(
            &entity->parts,
            *entity,
            wheel.x_wu,
            wheel.y_wu,
            wheel.h_offset_m,
            spec.wheel_half_length_wu,
            spec.wheel_half_height_m,
            spec.wheel_half_width_wu,
            0.0f,
            spec.wheel_color);
    }

    if (!spec.leg.enabled) {
        return;
    }

    for (const float side_sign : {-1.0f, 1.0f}) {
        const float side_offset = spec.leg.side_offset_wu * side_sign;
        const Vec3 upper_front = local_scene_point(*entity, spec.leg.upper_anchor_x_wu + spec.leg.upper_pair_gap_wu, side_offset, spec.leg.upper_anchor_h_offset_m);
        const Vec3 upper_rear = local_scene_point(*entity, spec.leg.upper_anchor_x_wu - spec.leg.upper_pair_gap_wu, side_offset, spec.leg.upper_anchor_h_offset_m);
        const Vec3 knee_front = local_scene_point(*entity, spec.leg.joint_x_wu + spec.leg.upper_pair_gap_wu, side_offset, spec.leg.joint_h_offset_m);
        const Vec3 knee_rear = local_scene_point(*entity, spec.leg.joint_x_wu - spec.leg.upper_pair_gap_wu, side_offset, spec.leg.joint_h_offset_m);
        const Vec3 knee_center = local_scene_point(*entity, spec.leg.joint_x_wu, side_offset, spec.leg.joint_h_offset_m);
        const Vec3 foot_center = local_scene_point(*entity, spec.leg.foot_x_wu, side_offset, spec.leg.foot_h_offset_m);

        append_segment_part(
            &entity->parts,
            upper_front,
            knee_front,
            entity->right_basis,
            entity->up_basis,
            spec.leg.upper_half_width_wu,
            spec.leg.upper_half_height_m,
            spec.leg.upper_color);
        append_segment_part(
            &entity->parts,
            upper_rear,
            knee_rear,
            entity->right_basis,
            entity->up_basis,
            spec.leg.upper_half_width_wu,
            spec.leg.upper_half_height_m,
            spec.leg.upper_color);
        append_segment_part(
            &entity->parts,
            knee_center,
            foot_center,
            entity->right_basis,
            entity->up_basis,
            spec.leg.lower_half_width_wu,
            spec.leg.lower_half_height_m,
            spec.leg.lower_color);

        const std::array<std::pair<Vec3, float>, 5> hinges = {
            std::make_pair(upper_front, spec.leg.hinge_half_forward_wu),
            std::make_pair(upper_rear, spec.leg.hinge_half_forward_wu),
            std::make_pair(knee_front, spec.leg.hinge_half_forward_wu),
            std::make_pair(knee_rear, spec.leg.hinge_half_forward_wu),
            std::make_pair(foot_center, spec.leg.foot_hub_half_right_wu),
        };
        for (const auto& [hinge_center, hinge_half_right] : hinges) {
            entity->parts.push_back(build_scene_box_part(
                hinge_center,
                entity->forward_basis,
                entity->right_basis,
                entity->up_basis,
                spec.leg.hinge_half_forward_wu,
                spec.leg.hinge_half_height_m,
                hinge_half_right,
                spec.leg.hinge_color));
        }
    }
}

struct RendererProjectileTrace {
    std::string team;
    std::vector<Vec3> points;
};

struct TerrainSceneState {
    int terrain_revision = -1;
    int grid_width = 0;
    int grid_height = 0;
    float cell_size = 1.0f;
    float height_scene_scale = 1.0f;
    float field_length_m = 28.0f;
    float field_width_m = 15.0f;
    float scene_units_per_meter_x = 1.0f;
    float scene_units_per_meter_y = 1.0f;
    float scene_height_units_per_meter = 1.0f;
    bool prefer_grid_terrain = false;
    float terrain_light_compensation = 1.08f;
    float entity_light_compensation = 1.04f;
    std::string terrain_asset_obj_path;
    std::vector<float> heights;
    std::vector<std::uint8_t> colors;
    Mat4 mvp = Mat4::identity();
    bool has_mvp = false;
};

std::vector<std::uint8_t> render_cpu_fallback(const TerrainSceneState& terrain, const std::vector<RendererEntityState>& entities, const std::vector<RendererProjectileTrace>& traces, int width, int height) {
    std::vector<std::uint8_t> frame(static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 4, 0);
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const double mix = height <= 1 ? 0.0 : static_cast<double>(y) / static_cast<double>(height - 1);
            const std::size_t offset = (static_cast<std::size_t>(y) * static_cast<std::size_t>(width) + static_cast<std::size_t>(x)) * 4;
            frame[offset + 0] = clamp_byte(12.0 + 20.0 * mix);
            frame[offset + 1] = clamp_byte(18.0 + 28.0 * mix);
            frame[offset + 2] = clamp_byte(24.0 + 36.0 * mix);
            frame[offset + 3] = 255;
        }
    }

    if (terrain.grid_width > 0 && terrain.grid_height > 0 && terrain.colors.size() >= static_cast<std::size_t>(terrain.grid_width * terrain.grid_height * 3)) {
        for (int grid_y = 0; grid_y < terrain.grid_height; ++grid_y) {
            for (int grid_x = 0; grid_x < terrain.grid_width; ++grid_x) {
                const std::size_t cell_index = static_cast<std::size_t>(grid_y) * static_cast<std::size_t>(terrain.grid_width) + static_cast<std::size_t>(grid_x);
                const std::size_t color_index = cell_index * 3;
                const double height_value = cell_index < terrain.heights.size() ? static_cast<double>(terrain.heights[cell_index]) : 0.0;
                const double shade = clamp_double(0.80 + height_value * terrain.height_scene_scale * 0.08, 0.45, 1.35);
                const std::array<std::uint8_t, 4> color = {
                    clamp_byte(static_cast<double>(terrain.colors[color_index + 0]) * shade),
                    clamp_byte(static_cast<double>(terrain.colors[color_index + 1]) * shade),
                    clamp_byte(static_cast<double>(terrain.colors[color_index + 2]) * shade),
                    255,
                };
                const int x0 = grid_x * width / std::max(terrain.grid_width, 1);
                const int x1 = (grid_x + 1) * width / std::max(terrain.grid_width, 1);
                const int y0 = grid_y * height / std::max(terrain.grid_height, 1);
                const int y1 = (grid_y + 1) * height / std::max(terrain.grid_height, 1);
                fill_rect(frame, width, height, x0, y0, x1, y1, color);
            }
        }
    }

    const double scene_span_x = std::max(1.0, static_cast<double>(terrain.grid_width));
    const double scene_span_y = std::max(1.0, static_cast<double>(terrain.grid_height));
    auto scene_to_screen = [&](const Vec3& point) {
        const int x = static_cast<int>(clamp_double((static_cast<double>(point.x) / scene_span_x + 0.5) * static_cast<double>(width - 1), 0.0, static_cast<double>(width - 1)));
        const int y = static_cast<int>(clamp_double((static_cast<double>(point.z) / scene_span_y + 0.5) * static_cast<double>(height - 1), 0.0, static_cast<double>(height - 1)));
        return std::pair<int, int>{x, y};
    };

    for (const RendererProjectileTrace& trace : traces) {
        for (std::size_t index = 1; index < trace.points.size(); ++index) {
            const auto start = scene_to_screen(trace.points[index - 1]);
            const auto end = scene_to_screen(trace.points[index]);
            const std::array<std::uint8_t, 4> color = trace.team == "blue"
                ? std::array<std::uint8_t, 4>{160, 210, 255, 225}
                : std::array<std::uint8_t, 4>{255, 192, 120, 225};
            draw_line(frame, width, height, start.first, start.second, end.first, end.second, color);
        }
    }

    for (const RendererEntityState& entity : entities) {
        const auto center = scene_to_screen(entity.center);
        const int radius = std::max(3, static_cast<int>(std::max(entity.half_extents.x, entity.half_extents.z) * width / std::max(scene_span_x, 1.0)));
        const std::array<std::uint8_t, 4> body_color = entity.alive
            ? (entity.team == "red" ? std::array<std::uint8_t, 4>{224, 86, 86, 235} : std::array<std::uint8_t, 4>{86, 142, 240, 235})
            : std::array<std::uint8_t, 4>{126, 132, 140, 210};
        draw_circle(frame, width, height, center.first, center.second, radius, body_color);
        draw_circle(frame, width, height, center.first, center.second, std::max(1, radius / 2), {240, 244, 248, 210});
    }
    return frame;
}

#if defined(_WIN32)

#ifndef APIENTRY
#define APIENTRY __stdcall
#endif
#ifndef APIENTRYP
#define APIENTRYP APIENTRY *
#endif
using GLsizeiptr = std::ptrdiff_t;
using GLintptr = std::ptrdiff_t;
using GLchar = char;

typedef void (APIENTRYP PFNGLGENVERTEXARRAYSPROC)(GLsizei n, GLuint* arrays);
typedef void (APIENTRYP PFNGLBINDVERTEXARRAYPROC)(GLuint array);
typedef void (APIENTRYP PFNGLDELETEVERTEXARRAYSPROC)(GLsizei n, const GLuint* arrays);
typedef void (APIENTRYP PFNGLGENBUFFERSPROC)(GLsizei n, GLuint* buffers);
typedef void (APIENTRYP PFNGLBINDBUFFERPROC)(GLenum target, GLuint buffer);
typedef void (APIENTRYP PFNGLBUFFERDATAPROC)(GLenum target, GLsizeiptr size, const void* data, GLenum usage);
typedef void (APIENTRYP PFNGLDELETEBUFFERSPROC)(GLsizei n, const GLuint* buffers);
typedef GLuint (APIENTRYP PFNGLCREATESHADERPROC)(GLenum type);
typedef void (APIENTRYP PFNGLSHADERSOURCEPROC)(GLuint shader, GLsizei count, const GLchar* const* string, const GLint* length);
typedef void (APIENTRYP PFNGLCOMPILESHADERPROC)(GLuint shader);
typedef void (APIENTRYP PFNGLGETSHADERIVPROC)(GLuint shader, GLenum pname, GLint* params);
typedef void (APIENTRYP PFNGLGETSHADERINFOLOGPROC)(GLuint shader, GLsizei maxLength, GLsizei* length, GLchar* infoLog);
typedef GLuint (APIENTRYP PFNGLCREATEPROGRAMPROC)();
typedef void (APIENTRYP PFNGLATTACHSHADERPROC)(GLuint program, GLuint shader);
typedef void (APIENTRYP PFNGLLINKPROGRAMPROC)(GLuint program);
typedef void (APIENTRYP PFNGLGETPROGRAMIVPROC)(GLuint program, GLenum pname, GLint* params);
typedef void (APIENTRYP PFNGLGETPROGRAMINFOLOGPROC)(GLuint program, GLsizei maxLength, GLsizei* length, GLchar* infoLog);
typedef void (APIENTRYP PFNGLUSEPROGRAMPROC)(GLuint program);
typedef void (APIENTRYP PFNGLDELETESHADERPROC)(GLuint shader);
typedef void (APIENTRYP PFNGLDELETEPROGRAMPROC)(GLuint program);
typedef GLint (APIENTRYP PFNGLGETUNIFORMLOCATIONPROC)(GLuint program, const GLchar* name);
typedef void (APIENTRYP PFNGLUNIFORMMATRIX4FVPROC)(GLint location, GLsizei count, GLboolean transpose, const GLfloat* value);
typedef void (APIENTRYP PFNGLUNIFORM4FPROC)(GLint location, GLfloat v0, GLfloat v1, GLfloat v2, GLfloat v3);
typedef void (APIENTRYP PFNGLUNIFORM3FPROC)(GLint location, GLfloat v0, GLfloat v1, GLfloat v2);
typedef void (APIENTRYP PFNGLUNIFORM1IPROC)(GLint location, GLint v0);
typedef void (APIENTRYP PFNGLUNIFORM1FPROC)(GLint location, GLfloat v0);
typedef void (APIENTRYP PFNGLENABLEVERTEXATTRIBARRAYPROC)(GLuint index);
typedef void (APIENTRYP PFNGLVERTEXATTRIBPOINTERPROC)(GLuint index, GLint size, GLenum type, GLboolean normalized, GLsizei stride, const void* pointer);
typedef void (APIENTRYP PFNGLACTIVETEXTUREPROC)(GLenum texture);
typedef void (APIENTRYP PFNGLGENFRAMEBUFFERSPROC)(GLsizei n, GLuint* ids);
typedef void (APIENTRYP PFNGLBINDFRAMEBUFFERPROC)(GLenum target, GLuint framebuffer);
typedef void (APIENTRYP PFNGLDELETEFRAMEBUFFERSPROC)(GLsizei n, const GLuint* framebuffers);
typedef GLenum (APIENTRYP PFNGLCHECKFRAMEBUFFERSTATUSPROC)(GLenum target);
typedef void (APIENTRYP PFNGLFRAMEBUFFERTEXTURE2DPROC)(GLenum target, GLenum attachment, GLenum textarget, GLuint texture, GLint level);
typedef void (APIENTRYP PFNGLGENRENDERBUFFERSPROC)(GLsizei n, GLuint* renderbuffers);
typedef void (APIENTRYP PFNGLBINDRENDERBUFFERPROC)(GLenum target, GLuint renderbuffer);
typedef void (APIENTRYP PFNGLRENDERBUFFERSTORAGEPROC)(GLenum target, GLenum internalformat, GLsizei width, GLsizei height);
typedef void (APIENTRYP PFNGLFRAMEBUFFERRENDERBUFFERPROC)(GLenum target, GLenum attachment, GLenum renderbuffertarget, GLuint renderbuffer);
typedef void (APIENTRYP PFNGLDELETERENDERBUFFERSPROC)(GLsizei n, const GLuint* renderbuffers);
typedef void (APIENTRYP PFNGLDRAWARRAYSPROC)(GLenum mode, GLint first, GLsizei count);
typedef void (APIENTRYP PFNGLDRAWELEMENTSPROC)(GLenum mode, GLsizei count, GLenum type, const void* indices);

#ifndef GL_VERTEX_SHADER
#define GL_VERTEX_SHADER 0x8B31
#endif
#ifndef GL_FRAGMENT_SHADER
#define GL_FRAGMENT_SHADER 0x8B30
#endif
#ifndef GL_COMPILE_STATUS
#define GL_COMPILE_STATUS 0x8B81
#endif
#ifndef GL_LINK_STATUS
#define GL_LINK_STATUS 0x8B82
#endif
#ifndef GL_INFO_LOG_LENGTH
#define GL_INFO_LOG_LENGTH 0x8B84
#endif
#ifndef GL_ARRAY_BUFFER
#define GL_ARRAY_BUFFER 0x8892
#endif
#ifndef GL_ELEMENT_ARRAY_BUFFER
#define GL_ELEMENT_ARRAY_BUFFER 0x8893
#endif
#ifndef GL_STATIC_DRAW
#define GL_STATIC_DRAW 0x88E4
#endif
#ifndef GL_DYNAMIC_DRAW
#define GL_DYNAMIC_DRAW 0x88E8
#endif
#ifndef GL_TEXTURE0
#define GL_TEXTURE0 0x84C0
#endif
#ifndef GL_FRAMEBUFFER
#define GL_FRAMEBUFFER 0x8D40
#endif
#ifndef GL_RENDERBUFFER
#define GL_RENDERBUFFER 0x8D41
#endif
#ifndef GL_COLOR_ATTACHMENT0
#define GL_COLOR_ATTACHMENT0 0x8CE0
#endif
#ifndef GL_DEPTH_ATTACHMENT
#define GL_DEPTH_ATTACHMENT 0x8D00
#endif
#ifndef GL_FRAMEBUFFER_COMPLETE
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5
#endif
#ifndef GL_DEPTH_COMPONENT24
#define GL_DEPTH_COMPONENT24 0x81A6
#endif
#ifndef GL_RGBA8
#define GL_RGBA8 0x8058
#endif
#ifndef GL_RGB8
#define GL_RGB8 0x8051
#endif
#ifndef GL_CLAMP_TO_EDGE
#define GL_CLAMP_TO_EDGE 0x812F
#endif

struct MeshVertex {
    float px;
    float py;
    float pz;
    float nx;
    float ny;
    float nz;
    float u;
    float v;
};

struct LineVertex {
    float px;
    float py;
    float pz;
    float r;
    float g;
    float b;
    float a;
};

struct GLFns {
    PFNGLGENVERTEXARRAYSPROC GenVertexArrays = nullptr;
    PFNGLBINDVERTEXARRAYPROC BindVertexArray = nullptr;
    PFNGLDELETEVERTEXARRAYSPROC DeleteVertexArrays = nullptr;
    PFNGLGENBUFFERSPROC GenBuffers = nullptr;
    PFNGLBINDBUFFERPROC BindBuffer = nullptr;
    PFNGLBUFFERDATAPROC BufferData = nullptr;
    PFNGLDELETEBUFFERSPROC DeleteBuffers = nullptr;
    PFNGLCREATESHADERPROC CreateShader = nullptr;
    PFNGLSHADERSOURCEPROC ShaderSource = nullptr;
    PFNGLCOMPILESHADERPROC CompileShader = nullptr;
    PFNGLGETSHADERIVPROC GetShaderiv = nullptr;
    PFNGLGETSHADERINFOLOGPROC GetShaderInfoLog = nullptr;
    PFNGLCREATEPROGRAMPROC CreateProgram = nullptr;
    PFNGLATTACHSHADERPROC AttachShader = nullptr;
    PFNGLLINKPROGRAMPROC LinkProgram = nullptr;
    PFNGLGETPROGRAMIVPROC GetProgramiv = nullptr;
    PFNGLGETPROGRAMINFOLOGPROC GetProgramInfoLog = nullptr;
    PFNGLUSEPROGRAMPROC UseProgram = nullptr;
    PFNGLDELETESHADERPROC DeleteShader = nullptr;
    PFNGLDELETEPROGRAMPROC DeleteProgram = nullptr;
    PFNGLGETUNIFORMLOCATIONPROC GetUniformLocation = nullptr;
    PFNGLUNIFORMMATRIX4FVPROC UniformMatrix4fv = nullptr;
    PFNGLUNIFORM4FPROC Uniform4f = nullptr;
    PFNGLUNIFORM3FPROC Uniform3f = nullptr;
    PFNGLUNIFORM1IPROC Uniform1i = nullptr;
    PFNGLUNIFORM1FPROC Uniform1f = nullptr;
    PFNGLENABLEVERTEXATTRIBARRAYPROC EnableVertexAttribArray = nullptr;
    PFNGLVERTEXATTRIBPOINTERPROC VertexAttribPointer = nullptr;
    PFNGLACTIVETEXTUREPROC ActiveTexture = nullptr;
    PFNGLGENFRAMEBUFFERSPROC GenFramebuffers = nullptr;
    PFNGLBINDFRAMEBUFFERPROC BindFramebuffer = nullptr;
    PFNGLDELETEFRAMEBUFFERSPROC DeleteFramebuffers = nullptr;
    PFNGLCHECKFRAMEBUFFERSTATUSPROC CheckFramebufferStatus = nullptr;
    PFNGLFRAMEBUFFERTEXTURE2DPROC FramebufferTexture2D = nullptr;
    PFNGLGENRENDERBUFFERSPROC GenRenderbuffers = nullptr;
    PFNGLBINDRENDERBUFFERPROC BindRenderbuffer = nullptr;
    PFNGLRENDERBUFFERSTORAGEPROC RenderbufferStorage = nullptr;
    PFNGLFRAMEBUFFERRENDERBUFFERPROC FramebufferRenderbuffer = nullptr;
    PFNGLDELETERENDERBUFFERSPROC DeleteRenderbuffers = nullptr;
    PFNGLDRAWARRAYSPROC DrawArrays = nullptr;
    PFNGLDRAWELEMENTSPROC DrawElements = nullptr;
};

bool is_invalid_wgl_proc(PROC proc) {
    return proc == nullptr || proc == reinterpret_cast<PROC>(0x1) || proc == reinterpret_cast<PROC>(0x2) || proc == reinterpret_cast<PROC>(0x3) || proc == reinterpret_cast<PROC>(-1);
}

class Win32OpenGLContext {
public:
    ~Win32OpenGLContext() {
        shutdown();
    }

    bool initialize(std::string* error_message) {
        if (ready_) {
            return true;
        }

        if (!register_window_class(error_message)) {
            return false;
        }

        instance_ = GetModuleHandleA(nullptr);
        hwnd_ = CreateWindowA(kWindowClassName, "RM26NativeHiddenGL", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 32, 32, nullptr, nullptr, instance_, nullptr);
        if (hwnd_ == nullptr) {
            if (error_message != nullptr) {
                *error_message = "CreateWindowA failed";
            }
            shutdown();
            return false;
        }
        hdc_ = GetDC(hwnd_);
        if (hdc_ == nullptr) {
            if (error_message != nullptr) {
                *error_message = "GetDC failed";
            }
            shutdown();
            return false;
        }

        PIXELFORMATDESCRIPTOR pixel_format{};
        pixel_format.nSize = sizeof(pixel_format);
        pixel_format.nVersion = 1;
        pixel_format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        pixel_format.iPixelType = PFD_TYPE_RGBA;
        pixel_format.cColorBits = 32;
        pixel_format.cDepthBits = 24;
        pixel_format.cStencilBits = 8;
        pixel_format.iLayerType = PFD_MAIN_PLANE;

        const int chosen_pixel_format = ChoosePixelFormat(hdc_, &pixel_format);
        if (chosen_pixel_format == 0 || !SetPixelFormat(hdc_, chosen_pixel_format, &pixel_format)) {
            if (error_message != nullptr) {
                *error_message = "SetPixelFormat failed";
            }
            shutdown();
            return false;
        }

        hglrc_ = wglCreateContext(hdc_);
        if (hglrc_ == nullptr || !wglMakeCurrent(hdc_, hglrc_)) {
            if (error_message != nullptr) {
                *error_message = "wglCreateContext failed";
            }
            shutdown();
            return false;
        }

        opengl32_module_ = LoadLibraryA("opengl32.dll");
        if (opengl32_module_ == nullptr) {
            if (error_message != nullptr) {
                *error_message = "LoadLibraryA(opengl32.dll) failed";
            }
            shutdown();
            return false;
        }

        if (!load_functions(error_message)) {
            shutdown();
            return false;
        }

        const GLubyte* version = glGetString(GL_VERSION);
        if (version == nullptr) {
            if (error_message != nullptr) {
                *error_message = "glGetString(GL_VERSION) returned null";
            }
            shutdown();
            return false;
        }
        ready_ = true;
        return true;
    }

    void make_current() const {
        if (hdc_ != nullptr && hglrc_ != nullptr) {
            wglMakeCurrent(hdc_, hglrc_);
        }
    }

    void shutdown() {
        ready_ = false;
        if (hglrc_ != nullptr) {
            wglMakeCurrent(nullptr, nullptr);
            wglDeleteContext(hglrc_);
            hglrc_ = nullptr;
        }
        if (hdc_ != nullptr && hwnd_ != nullptr) {
            ReleaseDC(hwnd_, hdc_);
            hdc_ = nullptr;
        }
        if (hwnd_ != nullptr) {
            DestroyWindow(hwnd_);
            hwnd_ = nullptr;
        }
        if (opengl32_module_ != nullptr) {
            FreeLibrary(opengl32_module_);
            opengl32_module_ = nullptr;
        }
    }

    const GLFns& gl() const {
        return gl_;
    }

private:
    static constexpr const char* kWindowClassName = "RM26NativeHiddenGLWindow";

    bool register_window_class(std::string* error_message) {
        static bool registered = false;
        if (registered) {
            return true;
        }
        WNDCLASSA window_class{};
        window_class.style = CS_OWNDC;
        window_class.lpfnWndProc = DefWindowProcA;
        window_class.hInstance = GetModuleHandleA(nullptr);
        window_class.lpszClassName = kWindowClassName;
        if (RegisterClassA(&window_class) == 0) {
            const DWORD error = GetLastError();
            if (error != ERROR_CLASS_ALREADY_EXISTS) {
                if (error_message != nullptr) {
                    *error_message = "RegisterClassA failed";
                }
                return false;
            }
        }
        registered = true;
        return true;
    }

    template <typename TProc>
    bool load_proc(TProc& destination, const char* name) {
        PROC proc = wglGetProcAddress(name);
        if (is_invalid_wgl_proc(proc)) {
            proc = GetProcAddress(opengl32_module_, name);
        }
        destination = reinterpret_cast<TProc>(proc);
        return destination != nullptr;
    }

    bool load_functions(std::string* error_message) {
        const bool ok =
            load_proc(gl_.GenVertexArrays, "glGenVertexArrays") &&
            load_proc(gl_.BindVertexArray, "glBindVertexArray") &&
            load_proc(gl_.DeleteVertexArrays, "glDeleteVertexArrays") &&
            load_proc(gl_.GenBuffers, "glGenBuffers") &&
            load_proc(gl_.BindBuffer, "glBindBuffer") &&
            load_proc(gl_.BufferData, "glBufferData") &&
            load_proc(gl_.DeleteBuffers, "glDeleteBuffers") &&
            load_proc(gl_.CreateShader, "glCreateShader") &&
            load_proc(gl_.ShaderSource, "glShaderSource") &&
            load_proc(gl_.CompileShader, "glCompileShader") &&
            load_proc(gl_.GetShaderiv, "glGetShaderiv") &&
            load_proc(gl_.GetShaderInfoLog, "glGetShaderInfoLog") &&
            load_proc(gl_.CreateProgram, "glCreateProgram") &&
            load_proc(gl_.AttachShader, "glAttachShader") &&
            load_proc(gl_.LinkProgram, "glLinkProgram") &&
            load_proc(gl_.GetProgramiv, "glGetProgramiv") &&
            load_proc(gl_.GetProgramInfoLog, "glGetProgramInfoLog") &&
            load_proc(gl_.UseProgram, "glUseProgram") &&
            load_proc(gl_.DeleteShader, "glDeleteShader") &&
            load_proc(gl_.DeleteProgram, "glDeleteProgram") &&
            load_proc(gl_.GetUniformLocation, "glGetUniformLocation") &&
            load_proc(gl_.UniformMatrix4fv, "glUniformMatrix4fv") &&
            load_proc(gl_.Uniform4f, "glUniform4f") &&
            load_proc(gl_.Uniform3f, "glUniform3f") &&
            load_proc(gl_.Uniform1i, "glUniform1i") &&
            load_proc(gl_.Uniform1f, "glUniform1f") &&
            load_proc(gl_.EnableVertexAttribArray, "glEnableVertexAttribArray") &&
            load_proc(gl_.VertexAttribPointer, "glVertexAttribPointer") &&
            load_proc(gl_.ActiveTexture, "glActiveTexture") &&
            load_proc(gl_.GenFramebuffers, "glGenFramebuffers") &&
            load_proc(gl_.BindFramebuffer, "glBindFramebuffer") &&
            load_proc(gl_.DeleteFramebuffers, "glDeleteFramebuffers") &&
            load_proc(gl_.CheckFramebufferStatus, "glCheckFramebufferStatus") &&
            load_proc(gl_.FramebufferTexture2D, "glFramebufferTexture2D") &&
            load_proc(gl_.GenRenderbuffers, "glGenRenderbuffers") &&
            load_proc(gl_.BindRenderbuffer, "glBindRenderbuffer") &&
            load_proc(gl_.RenderbufferStorage, "glRenderbufferStorage") &&
            load_proc(gl_.FramebufferRenderbuffer, "glFramebufferRenderbuffer") &&
            load_proc(gl_.DeleteRenderbuffers, "glDeleteRenderbuffers") &&
            load_proc(gl_.DrawArrays, "glDrawArrays") &&
            load_proc(gl_.DrawElements, "glDrawElements");
        if (!ok && error_message != nullptr) {
            *error_message = "OpenGL function loading failed";
        }
        return ok;
    }

    HINSTANCE instance_ = nullptr;
    HWND hwnd_ = nullptr;
    HDC hdc_ = nullptr;
    HGLRC hglrc_ = nullptr;
    HMODULE opengl32_module_ = nullptr;
    GLFns gl_{};
    bool ready_ = false;
};

struct MeshBuffer {
    GLuint vao = 0;
    GLuint vbo = 0;
    GLuint ebo = 0;
    GLsizei vertex_count = 0;
    GLsizei index_count = 0;
    GLenum primitive = GL_TRIANGLES;
    bool indexed = false;

    void release(const GLFns& gl) {
        if (ebo != 0) {
            gl.DeleteBuffers(1, &ebo);
            ebo = 0;
        }
        if (vbo != 0) {
            gl.DeleteBuffers(1, &vbo);
            vbo = 0;
        }
        if (vao != 0) {
            gl.DeleteVertexArrays(1, &vao);
            vao = 0;
        }
        vertex_count = 0;
        index_count = 0;
        indexed = false;
    }
};

struct FramebufferBundle {
    GLuint fbo = 0;
    GLuint color_texture = 0;
    GLuint depth_renderbuffer = 0;
    int width = 0;
    int height = 0;

    void release(const GLFns& gl) {
        if (depth_renderbuffer != 0) {
            gl.DeleteRenderbuffers(1, &depth_renderbuffer);
            depth_renderbuffer = 0;
        }
        if (fbo != 0) {
            gl.DeleteFramebuffers(1, &fbo);
            fbo = 0;
        }
        if (color_texture != 0) {
            glDeleteTextures(1, &color_texture);
            color_texture = 0;
        }
        width = 0;
        height = 0;
    }
};

struct ShaderProgram {
    GLuint program = 0;
    GLint mvp_location = -1;
    GLint light_dir_location = -1;
    GLint light_comp_location = -1;
    GLint color_location = -1;
    GLint texture_location = -1;

    void release(const GLFns& gl) {
        if (program != 0) {
            gl.DeleteProgram(program);
            program = 0;
        }
        mvp_location = -1;
        light_dir_location = -1;
        light_comp_location = -1;
        color_location = -1;
        texture_location = -1;
    }
};

GLuint compile_shader(const GLFns& gl, GLenum shader_type, const char* source, std::string* error_message) {
    GLuint shader = gl.CreateShader(shader_type);
    if (shader == 0) {
        if (error_message != nullptr) {
            *error_message = "glCreateShader failed";
        }
        return 0;
    }
    gl.ShaderSource(shader, 1, &source, nullptr);
    gl.CompileShader(shader);
    GLint status = GL_FALSE;
    gl.GetShaderiv(shader, GL_COMPILE_STATUS, &status);
    if (status == GL_TRUE) {
        return shader;
    }
    GLint log_length = 0;
    gl.GetShaderiv(shader, GL_INFO_LOG_LENGTH, &log_length);
    std::string log(static_cast<std::size_t>(std::max(log_length, 1)), '\0');
    gl.GetShaderInfoLog(shader, log_length, nullptr, log.data());
    gl.DeleteShader(shader);
    if (error_message != nullptr) {
        *error_message = log;
    }
    return 0;
}

bool link_program(const GLFns& gl, const char* vertex_shader_source, const char* fragment_shader_source, ShaderProgram* program, bool textured, std::string* error_message) {
    GLuint vertex_shader = compile_shader(gl, GL_VERTEX_SHADER, vertex_shader_source, error_message);
    if (vertex_shader == 0) {
        return false;
    }
    GLuint fragment_shader = compile_shader(gl, GL_FRAGMENT_SHADER, fragment_shader_source, error_message);
    if (fragment_shader == 0) {
        gl.DeleteShader(vertex_shader);
        return false;
    }
    program->program = gl.CreateProgram();
    gl.AttachShader(program->program, vertex_shader);
    gl.AttachShader(program->program, fragment_shader);
    gl.LinkProgram(program->program);
    gl.DeleteShader(vertex_shader);
    gl.DeleteShader(fragment_shader);

    GLint status = GL_FALSE;
    gl.GetProgramiv(program->program, GL_LINK_STATUS, &status);
    if (status != GL_TRUE) {
        GLint log_length = 0;
        gl.GetProgramiv(program->program, GL_INFO_LOG_LENGTH, &log_length);
        std::string log(static_cast<std::size_t>(std::max(log_length, 1)), '\0');
        gl.GetProgramInfoLog(program->program, log_length, nullptr, log.data());
        if (error_message != nullptr) {
            *error_message = log;
        }
        program->release(gl);
        return false;
    }

    program->mvp_location = gl.GetUniformLocation(program->program, "u_mvp");
    program->light_dir_location = gl.GetUniformLocation(program->program, "u_light_dir");
    program->light_comp_location = gl.GetUniformLocation(program->program, "u_light_comp");
    program->color_location = gl.GetUniformLocation(program->program, "u_color");
    program->texture_location = textured ? gl.GetUniformLocation(program->program, "u_texture") : -1;
    return true;
}

void append_box_face(std::vector<MeshVertex>& vertices, const Vec3& a, const Vec3& b, const Vec3& c, const Vec3& d, const Vec3& normal) {
    const Vec2 uv00{0.0f, 0.0f};
    const Vec2 uv10{1.0f, 0.0f};
    const Vec2 uv11{1.0f, 1.0f};
    const Vec2 uv01{0.0f, 1.0f};
    vertices.push_back({a.x, a.y, a.z, normal.x, normal.y, normal.z, uv00.x, uv00.y});
    vertices.push_back({b.x, b.y, b.z, normal.x, normal.y, normal.z, uv10.x, uv10.y});
    vertices.push_back({c.x, c.y, c.z, normal.x, normal.y, normal.z, uv11.x, uv11.y});
    vertices.push_back({a.x, a.y, a.z, normal.x, normal.y, normal.z, uv00.x, uv00.y});
    vertices.push_back({c.x, c.y, c.z, normal.x, normal.y, normal.z, uv11.x, uv11.y});
    vertices.push_back({d.x, d.y, d.z, normal.x, normal.y, normal.z, uv01.x, uv01.y});
}

void build_cube_vertices(std::vector<MeshVertex>* vertices) {
    vertices->clear();
    vertices->reserve(36);
    append_box_face(*vertices, {-1.0f, -1.0f, 1.0f}, {1.0f, -1.0f, 1.0f}, {1.0f, 1.0f, 1.0f}, {-1.0f, 1.0f, 1.0f}, {0.0f, 0.0f, 1.0f});
    append_box_face(*vertices, {1.0f, -1.0f, -1.0f}, {-1.0f, -1.0f, -1.0f}, {-1.0f, 1.0f, -1.0f}, {1.0f, 1.0f, -1.0f}, {0.0f, 0.0f, -1.0f});
    append_box_face(*vertices, {-1.0f, -1.0f, -1.0f}, {-1.0f, -1.0f, 1.0f}, {-1.0f, 1.0f, 1.0f}, {-1.0f, 1.0f, -1.0f}, {-1.0f, 0.0f, 0.0f});
    append_box_face(*vertices, {1.0f, -1.0f, 1.0f}, {1.0f, -1.0f, -1.0f}, {1.0f, 1.0f, -1.0f}, {1.0f, 1.0f, 1.0f}, {1.0f, 0.0f, 0.0f});
    append_box_face(*vertices, {-1.0f, 1.0f, 1.0f}, {1.0f, 1.0f, 1.0f}, {1.0f, 1.0f, -1.0f}, {-1.0f, 1.0f, -1.0f}, {0.0f, 1.0f, 0.0f});
    append_box_face(*vertices, {-1.0f, -1.0f, -1.0f}, {1.0f, -1.0f, -1.0f}, {1.0f, -1.0f, 1.0f}, {-1.0f, -1.0f, 1.0f}, {0.0f, -1.0f, 0.0f});
}

bool upload_mesh(const GLFns& gl, const std::vector<MeshVertex>& vertices, const std::vector<std::uint32_t>& indices, MeshBuffer* mesh) {
    mesh->release(gl);
    gl.GenVertexArrays(1, &mesh->vao);
    gl.GenBuffers(1, &mesh->vbo);
    gl.BindVertexArray(mesh->vao);
    gl.BindBuffer(GL_ARRAY_BUFFER, mesh->vbo);
    gl.BufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(vertices.size() * sizeof(MeshVertex)), vertices.data(), GL_STATIC_DRAW);
    gl.EnableVertexAttribArray(0);
    gl.VertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, sizeof(MeshVertex), reinterpret_cast<void*>(0));
    gl.EnableVertexAttribArray(1);
    gl.VertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, sizeof(MeshVertex), reinterpret_cast<void*>(sizeof(float) * 3));
    gl.EnableVertexAttribArray(2);
    gl.VertexAttribPointer(2, 2, GL_FLOAT, GL_FALSE, sizeof(MeshVertex), reinterpret_cast<void*>(sizeof(float) * 6));
    mesh->vertex_count = static_cast<GLsizei>(vertices.size());
    mesh->primitive = GL_TRIANGLES;
    if (!indices.empty()) {
        gl.GenBuffers(1, &mesh->ebo);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, mesh->ebo);
        gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, static_cast<GLsizeiptr>(indices.size() * sizeof(std::uint32_t)), indices.data(), GL_STATIC_DRAW);
        mesh->indexed = true;
        mesh->index_count = static_cast<GLsizei>(indices.size());
    }
    gl.BindVertexArray(0);
    return mesh->vao != 0 && mesh->vbo != 0;
}

bool upload_line_mesh(const GLFns& gl, const std::vector<LineVertex>& vertices, MeshBuffer* mesh) {
    mesh->release(gl);
    if (vertices.empty()) {
        return true;
    }
    gl.GenVertexArrays(1, &mesh->vao);
    gl.GenBuffers(1, &mesh->vbo);
    gl.BindVertexArray(mesh->vao);
    gl.BindBuffer(GL_ARRAY_BUFFER, mesh->vbo);
    gl.BufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(vertices.size() * sizeof(LineVertex)), vertices.data(), GL_DYNAMIC_DRAW);
    gl.EnableVertexAttribArray(0);
    gl.VertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, sizeof(LineVertex), reinterpret_cast<void*>(0));
    gl.EnableVertexAttribArray(1);
    gl.VertexAttribPointer(1, 4, GL_FLOAT, GL_FALSE, sizeof(LineVertex), reinterpret_cast<void*>(sizeof(float) * 3));
    mesh->vertex_count = static_cast<GLsizei>(vertices.size());
    mesh->primitive = GL_LINES;
    gl.BindVertexArray(0);
    return mesh->vao != 0 && mesh->vbo != 0;
}

void accumulate_triangle_normal(const Vec3& a, const Vec3& b, const Vec3& c, std::vector<Vec3>* normals, std::uint32_t ia, std::uint32_t ib, std::uint32_t ic) {
    const Vec3 normal = normalize(cross(b - a, c - a), {0.0f, 1.0f, 0.0f});
    (*normals)[ia] = (*normals)[ia] + normal;
    (*normals)[ib] = (*normals)[ib] + normal;
    (*normals)[ic] = (*normals)[ic] + normal;
}

bool build_grid_terrain_mesh(const TerrainSceneState& terrain, std::vector<MeshVertex>* vertices, std::vector<std::uint32_t>* indices) {
    if (terrain.grid_width <= 0 || terrain.grid_height <= 0) {
        return false;
    }
    const std::size_t vertex_count = static_cast<std::size_t>(terrain.grid_width) * static_cast<std::size_t>(terrain.grid_height);
    std::vector<Vec3> positions(vertex_count);
    std::vector<Vec3> normals(vertex_count, Vec3{});
    std::vector<Vec2> uvs(vertex_count);
    for (int grid_y = 0; grid_y < terrain.grid_height; ++grid_y) {
        for (int grid_x = 0; grid_x < terrain.grid_width; ++grid_x) {
            const std::size_t index = static_cast<std::size_t>(grid_y) * static_cast<std::size_t>(terrain.grid_width) + static_cast<std::size_t>(grid_x);
            const float height_value = index < terrain.heights.size() ? terrain.heights[index] * terrain.height_scene_scale : 0.0f;
            positions[index] = {
                static_cast<float>(grid_x) - static_cast<float>(terrain.grid_width) * 0.5f + 0.5f,
                height_value,
                static_cast<float>(grid_y) - static_cast<float>(terrain.grid_height) * 0.5f + 0.5f,
            };
            uvs[index] = {
                terrain.grid_width <= 1 ? 0.5f : static_cast<float>(grid_x) / static_cast<float>(terrain.grid_width - 1),
                terrain.grid_height <= 1 ? 0.5f : static_cast<float>(grid_y) / static_cast<float>(terrain.grid_height - 1),
            };
        }
    }

    indices->clear();
    for (int grid_y = 0; grid_y + 1 < terrain.grid_height; ++grid_y) {
        for (int grid_x = 0; grid_x + 1 < terrain.grid_width; ++grid_x) {
            const std::uint32_t i0 = static_cast<std::uint32_t>(grid_y * terrain.grid_width + grid_x);
            const std::uint32_t i1 = i0 + 1;
            const std::uint32_t i2 = i0 + static_cast<std::uint32_t>(terrain.grid_width);
            const std::uint32_t i3 = i2 + 1;
            indices->push_back(i0);
            indices->push_back(i2);
            indices->push_back(i1);
            indices->push_back(i1);
            indices->push_back(i2);
            indices->push_back(i3);
            accumulate_triangle_normal(positions[i0], positions[i2], positions[i1], &normals, i0, i2, i1);
            accumulate_triangle_normal(positions[i1], positions[i2], positions[i3], &normals, i1, i2, i3);
        }
    }

    vertices->clear();
    vertices->reserve(vertex_count);
    for (std::size_t index = 0; index < vertex_count; ++index) {
        const Vec3 normal = normalize(normals[index], {0.0f, 1.0f, 0.0f});
        vertices->push_back({
            positions[index].x,
            positions[index].y,
            positions[index].z,
            normal.x,
            normal.y,
            normal.z,
            uvs[index].x,
            uvs[index].y,
        });
    }
    return !vertices->empty() && !indices->empty();
}

int parse_obj_index(const std::string& token, int vertex_count) {
    const std::size_t slash_index = token.find('/');
    const std::string number = token.substr(0, slash_index);
    if (number.empty()) {
        return -1;
    }
    const int raw_index = std::stoi(number);
    if (raw_index > 0) {
        return raw_index - 1;
    }
    if (raw_index < 0) {
        return vertex_count + raw_index;
    }
    return -1;
}

bool build_obj_terrain_mesh(const TerrainSceneState& terrain, std::vector<MeshVertex>* vertices, std::vector<std::uint32_t>* indices) {
    if (terrain.terrain_asset_obj_path.empty()) {
        return false;
    }
    std::ifstream input(terrain.terrain_asset_obj_path);
    if (!input.is_open()) {
        return false;
    }

    std::vector<Vec3> raw_positions;
    std::vector<std::uint32_t> face_indices;
    std::string line;
    while (std::getline(input, line)) {
        if (line.empty() || line[0] == '#') {
            continue;
        }
        std::istringstream stream(line);
        std::string keyword;
        stream >> keyword;
        if (keyword == "v") {
            Vec3 value{};
            stream >> value.x >> value.y >> value.z;
            raw_positions.push_back(value);
            continue;
        }
        if (keyword == "f") {
            std::vector<int> polygon;
            std::string token;
            while (stream >> token) {
                const int index = parse_obj_index(token, static_cast<int>(raw_positions.size()));
                if (index >= 0 && index < static_cast<int>(raw_positions.size())) {
                    polygon.push_back(index);
                }
            }
            if (polygon.size() < 3) {
                continue;
            }
            for (std::size_t index = 1; index + 1 < polygon.size(); ++index) {
                face_indices.push_back(static_cast<std::uint32_t>(polygon[0]));
                face_indices.push_back(static_cast<std::uint32_t>(polygon[index]));
                face_indices.push_back(static_cast<std::uint32_t>(polygon[index + 1]));
            }
        }
    }

    if (raw_positions.empty() || face_indices.empty()) {
        return false;
    }

    std::vector<Vec3> positions(raw_positions.size());
    std::vector<Vec3> normals(raw_positions.size(), Vec3{});
    std::vector<Vec2> uvs(raw_positions.size());
    const float half_grid_x = static_cast<float>(terrain.grid_width) * 0.5f;
    const float half_grid_y = static_cast<float>(terrain.grid_height) * 0.5f;
    const float length_scale = terrain.field_length_m <= 1e-6f ? 1.0f : 1.0f / terrain.field_length_m;
    const float width_scale = terrain.field_width_m <= 1e-6f ? 1.0f : 1.0f / terrain.field_width_m;
    for (std::size_t index = 0; index < raw_positions.size(); ++index) {
        const Vec3& raw = raw_positions[index];
        positions[index] = {
            raw.x * terrain.scene_units_per_meter_x - half_grid_x,
            raw.y * terrain.scene_height_units_per_meter,
            raw.z * terrain.scene_units_per_meter_y - half_grid_y,
        };
        uvs[index] = {
            static_cast<float>(clamp_double(static_cast<double>(raw.x) * static_cast<double>(length_scale), 0.0, 1.0)),
            static_cast<float>(clamp_double(static_cast<double>(raw.z) * static_cast<double>(width_scale), 0.0, 1.0)),
        };
    }

    for (std::size_t index = 0; index + 2 < face_indices.size(); index += 3) {
        const std::uint32_t ia = face_indices[index + 0];
        const std::uint32_t ib = face_indices[index + 1];
        const std::uint32_t ic = face_indices[index + 2];
        accumulate_triangle_normal(positions[ia], positions[ib], positions[ic], &normals, ia, ib, ic);
    }

    vertices->clear();
    vertices->reserve(positions.size());
    for (std::size_t index = 0; index < positions.size(); ++index) {
        const Vec3 normal = normalize(normals[index], {0.0f, 1.0f, 0.0f});
        vertices->push_back({
            positions[index].x,
            positions[index].y,
            positions[index].z,
            normal.x,
            normal.y,
            normal.z,
            uvs[index].x,
            uvs[index].y,
        });
    }
    *indices = std::move(face_indices);
    return true;
}

class OffscreenOpenGLRenderer {
public:
    ~OffscreenOpenGLRenderer() {
        shutdown();
    }

    bool render(const TerrainSceneState& terrain, const std::vector<RendererEntityState>& entities, const std::vector<RendererProjectileTrace>& traces, int width, int height, std::vector<std::uint8_t>* rgba, std::string* error_message) {
        if (!initialize(error_message)) {
            return false;
        }
        context_.make_current();
        const GLFns& gl = context_.gl();

        if (!ensure_framebuffer(width, height, error_message)) {
            return false;
        }
        if (!ensure_programs(error_message)) {
            return false;
        }
        if (!ensure_cube_mesh()) {
            if (error_message != nullptr) {
                *error_message = "Cube mesh upload failed";
            }
            return false;
        }
        if (!ensure_terrain_resources(terrain, error_message)) {
            return false;
        }
        update_projectile_lines(traces);

        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer_.fbo);
        glViewport(0, 0, width, height);
        glClearColor(0.05f, 0.07f, 0.10f, 1.0f);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        glEnable(GL_DEPTH_TEST);
        glDisable(GL_CULL_FACE);
        glEnable(GL_BLEND);
        glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        const Mat4 scene_mvp = terrain.has_mvp ? terrain.mvp : Mat4::identity();
        draw_terrain(scene_mvp, terrain.terrain_light_compensation);
        draw_entities(scene_mvp, entities, terrain.entity_light_compensation);
        draw_projectiles(scene_mvp);

        std::vector<std::uint8_t> pixels(static_cast<std::size_t>(width) * static_cast<std::size_t>(height) * 4, 0);
        glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pixels.data());
        rgba->assign(pixels.size(), 0);
        const std::size_t row_bytes = static_cast<std::size_t>(width) * 4;
        for (int row = 0; row < height; ++row) {
            const std::size_t src_offset = static_cast<std::size_t>(height - 1 - row) * row_bytes;
            const std::size_t dst_offset = static_cast<std::size_t>(row) * row_bytes;
            std::memcpy(rgba->data() + dst_offset, pixels.data() + src_offset, row_bytes);
        }
        return true;
    }

private:
    bool initialize(std::string* error_message) {
        if (initialized_) {
            return true;
        }
        if (!context_.initialize(error_message)) {
            return false;
        }
        initialized_ = true;
        return true;
    }

    void shutdown() {
        if (!initialized_) {
            context_.shutdown();
            return;
        }
        context_.make_current();
        const GLFns& gl = context_.gl();
        terrain_mesh_.release(gl);
        cube_mesh_.release(gl);
        projectile_mesh_.release(gl);
        framebuffer_.release(gl);
        terrain_program_.release(gl);
        solid_program_.release(gl);
        line_program_.release(gl);
        if (terrain_texture_ != 0) {
            glDeleteTextures(1, &terrain_texture_);
            terrain_texture_ = 0;
        }
        context_.shutdown();
        initialized_ = false;
    }

    bool ensure_programs(std::string* error_message) {
        if (terrain_program_.program != 0 && solid_program_.program != 0 && line_program_.program != 0) {
            return true;
        }
        const GLFns& gl = context_.gl();
        static const char* terrain_vertex_shader = R"GLSL(
            #version 330
            layout(location = 0) in vec3 in_position;
            layout(location = 1) in vec3 in_normal;
            layout(location = 2) in vec2 in_uv;
            uniform mat4 u_mvp;
            out vec3 v_normal;
            out vec2 v_uv;
            void main() {
                gl_Position = u_mvp * vec4(in_position, 1.0);
                v_normal = in_normal;
                v_uv = in_uv;
            }
        )GLSL";
        static const char* terrain_fragment_shader = R"GLSL(
            #version 330
            in vec3 v_normal;
            in vec2 v_uv;
            uniform sampler2D u_texture;
            uniform vec3 u_light_dir;
            uniform float u_light_comp;
            out vec4 fragColor;
            void main() {
                vec3 base = texture(u_texture, v_uv).rgb;
                float light = (0.36 + max(dot(normalize(v_normal), normalize(u_light_dir)), 0.0) * 0.64) * u_light_comp;
                light = clamp(light, 0.22, 1.35);
                fragColor = vec4(base * light, 1.0);
            }
        )GLSL";
        static const char* solid_vertex_shader = R"GLSL(
            #version 330
            layout(location = 0) in vec3 in_position;
            layout(location = 1) in vec3 in_normal;
            uniform mat4 u_mvp;
            out vec3 v_normal;
            void main() {
                gl_Position = u_mvp * vec4(in_position, 1.0);
                v_normal = in_normal;
            }
        )GLSL";
        static const char* solid_fragment_shader = R"GLSL(
            #version 330
            in vec3 v_normal;
            uniform vec4 u_color;
            uniform vec3 u_light_dir;
            uniform float u_light_comp;
            out vec4 fragColor;
            void main() {
                float light = (0.32 + max(dot(normalize(v_normal), normalize(u_light_dir)), 0.0) * 0.68) * u_light_comp;
                light = clamp(light, 0.20, 1.28);
                fragColor = vec4(u_color.rgb * light, u_color.a);
            }
        )GLSL";
        static const char* line_vertex_shader = R"GLSL(
            #version 330
            layout(location = 0) in vec3 in_position;
            layout(location = 1) in vec4 in_color;
            uniform mat4 u_mvp;
            out vec4 v_color;
            void main() {
                gl_Position = u_mvp * vec4(in_position, 1.0);
                v_color = in_color;
            }
        )GLSL";
        static const char* line_fragment_shader = R"GLSL(
            #version 330
            in vec4 v_color;
            out vec4 fragColor;
            void main() {
                fragColor = v_color;
            }
        )GLSL";

        if (!link_program(gl, terrain_vertex_shader, terrain_fragment_shader, &terrain_program_, true, error_message)) {
            return false;
        }
        if (!link_program(gl, solid_vertex_shader, solid_fragment_shader, &solid_program_, false, error_message)) {
            return false;
        }
        if (!link_program(gl, line_vertex_shader, line_fragment_shader, &line_program_, false, error_message)) {
            return false;
        }
        return true;
    }

    bool ensure_framebuffer(int width, int height, std::string* error_message) {
        if (framebuffer_.fbo != 0 && framebuffer_.width == width && framebuffer_.height == height) {
            return true;
        }
        const GLFns& gl = context_.gl();
        framebuffer_.release(gl);
        gl.GenFramebuffers(1, &framebuffer_.fbo);
        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer_.fbo);

        glGenTextures(1, &framebuffer_.color_texture);
        glBindTexture(GL_TEXTURE_2D, framebuffer_.color_texture);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, framebuffer_.color_texture, 0);

        gl.GenRenderbuffers(1, &framebuffer_.depth_renderbuffer);
        gl.BindRenderbuffer(GL_RENDERBUFFER, framebuffer_.depth_renderbuffer);
        gl.RenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, width, height);
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, framebuffer_.depth_renderbuffer);

        const GLenum status = gl.CheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE) {
            if (error_message != nullptr) {
                *error_message = "Framebuffer is incomplete";
            }
            framebuffer_.release(gl);
            return false;
        }

        framebuffer_.width = width;
        framebuffer_.height = height;
        return true;
    }

    bool ensure_cube_mesh() {
        if (cube_mesh_.vao != 0) {
            return true;
        }
        std::vector<MeshVertex> vertices;
        build_cube_vertices(&vertices);
        std::vector<std::uint32_t> indices;
        return upload_mesh(context_.gl(), vertices, indices, &cube_mesh_);
    }

    bool ensure_terrain_resources(const TerrainSceneState& terrain, std::string* error_message) {
        const bool terrain_changed = terrain_revision_ != terrain.terrain_revision
            || terrain_grid_width_ != terrain.grid_width
            || terrain_grid_height_ != terrain.grid_height
            || terrain_asset_path_ != terrain.terrain_asset_obj_path
            || std::abs(terrain_scene_units_per_meter_x_ - terrain.scene_units_per_meter_x) > 1e-5f
            || std::abs(terrain_scene_units_per_meter_y_ - terrain.scene_units_per_meter_y) > 1e-5f
            || std::abs(terrain_scene_height_units_per_meter_ - terrain.scene_height_units_per_meter) > 1e-5f;

        if (terrain_changed) {
            std::vector<MeshVertex> vertices;
            std::vector<std::uint32_t> indices;
            bool built = false;
            if (terrain.prefer_grid_terrain) {
                built = build_grid_terrain_mesh(terrain, &vertices, &indices);
            }
            if (!built) {
                built = build_obj_terrain_mesh(terrain, &vertices, &indices);
            }
            if (!built) {
                built = build_grid_terrain_mesh(terrain, &vertices, &indices);
            }
            if (!built || !upload_mesh(context_.gl(), vertices, indices, &terrain_mesh_)) {
                if (error_message != nullptr) {
                    *error_message = "Terrain mesh build/upload failed";
                }
                return false;
            }
            terrain_revision_ = terrain.terrain_revision;
            terrain_grid_width_ = terrain.grid_width;
            terrain_grid_height_ = terrain.grid_height;
            terrain_asset_path_ = terrain.terrain_asset_obj_path;
            terrain_scene_units_per_meter_x_ = terrain.scene_units_per_meter_x;
            terrain_scene_units_per_meter_y_ = terrain.scene_units_per_meter_y;
            terrain_scene_height_units_per_meter_ = terrain.scene_height_units_per_meter;
        }

        update_terrain_texture(terrain);
        return true;
    }

    void update_terrain_texture(const TerrainSceneState& terrain) {
        if (terrain_texture_ == 0) {
            glGenTextures(1, &terrain_texture_);
        }
        const int texture_width = std::max(1, terrain.grid_width);
        const int texture_height = std::max(1, terrain.grid_height);
        std::vector<std::uint8_t> texture_data;
        if (terrain.colors.size() >= static_cast<std::size_t>(texture_width * texture_height * 3)) {
            texture_data = terrain.colors;
        } else {
            texture_data.assign(static_cast<std::size_t>(texture_width * texture_height * 3), 172);
        }
        glBindTexture(GL_TEXTURE_2D, terrain_texture_);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGB8, texture_width, texture_height, 0, GL_RGB, GL_UNSIGNED_BYTE, texture_data.data());
    }

    void update_projectile_lines(const std::vector<RendererProjectileTrace>& traces) {
        std::vector<LineVertex> vertices;
        for (const RendererProjectileTrace& trace : traces) {
            const std::array<float, 4> color = trace.team == "blue"
                ? std::array<float, 4>{0.66f, 0.83f, 1.00f, 0.92f}
                : std::array<float, 4>{1.00f, 0.78f, 0.45f, 0.92f};
            for (std::size_t index = 1; index < trace.points.size(); ++index) {
                const Vec3& start = trace.points[index - 1];
                const Vec3& end = trace.points[index];
                vertices.push_back({start.x, start.y, start.z, color[0], color[1], color[2], color[3]});
                vertices.push_back({end.x, end.y, end.z, color[0], color[1], color[2], color[3]});
            }
        }
        upload_line_mesh(context_.gl(), vertices, &projectile_mesh_);
    }

    void draw_terrain(const Mat4& mvp, float light_compensation) {
        const GLFns& gl = context_.gl();
        gl.UseProgram(terrain_program_.program);
        gl.UniformMatrix4fv(terrain_program_.mvp_location, 1, GL_TRUE, mvp.data());
        gl.Uniform3f(terrain_program_.light_dir_location, 0.35f, 0.92f, 0.28f);
        if (terrain_program_.light_comp_location >= 0) {
            gl.Uniform1f(terrain_program_.light_comp_location, light_compensation);
        }
        if (terrain_program_.texture_location >= 0) {
            gl.ActiveTexture(GL_TEXTURE0);
            glBindTexture(GL_TEXTURE_2D, terrain_texture_);
            gl.Uniform1i(terrain_program_.texture_location, 0);
        }
        gl.BindVertexArray(terrain_mesh_.vao);
        if (terrain_mesh_.indexed) {
            gl.DrawElements(terrain_mesh_.primitive, terrain_mesh_.index_count, GL_UNSIGNED_INT, nullptr);
        } else {
            gl.DrawArrays(terrain_mesh_.primitive, 0, terrain_mesh_.vertex_count);
        }
        gl.BindVertexArray(0);
    }

    void draw_entities(const Mat4& scene_mvp, const std::vector<RendererEntityState>& entities, float light_compensation) {
        const GLFns& gl = context_.gl();
        gl.UseProgram(solid_program_.program);
        gl.Uniform3f(solid_program_.light_dir_location, 0.25f, 0.90f, 0.35f);
        if (solid_program_.light_comp_location >= 0) {
            gl.Uniform1f(solid_program_.light_comp_location, light_compensation);
        }
        gl.BindVertexArray(cube_mesh_.vao);
        for (const RendererEntityState& entity : entities) {
            if (!entity.parts.empty()) {
                for (const RendererEntityState::Part& part : entity.parts) {
                    const Mat4 entity_mvp = multiply(scene_mvp, part.model);
                    gl.UniformMatrix4fv(solid_program_.mvp_location, 1, GL_TRUE, entity_mvp.data());
                    if (!entity.alive) {
                        gl.Uniform4f(solid_program_.color_location, 0.45f, 0.48f, 0.52f, 0.86f);
                    } else {
                        gl.Uniform4f(solid_program_.color_location, part.color[0], part.color[1], part.color[2], part.color[3]);
                    }
                    gl.DrawArrays(GL_TRIANGLES, 0, cube_mesh_.vertex_count);
                }
                continue;
            }
            const Mat4 model = multiply(
                translation_matrix(entity.center.x, entity.center.y, entity.center.z),
                multiply(rotation_y_matrix(entity.yaw_rad), scale_matrix(entity.half_extents.x, entity.half_extents.y, entity.half_extents.z))
            );
            const Mat4 entity_mvp = multiply(scene_mvp, model);
            gl.UniformMatrix4fv(solid_program_.mvp_location, 1, GL_TRUE, entity_mvp.data());
            if (!entity.alive) {
                gl.Uniform4f(solid_program_.color_location, 0.45f, 0.48f, 0.52f, 0.86f);
            } else if (entity.team == "red") {
                gl.Uniform4f(solid_program_.color_location, 0.88f, 0.36f, 0.34f, 0.94f);
            } else {
                gl.Uniform4f(solid_program_.color_location, 0.34f, 0.56f, 0.94f, 0.94f);
            }
            gl.DrawArrays(GL_TRIANGLES, 0, cube_mesh_.vertex_count);
        }
        gl.BindVertexArray(0);
    }

    void draw_projectiles(const Mat4& scene_mvp) {
        if (projectile_mesh_.vao == 0 || projectile_mesh_.vertex_count <= 0) {
            return;
        }
        const GLFns& gl = context_.gl();
        gl.UseProgram(line_program_.program);
        gl.UniformMatrix4fv(line_program_.mvp_location, 1, GL_TRUE, scene_mvp.data());
        glLineWidth(2.0f);
        gl.BindVertexArray(projectile_mesh_.vao);
        gl.DrawArrays(GL_LINES, 0, projectile_mesh_.vertex_count);
        gl.BindVertexArray(0);
    }

    Win32OpenGLContext context_;
    MeshBuffer terrain_mesh_;
    MeshBuffer cube_mesh_;
    MeshBuffer projectile_mesh_;
    FramebufferBundle framebuffer_;
    ShaderProgram terrain_program_;
    ShaderProgram solid_program_;
    ShaderProgram line_program_;
    GLuint terrain_texture_ = 0;
    int terrain_revision_ = std::numeric_limits<int>::min();
    int terrain_grid_width_ = 0;
    int terrain_grid_height_ = 0;
    float terrain_scene_units_per_meter_x_ = 0.0f;
    float terrain_scene_units_per_meter_y_ = 0.0f;
    float terrain_scene_height_units_per_meter_ = 0.0f;
    std::string terrain_asset_path_;
    bool initialized_ = false;
};

#endif

class NativeRendererBridge {
public:
    explicit NativeRendererBridge(py::dict config) : config_(std::move(config)) {}

    void set_scene(py::dict scene) {
        terrain_.terrain_revision = dict_int(scene, "terrain_revision", 0);
        terrain_.grid_width = dict_int(scene, "grid_width", 0);
        terrain_.grid_height = dict_int(scene, "grid_height", 0);
        terrain_.cell_size = static_cast<float>(dict_double(scene, "cell_size", 1.0));
        terrain_.height_scene_scale = static_cast<float>(dict_double(scene, "height_scene_scale", 1.0));
        terrain_.field_length_m = static_cast<float>(dict_double(scene, "field_length_m", 28.0));
        terrain_.field_width_m = static_cast<float>(dict_double(scene, "field_width_m", 15.0));
        terrain_.scene_units_per_meter_x = static_cast<float>(dict_double(scene, "scene_units_per_meter_x", 1.0));
        terrain_.scene_units_per_meter_y = static_cast<float>(dict_double(scene, "scene_units_per_meter_y", 1.0));
        terrain_.scene_height_units_per_meter = static_cast<float>(dict_double(scene, "scene_height_units_per_meter", terrain_.height_scene_scale));
        terrain_.prefer_grid_terrain = dict_bool(scene, "prefer_grid_terrain", false);
        terrain_.terrain_light_compensation = static_cast<float>(clamp_double(dict_double(scene, "terrain_light_compensation", 1.08), 0.70, 1.40));
        terrain_.entity_light_compensation = static_cast<float>(clamp_double(dict_double(scene, "entity_light_compensation", 1.04), 0.70, 1.35));
        terrain_.terrain_asset_obj_path = dict_string(scene, "terrain_asset_obj_path", "");
        terrain_.heights = bytes_to_vector<float>(scene.contains("height_bytes") ? py::reinterpret_borrow<py::object>(scene["height_bytes"]) : py::none());
        terrain_.colors = bytes_to_vector<std::uint8_t>(scene.contains("color_bytes") ? py::reinterpret_borrow<py::object>(scene["color_bytes"]) : py::none());

        terrain_.mvp = Mat4::identity();
        terrain_.has_mvp = false;
        if (scene.contains("camera")) {
            py::dict camera = py::reinterpret_borrow<py::dict>(scene["camera"]);
            if (camera.contains("mvp_bytes")) {
                terrain_.mvp = object_to_matrix4(py::reinterpret_borrow<py::object>(camera["mvp_bytes"]), Mat4::identity());
                terrain_.has_mvp = true;
            } else if (camera.contains("mvp")) {
                terrain_.mvp = object_to_matrix4(py::reinterpret_borrow<py::object>(camera["mvp"]), Mat4::identity());
                terrain_.has_mvp = true;
            }
        }

        entities_.clear();
        if (scene.contains("entities")) {
            for (const py::handle& item : py::reinterpret_borrow<py::list>(scene["entities"])) {
                py::dict entity = py::reinterpret_borrow<py::dict>(item);
                RendererEntityState state;
                const py::object scene_position = entity.contains("scene_position")
                    ? py::reinterpret_borrow<py::object>(entity["scene_position"])
                    : py::none();
                const py::object half_extents = entity.contains("half_extents")
                    ? py::reinterpret_borrow<py::object>(entity["half_extents"])
                    : py::none();
                state.id = dict_string(entity, "id", "");
                state.type = dict_string(entity, "type", "robot");
                state.team = dict_string(entity, "team", "neutral");
                state.center = handle_vec3(scene_position);
                state.half_extents = handle_vec3(half_extents, {0.5f, 0.5f, 0.5f});
                state.yaw_rad = static_cast<float>(dict_double(entity, "yaw_deg", 0.0) * kPi / 180.0);
                state.alive = entity.contains("alive") ? py::cast<bool>(entity["alive"]) : true;
                const Vec3 fallback_forward{std::cos(state.yaw_rad), 0.0f, std::sin(state.yaw_rad)};
                const Vec3 fallback_right{-std::sin(state.yaw_rad), 0.0f, std::cos(state.yaw_rad)};
                state.forward_basis = handle_vec3(entity.contains("basis_forward") ? py::reinterpret_borrow<py::object>(entity["basis_forward"]) : py::none(), fallback_forward);
                state.right_basis = handle_vec3(entity.contains("basis_right") ? py::reinterpret_borrow<py::object>(entity["basis_right"]) : py::none(), fallback_right);
                state.up_basis = handle_vec3(entity.contains("basis_up") ? py::reinterpret_borrow<py::object>(entity["basis_up"]) : py::none(), {0.0f, 1.0f, 0.0f});
                if (length(state.forward_basis) <= 1e-6f) {
                    state.forward_basis = fallback_forward;
                }
                if (length(state.right_basis) <= 1e-6f) {
                    state.right_basis = fallback_right;
                }
                if (length(state.up_basis) <= 1e-6f) {
                    state.up_basis = {0.0f, 1.0f, 0.0f};
                }

                if (entity.contains("model_spec") && py::isinstance<py::dict>(entity["model_spec"])) {
                    state.model_spec = parse_model_spec(py::reinterpret_borrow<py::dict>(entity["model_spec"]));
                    state.has_model_spec = true;
                    build_parts_from_model_spec(&state);
                } else if (entity.contains("parts")) {
                    for (const py::handle& part_item : py::reinterpret_borrow<py::list>(entity["parts"])) {
                        py::dict part = py::reinterpret_borrow<py::dict>(part_item);
                        const py::object model_matrix = part.contains("model_matrix_bytes")
                            ? py::reinterpret_borrow<py::object>(part["model_matrix_bytes"])
                            : (part.contains("model_matrix") ? py::reinterpret_borrow<py::object>(part["model_matrix"]) : py::none());
                        const py::object color_rgba = part.contains("color_rgba")
                            ? py::reinterpret_borrow<py::object>(part["color_rgba"])
                            : py::none();
                        RendererEntityState::Part native_part;
                        native_part.model = object_to_matrix4(model_matrix, Mat4::identity());
                        native_part.color = handle_color_rgba(color_rgba);
                        state.parts.push_back(std::move(native_part));
                    }
                }
                entities_.push_back(std::move(state));
            }
        }

        traces_.clear();
        if (scene.contains("projectile_traces")) {
            for (const py::handle& item : py::reinterpret_borrow<py::list>(scene["projectile_traces"])) {
                py::dict trace = py::reinterpret_borrow<py::dict>(item);
                RendererProjectileTrace state;
                state.team = dict_string(trace, "team", "neutral");
                if (trace.contains("points")) {
                    for (const py::handle& point : py::reinterpret_borrow<py::list>(trace["points"])) {
                        state.points.push_back(handle_vec3(point));
                    }
                }
                if (!state.points.empty()) {
                    traces_.push_back(std::move(state));
                }
            }
        }
    }

    py::bytes render_rgba(int width, int height) {
#if defined(_WIN32)
        std::vector<std::uint8_t> frame;
        std::string error_message;
        if (renderer_ == nullptr) {
            renderer_ = std::make_unique<OffscreenOpenGLRenderer>();
        }
        if (renderer_->render(terrain_, entities_, traces_, width, height, &frame, &error_message)) {
            return py::bytes(reinterpret_cast<const char*>(frame.data()), static_cast<py::ssize_t>(frame.size()));
        }
#endif
        const std::vector<std::uint8_t> fallback_frame = render_cpu_fallback(terrain_, entities_, traces_, width, height);
        return py::bytes(reinterpret_cast<const char*>(fallback_frame.data()), static_cast<py::ssize_t>(fallback_frame.size()));
    }

    py::dict build_info() const {
        return rm26_native::build_info();
    }

private:
    py::dict config_;
    TerrainSceneState terrain_{};
    std::vector<RendererEntityState> entities_;
    std::vector<RendererProjectileTrace> traces_;
#if defined(_WIN32)
    std::unique_ptr<OffscreenOpenGLRenderer> renderer_;
#endif
};

}  // namespace

namespace rm26_native {

void register_renderer_bindings(py::module_& module) {
    py::class_<NativeRendererBridge>(module, "NativeRendererBridge")
        .def(py::init<py::dict>(), py::arg("config") = py::dict())
        .def("set_scene", &NativeRendererBridge::set_scene)
        .def("render_rgba", &NativeRendererBridge::render_rgba)
        .def("build_info", &NativeRendererBridge::build_info);
}

}  // namespace rm26_native