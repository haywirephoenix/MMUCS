shader_type canvas_item;

uniform sampler2D blurred_wallpaper : source_color, filter_linear;
uniform vec4 tint : source_color = vec4(0.0, 0.0, 0.0, 0.027);
uniform bool glass_enabled = true;
uniform float max_tint : hint_range(0.0, 1) = 0.027;

uniform vec4 rim_color_start : source_color = vec4(1.0, 1.0, 1.0, 0.3);
uniform vec4 rim_color_end : source_color = vec4(1.0, 1.0, 1.0, 0.05);
uniform float rim_gradient_angle : hint_range(0.0, 360.0) = 45.0;
uniform float rim_gradient_falloff : hint_range(0.1, 5.0) = 1.0;

uniform float rim_thickness_pixels : hint_range(0.0, 50.0) = 4.0;
uniform float rim_glow : hint_range(0.0, 5.0) = 1.0;
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

float calculate_rim(vec2 pos, vec2 uv) {
	vec2 panel_size = pos / uv;
	vec2 half_size = panel_size * 0.5;
	vec2 centered_pos = pos - half_size;

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

	return rim_factor;
}

void fragment() {
	float rim_factor = calculate_rim(pixel_pos, UV);
	
	float rad = radians(rim_gradient_angle);
	vec2 dir = vec2(cos(rad), sin(rad));

	float grad_t = dot(UV - vec2(0.5), dir) + 0.5;
	grad_t = clamp(grad_t, 0.0, 1.0);

	grad_t = pow(grad_t, rim_gradient_falloff);

	vec4 dynamic_rim_color = mix(rim_color_start, rim_color_end, grad_t);

	vec3 final_rgb;
	float final_alpha;

	if (glass_enabled) {
		vec4 blur_color = texture(blurred_wallpaper, SCREEN_UV);
		final_rgb = mix(blur_color.rgb, tint.rgb, max_tint);

		final_alpha = mix(COLOR.a, 1.0, rim_factor * dynamic_rim_color.a);
		final_rgb = mix(final_rgb, dynamic_rim_color.rgb * rim_glow, rim_factor * dynamic_rim_color.a);

	} else {
		float bg_alpha = tint.a;
		float rim_alpha = rim_factor * dynamic_rim_color.a;

		final_alpha = clamp(bg_alpha + rim_alpha, 0.0, 1.0);

		float bg_visible_factor = bg_alpha * (1.0 - rim_alpha);

		vec3 bg_premult = tint.rgb * bg_visible_factor * 0.2;
		vec3 rim_premult = (dynamic_rim_color.rgb * rim_glow) * rim_alpha;

		final_rgb = (bg_premult + rim_premult) / max(final_alpha, 0.001);
	}

	COLOR = vec4(final_rgb, final_alpha);
}