#pragma once

namespace rm26::engine {

class Application {
public:
    bool initialize();
    void update(double dt);
    void render();
    bool initialized() const;

private:
    bool initialized_ = false;
    double last_dt_ = 0.0;
};

}  // namespace rm26::engine
