shader_type canvas_item;

uniform sampler2D blurred_wallpaper : source_color, filter_linear;
uniform vec4 tint : source_color = vec4(0.0, 0.0, 0.0, 0.3);
uniform bool glass_enabled = true;

uniform vec4 rim_color : source_color = vec4(1.0, 1.0, 1.0, 0.2);
uniform float rim_thickness : hint_range(0.0, 0.5) = 0.005;
uniform float rim_glow : hint_range(0.0, 5.0) = 1.0;

void fragment() {
	if (glass_enabled) {
		vec4 blur_color = texture(blurred_wallpaper, SCREEN_UV);
		vec3 final_rgb = mix(blur_color.rgb, tint.rgb, tint.a);
		
		vec2 distance_to_edge = min(UV, vec2(1.0) - UV);
		float closest_edge = min(distance_to_edge.x, distance_to_edge.y);
		
		float rim_factor = 1.0 - smoothstep(0.0, rim_thickness, closest_edge);
		final_rgb = mix(final_rgb, rim_color.rgb * rim_glow, rim_factor * rim_color.a);

		COLOR = vec4(final_rgb, COLOR.a);
	} else {

	}
}