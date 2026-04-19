#include "Texture.h"

#include <fstream>

namespace rm26::engine::renderer {

bool Texture::load(const std::string& path) {
    std::ifstream input(path, std::ios::binary);
    path_ = path;
    ready_ = input.good();
    return ready_;
}

const std::string& Texture::path() const {
    return path_;
}

bool Texture::ready() const {
    return ready_;
}

}  // namespace rm26::engine::renderer
