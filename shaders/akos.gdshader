shader_type canvas_item;

uniform sampler2D room_palette : filter_nearest;
uniform bool is_active = true;
uniform bool alpha_enabled = true;
uniform bool shadow_color_enabled = true;
uniform float shadow_amount : hint_range(0.0, 1.0) = 0.4;
uniform bool use_linear_filter = false;

vec4 get_decoded_color(sampler2D tex, ivec2 iuv) {
	vec4 data = texelFetch(tex, iuv, 0);

	// Alpha is stored as exact 0.0 or 1.0 by CreateDataImage (localIdx == 0 → 0.0)
	if (data.a < 0.5) {
		vec4 bga = vec4(0.0);
		if(!alpha_enabled) bga.a = 1.0;
		return bga;
	}

	// R stores globalIdx / 255.0 — adding 0.5 before truncation guards
	// against float precision making e.g. 4.9999 truncate to 4 instead of 5
	int idx = int(data.r * 255.0 + 0.5);

	// G is exactly 1.0 for shadow (globalIdx 1–7), 0.0 otherwise
	bool is_shadow = data.g > 0.5;

	vec3 base_color = texelFetch(room_palette, ivec2(idx, 0), 0).rgb;
	vec3 rgb = (shadow_color_enabled && is_shadow) ? vec3(0.0) : base_color;

	float a = 1.0;
	if (alpha_enabled && shadow_color_enabled && is_shadow) {
		a = shadow_amount;
	}

	return vec4(rgb, a);
}

void fragment() {
	if (!is_active) {
		COLOR = texture(TEXTURE, UV);
	} else {
		ivec2 tex_size = textureSize(TEXTURE, 0);
		

		vec2 pixel_uv = UV * vec2(tex_size);
		ivec2 iuv;

		if (use_linear_filter) {
			vec2 sample_pos = pixel_uv - 0.5;
			ivec2 i = ivec2(floor(sample_pos));
			vec2 f = fract(sample_pos);

			ivec2 lo = clamp(i,              ivec2(0), tex_size - 1);
			ivec2 hi = clamp(i + ivec2(1,1), ivec2(0), tex_size - 1);

			vec4 c00 = get_decoded_color(TEXTURE, ivec2(lo.x, lo.y));
			vec4 c10 = get_decoded_color(TEXTURE, ivec2(hi.x, lo.y));
			vec4 c01 = get_decoded_color(TEXTURE, ivec2(lo.x, hi.y));
			vec4 c11 = get_decoded_color(TEXTURE, ivec2(hi.x, hi.y));

			vec4 top    = mix(c00, c10, f.x);
			vec4 bottom = mix(c01, c11, f.x);
			COLOR = mix(top, bottom, f.y);
		} else {
			
			iuv = clamp(ivec2(pixel_uv), ivec2(0), tex_size - 1);
			COLOR = get_decoded_color(TEXTURE, iuv);
		}

		if (COLOR.a < 0.0001) discard;
	}
}