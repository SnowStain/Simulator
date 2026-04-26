#include "GLUtils.h"

#include <fstream>
#include <sstream>

namespace rm26::engine::renderer {

std::string read_text_file(const std::string& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input.is_open()) {
        return {};
    }
    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

}  // namespace rm26::engine::renderer
