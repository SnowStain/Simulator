#pragma once

#include <string>

namespace rm26::engine::core {

struct WindowDesc {
    int width = 1280;
    int height = 720;
    std::string title = "RM26 Engine";
};

class Window {
public:
    explicit Window(WindowDesc desc = {});
    bool initialize();
    bool initialized() const;
    const WindowDesc& desc() const;

private:
    WindowDesc desc_;
    bool initialized_ = false;
};

}  // namespace rm26::engine::core
