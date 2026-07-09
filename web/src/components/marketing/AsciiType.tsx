"use client";

import { useEffect, useRef } from "react";

/**
 * Giant typography rendered as a live ASCII/dither field (Codrops "Efecto"
 * technique, pointed at type instead of an image): the word is drawn to an
 * offscreen canvas, sampled per-cell as a luminance mask, shimmered with fbm,
 * warped by a pointer ripple, Bayer-dithered, and rasterized as procedural
 * 5x7 glyphs in the theme's primary color. Transparent outside the strokes,
 * so it composes cleanly onto a light page.
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
uniform vec2  u_mouse;
uniform vec3  u_ink;
uniform float u_cell;
uniform float u_intensity;
uniform sampler2D u_text;

out vec4 outColor;

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
  for (int i = 0; i < 3; i++) {
    v += a * vnoise(p);
    p = p * 2.03 + vec2(17.3, 9.1);
    a *= 0.5;
  }
  return v;
}

void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 cellId = floor(frag / u_cell);
  vec2 cellCenter = (cellId + 0.5) * u_cell;
  vec2 uv = cellCenter / u_res;

  // pointer ripple: displace the sample point radially, fading with distance
  vec2 m = u_mouse / u_res;
  float aspect = u_res.x / u_res.y;
  float d = length((uv - m) * vec2(aspect, 1.0));
  vec2 dir = normalize((uv - m) * vec2(aspect, 1.0) + 1e-4);
  vec2 suv = uv + dir * vec2(1.0 / aspect, 1.0) * 0.014 * exp(-d * 2.2) * sin(d * 26.0 - u_time * 3.4);

  float b = texture(u_text, suv).r;

  // organic shimmer drifting through the strokes
  b *= 0.68 + 0.5 * fbm(uv * vec2(5.0 * aspect, 5.0) + vec2(u_time * 0.14, -u_time * 0.1));
  // pointer glow, confined to the strokes
  b += 0.35 * exp(-d * d * 16.0) * texture(u_text, suv).r;
  b = clamp(b, 0.0, 1.0);

  // ordered dithering, then quantize to the glyph ramp
  ivec2 bi = ivec2(mod(cellId, 4.0));
  float threshold = (BAYER[bi.y * 4 + bi.x] + 0.5) / 16.0;
  float steps = 10.0;
  int idx = int(clamp(floor(b * steps + (threshold - 0.5) * 1.35), 0.0, steps - 1.0));

  vec2 local = fract(frag / u_cell);
  vec2 g = local * vec2(8.0, 11.0) - vec2(1.5, 2.0);
  float on = 0.0;
  if (g.x >= 0.0 && g.x < 5.0 && g.y >= 0.0 && g.y < 7.0) {
    int x = int(g.x);
    int y = 6 - int(g.y);
    int bit = y * 5 + x;
    uvec2 glyph = GLYPHS[idx];
    uint word = bit < 32 ? glyph.x : glyph.y;
    on = float((word >> uint(bit & 31)) & 1u);
  }

  float alpha = on * (0.3 + 0.7 * b) * u_intensity * step(0.02, b);
  outColor = vec4(u_ink * alpha, alpha);
}
`;

function readThemeInk(minLightness = 0): [number, number, number] {
  const raw = getComputedStyle(document.documentElement)
    .getPropertyValue("--primary")
    .trim();
  const m = raw.match(/([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/);
  if (!m) return [0.55, 0.35, 0.85];
  const [h, s] = [Number(m[1]), Number(m[2]) / 100];
  const l = Math.max(Number(m[3]) / 100, minLightness);
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => {
    const k = (n + h / 30) % 12;
    return l - a * Math.max(-1, Math.min(k - 3, Math.min(9 - k, 1)));
  };
  return [f(0), f(8), f(4)];
}

type AsciiTypeProps = {
  text?: string;
  className?: string;
  /** Cell size in CSS px — bigger = chunkier glyphs */
  cell?: number;
  intensity?: number;
};

export default function AsciiType({
  text = "SQLOS",
  className,
  cell = 13,
  intensity = 1,
}: AsciiTypeProps) {
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
        console.error("AsciiType:", gl.getShaderInfoLog(s));
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
      text: gl.getUniformLocation(prog, "u_text"),
    };

    const texture = gl.createTexture();
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.uniform1i(loc.text, 0);

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

    // rasterize the word to a mask texture, sized to the canvas
    const drawTextMask = () => {
      const mask = document.createElement("canvas");
      mask.width = width;
      mask.height = height;
      const ctx = mask.getContext("2d")!;
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, width, height);
      ctx.fillStyle = "#fff";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      const family = getComputedStyle(document.body).fontFamily;
      let size = height * 1.06;
      ctx.font = `800 ${size}px ${family}`;
      const measured = ctx.measureText(text).width;
      const maxWidth = width * 0.96;
      if (measured > maxWidth) size *= maxWidth / measured;
      ctx.font = `800 ${size}px ${family}`;
      ctx.fillText(text, width / 2, height * 0.56);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.LUMINANCE, gl.LUMINANCE, gl.UNSIGNED_BYTE, mask);
    };

    const resize = () => {
      const rect = canvas.getBoundingClientRect();
      const w = Math.max(1, Math.round(rect.width * dpr));
      const h = Math.max(1, Math.round(rect.height * dpr));
      if (w !== width || h !== height) {
        width = w;
        height = h;
        canvas.width = width;
        canvas.height = height;
        drawTextMask();
      }
    };

    const draw = (now: number) => {
      resize();
      gl.viewport(0, 0, width, height);
      gl.disable(gl.DEPTH_TEST);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      mouse.x += (mouse.tx - mouse.x) * 0.1;
      mouse.y += (mouse.ty - mouse.y) * 0.1;
      gl.uniform2f(loc.res, width, height);
      gl.uniform1f(loc.time, (now - start) / 1000);
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
      if (!shouldRun) draw(performance.now());
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
    window.addEventListener("pointermove", onPointer, { passive: true });

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
      window.removeEventListener("pointermove", onPointer);
      gl.deleteTexture(texture);
      gl.getExtension("WEBGL_lose_context")?.loseContext();
    };
  }, [text, cell, intensity]);

  return (
    <canvas
      ref={canvasRef}
      aria-hidden="true"
      className={["pointer-events-none h-full w-full", className ?? ""].join(" ")}
    />
  );
}
