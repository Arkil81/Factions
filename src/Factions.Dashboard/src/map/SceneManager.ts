import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import type { WorldSnapshot, NodeDto, FleetDto, CampaignFocusDto } from '../types/world'
import { factionColorNum, FLEET_KIND_COLORS } from './colors'
import { isEmptyNode } from './nodeLabel'
import { BlobTerritoryRenderer, BlobParams, DEFAULT_BLOB_PARAMS } from './BlobTerritoryRenderer'
import { kinematicTransitRatio, trailMilestoneDistances, routeFlownKm, routeLegProgressKm } from './fleetTransit'
import { routedStopCount, passedVertexCount } from './fleetRoute'
import { DEFENCE_HOLD_COLOR, ENGAGEMENT_COLOR, isEngaging, isHoldingDefence } from '../fleetEngagement'
import { siegeColor, siegeOf } from '../nodeSiege'
import { fleetEchelon, FleetEchelon } from './fleetEchelon'

// Blob layer renders at this fraction of screen resolution.
// At ½ res, fragment count drops 4× — enough headroom given clusters now
// bound the marched volume tightly, while keeping edges crisp enough that
// the bilinear upscale doesn't visibly alias.
const BLOB_RT_SCALE = 0.5

// Region membership is graph-based, while node positions may span an entire
// planetary body. Keep the overlay legible by encoding member count, not raw
// physical separation, in its on-map radius.
function campaignOverlayRadius(memberCount: number): number {
  return Math.min(120, 28 + Math.sqrt(Math.max(1, memberCount)) * 16)
}

interface FleetAnim {
  fleet: FleetDto
  mesh: THREE.Mesh
  routeLine: THREE.Line
  path: THREE.Vector3[]
  segLengths: number[]
  totalLength: number
}

/** The pulsing marker on a raid that is fighting at its target. Holds the `scaledMarkers` entry it
 *  registered rather than a plain size, because the pulse is written into that entry's base — the
 *  distance-scaling pass runs after this one each frame and would otherwise overwrite a scale set
 *  here. */
interface EngagementRing {
  ring: THREE.Mesh
  fleetMesh: THREE.Mesh
  scaleEntry: { obj: THREE.Object3D; base: number }
  baseSize: number
}

/** The pulsing marker on a node that is itself under attack. Same heartbeat as an engagement ring
 *  and the same reason for holding its `scaledMarkers` entry, but anchored to a fixed point rather
 *  than to a moving mesh: a player siege has no fleet to follow. */
interface SiegeRing {
  ring: THREE.Mesh
  position: THREE.Vector3
  scaleEntry: { obj: THREE.Object3D; base: number }
  baseSize: number
}

const ENGAGEMENT_COLOR_NUM = parseInt(ENGAGEMENT_COLOR.slice(1), 16)
const DEFENCE_HOLD_COLOR_NUM = parseInt(DEFENCE_HOLD_COLOR.slice(1), 16)

interface TrailParticle {
  sprite: THREE.Sprite
  emittedAtMs: number
}

const TRAIL_SPACING_KM = 100
const TRAIL_LIFETIME_MS = 45_000
const TRAIL_SIZE = 7

// Markers (node spheres, station rings, fleet cones, labels, trails) keep
// their world-space base size when viewed from at least this distance, and
// shrink linearly with camera proximity below it so their on-screen size
// stays constant instead of ballooning as the camera closes in — e.g. when
// inspecting surface nodes on a planet.
const MARKER_REF_DIST = 500

// Every fleet marker is this size, whatever the convoy. Strength is drawn above
// the marker as an echelon mark instead (fleetEchelon.ts) — size used to carry it,
// at 6 + convoySize * 1.5, which made a full squadron six times the area of a lone
// trader. 7.5 is what a single-ship fleet has always drawn.
const FLEET_MARKER_SIZE = 7.5
// Where the echelon mark's centre sits above the marker: just clear of the cone,
// which reaches 1.2 * size = 9.
const ECHELON_OFFSET = 18
// ... and clear of an alarm ring when the fleet draws one, since an engagement ring
// at the top of its pulse reaches 1.3 * size * 2.1 = 20.5. Applied only to the
// fleets that have a ring — every other marker would otherwise carry the gap too.
const ECHELON_OFFSET_RINGED = 27
// Sprite width; its canvas is 2:1, so the drawn height is half this.
const ECHELON_WIDTH = 22

interface Clickable {
  mesh: THREE.Mesh
  kind: 'node' | 'fleet'
  id: string
}

interface PlanetBlocker {
  mesh: THREE.Mesh
  center: THREE.Vector3
  surfaceRadius: number
  name: string
}

// Planet-attached camera state: the camera sits at center + u * radius and
// travels along the great circle whose plane normal is n (also camera up).
interface OrbitAttach {
  center: THREE.Vector3
  surfaceRadius: number
  name: string
  u: THREE.Vector3
  n: THREE.Vector3
  radius: number
}

const ORBIT_DRAG_SPEED = 0.005 // radians per pixel
// Attaching to a planet eases the camera into its orbit pose over this long
// instead of snapping position and aim in one frame.
const ORBIT_ENTER_MS = 800

export class SceneManager {
  private scene: THREE.Scene
  private camera: THREE.PerspectiveCamera
  private renderer: THREE.WebGLRenderer
  private controls: OrbitControls

  private nodeGroup    = new THREE.Group()
  private fleetGroup   = new THREE.Group()
  private routeGroup   = new THREE.Group()
  private trailGroup   = new THREE.Group()
  private campaignGroup = new THREE.Group()
  private planetGroup  = new THREE.Group()
  private labelGroup   = new THREE.Group()
  private playerGroup  = new THREE.Group()
  private blobRenderer = new BlobTerritoryRenderer()

  // Offscreen blob pipeline
  private blobScene       = new THREE.Scene()
  private blobRT:           THREE.WebGLRenderTarget
  private compositorScene = new THREE.Scene()
  private orthoCamera     = new THREE.OrthographicCamera(-1, 1, 1, -1, -1, 1)
  private compositorMat:    THREE.MeshBasicMaterial

  private clickables: Clickable[] = []
  private fleetAnims: FleetAnim[] = []
  private engagementRings: EngagementRing[] = []
  private defenceRings: EngagementRing[] = []
  private siegeRings: SiegeRing[] = []
  private echelonSprites: Array<{ sprite: THREE.Sprite; fleetMesh: THREE.Mesh; baseOffset: number; baseScale: number }> = []
  private fleetTrailDistances = new Map<string, number>()
  private trailParticles: TrailParticle[] = []
  private labelSprites: Array<{ sprite: THREE.Sprite; nodePos: THREE.Vector3; up: THREE.Vector3; baseOffset: number; baseScale: number }> = []
  private campaignRings: THREE.Line[] = []
  private scaledMarkers: Array<{ obj: THREE.Object3D; base: number }> = []
  // Player markers live outside updateScene's rebuild cycle so a faster player
  // poll can refresh them without rebuilding the whole scene.
  private playerMarkers: Array<{ obj: THREE.Object3D; base: number }> = []
  private playerLabels: Array<{ sprite: THREE.Sprite; nodePos: THREE.Vector3; up: THREE.Vector3; baseOffset: number; baseScale: number }> = []
  private planetSpheres: Array<{ center: THREE.Vector3; radius: number }> = []
  private planetBlockers: PlanetBlocker[] = []

  private orbitAttach: OrbitAttach | null = null
  private orbitDragButton = -1
  private orbitDragLast = { x: 0, y: 0 }
  private orbitDragDist = 0
  private orbitEnterStart = 0
  private orbitEnterFromPos = new THREE.Vector3()
  private orbitEnterFromQuat = new THREE.Quaternion()

  private raycaster = new THREE.Raycaster()
  private mouse = new THREE.Vector2(-9999, -9999)

  private selectedKind: 'node' | 'fleet' | null = null
  private selectedId: string | null = null
  private originalColors = new Map<THREE.Mesh, THREE.Color>()

  private animId = 0
  private ro?: ResizeObserver
  private disposed = false
  private hasFit = false
  // Off by default: the raymarched blob pass is a per-pixel fragment-shader
  // cost paid every frame regardless of camera motion — noticeable on larger
  // worlds. Gated here (not just hidden) so disabling it actually skips the
  // render passes and the per-update shader recompile, not just the composite.
  private blobsVisible = false

  onNodeClick?: (id: string) => void
  onFleetClick?: (id: string) => void
  onOrbitChange?: (planetName: string | null) => void

  constructor(container: HTMLElement) {
    this.scene = new THREE.Scene()
    // No scene.background — we clear the renderer manually in animate()
    // so the blob compositor pass can sit between the background clear
    // and the main scene render.

    const w = container.clientWidth || 800
    const h = container.clientHeight || 600

    this.camera = new THREE.PerspectiveCamera(55, w / h, 0.1, 1e8)
    this.camera.position.set(0, 2000, 4000)

    this.renderer = new THREE.WebGLRenderer({ antialias: true, logarithmicDepthBuffer: true })
    this.renderer.setSize(w, h)
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    container.appendChild(this.renderer.domElement)

    // ---- Offscreen blob render target ------------------------------------
    this.blobRT = new THREE.WebGLRenderTarget(
      Math.max(1, Math.round(w * BLOB_RT_SCALE)),
      Math.max(1, Math.round(h * BLOB_RT_SCALE)),
      { minFilter: THREE.LinearFilter, magFilter: THREE.LinearFilter },
    )
    // Compositor: fullscreen quad that alpha-blends the blob RT onto the
    // cleared screen before the main scene renders on top.
    this.compositorMat = new THREE.MeshBasicMaterial({
      map: this.blobRT.texture,
      transparent: true,
      blending: THREE.NormalBlending,
      depthTest: false,
      depthWrite: false,
    })
    this.compositorScene.add(
      new THREE.Mesh(new THREE.PlaneGeometry(2, 2), this.compositorMat),
    )

    // Blob group lives in its own scene so it only renders into the RT
    this.blobScene.add(this.blobRenderer.group)

    // ---- Orbit controls --------------------------------------------------
    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enableDamping = true
    this.controls.dampingFactor = 0.06
    this.controls.zoomSpeed = 0.5
    this.controls.minDistance = 10
    this.controls.maxDistance = 1e7
    this.controls.screenSpacePanning = true

    // ---- Lighting (main scene only) -------------------------------------
    this.scene.add(new THREE.AmbientLight(0xffffff, 0.5))
    const sun = new THREE.DirectionalLight(0xffffff, 1.0)
    sun.position.set(1, 2, 1).normalize()
    this.scene.add(sun)

    const grid = new THREE.GridHelper(100_000, 40, 0x1a2332, 0x1a2332)
    this.scene.add(grid)
    this.scene.add(buildStarField())

    this.scene.add(this.nodeGroup)
    this.scene.add(this.routeGroup)
    this.scene.add(this.trailGroup)
    this.scene.add(this.campaignGroup)
    this.scene.add(this.planetGroup)
    this.scene.add(this.fleetGroup)
    this.scene.add(this.labelGroup)
    this.scene.add(this.playerGroup)

    this.renderer.domElement.addEventListener('click', this.onClick)
    this.renderer.domElement.addEventListener('mousemove', this.onMouseMove)
    this.renderer.domElement.addEventListener('pointerdown', this.onOrbitPointerDown)
    this.renderer.domElement.addEventListener('pointermove', this.onOrbitPointerMove)
    this.renderer.domElement.addEventListener('pointerup', this.onOrbitPointerUp)
    this.renderer.domElement.addEventListener('contextmenu', this.onOrbitContextMenu)
    this.renderer.domElement.addEventListener('wheel', this.onOrbitWheel, { passive: false })

    this.ro = new ResizeObserver(() => {
      const cw = container.clientWidth
      const ch = container.clientHeight
      if (!cw || !ch) return
      this.camera.aspect = cw / ch
      this.camera.updateProjectionMatrix()
      this.renderer.setSize(cw, ch)
      this.blobRT.setSize(
        Math.max(1, Math.round(cw * BLOB_RT_SCALE)),
        Math.max(1, Math.round(ch * BLOB_RT_SCALE)),
      )
    })
    this.ro.observe(container)

    this.animate()
  }

  // ---- Public API ---------------------------------------------------------------

  updateScene(world: WorldSnapshot, visibleFactions: Set<string>, showEmpty: boolean, blobParams: BlobParams = DEFAULT_BLOB_PARAMS, blobsEnabled: boolean = this.blobsVisible, planetsEnabled: boolean = true): void {
    this.blobsVisible = blobsEnabled
    this.planetGroup.visible = planetsEnabled
    if (this.orbitAttach && !planetsEnabled) this.exitOrbit()
    clearGroup(this.nodeGroup)
    clearGroup(this.fleetGroup)
    clearGroup(this.routeGroup)
    clearGroup(this.campaignGroup)
    clearGroup(this.planetGroup)
    clearGroup(this.labelGroup)
    this.clickables = []
    this.fleetAnims = []
    this.engagementRings = []
    this.defenceRings = []
    this.siegeRings = []
    this.labelSprites = []
    this.echelonSprites = []
    this.campaignRings = []
    this.scaledMarkers = []
    this.planetBlockers = []
    this.originalColors.clear()

    const nodePos = new Map<string, THREE.Vector3>()
    for (const n of world.nodes) nodePos.set(n.id, new THREE.Vector3(n.x, n.y, n.z))

    // For nodes on/near a planet, labels hover outward along the surface
    // normal instead of world +Y — a +Y offset would bury lower-hemisphere
    // labels inside the now-opaque planet body. Kept on the instance so
    // updatePlayers (refreshed on its own cadence) can use the same logic.
    this.planetSpheres = (world.planets ?? []).map(p => ({
      center: new THREE.Vector3(p.x, p.y, p.z),
      radius: Math.max(p.surfaceRadius * 1.25, p.atmosphereRadius),
    }))
    const labelUpAt = (pos: THREE.Vector3): THREE.Vector3 => this.labelUpAt(pos)

    const sharedNodeGeo  = new THREE.SphereGeometry(1, 20, 14)
    // A four-sided cone reads as an arrowhead. Its local +Y axis is aligned to
    // the route direction below; unlike the former octahedron it never tumbles.
    const sharedFleetGeo = new THREE.ConeGeometry(0.8, 2.4, 4)

    for (const planet of world.planets ?? []) {
      const color = planetColor(planet.generator)
      // Opaque and depth-writing so markers on the far side are occluded;
      // only the translucent atmosphere shell lets things show through.
      const body = new THREE.Mesh(
        new THREE.SphereGeometry(planet.surfaceRadius, 24, 16),
        new THREE.MeshPhongMaterial({ color }),
      )
      body.position.set(planet.x, planet.y, planet.z)
      this.planetGroup.add(body)
      this.planetBlockers.push({
        mesh: body,
        center: body.position.clone(),
        surfaceRadius: planet.surfaceRadius,
        name: planet.name,
      })
      if (planet.hasAtmosphere && planet.atmosphereRadius > planet.surfaceRadius) {
        const atmosphere = new THREE.Mesh(
          new THREE.SphereGeometry(planet.atmosphereRadius, 24, 16),
          new THREE.MeshBasicMaterial({ color: 0x69b7ff, transparent: true, opacity: 0.055, side: THREE.BackSide, depthWrite: false }),
        )
        atmosphere.position.copy(body.position)
        atmosphere.renderOrder = -3
        this.planetGroup.add(atmosphere)
      }
    }

    for (const node of world.nodes) {
      const visible = !node.ownerTag || visibleFactions.has(node.ownerTag)
      if (!visible) continue

      const isEmpty = isEmptyNode(node)
      if (isEmpty && !showEmpty) continue

      if (isEmpty) {
        const emptyColor = node.ownerTag ? factionColorNum(node.ownerTag) : 0x445566
        const emptyMat = new THREE.MeshBasicMaterial({
          color: emptyColor,
          transparent: true,
          opacity: 0.18,
          depthWrite: false,
        })
        const emptyMesh = new THREE.Mesh(sharedNodeGeo, emptyMat)
        emptyMesh.position.set(node.x, node.y, node.z)
        emptyMesh.scale.setScalar(3)
        this.nodeGroup.add(emptyMesh)
        this.scaledMarkers.push({ obj: emptyMesh, base: 3 })
        continue
      }

      const color = node.ownerTag ? factionColorNum(node.ownerTag) : 0x445566
      const visualRadius = 5

      const mat = new THREE.MeshStandardMaterial({
        color,
        roughness: 0.35,
        metalness: 0.55,
        emissive: color,
        emissiveIntensity: 0.15,
      })
      const mesh = new THREE.Mesh(sharedNodeGeo, mat)
      mesh.position.set(node.x, node.y, node.z)
      mesh.scale.setScalar(visualRadius)
      this.nodeGroup.add(mesh)
      this.scaledMarkers.push({ obj: mesh, base: visualRadius })
      this.originalColors.set(mesh, new THREE.Color(color))

      if (node.hasStation) {
        const ringColor = node.hasInfrastructure ? color : 0xd0d7de
        const ringOpacity = node.hasInfrastructure ? 0.9 : 0.55
        const ringSize = 7
        const ringGeo = new THREE.RingGeometry(ringSize, ringSize + 2.8, 28)
        const ringMat = new THREE.MeshBasicMaterial({
          color: ringColor,
          transparent: true,
          opacity: ringOpacity,
          side: THREE.DoubleSide,
          depthWrite: false,
        })
        const ring = new THREE.Mesh(ringGeo, ringMat)
        ring.position.set(node.x, node.y, node.z)
        ring.lookAt(this.camera.position)
        this.nodeGroup.add(ring)
        this.scaledMarkers.push({ obj: ring, base: 1 })
      }

      // A node under siege draws its own alarm. This is deliberately node-anchored rather than
      // fleet-anchored: a player besieging a station produces no fleet, so a ring built inside the
      // fleet loop -- as the engagement ring is -- has nothing to hang off and the board reads as
      // peaceful while a station is being taken. See docs/issues/FAC-258.
      const siege = siegeOf(node)
      if (siege) {
        const siegeMat = new THREE.MeshBasicMaterial({
          color: parseInt(siegeColor(siege).slice(1), 16),
          transparent: true,
          opacity: 0.7,
          side: THREE.DoubleSide,
          depthWrite: false,
        })
        const siegeRing = new THREE.Mesh(new THREE.RingGeometry(1, 1.3, 48), siegeMat)
        const position = new THREE.Vector3(node.x, node.y, node.z)
        siegeRing.position.copy(position)
        siegeRing.lookAt(this.camera.position)
        this.nodeGroup.add(siegeRing)
        const scaleEntry = { obj: siegeRing as THREE.Object3D, base: visualRadius * 2.4 }
        this.scaledMarkers.push(scaleEntry)
        this.siegeRings.push({ ring: siegeRing, position, scaleEntry, baseSize: visualRadius })
      }

      if (node.ownerTag) {
        this.clickables.push({ mesh, kind: 'node', id: node.id })
      }

      if (node.hasStation) {
        const labelText = node.displayName ?? node.id
        const sprite = makeLabel(labelText)
        const labelScale = Math.max(60, visualRadius * 4)
        const labelNodePos = new THREE.Vector3(node.x, node.y, node.z)
        const up = labelUpAt(labelNodePos)
        const baseOffset = visualRadius + 18
        // Position and scale are set per-frame in updateLabels(), where the
        // hover offset shrinks together with the marker it hovers over.
        sprite.position.copy(labelNodePos).addScaledVector(up, baseOffset)
        sprite.scale.set(labelScale, labelScale * 0.28, 1)
        this.labelGroup.add(sprite)
        this.labelSprites.push({
          sprite,
          nodePos: labelNodePos,
          up,
          baseOffset,
          baseScale: labelScale,
        })
      }
    }

    // A focus is a rotating dashed circle facing the camera. The persisted members
    // determine its radius, keeping simultaneous focus regions visibly distinct.
    for (const faction of world.factions) {
      if (!visibleFactions.has(faction.tag)) continue
      const color = factionColorNum(faction.tag)
      for (const focus of faction.campaignFoci ?? []) {
        const center = nodePos.get(focus.centerNodeId)
        if (!center) continue
        const memberCount = focus.regionMembers.filter(memberId => nodePos.has(memberId)).length + 1
        const radius = campaignOverlayRadius(memberCount)
        const points: THREE.Vector3[] = []
        for (let i = 0; i <= 64; i++) {
          const angle = (i / 64) * Math.PI * 2
          points.push(new THREE.Vector3(Math.cos(angle) * radius, Math.sin(angle) * radius, 0))
        }
        const ring = new THREE.Line(
          new THREE.BufferGeometry().setFromPoints(points),
          new THREE.LineDashedMaterial({ color, dashSize: Math.max(8, radius * 0.14), gapSize: Math.max(5, radius * 0.08), transparent: true, opacity: 0.9, depthWrite: false }),
        )
        ring.computeLineDistances()
        ring.userData.radius = radius
        ring.position.copy(center)
        ring.lookAt(this.camera.position)
        this.campaignGroup.add(ring)
        this.campaignRings.push(ring)
      }
    }

    if (this.blobsVisible) {
      this.blobRenderer.update(world, visibleFactions, blobParams)
    } else {
      this.blobRenderer.clear()
    }

    for (const fleet of world.fleets) {
      if (!visibleFactions.has(fleet.actorTag)) continue

      const hexColor = FLEET_KIND_COLORS[fleet.kind] ?? '#ffffff'
      const color = parseInt(hexColor.slice(1), 16)

      const fMat = new THREE.MeshBasicMaterial({ color })
      const fMesh = new THREE.Mesh(sharedFleetGeo, fMat)
      // Sized to sit just above a node sphere (5) on screen; the cone geometry is
      // 2.4 units tall, so the raw world size here reads ~2.4x bigger. Constant:
      // how many hulls are in there is the echelon mark's job, below.
      const size = FLEET_MARKER_SIZE
      fMesh.scale.setScalar(size)
      fMesh.position.set(fleet.currentX, fleet.currentY, fleet.currentZ)
      this.fleetGroup.add(fMesh)
      this.scaledMarkers.push({ obj: fMesh, base: size })
      this.clickables.push({ mesh: fMesh, kind: 'fleet', id: fleet.fleetId })
      this.originalColors.set(fMesh, new THREE.Color(color))

      // Strength, above the marker. Drawn in the fleet's own kind colour so the mark reads as
      // part of that marker and not as a separate contact; positioned per-frame in updateLabels,
      // where it follows the moving mesh and shrinks with it.
      const echelon = fleetEchelon(fleet.convoySize)
      if (echelon.glyph !== 'none') {
        const sprite = makeEchelonSprite(echelon, hexColor)
        sprite.position.copy(fMesh.position)
        this.fleetGroup.add(sprite)
        const ringed = isEngaging(fleet) || isHoldingDefence(fleet)
        this.echelonSprites.push({
          sprite,
          fleetMesh: fMesh,
          baseOffset: ringed ? ECHELON_OFFSET_RINGED : ECHELON_OFFSET,
          baseScale: ECHELON_WIDTH,
        })
      }

      const origin = new THREE.Vector3(fleet.originX, fleet.originY, fleet.originZ)
      const target = new THREE.Vector3(fleet.targetX, fleet.targetY, fleet.targetZ)
      // Origin to target, straight: that is the course the grid is commanded along and the line
      // the leg's ETA is billed for. pathNodeIds is an encounter-waypoint list, not a flight plan,
      // and drawing it would put the marker somewhere the convoy never is.
      //
      // Extended across the clearance polyline the fleet is flying when it has one: arc chords take
      // seconds while the snapshot poll is tens of seconds apart, so animating one leg at a time
      // would freeze the marker at every waypoint. It stops where the routing does. A stop past
      // routedStopCount has no waypoints yet -- the resolver plans each leg on arrival at the one
      // before it -- so the only line drawable for it is a chord straight through whatever body
      // lies between, and an unrouted leg is exactly what does not fly one.
      const path: THREE.Vector3[] = [origin, target]
      const stops = fleet.remainingStops ?? []
      const routed = routedStopCount(stops)
      for (let i = 0; i < routed; i++) {
        const stop = stops[i]
        let stopPos: THREE.Vector3 | null = null
        if (stop.hasPosition) stopPos = new THREE.Vector3(stop.x, stop.y, stop.z)
        else if (stop.nodeId) {
          const p = nodePos.get(stop.nodeId)
          if (p) stopPos = p.clone()
        }
        if (!stopPos) break
        path.push(stopPos)
      }

      const segLengths: number[] = []
      let total = 0
      for (let i = 0; i < path.length - 1; i++) {
        total += path[i].distanceTo(path[i + 1])
        segLengths.push(total)
      }

      this.orientFleet(fMesh, path[0], path[1])

      // Route lines show only the remaining leg. Origin-to-target lines made a
      // moving fleet look as though it was still departing from its old stop.
      const routePoints = [fMesh.position.clone(), ...path.slice(1)]
      const lineMat = new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.25 })
      const lineGeo = new THREE.BufferGeometry().setFromPoints(routePoints)
      const routeLine = new THREE.Line(lineGeo, lineMat)
      this.routeGroup.add(routeLine)
      this.fleetAnims.push({ fleet, mesh: fMesh, routeLine, path, segLengths, totalLength: total })

      // A fleet on its dwell is parked on the node it flew to, so on the map it reads as an
      // ordinary fleet at rest. A ring is what says the dwell is running, and it lives in
      // fleetGroup so hiding routes cannot hide it.
      //
      // Attack and defence get their own ring, and the difference is not only colour: an
      // engagement pulses because it is an alarm, a reinforce holds steady because a garrison
      // standing at a station is not one. Reading the stop kind alone would draw the alarm for
      // both, and on a live board the reinforces outnumber the raids.
      const dwellRingColor = isEngaging(fleet) ? ENGAGEMENT_COLOR_NUM
        : isHoldingDefence(fleet) ? DEFENCE_HOLD_COLOR_NUM
        : null
      if (dwellRingColor !== null) {
        const dwellMat = new THREE.MeshBasicMaterial({
          color: dwellRingColor,
          transparent: true,
          opacity: 0.7,
          side: THREE.DoubleSide,
          depthWrite: false,
        })
        const dwellRing = new THREE.Mesh(new THREE.RingGeometry(1, 1.3, 48), dwellMat)
        dwellRing.position.copy(fMesh.position)
        dwellRing.lookAt(this.camera.position)
        this.fleetGroup.add(dwellRing)
        const scaleEntry = { obj: dwellRing as THREE.Object3D, base: size * 1.8 }
        this.scaledMarkers.push(scaleEntry)
        const entry = { ring: dwellRing, fleetMesh: fMesh, scaleEntry, baseSize: size }
        if (isEngaging(fleet)) this.engagementRings.push(entry)
        else this.defenceRings.push(entry)
      }

      const ringMat = new THREE.MeshBasicMaterial({
        color,
        transparent: true,
        opacity: 0.4,
        side: THREE.DoubleSide,
      })
      const ringGeo = new THREE.RingGeometry(7, 10.5, 32)
      const ring = new THREE.Mesh(ringGeo, ringMat)
      ring.position.copy(target)
      ring.lookAt(this.camera.position)
      this.routeGroup.add(ring)
      this.scaledMarkers.push({ obj: ring, base: 1 })
    }

    if (!this.hasFit && world.nodes.length > 0) {
      this.fitCamera(world.nodes)
      this.hasFit = true
    }

    if (this.selectedId) this.applySelectionHighlight(this.selectedId, this.selectedKind)
  }

  setVisible(factionTag: string, visible: boolean): void {
    void factionTag; void visible
  }

  setRoutesVisible(visible: boolean): void {
    this.routeGroup.visible = visible
    this.trailGroup.visible = visible
  }

  setSelected(kind: 'node' | 'fleet' | null, id: string | null): void {
    for (const [mesh, origColor] of this.originalColors)
      (mesh.material as THREE.MeshBasicMaterial | THREE.MeshStandardMaterial).color.copy(origColor)

    this.selectedKind = kind
    this.selectedId = id
    if (id && kind) this.applySelectionHighlight(id, kind)
  }

  focusNode(nodeId: string, nodes: NodeDto[]): void {
    this.exitOrbit()
    const node = nodes.find(n => n.id === nodeId)
    if (!node) return
    const target = new THREE.Vector3(node.x, node.y, node.z)
    this.controls.target.copy(target)
    const dist = 150
    this.camera.position.set(target.x + dist, target.y + dist * 0.5, target.z + dist)
    this.controls.update()
  }

  focusCampaignRegion(focus: CampaignFocusDto, nodes: NodeDto[]): void {
    this.exitOrbit()
    const ids = new Set([focus.centerNodeId, ...focus.regionMembers])
    const members = nodes.filter(node => ids.has(node.id))
    if (members.length === 0) return
    const target = members.reduce((sum, node) => sum.add(new THREE.Vector3(node.x, node.y, node.z)), new THREE.Vector3()).multiplyScalar(1 / members.length)
    const dist = Math.max(180, campaignOverlayRadius(members.length) * 2.8)
    this.controls.target.copy(target)
    this.camera.position.set(target.x + dist, target.y + dist * 0.55, target.z + dist)
    this.controls.update()
  }

  // Diamond marker + name label per online player with a live position.
  // Both render with depthTest off: a player standing on a planet sits below the
  // body mesh's average-radius sphere (real terrain has valleys), so an occludable
  // marker would be buried inside the opaque planet exactly when it matters most.
  updatePlayers(players: Array<{ steamId: string; name: string; hasPosition: boolean; x: number; y: number; z: number }>): void {
    clearGroup(this.playerGroup)
    this.playerMarkers = []
    this.playerLabels = []
    for (const p of players) {
      if (!p.hasPosition) continue
      const mesh = new THREE.Mesh(
        new THREE.OctahedronGeometry(1),
        new THREE.MeshBasicMaterial({ color: 0x2ecc71, depthTest: false, transparent: true }),
      )
      mesh.renderOrder = 20
      mesh.position.set(p.x, p.y, p.z)
      const base = 4
      mesh.scale.setScalar(base)
      this.playerGroup.add(mesh)
      this.playerMarkers.push({ obj: mesh, base })

      const sprite = makeLabel(p.name)
      ;(sprite.material as THREE.SpriteMaterial).depthTest = false
      sprite.renderOrder = 21
      const pos = new THREE.Vector3(p.x, p.y, p.z)
      const up = this.labelUpAt(pos)
      const baseOffset = 10
      const baseScale = 45
      sprite.position.copy(pos).addScaledVector(up, baseOffset)
      sprite.scale.set(baseScale, baseScale * 0.28, 1)
      this.playerGroup.add(sprite)
      this.playerLabels.push({ sprite, nodePos: pos, up, baseOffset, baseScale })
    }
  }

  private labelUpAt(pos: THREE.Vector3): THREE.Vector3 {
    for (const p of this.planetSpheres) {
      if (pos.distanceTo(p.center) < p.radius) return pos.clone().sub(p.center).normalize()
    }
    return new THREE.Vector3(0, 1, 0)
  }

  focusPosition(x: number, y: number, z: number, dist = 60): void {
    this.exitOrbit()
    const target = new THREE.Vector3(x, y, z)
    this.controls.target.copy(target)
    this.camera.position.set(target.x + dist, target.y + dist * 0.5, target.z + dist)
    this.controls.update()
  }

  focusFleet(fleetId: string, fleets: FleetDto[]): void {
    this.exitOrbit()
    const fleet = fleets.find(f => f.fleetId === fleetId)
    if (!fleet) return
    const target = new THREE.Vector3(fleet.currentX, fleet.currentY, fleet.currentZ)
    this.controls.target.copy(target)
    const dist = 200
    this.camera.position.set(target.x + dist, target.y + dist, target.z + dist)
    this.controls.update()
  }

  dispose(): void {
    this.disposed = true
    cancelAnimationFrame(this.animId)
    this.ro?.disconnect()
    this.renderer.domElement.removeEventListener('click', this.onClick)
    this.renderer.domElement.removeEventListener('mousemove', this.onMouseMove)
    this.renderer.domElement.removeEventListener('pointerdown', this.onOrbitPointerDown)
    this.renderer.domElement.removeEventListener('pointermove', this.onOrbitPointerMove)
    this.renderer.domElement.removeEventListener('pointerup', this.onOrbitPointerUp)
    this.renderer.domElement.removeEventListener('contextmenu', this.onOrbitContextMenu)
    this.renderer.domElement.removeEventListener('wheel', this.onOrbitWheel)
    this.blobRenderer.dispose()
    this.blobRT.dispose()
    this.compositorMat.dispose()
    this.renderer.dispose()
  }

  // ---- Planet orbit mode --------------------------------------------------------

  private enterOrbit(b: PlanetBlocker): void {
    const offset = this.camera.position.clone().sub(b.center)
    const minR = b.surfaceRadius * 1.05
    const maxR = b.surfaceRadius * 50
    const radius = Math.min(maxR, Math.max(minR, offset.length()))
    const u = offset.lengthSq() > 1e-9 ? offset.normalize() : new THREE.Vector3(1, 0, 0)
    // Initial orbit plane: contains the camera direction, tilted as little as
    // possible from horizontal, so attaching doesn't roll the view.
    const n = new THREE.Vector3(0, 1, 0).addScaledVector(u, -u.y)
    if (n.lengthSq() < 1e-6) n.set(0, 0, 1).addScaledVector(u, -u.z)
    n.normalize()
    this.orbitAttach = { center: b.center.clone(), surfaceRadius: b.surfaceRadius, name: b.name, u, n, radius }
    this.orbitEnterStart = performance.now()
    this.orbitEnterFromPos.copy(this.camera.position)
    this.orbitEnterFromQuat.copy(this.camera.quaternion)
    this.controls.enabled = false
    this.onOrbitChange?.(b.name)
  }

  exitOrbit(): void {
    if (!this.orbitAttach) return
    this.controls.target.copy(this.orbitAttach.center)
    this.orbitAttach = null
    this.orbitDragButton = -1
    // OrbitControls assumes the camera up captured at construction.
    this.camera.up.set(0, 1, 0)
    this.controls.enabled = true
    this.controls.update()
    this.onOrbitChange?.(null)
  }

  private onOrbitPointerDown = (e: PointerEvent): void => {
    if (!this.orbitAttach) return
    if (e.button === 1) {
      e.preventDefault()
      this.exitOrbit()
      return
    }
    if (e.button === 0 || e.button === 2) {
      this.orbitDragButton = e.button
      this.orbitDragLast = { x: e.clientX, y: e.clientY }
      this.orbitDragDist = 0
      this.renderer.domElement.setPointerCapture?.(e.pointerId)
    }
  }

  private onOrbitPointerMove = (e: PointerEvent): void => {
    if (!this.orbitAttach || this.orbitDragButton < 0) return
    const dx = e.clientX - this.orbitDragLast.x
    const dy = e.clientY - this.orbitDragLast.y
    this.orbitDragLast = { x: e.clientX, y: e.clientY }
    this.orbitDragDist += Math.abs(dx) + Math.abs(dy)
    const o = this.orbitAttach
    if (this.orbitDragButton === 0) {
      // Travel along the current orbit plane.
      o.u.applyQuaternion(new THREE.Quaternion().setFromAxisAngle(o.n, -dx * ORBIT_DRAG_SPEED)).normalize()
    } else {
      // Vertical drag tilts the orbit plane about the screen-right axis
      // (inclination); horizontal drag rolls it about the radial axis.
      const right = new THREE.Vector3().crossVectors(o.n, o.u).normalize()
      const tilt = new THREE.Quaternion().setFromAxisAngle(right, -dy * ORBIT_DRAG_SPEED)
      o.u.applyQuaternion(tilt).normalize()
      o.n.applyQuaternion(tilt)
      o.n.applyQuaternion(new THREE.Quaternion().setFromAxisAngle(o.u, dx * ORBIT_DRAG_SPEED))
      // Re-orthogonalise so numeric drift can't shear the orbit basis.
      o.n.addScaledVector(o.u, -o.n.dot(o.u)).normalize()
    }
  }

  private onOrbitPointerUp = (): void => {
    this.orbitDragButton = -1
  }

  private onOrbitContextMenu = (e: MouseEvent): void => {
    if (this.orbitAttach) e.preventDefault()
  }

  private onOrbitWheel = (e: WheelEvent): void => {
    if (!this.orbitAttach) return
    e.preventDefault()
    const o = this.orbitAttach
    o.radius = Math.min(
      o.surfaceRadius * 50,
      Math.max(o.surfaceRadius * 1.05, o.radius * Math.exp(e.deltaY * 0.001)),
    )
  }

  // ---- Private ------------------------------------------------------------------

  private animate = (): void => {
    if (this.disposed) return
    this.animId = requestAnimationFrame(this.animate)
    if (this.orbitAttach) {
      const o = this.orbitAttach
      const targetPos = o.center.clone().addScaledVector(o.u, o.radius)
      const t = (performance.now() - this.orbitEnterStart) / ORBIT_ENTER_MS
      if (t < 1) {
        // Ease from the attach-time pose into the orbit pose. The target is
        // recomputed every frame so drags/zoom during the fly-in still land.
        const k = t * t * (3 - 2 * t)
        const targetQuat = new THREE.Quaternion().setFromRotationMatrix(
          new THREE.Matrix4().lookAt(targetPos, o.center, o.n),
        )
        this.camera.position.lerpVectors(this.orbitEnterFromPos, targetPos, k)
        this.camera.quaternion.slerpQuaternions(this.orbitEnterFromQuat, targetQuat, k)
      } else {
        this.camera.position.copy(targetPos)
        this.camera.up.copy(o.n)
        this.camera.lookAt(o.center)
      }
    } else {
      this.controls.update()
    }
    this.updateFleetPositions()
    this.updateMarkerScales()
    this.updateLabels()
    for (const ring of this.campaignRings) {
      const phase = ((ring.userData.phase as number | undefined) ?? 0) + 0.004
      ring.userData.phase = phase
      const radius = ring.userData.radius as number
      const positions = ring.geometry.getAttribute('position') as THREE.BufferAttribute
      for (let i = 0; i <= 64; i++) {
        const angle = (i / 64) * Math.PI * 2 + phase
        positions.setXYZ(i, Math.cos(angle) * radius, Math.sin(angle) * radius, 0)
      }
      positions.needsUpdate = true
      ring.lookAt(this.camera.position)
    }

    if (this.blobsVisible) {
      // Pass 1 — render blob clusters into the low-res offscreen RT.
      // depthTest: true in the blob materials prevents cluster-on-cluster
      // alpha stacking: the frontmost cluster surface wins each pixel.
      this.renderer.setRenderTarget(this.blobRT)
      this.renderer.setClearColor(0x000000, 0)
      this.renderer.clear()
      this.renderer.render(this.blobScene, this.camera)
      this.renderer.setRenderTarget(null)

      // Pass 2 — clear the screen to the background colour.
      this.renderer.setClearColor(0x0d1117, 1)
      this.renderer.clear()

      // Pass 3 — composite the upscaled blob RT onto the cleared screen.
      // autoClear off so subsequent passes don't erase it.
      this.renderer.autoClearColor   = false
      this.renderer.autoClearDepth   = false
      this.renderer.autoClearStencil = false
      this.renderer.render(this.compositorScene, this.orthoCamera)

      // Pass 4 — main scene (grid, nodes, fleets, labels) on top of blobs.
      // Depth buffer is clean from Pass 2; compositor wrote no depth.
      this.renderer.render(this.scene, this.camera)

      this.renderer.autoClearColor   = true
      this.renderer.autoClearDepth   = true
      this.renderer.autoClearStencil = true
    } else {
      // Blobs disabled: skip the offscreen raymarch + composite entirely —
      // that per-pixel fragment-shader pass is the actual perf cost, not
      // just its visibility. Plain clear + main scene render only.
      this.renderer.setClearColor(0x0d1117, 1)
      this.renderer.render(this.scene, this.camera)
    }
  }

  // Shrink factor keeping a marker's on-screen size constant once the camera
  // is closer than MARKER_REF_DIST; at or beyond it, markers stay at base size.
  private markerScale(pos: THREE.Vector3): number {
    return Math.min(1, this.camera.position.distanceTo(pos) / MARKER_REF_DIST)
  }

  private updateMarkerScales(): void {
    for (const { obj, base } of this.scaledMarkers) {
      obj.scale.setScalar(base * this.markerScale(obj.position))
    }
    for (const { obj, base } of this.playerMarkers) {
      obj.scale.setScalar(base * this.markerScale(obj.position))
    }
  }

  private updateLabels(): void {
    for (const { sprite, nodePos, up, baseOffset, baseScale } of this.labelSprites) {
      const k = this.markerScale(nodePos)
      sprite.position.copy(nodePos).addScaledVector(up, baseOffset * k)
      const s = baseScale * k
      sprite.scale.set(s, s * 0.28, 1)
      const dist = this.camera.position.distanceTo(nodePos)
      const opacity = 1 - Math.min(1, Math.max(0, (dist - 300) / 900))
      ;(sprite.material as THREE.SpriteMaterial).opacity = opacity
    }
    // Player labels: same shrink-with-zoom behavior, but no distance fade —
    // "where is the player" should stay readable from any range.
    for (const { sprite, nodePos, up, baseOffset, baseScale } of this.playerLabels) {
      const k = this.markerScale(nodePos)
      sprite.position.copy(nodePos).addScaledVector(up, baseOffset * k)
      const s = baseScale * k
      sprite.scale.set(s, s * 0.28, 1)
    }

    // Echelon marks hover along screen up, not world up or a surface normal: the mark belongs
    // above its own symbol however the camera is rolled, the way it would on a plotting board.
    if (this.echelonSprites.length > 0) {
      const screenUp = new THREE.Vector3(0, 1, 0).applyQuaternion(this.camera.quaternion)
      for (const { sprite, fleetMesh, baseOffset, baseScale } of this.echelonSprites) {
        const k = this.markerScale(fleetMesh.position)
        sprite.position.copy(fleetMesh.position).addScaledVector(screenUp, baseOffset * k)
        const s = baseScale * k
        sprite.scale.set(s, s * 0.5, 1)
      }
    }
  }

  private updateFleetPositions(): void {
    const now = Date.now()
    this.updateTrailParticles(now)
    for (const { fleet, mesh, routeLine, path, segLengths, totalLength } of this.fleetAnims) {
      const ratio = kinematicTransitRatio(fleet, now)

      if (path.length < 2 || totalLength === 0) {
        mesh.position.set(
          fleet.originX + (fleet.targetX - fleet.originX) * ratio,
          fleet.originY + (fleet.targetY - fleet.originY) * ratio,
          fleet.originZ + (fleet.targetZ - fleet.originZ) * ratio,
        )
        this.orientFleet(mesh, new THREE.Vector3(fleet.originX, fleet.originY, fleet.originZ), new THREE.Vector3(fleet.targetX, fleet.targetY, fleet.targetZ))
        this.emitFleetTrail(fleet.fleetId, path, segLengths, totalLength, ratio * totalLength,
                            ratio * totalLength, mesh.material as THREE.MeshBasicMaterial, now)
        this.updateRouteLineStart(routeLine, mesh.position, 0)
        continue
      }

      // On a clearance route the marker walks the whole polyline on the route's own profile; the
      // per-leg ratio would restart at every arc chord.
      const chainKm = routeLegProgressKm(fleet, now)
      const dist = Math.max(0, Math.min(totalLength, chainKm ?? ratio * totalLength))
      // Trail spacing counts route-absolute distance, which only increases; the polyline the client
      // draws shrinks as legs complete, so per-leg distance would stall the trail on short chords.
      const trailKm = routeFlownKm(fleet, now) ?? dist
      let seg = 0
      while (seg < segLengths.length - 1 && segLengths[seg] < dist) seg++
      const segStart = seg === 0 ? 0 : segLengths[seg - 1]
      const segEnd = segLengths[seg]
      const segLen = segEnd - segStart
      const t = segLen > 0 ? (dist - segStart) / segLen : 0
      mesh.position.lerpVectors(path[seg], path[seg + 1], t)
      this.orientFleet(mesh, path[seg], path[seg + 1])
      this.emitFleetTrail(fleet.fleetId, path, segLengths, totalLength, dist, trailKm, mesh.material as THREE.MeshBasicMaterial, now)
      this.updateRouteLineStart(routeLine, mesh.position, seg)
    }
    this.updateAlarmRings(now)
  }

  /** Beats every alarm ring on the map together, off the wall clock: a fight is an alarm, and two
   *  of them running at once should look like two of the same thing. ~2.5 s a cycle, slow enough to
   *  read as a heartbeat rather than a flicker.
   *
   *  Two kinds ride this beat — a raid fighting at its target, which follows its fleet's mesh, and
   *  a node under player siege, which is anchored to the node because it has no fleet. A defensive
   *  dwell is repositioned here too but deliberately does not pulse. */
  private updateAlarmRings(nowMs: number): void {
    if (this.engagementRings.length === 0 && this.defenceRings.length === 0
        && this.siegeRings.length === 0) return
    const pulse = 0.5 + 0.5 * Math.sin(nowMs / 400)
    for (const { ring, fleetMesh, scaleEntry, baseSize } of this.engagementRings) {
      ring.position.copy(fleetMesh.position)
      ring.lookAt(this.camera.position)
      scaleEntry.base = baseSize * (1.6 + 0.5 * pulse)
      ;(ring.material as THREE.MeshBasicMaterial).opacity = 0.35 + 0.45 * pulse
    }
    // A reinforce holding its dwell rides no beat at all. It shares the ring shape so the two are
    // legible as the same mechanic, and stays steady so it never competes for attention with the
    // alarms: what needs an operator is the station being attacked, not the one being shored up.
    for (const { ring, fleetMesh, scaleEntry, baseSize } of this.defenceRings) {
      ring.position.copy(fleetMesh.position)
      ring.lookAt(this.camera.position)
      scaleEntry.base = baseSize * 1.6
      ;(ring.material as THREE.MeshBasicMaterial).opacity = 0.45
    }
    // Same beat, deliberately: a raid fighting at a node and a player besieging one are the same
    // alarm to whoever owns it, and two alarms out of phase read as two different things.
    for (const { ring, position, scaleEntry, baseSize } of this.siegeRings) {
      ring.position.copy(position)
      ring.lookAt(this.camera.position)
      scaleEntry.base = baseSize * (2.2 + 0.7 * pulse)
      ;(ring.material as THREE.MeshBasicMaterial).opacity = 0.35 + 0.45 * pulse
    }
  }

  private positionAlongRoute(path: THREE.Vector3[], segLengths: number[], totalLength: number, distance: number): THREE.Vector3 {
    if (path.length < 2 || totalLength <= 0) return path[0]?.clone() ?? new THREE.Vector3()
    let seg = 0
    while (seg < segLengths.length - 1 && segLengths[seg] < distance) seg++
    const start = seg === 0 ? 0 : segLengths[seg - 1]
    const length = segLengths[seg] - start
    return new THREE.Vector3().lerpVectors(path[seg], path[seg + 1], length > 0 ? (distance - start) / length : 0)
  }

  /**
   * @param distanceKm  how far along `path` the marker currently is.
   * @param odometerKm  monotonic distance flown, which milestones are counted in. On a clearance
   *   route this spans the whole polyline while `path` only covers what is left to fly, so the two
   *   differ by a fixed offset for the life of a leg.
   */
  private emitFleetTrail(fleetId: string, path: THREE.Vector3[], segLengths: number[], totalLength: number, distanceKm: number, odometerKm: number, fleetMaterial: THREE.MeshBasicMaterial, nowMs: number): void {
    const previous = this.fleetTrailDistances.get(fleetId)
    this.fleetTrailDistances.set(fleetId, odometerKm)
    if (previous === undefined) return
    const pathOffsetKm = odometerKm - distanceKm
    for (const milestone of trailMilestoneDistances(previous, odometerKm, TRAIL_SPACING_KM)) {
      const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
        color: fleetMaterial.color,
        transparent: true,
        opacity: 0.9,
        depthWrite: false,
      }))
      sprite.position.copy(this.positionAlongRoute(path, segLengths, totalLength, milestone - pathOffsetKm))
      sprite.scale.setScalar(TRAIL_SIZE * this.markerScale(sprite.position))
      this.trailGroup.add(sprite)
      this.trailParticles.push({ sprite, emittedAtMs: nowMs })

    }
  }

  private updateTrailParticles(nowMs: number): void {
    this.trailParticles = this.trailParticles.filter(particle => {
      const age = (nowMs - particle.emittedAtMs) / TRAIL_LIFETIME_MS
      if (age >= 1) {
        this.trailGroup.remove(particle.sprite)
        particle.sprite.material.dispose()
        return false
      }
      particle.sprite.scale.setScalar(TRAIL_SIZE * (1 + age * 1.5) * this.markerScale(particle.sprite.position))
      ;(particle.sprite.material as THREE.SpriteMaterial).opacity = 0.9 * (1 - age)
      return true
    })
  }

  /**
   * Re-anchors the route line on the marker, and collapses the vertices behind it.
   *
   * The line's points are `[marker, ...path.slice(1)]`, so point `i` is `path[i]` for `i >= 1`.
   * Which ones those are is `passedVertexCount`. Collapsing them onto the marker makes their
   * segments zero-length, which draws nothing, and leaves the buffer the size it was allocated at.
   */
  private updateRouteLineStart(routeLine: THREE.Line, position: THREE.Vector3, segment: number): void {
    const points = routeLine.geometry.getAttribute('position') as THREE.BufferAttribute
    const passed = passedVertexCount(segment, points.count)
    for (let i = 0; i < passed; i++) points.setXYZ(i, position.x, position.y, position.z)
    points.needsUpdate = true
  }

  private orientFleet(mesh: THREE.Mesh, from: THREE.Vector3, to: THREE.Vector3): void {
    const direction = to.clone().sub(from)
    if (direction.lengthSq() > 1e-12)
      mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction.normalize())
  }

  private applySelectionHighlight(id: string, kind: 'node' | 'fleet' | null): void {
    for (const c of this.clickables) {
      if (c.id === id && c.kind === kind) {
        const mat = c.mesh.material as THREE.MeshBasicMaterial | THREE.MeshStandardMaterial
        mat.color.set(0xffffff)
      }
    }
  }

  private onClick = (e: MouseEvent): void => {
    // A left-drag in orbit mode ends with a click event; don't treat it as a
    // selection.
    if (this.orbitDragDist > 5) {
      this.orbitDragDist = 0
      return
    }
    const rect = this.renderer.domElement.getBoundingClientRect()
    this.mouse.set(
      ((e.clientX - rect.left) / rect.width) * 2 - 1,
      -((e.clientY - rect.top) / rect.height) * 2 + 1,
    )
    this.raycaster.setFromCamera(this.mouse, this.camera)
    const meshes = this.clickables.map(c => c.mesh)
    // Visible planet bodies block clicks so markers they occlude can't be
    // selected through them (raycasting ignores rendering occlusion).
    const blockers = this.planetGroup.visible ? this.planetBlockers : []
    const hits = this.raycaster.intersectObjects([...meshes, ...blockers.map(b => b.mesh)])
    if (hits.length === 0) return

    const hit = hits[0].object as THREE.Mesh
    const blocker = blockers.find(b => b.mesh === hit)
    if (blocker) {
      this.enterOrbit(blocker)
      return
    }
    const entry = this.clickables.find(c => c.mesh === hit)
    if (!entry) return

    if (entry.kind === 'node') this.onNodeClick?.(entry.id)
    else this.onFleetClick?.(entry.id)
  }

  private onMouseMove = (e: MouseEvent): void => {
    const rect = this.renderer.domElement.getBoundingClientRect()
    this.mouse.set(
      ((e.clientX - rect.left) / rect.width) * 2 - 1,
      -((e.clientY - rect.top) / rect.height) * 2 + 1,
    )
  }

  private fitCamera(nodes: NodeDto[]): void {
    const box = new THREE.Box3()
    for (const n of nodes) box.expandByPoint(new THREE.Vector3(n.x, n.y, n.z))
    const center = box.getCenter(new THREE.Vector3())
    const size = box.getSize(new THREE.Vector3()).length()
    const dist = Math.max(size * 0.7, 300)
    this.camera.position.set(center.x + dist * 0.6, center.y + dist * 0.5, center.z + dist)
    this.controls.target.copy(center)
    this.controls.update()
  }
}

// ---- Helpers ------------------------------------------------------------------

function planetColor(generator: string): number {
  switch (generator.toLowerCase()) {
    case 'earthlike': return 0x3a8f62
    case 'mars': return 0xa94a35
    case 'alien': return 0x7250a8
    case 'europa': return 0xc7d7e6
    case 'moon': return 0x9a9a9a
    case 'pertam': return 0xc99b55
    case 'titan': return 0xd99044
    case 'triton': return 0x8bd5e7
    default: return 0x697d98
  }
}

function clearGroup(g: THREE.Group): void {
  while (g.children.length > 0) {
    const child = g.children[0]
    g.remove(child)
    if (child instanceof THREE.Sprite) {
      child.geometry.dispose()
      const mat = child.material as THREE.SpriteMaterial
      mat.map?.dispose()
      mat.dispose()
    } else if (child instanceof THREE.Mesh) {
      child.geometry.dispose()
      if (Array.isArray(child.material)) child.material.forEach(m => m.dispose())
      else child.material.dispose()
    } else if (child instanceof THREE.Line) {
      child.geometry.dispose()
      ;(child.material as THREE.Material).dispose()
    }
  }
}

function makeLabel(text: string): THREE.Sprite {
  const W = 256, H = 64
  const canvas = document.createElement('canvas')
  canvas.width = W
  canvas.height = H
  const ctx = canvas.getContext('2d')!
  ctx.clearRect(0, 0, W, H)
  ctx.font = 'bold 22px monospace'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.shadowColor = 'rgba(0,0,0,0.9)'
  ctx.shadowBlur = 6
  ctx.fillStyle = '#c9d1d9'
  ctx.fillText(text, W / 2, H / 2)
  const tex = new THREE.CanvasTexture(canvas)
  // depthTest on so opaque planet bodies occlude far-side labels.
  const mat = new THREE.SpriteMaterial({ map: tex, transparent: true })
  return new THREE.Sprite(mat)
}

/**
 * The echelon mark that hovers over a fleet marker: dots, then bars, then a cross, in the tactical
 * convention where strength is a mark above the symbol rather than a bigger symbol. Repeating a
 * mark is what keeps a 20-hull fleet the same number of pixels as a lone one.
 */
function makeEchelonSprite(echelon: FleetEchelon, colorHex: string): THREE.Sprite {
  const W = 128, H = 64
  const canvas = document.createElement('canvas')
  canvas.width = W
  canvas.height = H
  const ctx = canvas.getContext('2d')!
  ctx.clearRect(0, 0, W, H)
  // The same dark glow the labels use, so a mark stays legible over a bright territory blob.
  ctx.shadowColor = 'rgba(0,0,0,0.9)'
  ctx.shadowBlur = 8
  ctx.fillStyle = colorHex
  ctx.strokeStyle = colorHex
  const cx = W / 2, cy = H / 2

  if (echelon.glyph === 'cross') {
    const r = 17
    ctx.lineWidth = 9
    ctx.lineCap = 'round'
    ctx.beginPath()
    ctx.moveTo(cx - r, cy - r); ctx.lineTo(cx + r, cy + r)
    ctx.moveTo(cx + r, cy - r); ctx.lineTo(cx - r, cy + r)
    ctx.stroke()
  } else {
    const spacing = echelon.glyph === 'dot' ? 26 : 22
    const first = cx - ((echelon.count - 1) * spacing) / 2
    for (let i = 0; i < echelon.count; i++) {
      const x = first + i * spacing
      if (echelon.glyph === 'dot') {
        ctx.beginPath()
        ctx.arc(x, cy, 9, 0, Math.PI * 2)
        ctx.fill()
      } else {
        ctx.fillRect(x - 5, cy - 20, 10, 40)
      }
    }
  }

  const tex = new THREE.CanvasTexture(canvas)
  // depthTest on, as for labels: a planet body hides the marks of what is behind it.
  return new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false }))
}

function buildStarField(): THREE.Points {
  const count = 3000
  const positions = new Float32Array(count * 3)
  for (let i = 0; i < count; i++) {
    positions[i * 3]     = (Math.random() - 0.5) * 4_000_000
    positions[i * 3 + 1] = (Math.random() - 0.5) * 4_000_000
    positions[i * 3 + 2] = (Math.random() - 0.5) * 4_000_000
  }
  const geo = new THREE.BufferGeometry()
  geo.setAttribute('position', new THREE.BufferAttribute(positions, 3))
  const mat = new THREE.PointsMaterial({ color: 0x334455, size: 80, sizeAttenuation: true })
  return new THREE.Points(geo, mat)
}
