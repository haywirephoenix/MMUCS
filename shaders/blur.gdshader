shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform float amount : hint_range(0.0, 10.0);
uniform vec4 tint : source_color = vec4(0.0, 0.0, 0.0, 0.3);

void fragment() {
	vec4 blur_color = textureLod(SCREEN_TEXTURE, SCREEN_UV, amount);
	
	vec3 final_rgb = mix(blur_color.rgb, tint.rgb, tint.a);

	COLOR = vec4(final_rgb, COLOR.a);
}