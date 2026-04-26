#include "Scene.h"

#include <algorithm>

namespace rm26::engine::scene {

void Scene::add_node(const std::string& id) {
    if (!has_node(id)) {
        node_ids_.push_back(id);
    }
}

bool Scene::has_node(const std::string& id) const {
    return std::find(node_ids_.begin(), node_ids_.end(), id) != node_ids_.end();
}

std::size_t Scene::node_count() const {
    return node_ids_.size();
}

Camera& Scene::camera() {
    return camera_;
}

const Camera& Scene::camera() const {
    return camera_;
}

}  // namespace rm26::engine::scene
