// XEngine three.js authoring page. Renders deterministic frames on demand for the Windows
// authoring host (A1b). The scene, camera and lighting are fixed; only `timeSeconds` and `seed`
// drive the image, so identical requests on this machine produce byte-identical PNGs — that is
// what makes panel and CLI exports comparable.
import * as THREE from 'three';
import { GLTFExporter } from './lib/exporters/GLTFExporter.js';
import { EffectComposer } from './lib/postprocessing/EffectComposer.js';
import { RenderPass } from './lib/postprocessing/RenderPass.js';
import { ShaderPass } from './lib/postprocessing/ShaderPass.js';
import { UnrealBloomPass } from './lib/postprocessing/UnrealBloomPass.js';
import { OutputPass } from './lib/postprocessing/OutputPass.js';

const hud = document.getElementById('hud');

function reportError(context, error) {
  const message = context + ': ' + (error && error.message ? error.message : String(error));
  hud.textContent = 'HOST ERROR — ' + message;
  try {
    window.chrome.webview.postMessage({ type: 'log', message });
  } catch { /* no webview channel (plain browser debugging) */ }
}
window.addEventListener('error', (e) => reportError('window.error', e.error || e.message));
window.addEventListener('unhandledrejection', (e) => reportError('unhandledrejection', e.reason));

const renderer = new THREE.WebGLRenderer({
  antialias: true,
  alpha: false,
  preserveDrawingBuffer: true, // toDataURL must observe a settled framebuffer.
  powerPreference: 'high-performance',
});
renderer.domElement.addEventListener('webglcontextlost', (e) => {
  e.preventDefault();
  reportError('webglcontextlost', new Error('context lost'));
});
renderer.setClearColor(0x101418, 1);
document.getElementById('app').appendChild(renderer.domElement);
renderer.domElement.style.width = '100%';
renderer.domElement.style.height = '100%';

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100);
camera.position.set(0, 1.6, 5);
camera.lookAt(0, 0.4, 0);

const sun = new THREE.DirectionalLight(0xfff2e0, 2.4);
sun.position.set(3, 5, 2);
scene.add(sun);
const rim = new THREE.DirectionalLight(0x4a7fd6, 1.1);
rim.position.set(-4, 2, -3);
scene.add(rim);
scene.add(new THREE.AmbientLight(0x30343c, 1.4));

const grid = new THREE.GridHelper(10, 20, 0x2a3a52, 0x1a2434);
scene.add(grid);

// Seed → deterministic hue/offset. No Math.random, no Date.now anywhere in the render path.
function seedToParams(seed) {
  let x = (seed | 0) + 0x6d2b79f5;
  x = Math.imul(x ^ (x >>> 15), x | 1);
  x ^= x + Math.imul(x ^ (x >>> 7), x | 61);
  const u = ((x ^ (x >>> 14)) >>> 0) / 4294967296;
  const v = (((x * 48271) % 2147483647 + 2147483647) % 2147483647) / 2147483647;
  return { hue: u, offset: v * Math.PI * 2 };
}

let params = seedToParams(0);
let material = makeMaterial(params.hue);
let geometry = makeGeometry('torusKnot');
const knot = new THREE.Mesh(geometry, material);
knot.position.y = 0.75;
scene.add(knot);

function makeMaterial(hue) {
  return new THREE.MeshStandardMaterial({
    color: new THREE.Color().setHSL(hue, 0.55, 0.52),
    metalness: 0.85,
    roughness: 0.25,
  });
}

function makeGeometry(kind) {
  switch (kind) {
    case 'sphere': return new THREE.SphereGeometry(0.95, 64, 32);
    case 'box': return new THREE.BoxGeometry(1.3, 1.3, 1.3);
    case 'torusKnot':
    default: return new THREE.TorusKnotGeometry(0.9, 0.3, 160, 32);
  }
}

function resizeTo(w, h) {
  renderer.setPixelRatio(1); // Requested size = backing-store size; DPI is the host's business.
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}

const pendingSize = { w: 0, h: 0 };

// ---------------------------------------------------------------------------
// A4 — constrained native-effect templates. Every effect below is parameterised
// deterministically (seed + time only), so exports stay reproducible.
// ---------------------------------------------------------------------------

// Fresnel rim as an additive back-side shell around the main body — implemented without a
// custom ShaderMaterial so the GLB export path keeps standard PBR materials.
function applyFresnel(body, fresnel, timeSeconds) {
  const existing = body.getObjectByName('fresnel_shell');
  if (existing) body.remove(existing);
  if (!fresnel) return;

  const shell = new THREE.Mesh(
    body.geometry.clone(),
    new THREE.MeshBasicMaterial({
      color: new THREE.Color(fresnel.color[0], fresnel.color[1], fresnel.color[2]),
      transparent: true,
      opacity: Math.min(0.32, fresnel.intensity * 0.22),
      blending: THREE.AdditiveBlending,
      side: THREE.BackSide,
      depthWrite: false,
    }),
  );
  shell.name = 'fresnel_shell';
  shell.scale.setScalar(1.03 + Math.min(fresnel.power, 6) * 0.0025);
  shell.rotation.copy(body.rotation);
  body.add(shell);
}

// Loop: a deterministic ring stream orbiting the body. Burst: particles launched radially
// from the body at t=1s of the clip (deterministic via the seed hash, no Math.random).
let particles = null;
function applyParticles(particlesSpec, timeSeconds, seed) {
  if (particles) {
    scene.remove(particles);
    particles.geometry.dispose();
    particles = null;
  }
  if (!particlesSpec) return;

  const count = particlesSpec.count;
  const positions = new Float32Array(count * 3);
  let state = (seed | 0) + 0x9e3779b9;
  const nextUnit = () => {
    state = Math.imul(state ^ (state >>> 15), state | 1);
    state ^= state + Math.imul(state ^ (state >>> 7), state | 61);
    return ((state ^ (state >>> 14)) >>> 0) / 4294967296;
  };

  const orbitRadius = 1.9;
  const launch = 1.0; // burst detonation time in seconds
  for (let i = 0; i < count; i++) {
    let x, y, z;
    if (particlesSpec.mode === 'burst') {
      const age = Math.max(0, timeSeconds - launch) * particlesSpec.speed;
      const theta = nextUnit() * Math.PI * 2;
      const phi = nextUnit() * Math.PI;
      const r = 0.4 + (age * (1.2 + nextUnit() * 0.6)) % 3.0;
      x = r * Math.sin(phi) * Math.cos(theta);
      z = r * Math.sin(phi) * Math.sin(theta);
      y = 0.75 + r * Math.cos(phi);
    } else {
      const phase = (nextUnit() * Math.PI * 2) + timeSeconds * particlesSpec.speed * (0.4 + nextUnit() * 0.4);
      const tilt = nextUnit() * Math.PI;
      x = orbitRadius * Math.sin(tilt) * Math.cos(phase);
      z = orbitRadius * Math.sin(tilt) * Math.sin(phase);
      y = 0.75 + 0.45 * Math.cos(tilt) * Math.sin(timeSeconds * particlesSpec.speed + nextUnit() * Math.PI);
    }
    positions[i * 3] = x;
    positions[i * 3 + 1] = y;
    positions[i * 3 + 2] = z;
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  const material = new THREE.PointsMaterial({
    color: new THREE.Color(particlesSpec.color[0], particlesSpec.color[1], particlesSpec.color[2]),
    size: particlesSpec.size,
    transparent: true,
    opacity: 0.9,
    depthWrite: false,
  });
  particles = new THREE.Points(geometry, material);
  particles.name = 'particles';
  scene.add(particles);
}

// Post chain: RenderPass → UnrealBloom → combined vignette/saturation/brightness shader → OutputPass.
let composer = null;
let colorPass = null;
let bloomPass = null;
const gradeShader = {
  uniforms: {
    tDiffuse: { value: null },
    vignette: { value: 0.0 },
    saturation: { value: 0.0 },
    brightness: { value: 0.0 },
  },
  vertexShader: /* glsl */ `
    varying vec2 vUv;
    void main() {
      vUv = uv;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }`,
  fragmentShader: /* glsl */ `
    uniform sampler2D tDiffuse;
    uniform float vignette;
    uniform float saturation;
    uniform float brightness;
    varying vec2 vUv;
    void main() {
      vec4 c = texture2D(tDiffuse, vUv);
      float d = distance(vUv, vec2(0.5));
      c.rgb *= 1.0 - vignette * smoothstep(0.35, 0.85, d);
      float l = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722));
      c.rgb = mix(vec3(l), c.rgb, 1.0 + saturation);
      c.rgb += brightness;
      gl_FragColor = c;
    }`,
};

function ensureComposer(width, height, post) {
  if (!composer) {
    composer = new EffectComposer(renderer);
    composer.addPass(new RenderPass(scene, camera));
    bloomPass = new UnrealBloomPass(new THREE.Vector2(256, 256), 0.0, 0.4, 0.85);
    composer.addPass(bloomPass);
    colorPass = new ShaderPass(gradeShader);
    composer.addPass(colorPass);
    composer.addPass(new OutputPass());
  }
  composer.setSize(width, height);
  bloomPass.enabled = post.bloomIntensity > 0;
  bloomPass.strength = post.bloomIntensity;
  bloomPass.threshold = post.bloomThreshold;
  bloomPass.radius = 0.4;
  colorPass.enabled = post.vignette > 0 || post.saturation !== 0 || post.brightness !== 0;
  colorPass.uniforms.vignette.value = post.vignette;
  colorPass.uniforms.saturation.value = post.saturation;
  colorPass.uniforms.brightness.value = post.brightness;
}

function updateHud(text) {
  hud.textContent = text;
}

// Wire protocol surface. render() is deliberately SYNCHRONOUS: CoreWebView2.ExecuteScriptAsync
// does not await promises, so the result JSON must be the plain return value. The optional
// response delay used by timeout tests lives on the host side for the same reason.
// GLB export (A3). GLTFExporter is asynchronous (image/blob work), and ExecuteScriptAsync does
// not await promises, so exportGltf() starts the job and pollGltf() retrieves the finished
// payload; the host polls until ok or timeout.
let gltfJob = null;

function buildExportScene(req) {
  // Deliberate coverage of the A3 acceptance matrix:
  //   - full node hierarchy (root → pivot → meshes)
  //   - multiple materials / primitives (matA PBR metal, matB rough + emissive)
  //   - a negative-scale node (mirror)
  //   - one BLEND transparent material and one alphaTest cutoff material
  //   - a declared animation clip (pivot spin)
  const root = new THREE.Group();
  root.name = 'threefx_root';

  const matA = new THREE.MeshStandardMaterial({
    name: 'mat_main',
    color: new THREE.Color().setHSL((req.hue ?? 0.25), 0.55, 0.52),
    metalness: req.metalness ?? 0.85,
    roughness: req.roughness ?? 0.25,
  });
  const knot = new THREE.Mesh(makeGeometry(req.mesh || 'torusKnot'), matA);
  knot.name = 'main_body';
  knot.position.y = 0.75;

  const pivot = new THREE.Group();
  pivot.name = 'pivot';
  pivot.add(knot);

  const matB = new THREE.MeshStandardMaterial({
    name: 'mat_glass',
    color: new THREE.Color().setHSL(((req.hue ?? 0.25) + 0.5) % 1, 0.6, 0.5),
    metalness: 0.2,
    roughness: 0.6,
    transparent: true,
    opacity: 0.7,
  });
  const lens = new THREE.Mesh(new THREE.SphereGeometry(0.35, 32, 16), matB);
  lens.name = 'glass_shell';
  lens.position.set(1.2, 1.2, 0);

  const matC = new THREE.MeshStandardMaterial({
    name: 'mat_beacon',
    color: 0xff8830,
    metalness: 0,
    roughness: 0.9,
    emissive: new THREE.Color(0.9, 0.25, 0.05),
    emissiveIntensity: 1.5,
  });
  const beacon = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.3, 0.3), matC);
  beacon.name = 'emissive_beacon';
  beacon.position.set(-1.2, 1.0, 0.4);
  beacon.scale.x = -1; // negative scale node

  const matD = new THREE.MeshStandardMaterial({
    name: 'mat_blade',
    color: 0xffffff,
    metalness: 0,
    roughness: 1,
    alphaTest: 0.5,
    side: THREE.DoubleSide,
  });
  const blade = new THREE.Mesh(new THREE.PlaneGeometry(0.8, 1.6), matD);
  blade.name = 'cutoff_blade';
  blade.position.set(0, 0.9, -1.0);
  blade.rotation.y = 0.6;

  root.add(pivot, lens, beacon, blade);

  const clip = new THREE.AnimationClip('spin', 2, [
    new THREE.QuaternionKeyframeTrack('pivot.quaternion', [0, 1, 2], [
      0, 0, 0, 1,
      0, 0.7071067811865476, 0, 0.7071067811865476,
      0, 0, 0, 1,
    ]),
  ]);

  return { root, clip };
}

window.__threeHost = {
  threeRevision: THREE.REVISION,

  exportGltf(req) {
    try {
      const { root, clip } = buildExportScene(req);
      gltfJob = { state: 'running' };
      const exporter = new GLTFExporter();
      exporter.parse(
        root,
        (gltf) => {
          // Binary export → ArrayBuffer; encode via data URL because btoa() cannot take a
          // DataView and the payload easily exceeds the string-argument size of scripts.
          gltfJob = { state: 'pending-encode' };
          try {
            const blob = new Blob([gltf], { type: 'model/gltf-binary' });
            const reader = new FileReader();
            reader.onload = () => {
              const dataUrl = String(reader.result);
              gltfJob = { state: 'ok', base64: dataUrl.slice(dataUrl.indexOf(',') + 1) };
            };
            reader.onerror = () => { gltfJob = { state: 'error', error: 'blob read failed' }; };
            reader.readAsDataURL(blob);
          } catch (e) {
            gltfJob = { state: 'error', error: String(e && e.message || e) };
          }
        },
        (error) => { gltfJob = { state: 'error', error: String(error && error.message || error) }; },
        { binary: true, animations: [clip] },
      );
      return { ok: true, started: true };
    } catch (error) {
      gltfJob = { state: 'error', error: String(error && error.message || error) };
      return { ok: false, error: String(error && error.message || error) };
    }
  },

  pollGltf() {
    if (gltfJob === null) return { state: 'idle' };
    if (gltfJob.state === 'ok') {
      const payload = gltfJob;
      gltfJob = null;
      return payload;
    }
    return gltfJob;
  },

  render(req) {
    const started = performance.now();
    try {
      if (req.fail) {
        throw new Error('__testRenderFail: simulated page render failure');
      }

      params = seedToParams(req.seed);
      if (geometry.type !== undefined && req.mesh) {
        geometry.dispose();
        geometry = makeGeometry(req.mesh);
        knot.geometry = geometry;
      }
      const hue = (req.hue !== undefined && req.hue !== null) ? req.hue : params.hue;
      material.color.setHSL(hue, 0.55, 0.52);
      material.metalness = (req.metalness !== undefined && req.metalness !== null) ? req.metalness : 0.85;
      material.roughness = (req.roughness !== undefined && req.roughness !== null) ? req.roughness : 0.25;
      if (req.background && req.background.length === 3) {
        renderer.setClearColor(new THREE.Color(req.background[0], req.background[1], req.background[2]), 1);
      } else {
        renderer.setClearColor(0x101418, 1);
      }
      resizeTo(req.width, req.height);
      pendingSize.w = req.width;
      pendingSize.h = req.height;

      knot.rotation.x = req.timeSeconds * 0.7 + params.offset;
      knot.rotation.y = req.timeSeconds * 1.1;
      sun.position.set(Math.sin(req.timeSeconds * 0.3) * 4, 5, Math.cos(req.timeSeconds * 0.3) * 4);

      // A5 — transparent bake: keep the RGB clear color but drop alpha so the flipbook frames
      // composite over any target.
      if (req.transparent) renderer.setClearColor(0x101418, 0);
      else renderer.setClearColor(0x101418, 1);

      // A4 effect templates (deterministic in seed+time only).
      const effects = req.effects || {};
      applyFresnel(knot, effects.fresnel, req.timeSeconds);
      applyParticles(effects.particles, req.timeSeconds, req.seed);
      const post = effects.post;
      if (post && (post.bloomIntensity > 0 || post.vignette > 0 || post.saturation !== 0 || post.brightness !== 0 || (post.tonemap && post.tonemap !== 'none'))) {
        ensureComposer(req.width, req.height, post);
        composer.render();
      } else {
        renderer.render(scene, camera);
      }

      const dataUrl = renderer.domElement.toDataURL('image/png');

      updateHud(
        'three r' + THREE.REVISION + ' — ready\n' +
        'last render ' + req.width + 'x' + req.height + ' @ t=' + req.timeSeconds.toFixed(2) +
        ' seed=' + req.seed +
        (effects.fresnel ? '\nfresnel ON' : '') +
        (effects.particles ? '\nparticles ' + effects.particles.mode : '') +
        (post && post.bloomIntensity > 0 ? ' bloom ' + post.bloomIntensity : '') +
        (post && post.vignette > 0 ? ' vignette' : '') + '\n' +
        'devicePixelRatio ' + window.devicePixelRatio.toFixed(2)
      );

      return {
        ok: true,
        dataUrl,
        threeRevision: THREE.REVISION,
        ms: performance.now() - started,
        effectsApplied: [
          ...(effects.fresnel ? ['fresnel'] : []),
          ...(effects.particles ? ['particles:' + effects.particles.mode] : []),
          ...(post && post.bloomIntensity > 0 ? ['bloom'] : []),
          ...(post && post.vignette > 0 ? ['vignette'] : []),
          ...(post && post.saturation !== 0 ? ['saturation'] : []),
          ...(post && post.brightness !== 0 ? ['brightness'] : []),
        ],
      };
    } catch (error) {
      updateHud('three r' + THREE.REVISION + ' — render error: ' + error.message);
      return { ok: false, error: String(error && error.message || error) };
    }
  },
};

window.addEventListener('resize', () => {
  // Keep the live view filling the window between requests; exports still pin their own size.
  const w = Math.max(1, window.innerWidth);
  const h = Math.max(1, window.innerHeight);
  resizeTo(w, h);
  renderer.render(scene, camera);
});

// Initial fill so the visible window is never blank, then announce readiness.
resizeTo(Math.max(1, window.innerWidth), Math.max(1, window.innerHeight));
renderer.render(scene, camera);
updateHud('three r' + THREE.REVISION + ' — ready\ndevicePixelRatio ' + window.devicePixelRatio.toFixed(2));
window.chrome.webview.postMessage({
  type: 'ready',
  threeRevision: THREE.REVISION,
});
