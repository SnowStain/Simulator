#include "PhysicsWorld.h"

namespace rm26::engine::physics {

bool PhysicsWorld::initialize() {
    initialized_ = true;
    return true;
}

void PhysicsWorld::sync_body(const RigidBodyDesc& body) {
    bodies_[body.id] = body;
}

void PhysicsWorld::step(double dt) {
    for (auto& [id, body] : bodies_) {
        body.position[0] += body.velocity[0] * dt;
        body.position[1] += body.velocity[1] * dt;
        body.position[2] += body.velocity[2] * dt;
    }
}

std::size_t PhysicsWorld::body_count() const {
    return bodies_.size();
}

bool PhysicsWorld::initialized() const {
    return initialized_;
}

}  // namespace rm26::engine::physics
