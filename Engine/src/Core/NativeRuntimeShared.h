#pragma once

#include <pybind11/pybind11.h>
#include <pybind11/stl.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace py = pybind11;

namespace rm26_native {

constexpr double kPi = 3.14159265358979323846;
#if defined(_WIN32)
constexpr int kRendererFeatureLevel = 4;
#else
constexpr int kRendererFeatureLevel = 1;
#endif
#if RM26_NATIVE_HAS_BULLET
constexpr int kPhysicsFeatureLevel = 4;
#else
constexpr int kPhysicsFeatureLevel = 0;
#endif

inline double clamp_double(double value, double min_value, double max_value) {
    return std::max(min_value, std::min(value, max_value));
}

inline std::uint8_t clamp_byte(double value) {
    return static_cast<std::uint8_t>(clamp_double(value, 0.0, 255.0));
}

inline int dict_int(const py::dict& payload, const char* key, int default_value) {
    if (!payload.contains(key)) {
        return default_value;
    }
    return py::cast<int>(payload[key]);
}

inline double dict_double(const py::dict& payload, const char* key, double default_value) {
    if (!payload.contains(key)) {
        return default_value;
    }
    return py::cast<double>(payload[key]);
}

inline std::string dict_string(const py::dict& payload, const char* key, const std::string& default_value) {
    if (!payload.contains(key)) {
        return default_value;
    }
    return py::cast<std::string>(payload[key]);
}

template <typename TValue>
std::vector<TValue> bytes_to_vector(const py::object& object) {
    if (object.is_none()) {
        return {};
    }
    const std::string buffer = py::cast<std::string>(object);
    const std::size_t element_count = buffer.size() / sizeof(TValue);
    std::vector<TValue> output(element_count);
    if (!output.empty()) {
        std::memcpy(output.data(), buffer.data(), element_count * sizeof(TValue));
    }
    return output;
}

struct Vec2 {
    float x = 0.0f;
    float y = 0.0f;
};

struct Vec3 {
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

inline Vec3 operator+(const Vec3& left, const Vec3& right) {
    return {left.x + right.x, left.y + right.y, left.z + right.z};
}

inline Vec3 operator-(const Vec3& left, const Vec3& right) {
    return {left.x - right.x, left.y - right.y, left.z - right.z};
}

inline Vec3 operator*(const Vec3& value, float scale) {
    return {value.x * scale, value.y * scale, value.z * scale};
}

inline float dot(const Vec3& left, const Vec3& right) {
    return left.x * right.x + left.y * right.y + left.z * right.z;
}

inline Vec3 cross(const Vec3& left, const Vec3& right) {
    return {
        left.y * right.z - left.z * right.y,
        left.z * right.x - left.x * right.z,
        left.x * right.y - left.y * right.x,
    };
}

inline float length(const Vec3& value) {
    return std::sqrt(dot(value, value));
}

inline Vec3 normalize(const Vec3& value, const Vec3& fallback = Vec3{0.0f, 1.0f, 0.0f}) {
    const float magnitude = length(value);
    if (magnitude <= 1e-6f) {
        return fallback;
    }
    return value * (1.0f / magnitude);
}

inline Vec3 handle_vec3(const py::handle& object, const Vec3& fallback = Vec3{}) {
    if (object.is_none()) {
        return fallback;
    }
    Vec3 result = fallback;
    py::sequence sequence = py::reinterpret_borrow<py::sequence>(object);
    if (sequence.size() > 0) {
        result.x = py::cast<float>(sequence[0]);
    }
    if (sequence.size() > 1) {
        result.y = py::cast<float>(sequence[1]);
    }
    if (sequence.size() > 2) {
        result.z = py::cast<float>(sequence[2]);
    }
    return result;
}

struct Mat4 {
    std::array<float, 16> values{};

    static Mat4 identity() {
        Mat4 matrix;
        matrix.values = {
            1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f,
        };
        return matrix;
    }

    const float* data() const {
        return values.data();
    }
};

inline Mat4 multiply(const Mat4& left, const Mat4& right) {
    Mat4 result{};
    for (int row = 0; row < 4; ++row) {
        for (int column = 0; column < 4; ++column) {
            float sum = 0.0f;
            for (int inner = 0; inner < 4; ++inner) {
                sum += left.values[row * 4 + inner] * right.values[inner * 4 + column];
            }
            result.values[row * 4 + column] = sum;
        }
    }
    return result;
}

inline Mat4 translation_matrix(float tx, float ty, float tz) {
    Mat4 matrix = Mat4::identity();
    matrix.values[3] = tx;
    matrix.values[7] = ty;
    matrix.values[11] = tz;
    return matrix;
}

inline Mat4 scale_matrix(float sx, float sy, float sz) {
    Mat4 matrix = Mat4::identity();
    matrix.values[0] = sx;
    matrix.values[5] = sy;
    matrix.values[10] = sz;
    return matrix;
}

inline Mat4 rotation_y_matrix(float yaw_rad) {
    const float c = std::cos(yaw_rad);
    const float s = std::sin(yaw_rad);
    Mat4 matrix = Mat4::identity();
    matrix.values[0] = c;
    matrix.values[2] = s;
    matrix.values[8] = -s;
    matrix.values[10] = c;
    return matrix;
}

inline Mat4 object_to_matrix4(const py::object& object, const Mat4& fallback = Mat4::identity()) {
    if (object.is_none()) {
        return fallback;
    }
    Mat4 matrix = fallback;
    if (py::isinstance<py::bytes>(object) || py::isinstance<py::bytearray>(object)) {
        const std::vector<float> values = bytes_to_vector<float>(object);
        if (values.size() >= 16) {
            std::copy_n(values.begin(), 16, matrix.values.begin());
            return matrix;
        }
        return fallback;
    }
    py::sequence sequence = py::reinterpret_borrow<py::sequence>(object);
    if (sequence.size() == 16) {
        for (int index = 0; index < 16; ++index) {
            matrix.values[static_cast<std::size_t>(index)] = py::cast<float>(sequence[index]);
        }
        return matrix;
    }
    if (sequence.size() == 4) {
        int write_index = 0;
        for (const py::handle& row_handle : sequence) {
            py::sequence row = py::reinterpret_borrow<py::sequence>(row_handle);
            for (const py::handle& value_handle : row) {
                if (write_index >= 16) {
                    break;
                }
                matrix.values[static_cast<std::size_t>(write_index++)] = py::cast<float>(value_handle);
            }
        }
        if (write_index == 16) {
            return matrix;
        }
    }
    return fallback;
}

inline py::dict build_info() {
    py::dict info;
    info["module_name"] = "rm26_native";
#if defined(_WIN32)
    info["renderer_backend"] = "wgl_offscreen_opengl";
#else
    info["renderer_backend"] = "cpu_cpp_fallback";
#endif
#if RM26_NATIVE_HAS_BULLET
    info["physics_backend"] = "bullet_heightfield_bridge";
#else
    info["physics_backend"] = "bullet_cpp_unavailable";
#endif
    info["renderer_feature_level"] = kRendererFeatureLevel;
    info["physics_feature_level"] = kPhysicsFeatureLevel;
    info["has_bullet"] = py::bool_(RM26_NATIVE_HAS_BULLET == 1);
    info["has_opengl"] = py::bool_(kRendererFeatureLevel >= 4);
    return info;
}

}  // namespace rm26_native