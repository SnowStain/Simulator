#pragma once

#include <string>

namespace rm26::engine::renderer {

class Texture {
public:
    bool load(const std::string& path);
    const std::string& path() const;
    bool ready() const;

private:
    std::string path_;
    bool ready_ = false;
};

}  // namespace rm26::engine::renderer
