/**
 * Metaball territory renderer — proximity-clustered DataTexture, Phong-lit.
 *
 * Faction nodes are grouped into connected components using cullDist as the
 * edge radius (same radius the shader uses for local field contributions).
 * Each cluster gets one tight bounding sphere mesh + a DataTexture packing
 * its nodes and any nearby enemy nodes for gap shading.
 *
 * This keeps bounding spheres small (proportional to cluster extent, not the
 * whole faction), eliminating per-node overdraw without the zoom-out blowup
 * of a single per-faction sphere.
 *
 * Phong shading uses the analytical field gradient ∇F at no extra cost.
 */
import * as THREE from 'three'
import type { NodeDto, WorldSnapshot } from '../types/world'
import { factionColorNum } from './colors'

const MIN_STR = 0.40

export interface BlobParams {
  sigmaScale:    number
  sphereMult:    number
  maxSearchDist: number
  isolatedSigma: number
  cullDist:      number
  threshold:     number
  marchSteps:    number
  alpha:         number
  gapMargin:     number
}

export const DEFAULT_BLOB_PARAMS: BlobParams = {
  sigmaScale:    1.50,
  sphereMult:    1.50,
  maxSearchDist: 500,
  isolatedSigma: 40,
  cullDist:      1200,
  threshold:     0.30,
  marchSteps:    10,
  alpha:         0.20,
  gapMargin:     1.0,
}

// ---------------------------------------------------------------------------

const VERT = `
varying vec3 vWorldPos;
void main() {
  vec4 wp = modelMatrix * vec4(position, 1.0);
  vWorldPos = wp.xyz;
  gl_Position = projectionMatrix * viewMatrix * wp;
}
`

function makeFrag(p: BlobParams, nodeCount: number): string { return `
#define NODE_COUNT  ${nodeCount}
#define MARCH_STEPS ${Math.round(p.marchSteps)}

varying vec3 vWorldPos;

uniform vec3      uSphereCenter;
uniform float     uSphereRadius;
// (nodeCount * 2) x 1, RGBA32F.
// texel 2i:   [x, y, z, str]
// texel 2i+1: [sig, isOwn, 0, 0]
uniform sampler2D uNodeData;
uniform float     uTexWidth;
uniform vec3      uFactionColor;
uniform float     uThreshold;
uniform float     uGapMargin;
uniform float     uAlpha;

const vec3 LIGHT_DIR = vec3(0.5, 0.9, 0.7);

vec4 nodeTexel(int idx) {
  float u = (float(idx) + 0.5) / uTexWidth;
  return texture2D(uNodeData, vec2(u, 0.5));
}

// Evaluate the field at a world position: own/other faction field sums and
// the analytical gradient of the own field (for the surface normal).
void fieldAt(vec3 pos, out float ownField, out float otherField, out vec3 gradSum) {
  ownField   = 0.0;
  otherField = 0.0;
  gradSum    = vec3(0.0);
  for (int i = 0; i < NODE_COUNT; i++) {
    vec4 nd0 = nodeTexel(i * 2);
    vec4 nd1 = nodeTexel(i * 2 + 1);

    float sig = max(nd1.x, 0.001);
    vec3  d   = pos - nd0.xyz;
    float r2  = dot(d, d) / (sig * sig);
    if (r2 >= 1.0) continue;
    float w = 1.0 - r2;
    float g = nd0.w * w * w;

    if (nd1.y > 0.5) {
      ownField += g;
      gradSum  += (-4.0 * nd0.w * w / (sig * sig)) * d;
    } else {
      otherField += g;
    }
  }
}

// Cheap variant for the bracket search: own/other sums only, no gradient.
// The surface normal is only needed once, at the refined hit point.
void fieldOO(vec3 pos, out float ownField, out float otherField) {
  ownField   = 0.0;
  otherField = 0.0;
  for (int i = 0; i < NODE_COUNT; i++) {
    vec4 nd0 = nodeTexel(i * 2);
    vec4 nd1 = nodeTexel(i * 2 + 1);
    float sig = max(nd1.x, 0.001);
    vec3  d   = pos - nd0.xyz;
    float r2  = dot(d, d) / (sig * sig);
    if (r2 >= 1.0) continue;
    float w = 1.0 - r2;
    float g = nd0.w * w * w;
    if (nd1.y > 0.5) ownField += g; else otherField += g;
  }
}

bool inside(float ownField, float otherField) {
  return ownField >= uThreshold && ownField > otherField * uGapMargin;
}

void main() {
  vec3 rayDir = normalize(vWorldPos - cameraPosition);

  vec3  oc   = cameraPosition - uSphereCenter;
  float b    = dot(oc, rayDir);
  float c    = dot(oc, oc) - uSphereRadius * uSphereRadius;
  float disc = b * b - c;
  if (disc < 0.0) discard;

  float sqrtD  = sqrt(disc);
  float tEntry = max(-b - sqrtD, 0.0);
  float tExit  = -b + sqrtD;
  if (tExit <= tEntry) discard;

  float stepSize = (tExit - tEntry) / float(MARCH_STEPS);
  vec3  L        = normalize(LIGHT_DIR);

  float tPrev = tEntry;
  for (int s = 1; s <= MARCH_STEPS; s++) {
    float tCur = tEntry + float(s) * stepSize;
    vec3  pos  = cameraPosition + tCur * rayDir;

    float ownField, otherField;
    fieldOO(pos, ownField, otherField);

    if (inside(ownField, otherField)) {
      // Step crossed the surface between tPrev and tCur. Bisect to pin the
      // exact crossing so the normal and alpha come from the true surface,
      // not whichever discrete step happened to land first. Without this the
      // shading bands along step boundaries (more steps -> more bands).
      float ta = tPrev, tb = tCur;
      for (int k = 0; k < 5; k++) {
        float tm = 0.5 * (ta + tb);
        float om, otm;
        fieldOO(cameraPosition + tm * rayDir, om, otm);
        if (inside(om, otm)) tb = tm; else ta = tm;
      }

      vec3  hitPos = cameraPosition + tb * rayDir;
      float ownH, otherH; vec3 gradH;
      fieldAt(hitPos, ownH, otherH, gradH);

      vec3  N    = normalize(-gradH);
      vec3  V    = -rayDir;
      float diff = clamp(dot(N, L), 0.0, 1.0);
      vec3  R    = reflect(-L, N);
      float spec = pow(clamp(dot(R, V), 0.0, 1.0), 32.0) * 0.40;
      float rim  = pow(1.0 - clamp(dot(N, V), 0.0, 1.0), 2.5) * 0.20;
      vec3  lit  = uFactionColor * (0.30 + 0.70 * diff) + vec3(spec + rim);
      gl_FragColor = vec4(lit, uAlpha);
      return;
    }
    tPrev = tCur;
  }
  discard;
}
` }

// ---------------------------------------------------------------------------

function dist3sq(a: NodeDto, b: NodeDto): number {
  const dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z
  return dx * dx + dy * dy + dz * dz
}

function dist3(a: NodeDto, b: NodeDto): number {
  return Math.sqrt(dist3sq(a, b))
}

function adaptiveSigma(node: NodeDto, factionNodes: NodeDto[], p: BlobParams): number {
  let minDist = Infinity
  for (const other of factionNodes) {
    if (other === node) continue
    const d = dist3(node, other)
    if (d < minDist) minDist = d
  }
  if (minDist > p.maxSearchDist) return p.isolatedSigma
  return minDist * p.sigmaScale
}

// Connected-component clustering via union-find.
// Two nodes are connected if their distance is within the threshold.
function clusterNodes(nodes: NodeDto[], threshold: number): NodeDto[][] {
  const n = nodes.length
  const parent = new Int32Array(n)
  for (let i = 0; i < n; i++) parent[i] = i

  function find(i: number): number {
    while (parent[i] !== i) { parent[i] = parent[parent[i]]; i = parent[i] }
    return i
  }

  const threshSq = threshold * threshold
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) {
      if (dist3sq(nodes[i], nodes[j]) <= threshSq) {
        const ri = find(i), rj = find(j)
        if (ri !== rj) parent[ri] = rj
      }
    }
  }

  const groups = new Map<number, NodeDto[]>()
  for (let i = 0; i < n; i++) {
    const root = find(i)
    if (!groups.has(root)) groups.set(root, [])
    groups.get(root)!.push(nodes[i])
  }
  return Array.from(groups.values())
}

// ---------------------------------------------------------------------------

export class BlobTerritoryRenderer {
  readonly group = new THREE.Group()
  private meshes: THREE.Mesh[] = []
  private textures: THREE.DataTexture[] = []

  update(world: WorldSnapshot, visibleFactions: Set<string>, p: BlobParams = DEFAULT_BLOB_PARAMS): void {
    this.clear()

    const owned = world.nodes.filter(
      n => n.ownerTag && visibleFactions.has(n.ownerTag) && n.radius > 0,
    )
    if (owned.length === 0) return

    const byFaction = new Map<string, NodeDto[]>()
    for (const n of owned) {
      const tag = n.ownerTag!
      if (!byFaction.has(tag)) byFaction.set(tag, [])
      byFaction.get(tag)!.push(n)
    }

    const sigmaOf = new Map<NodeDto, number>()
    for (const n of owned) {
      sigmaOf.set(n, adaptiveSigma(n, byFaction.get(n.ownerTag!)!, p))
    }

    for (const [tag, factionNodes] of byFaction) {
      const factionColor = new THREE.Color(factionColorNum(tag))

      // Split this faction's nodes into spatial clusters using cullDist.
      // Nodes that share field influence are already within this radius of
      // each other, so clusters map naturally to visually connected regions.
      const clusters = clusterNodes(factionNodes, p.cullDist)

      for (const cluster of clusters) {
        // ---- Centroid + core span of this cluster's own members ----------
        let cx = 0, cy = 0, cz = 0
        for (const n of cluster) { cx += n.x; cy += n.y; cz += n.z }
        cx /= cluster.length; cy /= cluster.length; cz /= cluster.length

        let coreR = 0
        for (const n of cluster) {
          const dx = n.x - cx, dy = n.y - cy, dz = n.z - cz
          const d = Math.sqrt(dx * dx + dy * dy + dz * dz)
          if (d > coreR) coreR = d
        }

        // ---- Nodes for this cluster's texture ----------------------------
        // Include any node (own or enemy) whose compact support (radius = sigma)
        // reaches the cluster core. Own neighbours keep adjacent clusters'
        // surfaces continuous; enemy neighbours feed the dominance gap. We
        // deliberately do NOT pull in far nodes — their field never touches
        // this volume, and including them would only bloat the texture.
        const textureNodes: Array<{ n: NodeDto; own: boolean; d: number; sig: number }> = []
        for (const n of owned) {
          const sig = sigmaOf.get(n) ?? p.isolatedSigma
          const dx = n.x - cx, dy = n.y - cy, dz = n.z - cz
          const d = Math.sqrt(dx * dx + dy * dy + dz * dz)
          if (d > coreR + sig) continue
          textureNodes.push({ n, own: n.ownerTag === tag, d, sig })
        }

        // ---- Bounding sphere contains the FULL support of every rendered
        // node, so no field is clipped at the sphere boundary (the cause of
        // the concentric-ring artifact). Support radius = sigma (kernel is
        // exactly zero beyond it), so reach = dist(centroid,node) + sigma.
        let boundR = 0
        for (const tn of textureNodes) {
          const reach = tn.d + tn.sig
          if (reach > boundR) boundR = reach
        }
        boundR *= 1.05

        const nodeCount = textureNodes.length
        const texWidth  = nodeCount * 2
        const data      = new Float32Array(texWidth * 4)

        for (let i = 0; i < nodeCount; i++) {
          const { n, own, sig } = textureNodes[i]
          const str  = Math.max(n.holdStrength, MIN_STR)
          const base = i * 8
          data[base]     = n.x
          data[base + 1] = n.y
          data[base + 2] = n.z
          data[base + 3] = str
          data[base + 4] = sig
          data[base + 5] = own ? 1.0 : 0.0
          data[base + 6] = 0
          data[base + 7] = 0
        }

        const tex = new THREE.DataTexture(data, texWidth, 1, THREE.RGBAFormat, THREE.FloatType)
        tex.magFilter = THREE.NearestFilter
        tex.minFilter = THREE.NearestFilter
        tex.needsUpdate = true
        this.textures.push(tex)

        // ---- Shader + mesh -----------------------------------------------
        const centroid = new THREE.Vector3(cx, cy, cz)

        const mat = new THREE.ShaderMaterial({
          vertexShader:   VERT,
          fragmentShader: makeFrag(p, nodeCount),
          transparent:    true,
          // depthWrite MUST stay false: a raymarched sphere writes depth at
          // its bounding-sphere surface, not the iso-surface hit, so enabling
          // it makes overlapping clusters punch holes in each other (seams).
          // Correct metaball compositing is back-to-front alpha blending,
          // which three.js does for transparent meshes automatically.
          depthWrite:     false,
          depthTest:      true,
          blending:       THREE.NormalBlending,
          uniforms: {
            uSphereCenter: { value: centroid.clone() },
            uSphereRadius: { value: boundR },
            uNodeData:     { value: tex },
            uTexWidth:     { value: texWidth },
            uFactionColor: { value: factionColor },
            uThreshold:    { value: p.threshold },
            uGapMargin:    { value: p.gapMargin },
            uAlpha:        { value: p.alpha },
          },
        })

        const geo  = new THREE.SphereGeometry(boundR, 22, 16)
        const mesh = new THREE.Mesh(geo, mat)
        mesh.position.copy(centroid)
        this.group.add(mesh)
        this.meshes.push(mesh)
      }
    }
  }

  clear(): void {
    for (const mesh of this.meshes) {
      mesh.geometry.dispose()
      ;(mesh.material as THREE.Material).dispose()
      this.group.remove(mesh)
    }
    for (const tex of this.textures) tex.dispose()
    this.meshes = []
    this.textures = []
  }

  dispose(): void {
    this.clear()
  }
}
