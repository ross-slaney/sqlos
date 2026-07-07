"use client";

import { useEffect, useRef } from "react";

/**
 * Real-time ASCII + ordered-dithering background, rendered as a single
 * WebGL2 fragment shader (technique after Codrops' "Efecto" article:
 * cell-based luminance sampling, procedural 5x7 glyphs selected by
 * brightness, 4x4 Bayer matrix dithering before quantization).
 *
 * The canvas is transparent: glyphs are drawn in the theme's primary color
 * with per-glyph alpha, so it adapts to light/dark and all accent themes.
 */

const VERT = `#version 300 es
precision highp float;
const vec2 pos[3] = vec2[3](vec2(-1.,-1.), vec2(3.,-1.), vec2(-1.,3.));
void main() { gl_Position = vec4(pos[gl_VertexID], 0., 1.); }
`;

const FRAG = `#version 300 es
precision highp float;

uniform vec2  u_res;
uniform float u_time;
uniform vec2  u_mouse;      // px, canvas space
uniform vec3  u_ink;        // glyph color (theme primary)
uniform float u_cell;       // cell size in px
uniform float u_intensity;  // overall alpha scale

out vec4 outColor;

// 5x7 glyph bitmaps, bit i = y*5+x, packed lo/hi.
// Ramp (dark -> bright): ' ' . : - = + * # % @
const uvec2 GLYPHS[10] = uvec2[10](
  uvec2(0u, 0u),
  uvec2(134217728u, 0u),
  uvec2(134217856u, 0u),
  uvec2(458752u, 0u),
  uvec2(14694400u, 0u),
  uvec2(139432064u, 0u),
  uvec2(156718208u, 0u),
  uvec2(3198495722u, 2u),
  uvec2(1914839667u, 6u),
  uvec2(2212165166u, 7u)
);

// 4x4 Bayer matrix, normalized to [0,1)
const float BAYER[16] = float[16](
   0., 8., 2., 10.,
  12., 4., 14., 6.,
   3., 11., 1., 9.,
  15., 7., 13., 5.
);

float hash(vec2 p) {
  return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3. - 2. * f);
  return mix(
    mix(hash(i), hash(i + vec2(1., 0.)), u.x),
    mix(hash(i + vec2(0., 1.)), hash(i + vec2(1., 1.)), u.x),
    u.y);
}

float fbm(vec2 p) {
  float v = 0., a = 0.5;
  for (int i = 0; i < 4; i++) {
    v += a * vnoise(p);
    p = p * 2.03 + vec2(17.3, 9.1);
    a *= 0.5;
  }
  return v;
}

// Animated luminance field: drifting fbm "clouds" + slow domain warp
// + a soft glow and ripple around the pointer.
float field(vec2 uv, float t) {
  vec2 p = uv * 3.0;
  vec2 warp = vec2(
    fbm(p * 0.9 + vec2(0.0, t * 0.11)),
    fbm(p * 0.9 + vec2(5.2, -t * 0.09)));
  float b = fbm(p + 1.6 * warp + vec2(t * 0.05, -t * 0.03));
  b = smoothstep(0.28, 0.86, b);

  // pointer glow + expanding ripple (aspect-corrected distance)
  float d = length((uv - u_mouse / u_res) * vec2(u_res.x / u_res.y, 1.0));
  b += 0.55 * exp(-d * d * 22.0);
  b += 0.18 * exp(-d * 4.0) * sin(d * 34.0 - t * 3.2);
  return clamp(b, 0., 1.);
}

void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 cellId = floor(frag / u_cell);
  vec2 cellCenter = (cellId + 0.5) * u_cell;
  vec2 uv = cellCenter / u_res;

  float b = field(uv, u_time);

  // fade toward top so content above stays readable, and vignette edges
  float fade = smoothstep(1.06, 0.45, uv.y) * 0.75 + 0.25;
  b *= fade;

  // ordered dithering before quantization to the 10-step ramp
  ivec2 bi = ivec2(mod(cellId, 4.0));
  float threshold = (BAYER[bi.y * 4 + bi.x] + 0.5) / 16.0;
  float steps = 10.0;
  int idx = int(clamp(floor(b * steps + (threshold - 0.5) * 1.35), 0.0, steps - 1.0));

  // rasterize the glyph: 5x7 grid centered in an 8x11 cell
  vec2 local = fract(frag / u_cell);           // 0..1 inside cell
  vec2 g = local * vec2(8.0, 11.0) - vec2(1.5, 2.0);
  float on = 0.0;
  if (g.x >= 0.0 && g.x < 5.0 && g.y >= 0.0 && g.y < 7.0) {
    int x = int(g.x);
    int y = 6 - int(g.y);                      // flip: row 0 is top
    int bit = y * 5 + x;
    uvec2 glyph = GLYPHS[idx];
    uint word = bit < 32 ? glyph.x : glyph.y;
    on = float((word >> uint(bit & 31)) & 1u);
  }

  float alpha = on * (0.22 + 0.78 * b) * u_intensity;
  outColor = vec4(u_ink * alpha, alpha);       // premultiplied
}
`;

type AsciiShaderProps = {
  className?: string;
  /** Cell size in CSS px */
  cell?: number;
  /** Animation speed multiplier */
  speed?: number;
  /** Overall opacity of the glyph field */
  intensity?: number;
  /** Track pointer for glow/ripple */
  interactive?: boolean;
};

function readThemeInk(): [number, number, number] {
  const raw = getComputedStyle(document.documentElement)
    .getPropertyValue("--primary")
    .trim();
  const m = raw.match(/([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/);
  if (!m) return [0.55, 0.35, 0.85];
  const [h, s, l] = [Number(m[1]), Number(m[2]) / 100, Number(m[3]) / 100];
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => {
    const k = (n + h / 30) % 12;
    return l - a * Math.max(-1, Math.min(k - 3, Math.min(9 - k, 1)));
  };
  return [f(0), f(8), f(4)];
}

export default function AsciiShader({
  className,
  cell = 11,
  speed = 1,
  intensity = 1,
  interactive = true,
}: AsciiShaderProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const gl = canvas.getContext("webgl2", {
      alpha: true,
      premultipliedAlpha: true,
      antialias: false,
      powerPreference: "low-power",
    });
    if (!gl) return;

    const compile = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
        console.error("AsciiShader:", gl.getShaderInfoLog(s));
        return null;
      }
      return s;
    };
    const vs = compile(gl.VERTEX_SHADER, VERT);
    const fs = compile(gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) return;
    const prog = gl.createProgram()!;
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return;
    gl.useProgram(prog);

    const loc = {
      res: gl.getUniformLocation(prog, "u_res"),
      time: gl.getUniformLocation(prog, "u_time"),
      mouse: gl.getUniformLocation(prog, "u_mouse"),
      ink: gl.getUniformLocation(prog, "u_ink"),
      cell: gl.getUniformLocation(prog, "u_cell"),
      intensity: gl.getUniformLocation(prog, "u_intensity"),
    };

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    let width = 0;
    let height = 0;
    const mouse = { x: -9999, y: -9999, tx: -9999, ty: -9999 };
    let ink = readThemeInk();
    let raf = 0;
    let visible = true;
    let pageVisible = !document.hidden;
    let running = false;
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const start = performance.now();

    const resize = () => {
      const rect = canvas.getBoundingClientRect();
      width = Math.max(1, Math.round(rect.width * dpr));
      height = Math.max(1, Math.round(rect.height * dpr));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
    };

    const draw = (now: number) => {
      resize();
      gl.viewport(0, 0, width, height);
      gl.disable(gl.DEPTH_TEST);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      mouse.x += (mouse.tx - mouse.x) * 0.08;
      mouse.y += (mouse.ty - mouse.y) * 0.08;
      gl.uniform2f(loc.res, width, height);
      gl.uniform1f(loc.time, ((now - start) / 1000) * speed);
      gl.uniform2f(loc.mouse, mouse.x * dpr, height - mouse.y * dpr);
      gl.uniform3f(loc.ink, ink[0], ink[1], ink[2]);
      gl.uniform1f(loc.cell, cell * dpr);
      gl.uniform1f(loc.intensity, intensity);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    };

    const loop = (now: number) => {
      if (!running) return;
      draw(now);
      raf = requestAnimationFrame(loop);
    };

    const sync = () => {
      const shouldRun = visible && pageVisible && !reducedMotion.matches;
      if (shouldRun && !running) {
        running = true;
        raf = requestAnimationFrame(loop);
      } else if (!shouldRun && running) {
        running = false;
        cancelAnimationFrame(raf);
      }
      if (!shouldRun) draw(performance.now()); // static frame
    };

    const io = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      sync();
    });
    io.observe(canvas);

    const onVis = () => {
      pageVisible = !document.hidden;
      sync();
    };
    document.addEventListener("visibilitychange", onVis);
    reducedMotion.addEventListener("change", sync);

    const onPointer = (e: PointerEvent) => {
      const rect = canvas.getBoundingClientRect();
      mouse.tx = e.clientX - rect.left;
      mouse.ty = e.clientY - rect.top;
      if (mouse.x < -999) {
        mouse.x = mouse.tx;
        mouse.y = mouse.ty;
      }
    };
    if (interactive) window.addEventListener("pointermove", onPointer, { passive: true });

    // re-read the ink color when the theme (class or data-theme) changes
    const mo = new MutationObserver(() => {
      ink = readThemeInk();
      if (!running) draw(performance.now());
    });
    mo.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class", "data-theme"],
    });

    const ro = new ResizeObserver(() => {
      if (!running) draw(performance.now());
    });
    ro.observe(canvas);

    sync();

    return () => {
      running = false;
      cancelAnimationFrame(raf);
      io.disconnect();
      mo.disconnect();
      ro.disconnect();
      document.removeEventListener("visibilitychange", onVis);
      reducedMotion.removeEventListener("change", sync);
      if (interactive) window.removeEventListener("pointermove", onPointer);
      gl.getExtension("WEBGL_lose_context")?.loseContext();
    };
  }, [cell, speed, intensity, interactive]);

  return (
    <canvas
      ref={canvasRef}
      aria-hidden="true"
      className={["pointer-events-none h-full w-full", className ?? ""].join(" ")}
    />
  );
}
