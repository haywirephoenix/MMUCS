shader_type canvas_item;

uniform sampler2D blurred_wallpaper : source_color, filter_linear;
uniform vec4 tint : source_color = vec4(0.0, 0.0, 0.0, 0.027);
uniform bool glass_enabled = true;
uniform float max_tint : hint_range(0.0, 0.05) = 0.027;

uniform vec4 rim_color : source_color = vec4(1.0, 1.0, 1.0, 0.2);
uniform float rim_thickness_pixels : hint_range(0.0, 50.0) = 4.0;
uniform float rim_glow : hint_range(0.0, 5.0) = 1.0;
// New uniform to control the shape of the glow curve
uniform float rim_falloff : hint_range(0.1, 8.0) = 1.0;
uniform float corner_radius_pixels : hint_range(0.0, 100.0) = 16.0;

varying vec2 pixel_pos;

void vertex() {
	pixel_pos = VERTEX;
}

float sd_rounded_box(vec2 p, vec2 b, float r) {
	vec2 d = abs(p) - b + vec2(r);
	return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}

void fragment() {
	if (glass_enabled) {
		vec4 blur_color = texture(blurred_wallpaper, SCREEN_UV);
		vec3 final_rgb = mix(blur_color.rgb, tint.rgb, max_tint);

		vec2 panel_size = pixel_pos / UV;
		vec2 half_size = panel_size * 0.5;
		vec2 centered_pos = pixel_pos - half_size;

		float max_radius = min(half_size.x, half_size.y);
		float radius = min(corner_radius_pixels, max_radius);

		float distance_to_edge_sdf = sd_rounded_box(centered_pos, half_size, radius);
		float closest_edge_pixels = abs(distance_to_edge_sdf);

		float normalized_dist = clamp(closest_edge_pixels / max(rim_thickness_pixels, 0.001), 0.0, 1.0);

		float base_glow = 1.0 - normalized_dist;

		float rim_factor = pow(base_glow, rim_falloff);

		if (distance_to_edge_sdf > 0.0) {
			rim_factor = 0.0;
		}

		final_rgb = mix(final_rgb, rim_color.rgb * rim_glow, rim_factor * rim_color.a);

		COLOR = vec4(final_rgb, COLOR.a);
	} else {
		vec4 normalCol = tint * 0.25;
		normalCol.a = 1.0;
		COLOR = normalCol;
	}
}