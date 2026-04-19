# rm26_native

This directory contains the current rm26_native binding implementation retained for compatibility.

The primary native engine project layout now lives under Engine/.

Current scope:

- Python binding module: rm26_native
- Compatibility binding source consumed by Engine/src/Python/Bindings.cpp
- Windows packaging entrypoints: build_native_3d.bat and package_3d_simulator.bat

Current feature levels exposed by the native module:

- Renderer: level 1
- Physics: level 2 when Bullet is available, otherwise level 0

The Python runtime only switches to the native renderer and native Bullet backend when the reported feature level reaches the configured runtime threshold. The default threshold is 10, so this cut keeps the existing Python runtime stable while establishing the final native integration boundary.

Build prerequisites:

- CMake 3.22 or newer
- A C++20 compiler
- Python with pybind11 installed
- OpenGL development headers
- Bullet development package if native physics should be compiled in

Recommended Windows flow:

1. Create the Python environment with setup_windows_env.bat.
2. Install pybind11 into that environment.
3. Run build_native_3d.bat.
4. Run package_3d_simulator.bat when the native build succeeds.
