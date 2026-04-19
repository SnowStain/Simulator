#version 330 core
layout(location = 0) in vec3 in_position;
layout(location = 1) in vec3 in_normal;
layout(location = 2) in vec2 in_uv;

uniform mat4 u_mvp;

out vec3 v_normal;
out vec2 v_uv;

void main() {
    gl_Position = u_mvp * vec4(in_position, 1.0);
    v_normal = in_normal;
    v_uv = in_uv;
}
