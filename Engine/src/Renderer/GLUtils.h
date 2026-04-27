#pragma once

#include <string>

namespace rm26::engine::renderer {

struct VertexLayout {
    int position_components = 3;
    int normal_components = 3;
    int uv_components = 2;
};

std::string read_text_file(const std::string& path);

}  // namespace rm26::engine::renderer
