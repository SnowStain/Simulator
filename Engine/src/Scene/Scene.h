#pragma once

#include <string>
#include <vector>

#include "Camera.h"

namespace rm26::engine::scene {

class Scene {
public:
    void add_node(const std::string& id);
    bool has_node(const std::string& id) const;
    std::size_t node_count() const;
    Camera& camera();
    const Camera& camera() const;

private:
    std::vector<std::string> node_ids_;
    Camera camera_;
};

}  // namespace rm26::engine::scene
