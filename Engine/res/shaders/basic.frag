#version 330 core
in vec3 v_normal;
in vec2 v_uv;

uniform sampler2D u_texture;
uniform vec3 u_light_dir;

out vec4 fragColor;

void main() {
    vec3 base = texture(u_texture, v_uv).rgb;
    float light = 0.40 + max(dot(normalize(v_normal), normalize(u_light_dir)), 0.0) * 0.60;
    fragColor = vec4(base * light, 1.0);
}
