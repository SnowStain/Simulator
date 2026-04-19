#include "Physics/NativePhysics.h"

#include "Core/NativeRuntimeShared.h"

#include <memory>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>

#if RM26_NATIVE_HAS_BULLET
#include <btBulletDynamicsCommon.h>
#include <BulletCollision/CollisionShapes/btHeightfieldTerrainShape.h>
#endif

namespace {

using namespace rm26_native;

struct PhysicsBodyState {
    std::string id;
    double half_x = 0.0;
    double half_y = 0.0;
    double half_z = 0.0;
    double mass = 0.0;
    double x = 0.0;
    double y = 0.0;
    double z = 0.0;
    double vx = 0.0;
    double vy = 0.0;
    double vz = 0.0;
    double angle_deg = 0.0;
    double angular_velocity_deg = 0.0;
#if RM26_NATIVE_HAS_BULLET
    std::unique_ptr<btCollisionShape> shape;
    std::unique_ptr<btDefaultMotionState> motion_state;
    std::unique_ptr<btRigidBody> body;
#endif
};

class NativePhysicsBridge {
public:
    explicit NativePhysicsBridge(py::dict config) : config_(std::move(config)) {
        if (config_.contains("physics")) {
            const py::dict physics = py::reinterpret_borrow<py::dict>(config_["physics"]);
            fixed_time_step_ = dict_double(physics, "bullet_fixed_time_step_sec", dict_double(physics, "pybullet_fixed_time_step_sec", 1.0 / 120.0));
            gravity_ = dict_double(physics, "jump_gravity_mps2", 9.8);
        }
#if RM26_NATIVE_HAS_BULLET
        collision_configuration_ = std::make_unique<btDefaultCollisionConfiguration>();
        dispatcher_ = std::make_unique<btCollisionDispatcher>(collision_configuration_.get());
        broadphase_ = std::make_unique<btDbvtBroadphase>();
        solver_ = std::make_unique<btSequentialImpulseConstraintSolver>();
        dynamics_world_ = std::make_unique<btDiscreteDynamicsWorld>(dispatcher_.get(), broadphase_.get(), solver_.get(), collision_configuration_.get());
        dynamics_world_->setGravity(btVector3(0.0, 0.0, -gravity_));
#endif
    }

    ~NativePhysicsBridge() {
        shutdown();
    }

    void shutdown() {
#if RM26_NATIVE_HAS_BULLET
        if (dynamics_world_ != nullptr) {
            for (auto& [id, state] : bodies_) {
                if (state.body != nullptr) {
                    dynamics_world_->removeRigidBody(state.body.get());
                }
            }
            if (terrain_body_ != nullptr) {
                dynamics_world_->removeRigidBody(terrain_body_.get());
            }
        }
#endif
        bodies_.clear();
#if RM26_NATIVE_HAS_BULLET
        terrain_body_.reset();
        terrain_motion_state_.reset();
        terrain_shape_.reset();
        dynamics_world_.reset();
        solver_.reset();
        broadphase_.reset();
        dispatcher_.reset();
        collision_configuration_.reset();
#endif
    }

    void set_terrain(py::dict terrain) {
        terrain_raster_version_ = dict_int(terrain, "raster_version", -1);
        terrain_grid_width_ = dict_int(terrain, "runtime_grid_width", 0);
        terrain_grid_height_ = dict_int(terrain, "runtime_grid_height", 0);
        terrain_cell_width_m_ = dict_double(terrain, "runtime_cell_width_m", 0.1);
        terrain_cell_height_m_ = dict_double(terrain, "runtime_cell_height_m", 0.1);
        terrain_heights_ = bytes_to_vector<float>(terrain.contains("height_bytes") ? py::reinterpret_borrow<py::object>(terrain["height_bytes"]) : py::none());
#if RM26_NATIVE_HAS_BULLET
        rebuild_terrain();
#endif
    }

    void sync_entities(py::list entities) {
        std::unordered_set<std::string> active_ids;
        for (const py::handle& item : entities) {
            py::dict entity = py::reinterpret_borrow<py::dict>(item);
            PhysicsBodyState desired;
            const py::object position_object = entity.contains("position")
                ? py::reinterpret_borrow<py::object>(entity["position"])
                : py::none();
            const py::object velocity_object = entity.contains("velocity")
                ? py::reinterpret_borrow<py::object>(entity["velocity"])
                : py::none();
            const py::object half_extents_object = entity.contains("half_extents")
                ? py::reinterpret_borrow<py::object>(entity["half_extents"])
                : py::none();
            desired.id = dict_string(entity, "id", "");
            desired.mass = dict_double(entity, "mass", 0.0);
            const Vec3 position = handle_vec3(position_object);
            const Vec3 velocity = handle_vec3(velocity_object);
            const Vec3 half_extents = handle_vec3(half_extents_object, {0.4f, 0.3f, 0.1f});
            desired.x = position.x;
            desired.y = position.y;
            desired.z = position.z;
            desired.vx = velocity.x;
            desired.vy = velocity.y;
            desired.vz = velocity.z;
            desired.half_x = half_extents.x;
            desired.half_y = half_extents.y;
            desired.half_z = half_extents.z;
            desired.angle_deg = dict_double(entity, "angle_deg", 0.0);
            desired.angular_velocity_deg = dict_double(entity, "angular_velocity_deg", 0.0);

            active_ids.insert(desired.id);
            auto iterator = bodies_.find(desired.id);
            if (iterator == bodies_.end()) {
                iterator = bodies_.emplace(desired.id, PhysicsBodyState{}).first;
                iterator->second.id = desired.id;
            }
            PhysicsBodyState& record = iterator->second;
#if RM26_NATIVE_HAS_BULLET
            const bool needs_rebuild =
                record.body == nullptr ||
                std::abs(record.half_x - desired.half_x) > 1e-4 ||
                std::abs(record.half_y - desired.half_y) > 1e-4 ||
                std::abs(record.half_z - desired.half_z) > 1e-4 ||
                std::abs(record.mass - desired.mass) > 1e-4;
#else
            const bool needs_rebuild = false;
#endif
            record.half_x = desired.half_x;
            record.half_y = desired.half_y;
            record.half_z = desired.half_z;
            record.mass = desired.mass;
            record.x = desired.x;
            record.y = desired.y;
            record.z = desired.z;
            record.vx = desired.vx;
            record.vy = desired.vy;
            record.vz = desired.vz;
            record.angle_deg = desired.angle_deg;
            record.angular_velocity_deg = desired.angular_velocity_deg;
#if RM26_NATIVE_HAS_BULLET
            if (needs_rebuild) {
                recreate_body(record);
            }
            sync_body_state(record);
#endif
        }

        for (auto iterator = bodies_.begin(); iterator != bodies_.end();) {
            if (active_ids.contains(iterator->first)) {
                ++iterator;
                continue;
            }
#if RM26_NATIVE_HAS_BULLET
            if (iterator->second.body != nullptr && dynamics_world_ != nullptr) {
                dynamics_world_->removeRigidBody(iterator->second.body.get());
            }
#endif
            iterator = bodies_.erase(iterator);
        }
    }

    void step(double dt) {
#if RM26_NATIVE_HAS_BULLET
        if (dynamics_world_ != nullptr) {
            dynamics_world_->stepSimulation(static_cast<btScalar>(dt), 4, static_cast<btScalar>(fixed_time_step_));
            return;
        }
#endif
        for (auto& [id, body] : bodies_) {
            body.x += body.vx * dt;
            body.y += body.vy * dt;
            body.z += body.vz * dt;
            body.vz -= gravity_ * dt;
            body.angle_deg += body.angular_velocity_deg * dt;
        }
    }

    py::list snapshot_entities() const {
        py::list snapshot;
        for (const auto& [id, record] : bodies_) {
            py::dict entry;
            entry["id"] = record.id;
#if RM26_NATIVE_HAS_BULLET
            if (record.body != nullptr) {
                const btTransform& transform = record.body->getWorldTransform();
                const btVector3 origin = transform.getOrigin();
                const btVector3 linear_velocity = record.body->getLinearVelocity();
                const btVector3 angular_velocity = record.body->getAngularVelocity();
                const btQuaternion rotation = transform.getRotation();
                const double siny_cosp = 2.0 * (rotation.w() * rotation.z() + rotation.x() * rotation.y());
                const double cosy_cosp = 1.0 - 2.0 * (rotation.y() * rotation.y() + rotation.z() * rotation.z());
                entry["x"] = origin.x();
                entry["y"] = origin.y();
                entry["z"] = origin.z();
                entry["vx"] = linear_velocity.x();
                entry["vy"] = linear_velocity.y();
                entry["vz"] = linear_velocity.z();
                entry["angle_deg"] = std::atan2(siny_cosp, cosy_cosp) * 180.0 / kPi;
                entry["angular_velocity_deg"] = static_cast<double>(angular_velocity.z()) * 180.0 / kPi;
            }
#else
            entry["x"] = record.x;
            entry["y"] = record.y;
            entry["z"] = record.z;
            entry["vx"] = record.vx;
            entry["vy"] = record.vy;
            entry["vz"] = record.vz;
            entry["angle_deg"] = record.angle_deg;
            entry["angular_velocity_deg"] = record.angular_velocity_deg;
#endif
            snapshot.append(entry);
        }
        return snapshot;
    }

    py::dict simulate_ballistic_projectile(py::dict shot) const {
        const py::object start_point_object = shot.contains("start_point")
            ? py::reinterpret_borrow<py::object>(shot["start_point"])
            : py::none();
        const py::object velocity_object = shot.contains("velocity")
            ? py::reinterpret_borrow<py::object>(shot["velocity"])
            : py::none();
        const Vec3 start_point = handle_vec3(start_point_object);
        Vec3 velocity = handle_vec3(velocity_object);
        const double gravity = dict_double(shot, "gravity", 9.8);
        const double drag = dict_double(shot, "drag", 0.0);
        const double max_range = dict_double(shot, "max_range", 1.0);
        const double step = std::max(0.001, dict_double(shot, "step", 0.01));

        std::vector<std::array<double, 3>> points;
        points.push_back({start_point.x, start_point.y, start_point.z});
        std::array<double, 3> point = {start_point.x, start_point.y, start_point.z};
        double travelled = 0.0;
        while (travelled < max_range) {
            const double speed = std::sqrt(velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z);
            if (speed <= 1e-6) {
                break;
            }
            const double acceleration_x = -drag * speed * velocity.x;
            const double acceleration_y = -drag * speed * velocity.y;
            const double acceleration_z = -gravity - drag * speed * velocity.z;
            std::array<double, 3> next_point = {
                point[0] + velocity.x * step + 0.5 * acceleration_x * step * step,
                point[1] + velocity.y * step + 0.5 * acceleration_y * step * step,
                point[2] + velocity.z * step + 0.5 * acceleration_z * step * step,
            };
            velocity.x = static_cast<float>(velocity.x + acceleration_x * step);
            velocity.y = static_cast<float>(velocity.y + acceleration_y * step);
            velocity.z = static_cast<float>(velocity.z + acceleration_z * step);
            travelled += std::sqrt(
                (next_point[0] - point[0]) * (next_point[0] - point[0]) +
                (next_point[1] - point[1]) * (next_point[1] - point[1]) +
                (next_point[2] - point[2]) * (next_point[2] - point[2])
            );
            point = next_point;
            points.push_back(point);
            if (point[2] <= 0.0) {
                break;
            }
        }

        py::dict response;
        response["points"] = points;
        return response;
    }

    py::dict build_info() const {
        return rm26_native::build_info();
    }

private:
#if RM26_NATIVE_HAS_BULLET
    void rebuild_terrain() {
        if (dynamics_world_ == nullptr) {
            return;
        }
        if (terrain_body_ != nullptr) {
            dynamics_world_->removeRigidBody(terrain_body_.get());
            terrain_body_.reset();
            terrain_motion_state_.reset();
            terrain_shape_.reset();
        }
        if (terrain_grid_width_ <= 1 || terrain_grid_height_ <= 1 || terrain_heights_.empty()) {
            return;
        }
        const auto [min_it, max_it] = std::minmax_element(terrain_heights_.begin(), terrain_heights_.end());
        const btScalar min_height = static_cast<btScalar>(*min_it);
        const btScalar max_height = static_cast<btScalar>(*max_it);
        terrain_shape_ = std::make_unique<btHeightfieldTerrainShape>(
            terrain_grid_width_,
            terrain_grid_height_,
            terrain_heights_.data(),
            1.0,
            min_height,
            max_height,
            2,
            PHY_FLOAT,
            false
        );
        terrain_shape_->setLocalScaling(btVector3(static_cast<btScalar>(terrain_cell_width_m_), static_cast<btScalar>(terrain_cell_height_m_), 1.0));
        btTransform transform;
        transform.setIdentity();
        transform.setOrigin(btVector3(
            static_cast<btScalar>(terrain_cell_width_m_ * static_cast<double>(std::max(terrain_grid_width_ - 1, 0)) * 0.5),
            static_cast<btScalar>(terrain_cell_height_m_ * static_cast<double>(std::max(terrain_grid_height_ - 1, 0)) * 0.5),
            static_cast<btScalar>((static_cast<double>(min_height) + static_cast<double>(max_height)) * 0.5)
        ));
        terrain_motion_state_ = std::make_unique<btDefaultMotionState>(transform);
        btRigidBody::btRigidBodyConstructionInfo info(0.0, terrain_motion_state_.get(), terrain_shape_.get(), btVector3(0.0, 0.0, 0.0));
        info.m_friction = 1.0f;
        terrain_body_ = std::make_unique<btRigidBody>(info);
        terrain_body_->setCollisionFlags(terrain_body_->getCollisionFlags() | btCollisionObject::CF_STATIC_OBJECT);
        dynamics_world_->addRigidBody(terrain_body_.get());
    }

    void recreate_body(PhysicsBodyState& record) {
        if (dynamics_world_ == nullptr) {
            return;
        }
        if (record.body != nullptr) {
            dynamics_world_->removeRigidBody(record.body.get());
            record.body.reset();
            record.motion_state.reset();
            record.shape.reset();
        }
        record.shape = std::make_unique<btBoxShape>(btVector3(
            static_cast<btScalar>(record.half_x),
            static_cast<btScalar>(record.half_y),
            static_cast<btScalar>(record.half_z)
        ));
        btTransform transform;
        transform.setIdentity();
        btQuaternion rotation;
        rotation.setRotation(btVector3(0.0, 0.0, 1.0), static_cast<btScalar>(record.angle_deg * kPi / 180.0));
        transform.setRotation(rotation);
        transform.setOrigin(btVector3(static_cast<btScalar>(record.x), static_cast<btScalar>(record.y), static_cast<btScalar>(record.z)));
        record.motion_state = std::make_unique<btDefaultMotionState>(transform);
        btVector3 inertia(0.0, 0.0, 0.0);
        if (record.mass > 0.0) {
            record.shape->calculateLocalInertia(static_cast<btScalar>(record.mass), inertia);
        }
        btRigidBody::btRigidBodyConstructionInfo info(static_cast<btScalar>(record.mass), record.motion_state.get(), record.shape.get(), inertia);
        info.m_friction = 0.95f;
        info.m_restitution = 0.02f;
        record.body = std::make_unique<btRigidBody>(info);
        record.body->setActivationState(DISABLE_DEACTIVATION);
        dynamics_world_->addRigidBody(record.body.get());
    }

    void sync_body_state(PhysicsBodyState& record) {
        if (record.body == nullptr) {
            return;
        }
        btTransform transform;
        transform.setIdentity();
        btQuaternion rotation;
        rotation.setRotation(btVector3(0.0, 0.0, 1.0), static_cast<btScalar>(record.angle_deg * kPi / 180.0));
        transform.setRotation(rotation);
        transform.setOrigin(btVector3(static_cast<btScalar>(record.x), static_cast<btScalar>(record.y), static_cast<btScalar>(record.z)));
        record.body->setWorldTransform(transform);
        if (record.motion_state != nullptr) {
            record.motion_state->setWorldTransform(transform);
        }
        record.body->setLinearVelocity(btVector3(static_cast<btScalar>(record.vx), static_cast<btScalar>(record.vy), static_cast<btScalar>(record.vz)));
        record.body->setAngularVelocity(btVector3(0.0, 0.0, static_cast<btScalar>(record.angular_velocity_deg * kPi / 180.0)));
        record.body->activate(true);
    }
#endif

    py::dict config_;
    double fixed_time_step_ = 1.0 / 120.0;
    double gravity_ = 9.8;
    std::unordered_map<std::string, PhysicsBodyState> bodies_;
    int terrain_raster_version_ = -1;
    int terrain_grid_width_ = 0;
    int terrain_grid_height_ = 0;
    double terrain_cell_width_m_ = 0.1;
    double terrain_cell_height_m_ = 0.1;
    std::vector<float> terrain_heights_;
#if RM26_NATIVE_HAS_BULLET
    std::unique_ptr<btDefaultCollisionConfiguration> collision_configuration_;
    std::unique_ptr<btCollisionDispatcher> dispatcher_;
    std::unique_ptr<btDbvtBroadphase> broadphase_;
    std::unique_ptr<btSequentialImpulseConstraintSolver> solver_;
    std::unique_ptr<btDiscreteDynamicsWorld> dynamics_world_;
    std::unique_ptr<btHeightfieldTerrainShape> terrain_shape_;
    std::unique_ptr<btDefaultMotionState> terrain_motion_state_;
    std::unique_ptr<btRigidBody> terrain_body_;
#endif
};

}  // namespace

namespace rm26_native {

void register_physics_bindings(py::module_& module) {
    py::class_<NativePhysicsBridge>(module, "NativePhysicsBridge")
        .def(py::init<py::dict>(), py::arg("config") = py::dict())
        .def("set_terrain", &NativePhysicsBridge::set_terrain)
        .def("sync_entities", &NativePhysicsBridge::sync_entities)
        .def("step", &NativePhysicsBridge::step)
        .def("snapshot_entities", &NativePhysicsBridge::snapshot_entities)
        .def("simulate_ballistic_projectile", &NativePhysicsBridge::simulate_ballistic_projectile)
        .def("shutdown", &NativePhysicsBridge::shutdown)
        .def("build_info", &NativePhysicsBridge::build_info);
}

}  // namespace rm26_native