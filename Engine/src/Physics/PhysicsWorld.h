#pragma once

#include <cstddef>
#include <string>
#include <unordered_map>

#include "RigidBody.h"

namespace rm26::engine::physics {

class PhysicsWorld {
public:
    bool initialize();
    void sync_body(const RigidBodyDesc& body);
    void step(double dt);
    std::size_t body_count() const;
    bool initialized() const;

private:
    bool initialized_ = false;
    std::unordered_map<std::string, RigidBodyDesc> bodies_;
};

}  // namespace rm26::engine::physics
