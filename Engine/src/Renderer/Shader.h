#pragma once

#include <string>

namespace rm26::engine::renderer {

class Shader {
public:
    bool load_from_files(const std::string& vertex_path, const std::string& fragment_path);
    bool ready() const;
    const std::string& vertex_source() const;
    const std::string& fragment_source() const;

private:
    std::string vertex_source_;
    std::string fragment_source_;
    bool ready_ = false;
};

}  // namespace rm26::engine::renderer
