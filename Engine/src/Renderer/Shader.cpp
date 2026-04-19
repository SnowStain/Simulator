#include "Shader.h"
#include "GLUtils.h"

namespace rm26::engine::renderer {

bool Shader::load_from_files(const std::string& vertex_path, const std::string& fragment_path) {
    vertex_source_ = read_text_file(vertex_path);
    fragment_source_ = read_text_file(fragment_path);
    ready_ = !vertex_source_.empty() && !fragment_source_.empty();
    return ready_;
}

bool Shader::ready() const {
    return ready_;
}

const std::string& Shader::vertex_source() const {
    return vertex_source_;
}

const std::string& Shader::fragment_source() const {
    return fragment_source_;
}

}  // namespace rm26::engine::renderer
