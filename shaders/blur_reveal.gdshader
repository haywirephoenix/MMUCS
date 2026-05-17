shader_type canvas_item;

uniform sampler2D blurred_wallpaper : source_color, filter_linear;
uniform vec4 tint : source_color = vec4(0.0, 0.0, 0.0, 0.3);
uniform bool glass_enabled = true;

void fragment() {
	if (glass_enabled) {
		vec4 blur_color = texture(blurred_wallpaper, SCREEN_UV);

		vec3 final_rgb = mix(blur_color.rgb, tint.rgb, tint.a);
		COLOR = vec4(final_rgb, COLOR.a);
	} else {
// 		COLOR = vec4(tint.rgb, COLOR.a);
	}
}