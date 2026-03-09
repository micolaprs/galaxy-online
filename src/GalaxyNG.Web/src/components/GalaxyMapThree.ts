import * as THREE from 'three';

export interface ThreePlanet {
  name: string;
  x: number;
  y: number;
  size: number;
  color: string;
  ownerId: string | null;
  hasShips?: boolean;
  population?: number;
}

export interface ThreeFleetRoute {
  ownerId?: string;
  ownerName?: string;
  origin: string;
  destination: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  color: string;
  fleetName?: string;
  ships?: number;
  active?: boolean;
  speed?: number;
  progress?: number;
}

const STAR_COUNT = 800;
const ASTEROID_COUNT = 11;
const ROUTE_POINT_COUNT = 16;

interface PlanetVisual {
  planet: ThreePlanet;
  mesh: THREE.Mesh;
  halo: THREE.Mesh;
  tilt: number;
  rotationSpeed: number;
  wobbleOffset: number;
}

interface AsteroidFlyby {
  mesh: THREE.Mesh;
  velocity: THREE.Vector3;
  spin: THREE.Vector3;
  bounds: number;
}

interface ClickPulse {
  mesh: THREE.Mesh;
  life: number;
  ttl: number;
}

interface FleetRouteVisual {
  route: ThreeFleetRoute;
  traveledLine: THREE.Line;
  laneDashes: THREE.LineSegments;
  dashCount: number;
  ship: THREE.Group;
  shipGlow: THREE.Mesh;
  exhaustParticles: THREE.Points;
  thrustPhase: number;
  start: THREE.Vector3;
  end: THREE.Vector3;
}

export interface ThreeCombatEvents {
  turn: number;
  battlePlanets: string[];
  bombingPlanets: string[];
}

interface CombatMarkerVisual {
  type: 'battle' | 'bombing';
  primary: THREE.Mesh;
  secondary: THREE.Mesh;
  baseScale: number;
  phase: number;
}

interface CombatBurstVisual {
  type: 'battle' | 'bombing';
  mesh: THREE.Mesh;
  life: number;
  ttl: number;
}

interface ProductionRingVisual {
  mesh: THREE.Mesh;
  orbitSpeed: number;
  orbitPhase: number;
}

export class GalaxyMapThree {
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.OrthographicCamera;
  private starField!: THREE.Points;
  private starFieldBright!: THREE.Points;

  // Planet meshes & metadata
  private cleanupMeshes: THREE.Object3D[] = [];
  private planetVisuals: PlanetVisual[] = [];
  private asteroidFlybys: AsteroidFlyby[] = [];
  private clickPulses: ClickPulse[] = [];
  private planets: ThreePlanet[] = [];
  private fleetRoutes: ThreeFleetRoute[] = [];
  private routeVisuals: FleetRouteVisual[] = [];
  private combatEvents: ThreeCombatEvents = { turn: -1, battlePlanets: [], bombingPlanets: [] };
  private combatMarkers: CombatMarkerVisual[] = [];
  private combatBursts: CombatBurstVisual[] = [];
  private productionRings: ProductionRingVisual[] = [];
  // Pan animation
  private panTarget: { x: number; y: number } | null = null;
  private panAnimT = 0;
  private panAnimFrom = { x: 0, y: 0 };
  private showAllRoutes = false;
  private routeFocusPlanet: string | null = null;
  private galaxySize = 0;

  // Selection ring
  private selectionRing: THREE.Mesh | null = null;
  private selectedName: string | null      = null;
  private selectedPlanet: ThreePlanet | null = null;

  // Planet info overlay (HTML element over the canvas)
  private infoOverlay!: HTMLDivElement;

  // Pan / zoom
  private panX = 0;
  private panY = 0;
  private zoom = 1;       // world units per screen pixel (decreasing = zoom in)
  private dragging = false;
  private dragStart = { x: 0, y: 0, panX: 0, panY: 0 };

  private raycaster = new THREE.Raycaster();
  private pointer   = new THREE.Vector2();

  private animFrame: number | null = null;
  private clock = 0;          // animation time in seconds
  private lastTime = 0;        // last rAF timestamp

  /** Called when user clicks a planet. */
  onPlanetClick?: (name: string, screenX: number, screenY: number) => void;
  onFleetClick?: (route: ThreeFleetRoute, screenX: number, screenY: number) => void;
  onMapClick?: () => void;

  constructor(private container: HTMLElement) {
    this.init();
  }

  // ---- Public API ----

  setData(
    galaxySize: number,
    planets: ThreePlanet[],
    fleetRoutes: ThreeFleetRoute[] = [],
    combatEvents?: ThreeCombatEvents,
  ): void {
    const isFirstLoad = this.galaxySize === 0;
    const sizeChanged = this.galaxySize !== galaxySize;
    this.galaxySize = galaxySize;
    this.planets    = planets;
    this.fleetRoutes = fleetRoutes;
    this.combatEvents = combatEvents ?? { turn: -1, battlePlanets: [], bombingPlanets: [] };
    this.buildPlanetMeshes();
    if (isFirstLoad || sizeChanged) this.fitToView();
    this.updateRouteVisibility();
    this.render();
  }

  setRouteDisplay(showAll: boolean, focusPlanet: string | null): void {
    this.showAllRoutes = showAll;
    this.routeFocusPlanet = focusPlanet;
    this.updateRouteVisibility();
    this.render();
  }

  triggerTurnCombatBursts(events: ThreeCombatEvents): void {
    const spawnBurst = (planetName: string, type: 'battle' | 'bombing') => {
      const planet = this.planets.find(p => p.name === planetName);
      if (!planet) return;
      const radius = this.planetRadius(planet.size);
      const geo = new THREE.RingGeometry(radius * 1.3, radius * 1.55, 44);
      const mat = new THREE.MeshBasicMaterial({
        color: type === 'battle' ? 0xfb7185 : 0xfbbf24,
        transparent: true,
        opacity: 0.95,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const mesh = new THREE.Mesh(geo, mat);
      mesh.position.set(planet.x, -planet.y, 0.66);
      this.scene.add(mesh);
      this.combatBursts.push({
        type,
        mesh,
        life: 0,
        ttl: type === 'battle' ? 1.35 : 1.05,
      });
    };

    for (const planetName of events.battlePlanets) spawnBurst(planetName, 'battle');
    for (const planetName of events.bombingPlanets) spawnBurst(planetName, 'bombing');
  }

  select(name: string | null): void {
    this.selectedName = name;
    this.updateSelectionRing();
    this.render();
  }

  panToAndSelect(name: string): void {
    this.selectedName = name;
    this.updateSelectionRing();
    const planet = this.planets.find(p => p.name === name);
    if (planet) {
      this.panAnimFrom = { x: this.panX, y: this.panY };
      this.panTarget = { x: planet.x, y: -planet.y };
      this.panAnimT = 0;
    }
  }

  destroy(): void {
    if (this.animFrame !== null) cancelAnimationFrame(this.animFrame);
    this.disposeSceneObjects();
    for (const pulse of this.clickPulses) {
      this.scene.remove(pulse.mesh);
      pulse.mesh.geometry.dispose();
      (pulse.mesh.material as THREE.Material).dispose();
    }
    this.clickPulses = [];
    if (this.selectionRing) {
      this.scene.remove(this.selectionRing);
      this.selectionRing.geometry.dispose();
      (this.selectionRing.material as THREE.Material).dispose();
      this.selectionRing = null;
    }
    this.renderer.dispose();
    this.container.innerHTML = '';
  }

  /** Update overlay position (call after pan/zoom) — used internally by the loop. */
  private updateOverlayPosition(): void {
    if (!this.selectedPlanet) return;
    const p = this.selectedPlanet;
    const pos = this.worldToScreen(p.x, -p.y);
    this.infoOverlay.style.left = `${Math.round(pos.x + 18)}px`;
    this.infoOverlay.style.top  = `${Math.round(pos.y - 60)}px`;
  }

  resize(): void {
    const w = this.container.clientWidth  || 800;
    const h = this.container.clientHeight || 600;
    this.renderer.setSize(w, h);
    this.updateCamera();
    this.render();
  }

  // ---- Init ----

  private init(): void {
    const w = this.container.clientWidth  || 800;
    const h = this.container.clientHeight || 600;

    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(window.devicePixelRatio);
    this.renderer.setSize(w, h);
    this.renderer.setClearColor(0x0a1122);
    this.container.appendChild(this.renderer.domElement);

    this.scene  = new THREE.Scene();
    this.camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 1000);
    this.camera.position.set(0, 0, 10);

    this.buildStarField();
    this.setupLights();
    this.setupEvents();
    this.updateCamera();
    this.buildInfoOverlay();
    this.startLoop();
  }

  private buildStarField(): void {
    const positions = new Float32Array(STAR_COUNT * 3);
    for (let i = 0; i < STAR_COUNT; i++) {
      positions[i * 3] = (Math.random() - 0.5) * 1100;
      positions[i * 3 + 1] = (Math.random() - 0.5) * 1100;
      positions[i * 3 + 2] = -6;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));

    const mat = new THREE.PointsMaterial({
      color: 0xffffff, sizeAttenuation: true,
      size: 0.85, transparent: true, opacity: 0.7,
    });
    this.starField = new THREE.Points(geo, mat);
    this.scene.add(this.starField);

    const brightPositions = new Float32Array(Math.floor(STAR_COUNT * 0.35) * 3);
    for (let i = 0; i < brightPositions.length / 3; i++) {
      brightPositions[i * 3] = (Math.random() - 0.5) * 1300;
      brightPositions[i * 3 + 1] = (Math.random() - 0.5) * 1300;
      brightPositions[i * 3 + 2] = -7;
    }
    const brightGeo = new THREE.BufferGeometry();
    brightGeo.setAttribute('position', new THREE.BufferAttribute(brightPositions, 3));
    const brightMat = new THREE.PointsMaterial({
      color: 0xdbeafe,
      sizeAttenuation: true,
      size: 1.45,
      transparent: true,
      opacity: 0.82,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    });
    this.starFieldBright = new THREE.Points(brightGeo, brightMat);
    this.scene.add(this.starFieldBright);
  }

  private buildAsteroidFlybys(): void {
    this.disposeAsteroidFlybys();
    const span = Math.max(220, this.galaxySize * 1.5);

    for (let i = 0; i < ASTEROID_COUNT; i++) {
      const geo = new THREE.SphereGeometry(0.22 + Math.random() * 0.26, 10, 8);
      const mat = new THREE.MeshPhongMaterial({
        color: 0xcbd5e1,
        emissive: 0x1e293b,
        emissiveIntensity: 0.18,
        transparent: true,
        opacity: 0.45 + Math.random() * 0.25,
      });
      const mesh = new THREE.Mesh(geo, mat);
      mesh.position.set(
        (Math.random() - 0.5) * span,
        (Math.random() - 0.5) * span,
        1.5 + Math.random() * 2.5,
      );
      mesh.scale.set(1.8 + Math.random() * 1.4, 0.5 + Math.random() * 0.3, 0.55 + Math.random() * 0.4);
      mesh.rotation.set(Math.random() * Math.PI, Math.random() * Math.PI, Math.random() * Math.PI);
      this.scene.add(mesh);
      this.cleanupMeshes.push(mesh);
      this.asteroidFlybys.push({
        mesh,
        velocity: new THREE.Vector3(
          3.5 + Math.random() * 4,
          -1.2 - Math.random() * 2.2,
          0,
        ),
        spin: new THREE.Vector3(
          (Math.random() - 0.5) * 0.8,
          (Math.random() - 0.5) * 0.9,
          (Math.random() - 0.5) * 0.7,
        ),
        bounds: span * 0.7,
      });
    }
  }

  private setupLights(): void {
    const ambient = new THREE.AmbientLight(0x223355, 0.8);
    this.scene.add(ambient);

    const dir = new THREE.DirectionalLight(0x88bbff, 1.2);
    dir.position.set(50, 80, 100);
    this.scene.add(dir);
  }

  // ---- Planet meshes ----

  private buildPlanetMeshes(): void {
    this.disposeSceneObjects();
    if (this.selectionRing) {
      this.scene.remove(this.selectionRing);
      this.selectionRing.geometry.dispose();
      (this.selectionRing.material as THREE.Material).dispose();
      this.selectionRing = null;
    }

    for (const p of this.planets) {
      const radius = this.planetRadius(p.size);
      const geo    = new THREE.SphereGeometry(radius, 16, 12);
      const mat    = new THREE.MeshPhongMaterial({
        color:    this.hexColor(p.color),
        shininess: 40,
        emissive: this.hexColor(p.color),
        emissiveIntensity: 0.15,
      });
      const mesh = new THREE.Mesh(geo, mat);
      mesh.position.set(p.x, -p.y, 0);   // flip Y (game Y grows down)
      mesh.userData['planet'] = p.name;
      this.scene.add(mesh);
      this.cleanupMeshes.push(mesh);

      // Halo — tight, just outside the sphere
      const haloInner = radius * 1.18;
      const haloOuter = radius * 1.40;
      const haloGeo = new THREE.RingGeometry(haloInner, haloOuter, 40);
      const haloMat = new THREE.MeshBasicMaterial({
        color: this.hexColor(p.color),
        transparent: true,
        opacity: 0.14,
        blending: THREE.AdditiveBlending,
        side: THREE.DoubleSide,
        depthWrite: false,
      });
      const halo = new THREE.Mesh(haloGeo, haloMat);
      halo.position.set(p.x, -p.y, -0.12);
      this.scene.add(halo);
      this.cleanupMeshes.push(halo);
      this.planetVisuals.push({
        planet: p,
        mesh,
        halo,
        tilt: (Math.random() - 0.5) * 0.28,
        rotationSpeed: 0.11 + Math.random() * 0.18,
        wobbleOffset: Math.random() * Math.PI * 2,
      });

      // Ship dot
      if (p.hasShips) {
        const dotGeo = new THREE.SphereGeometry(radius * 0.32, 8, 6);
        const dotMat = new THREE.MeshBasicMaterial({ color: 0x38bdf8 });
        const dot    = new THREE.Mesh(dotGeo, dotMat);
        dot.position.set(p.x + radius * 1.1, -(p.y - radius * 1.1), 0.5);
        this.scene.add(dot);
        this.cleanupMeshes.push(dot);
      }

      // Production orbit ring — one subtle ring, tightly capped
      if (p.ownerId) {
        const devRatio = p.size > 0 ? Math.min(1, (p.population ?? 0) / p.size) : 0.3;
        const ringR = Math.min(radius * 1.5, 2.2); // cap absolute size
        const rGeo = new THREE.RingGeometry(ringR, ringR + 0.12, 32);
        const rMat = new THREE.MeshBasicMaterial({
          color: this.hexColor(p.color),
          transparent: true,
          opacity: 0.10 + devRatio * 0.14,
          side: THREE.DoubleSide,
          blending: THREE.AdditiveBlending,
          depthWrite: false,
        });
        const rMesh = new THREE.Mesh(rGeo, rMat);
        rMesh.position.set(p.x, -p.y, 0.05);
        this.scene.add(rMesh);
        this.cleanupMeshes.push(rMesh);
        this.productionRings.push({
          mesh: rMesh,
          orbitSpeed: 0.15 * (0.8 + Math.random() * 0.4) * (Math.random() < 0.5 ? 1 : -1),
          orbitPhase: Math.random() * Math.PI * 2,
        });
      }
    }

    this.buildAsteroidFlybys();
    this.buildFleetRouteMeshes();
    this.buildCombatMarkers();
    this.updateSelectionRing();
  }

  private buildCombatMarkers(): void {
    this.combatMarkers = [];
    if (!this.combatEvents) return;

    const markerPlanets = new Map<string, 'battle' | 'bombing'>();
    for (const planet of this.combatEvents.battlePlanets) markerPlanets.set(planet, 'battle');
    for (const planet of this.combatEvents.bombingPlanets) markerPlanets.set(planet, 'bombing');

    for (const [planetName, kind] of markerPlanets.entries()) {
      const planet = this.planets.find(p => p.name === planetName);
      if (!planet) continue;
      const radius = this.planetRadius(planet.size);
      const x = planet.x;
      const y = -planet.y;

      const primaryGeo = new THREE.RingGeometry(radius * 1.45, radius * 1.75, 52);
      const primaryMat = new THREE.MeshBasicMaterial({
        color: kind === 'battle' ? 0xf43f5e : 0xfb923c,
        transparent: true,
        opacity: 0.65,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const primary = new THREE.Mesh(primaryGeo, primaryMat);
      primary.position.set(x, y, 0.48);
      this.scene.add(primary);
      this.cleanupMeshes.push(primary);

      const secondaryGeo = kind === 'battle'
        ? new THREE.RingGeometry(radius * 1.9, radius * 2.15, 46)
        : new THREE.CircleGeometry(radius * 0.9, 36);
      const secondaryMat = new THREE.MeshBasicMaterial({
        color: kind === 'battle' ? 0xfda4af : 0xfbbf24,
        transparent: true,
        opacity: kind === 'battle' ? 0.45 : 0.35,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const secondary = new THREE.Mesh(secondaryGeo, secondaryMat);
      secondary.position.set(x, y, 0.51);
      this.scene.add(secondary);
      this.cleanupMeshes.push(secondary);

      this.combatMarkers.push({
        type: kind,
        primary,
        secondary,
        baseScale: 1 + Math.random() * 0.08,
        phase: Math.random() * Math.PI * 2,
      });
    }
  }

  private buildFleetRouteMeshes(): void {
    this.routeVisuals = [];

    for (const route of this.fleetRoutes) {
      const start = new THREE.Vector3(route.x1, -route.y1, -0.04);
      const end = new THREE.Vector3(route.x2, -route.y2, -0.04);
      const progress = this.routeProgress(route.progress);
      const shipPos = new THREE.Vector3().lerpVectors(start, end, progress);

      // Faint traveled line
      const traveledGeometry = new THREE.BufferGeometry().setFromPoints([start, shipPos]);
      const traveledMaterial = new THREE.LineBasicMaterial({
        color: this.hexColor(route.color),
        transparent: true,
        opacity: 0.12,
        depthTest: false,
      });
      const traveledLine = new THREE.Line(traveledGeometry, traveledMaterial);
      traveledLine.userData['fleetRoute'] = route;
      traveledLine.renderOrder = 10;
      this.scene.add(traveledLine);
      this.cleanupMeshes.push(traveledLine);

      // N dash segments for remaining path (N = remaining turns)
      const dashCount = this.computeRemainingTurns(route, start, end);
      const dashPositions = new Float32Array(dashCount * 2 * 3);
      const dashGeo = new THREE.BufferGeometry();
      dashGeo.setAttribute('position', new THREE.BufferAttribute(dashPositions, 3));
      const dashMat = new THREE.LineBasicMaterial({
        color: this.hexColor(route.color),
        transparent: true,
        opacity: 0.88,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        depthTest: false,
      });
      const laneDashes = new THREE.LineSegments(dashGeo, dashMat);
      laneDashes.userData['fleetRoute'] = route;
      laneDashes.renderOrder = 11;
      this.scene.add(laneDashes);
      this.cleanupMeshes.push(laneDashes);

      const ship = this.createFleetShip(route);
      ship.position.set(shipPos.x, shipPos.y, 0.22);
      this.scene.add(ship);
      this.cleanupMeshes.push(ship);

      // Fleet glow — always visible, scales with threat
      const ships = route.ships ?? 1;
      const glowRadius = this.fleetGlowRadius(ships);
      const glowGeo = new THREE.CircleGeometry(glowRadius, 20);
      const glowMat = new THREE.MeshBasicMaterial({
        color: this.hexColor(route.color),
        transparent: true,
        opacity: 0.28,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        side: THREE.DoubleSide,
      });
      const shipGlow = new THREE.Mesh(glowGeo, glowMat);
      shipGlow.position.set(shipPos.x, shipPos.y, 0.15);
      shipGlow.userData['fleetRoute'] = route;
      this.scene.add(shipGlow);
      this.cleanupMeshes.push(shipGlow);

      // Engine exhaust particles — always visible
      const exhaustCount = 10;
      const exhaustPos = new Float32Array(exhaustCount * 3);
      const exhaustGeo = new THREE.BufferGeometry();
      exhaustGeo.setAttribute('position', new THREE.BufferAttribute(exhaustPos, 3));
      const exhaustColor = ships > 100 ? 0xff7733 : ships > 30 ? 0xfbbf24 : 0x38bdf8;
      const exhaustMat = new THREE.PointsMaterial({
        color: exhaustColor,
        size: ships > 100 ? 7 : ships > 30 ? 5 : 3.5,
        sizeAttenuation: false,
        transparent: true,
        opacity: 0.85,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const exhaustParticles = new THREE.Points(exhaustGeo, exhaustMat);
      exhaustParticles.userData['fleetRoute'] = route;
      this.scene.add(exhaustParticles);
      this.cleanupMeshes.push(exhaustParticles);

      this.routeVisuals.push({
        route,
        traveledLine,
        laneDashes,
        dashCount,
        ship,
        shipGlow,
        exhaustParticles,
        thrustPhase: Math.random() * Math.PI * 2,
        start,
        end,
      });
    }
  }

  private updateSelectionRing(): void {
    if (this.selectionRing) {
      this.scene.remove(this.selectionRing);
      this.selectionRing.geometry.dispose();
      (this.selectionRing.material as THREE.Material).dispose();
      this.selectionRing = null;
    }

    const planet = this.selectedName
      ? this.planets.find(p => p.name === this.selectedName) ?? null
      : null;
    this.selectedPlanet = planet;

    if (!planet) {
      this.infoOverlay?.classList.add('hidden');
      return;
    }

    const r   = this.planetRadius(planet.size) + 0.8;
    const geo = new THREE.RingGeometry(r, r + 0.3, 32);
    const mat = new THREE.MeshBasicMaterial({
      color: 0xfacc15,
      side: THREE.DoubleSide,
      transparent: true,
      opacity: 0.82,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    });
    this.selectionRing = new THREE.Mesh(geo, mat);
    this.selectionRing.position.set(planet.x, -planet.y, 0.2);
    this.scene.add(this.selectionRing);

    this.showInfoOverlay(planet);
  }

  private planetRadius(size: number): number {
    return Math.max(0.4, Math.min(1.6, (size / 1000) * 2.8));
  }

  private hexColor(css: string): number {
    return parseInt(css.replace('#', ''), 16);
  }

  // ---- Planet info overlay ----

  private buildInfoOverlay(): void {
    this.infoOverlay = document.createElement('div');
    this.infoOverlay.className = 'gm-planet-info hidden';
    this.container.appendChild(this.infoOverlay);
  }

  private showInfoOverlay(p: ThreePlanet): void {
    const pop     = Math.round(p.population ?? 0);
    const devPct  = p.size > 0 ? Math.min(100, Math.round((pop / p.size) * 100)) : 0;
    const barColor = devPct >= 70 ? '#4ade80' : devPct >= 35 ? '#facc15' : '#f87171';
    const devLabel = devPct >= 80 ? 'Техноядро' : devPct >= 55 ? 'Промышленный мир' : devPct >= 30 ? 'Колония' : 'Форпост';
    const ownerBadge = p.ownerId
      ? `<span class="gmi-dot" style="background:${p.color}"></span>`
      : '';

    this.infoOverlay.innerHTML = `
      <div class="gmi-name">${ownerBadge}${esc(p.name)}</div>
      <div class="gmi-row"><span class="gmi-lbl">Размер</span><span class="gmi-val">${p.size}</span></div>
      <div class="gmi-row"><span class="gmi-lbl">Население</span><span class="gmi-val">${pop}</span></div>
      <div class="gmi-dev-chip">${devLabel}</div>
      <div class="gmi-bar-label">Развитие <span class="gmi-pct">${devPct}%</span></div>
      <div class="gmi-bar-bg">
        <div class="gmi-bar-fill" style="--target-w:${devPct}%;--bar-color:${barColor}"></div>
      </div>
    `;
    this.infoOverlay.classList.remove('hidden');
    this.updateOverlayPosition();
  }

  private worldToScreen(wx: number, wy: number): { x: number; y: number } {
    const w = this.container.clientWidth  || 800;
    const h = this.container.clientHeight || 600;
    const vec = new THREE.Vector3(wx, wy, 0);
    vec.project(this.camera);
    return { x: (vec.x + 1) / 2 * w, y: (-vec.y + 1) / 2 * h };
  }

  // ---- Camera ----

  private fitToView(): void {
    const w = this.container.clientWidth  || 800;
    const h = this.container.clientHeight || 600;
    const gs = this.galaxySize;

    // We want the galaxy to fit with padding
    const pad  = gs * 0.12;
    const worldW = gs + pad * 2;
    const worldH = gs + pad * 2;

    const aspect = w / h;
    if (worldW / aspect > worldH) {
      this.zoom = worldW / w;
    } else {
      this.zoom = worldH / h;
    }

    // Center: planets are placed at (x, -y) in world space,
    // so galaxy spans X: 0→gs, Y: -gs→0 → center is (gs/2, -gs/2)
    this.panX = gs / 2;
    this.panY = -(gs / 2);

    this.updateCamera();
  }

  private updateCamera(): void {
    const w = this.container.clientWidth  || 800;
    const h = this.container.clientHeight || 600;

    const halfW = (w / 2) * this.zoom;
    const halfH = (h / 2) * this.zoom;

    this.camera.left   = this.panX - halfW;
    this.camera.right  = this.panX + halfW;
    this.camera.top    = this.panY + halfH;
    this.camera.bottom = this.panY - halfH;
    this.camera.updateProjectionMatrix();
  }

  // ---- Events ----

  private setupEvents(): void {
    const el = this.renderer.domElement;

    el.addEventListener('wheel', e => {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 0.85 : 1 / 0.85;
      const rect   = el.getBoundingClientRect();
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      const w  = rect.width;
      const h  = rect.height;

      // World position under mouse before zoom
      const wx = this.panX + (mx - w / 2) * this.zoom;
      const wy = this.panY - (my - h / 2) * this.zoom;

      this.zoom *= factor;

      // Adjust pan so mouse stays over same world point
      this.panX = wx - (mx - w / 2) * this.zoom;
      this.panY = wy + (my - h / 2) * this.zoom;

      this.updateCamera();
      this.render();
    }, { passive: false });

    el.addEventListener('mousedown', e => {
      this.dragging  = true;
      this.dragStart = { x: e.clientX, y: e.clientY, panX: this.panX, panY: this.panY };
    });

    el.addEventListener('mousemove', e => {
      if (!this.dragging) return;
      this.panX = this.dragStart.panX - (e.clientX - this.dragStart.x) * this.zoom;
      this.panY = this.dragStart.panY + (e.clientY - this.dragStart.y) * this.zoom;
      this.updateCamera();
      this.render();
    });

    el.addEventListener('mouseup', e => {
      const dx = Math.abs(e.clientX - this.dragStart.x);
      const dy = Math.abs(e.clientY - this.dragStart.y);
      this.dragging = false;
      if (dx < 4 && dy < 4) this.handleClick(e);
    });

    el.addEventListener('mouseleave', () => { this.dragging = false; });
  }

  private handleClick(e: MouseEvent): void {
    this.onMapClick?.();

    const rect = this.renderer.domElement.getBoundingClientRect();
    this.pointer.x =  ((e.clientX - rect.left)  / rect.width)  * 2 - 1;
    this.pointer.y = -((e.clientY - rect.top)   / rect.height) * 2 + 1;

    this.raycaster.setFromCamera(this.pointer, this.camera);

    // Prefer planets over fleet visuals
    const planetTargets = this.planetVisuals.map(visual => visual.mesh);
    const planetHits    = this.raycaster.intersectObjects(planetTargets);
    if (planetHits.length > 0) {
      const name = planetHits[0].object.userData['planet'] as string;
      this.selectedName = name;
      this.updateSelectionRing();
      this.spawnClickPulse(planetHits[0].point.x, planetHits[0].point.y);
      this.render();
      this.onPlanetClick?.(name, e.clientX, e.clientY);
      return;
    }

    const fleetTargets: THREE.Object3D[] = [];
    for (const rv of this.routeVisuals) {
      fleetTargets.push(rv.traveledLine, rv.laneDashes, rv.ship, rv.shipGlow, rv.exhaustParticles);
    }
    this.raycaster.params.Line!.threshold = 1.2;
    this.raycaster.params.Points!.threshold = 10;
    const fleetHits = this.raycaster.intersectObjects(fleetTargets, true);
    const fleetRoute = fleetHits.find(h => h.object.userData['fleetRoute'])?.object.userData['fleetRoute'] as ThreeFleetRoute | undefined;
    if (fleetRoute) {
      this.spawnClickPulse(fleetHits[0]!.point.x, fleetHits[0]!.point.y);
      this.onFleetClick?.(fleetRoute, e.clientX, e.clientY);
    }
  }

  // ---- Animation loop ----

  private startLoop(): void {
    const loop = (ts: number) => {
      const dt = Math.min((ts - this.lastTime) / 1000, 0.1); // cap at 100ms
      this.lastTime = ts;
      this.clock += dt;
      this.animate(dt);
      this.renderer.render(this.scene, this.camera);
      this.animFrame = requestAnimationFrame(loop);
    };
    this.animFrame = requestAnimationFrame(loop);
  }

  private animate(dt: number): void {
    for (const visual of this.planetVisuals) {
      visual.mesh.rotation.y += visual.rotationSpeed * dt;
      visual.mesh.rotation.x = visual.tilt + Math.sin(this.clock * 0.35 + visual.wobbleOffset) * 0.08;
      visual.halo.rotation.z += (0.05 + visual.rotationSpeed * 0.2) * dt;
      const haloMat = visual.halo.material as THREE.MeshBasicMaterial;
      haloMat.opacity = 0.08 + Math.max(0, Math.sin(this.clock * 1.2 + visual.wobbleOffset)) * 0.12;
      const baseScale = 1 + Math.sin(this.clock * 0.7 + visual.wobbleOffset) * 0.03;
      visual.halo.scale.set(baseScale, 1 + Math.cos(this.clock * 0.6 + visual.wobbleOffset) * 0.05, 1);
    }

    if (this.selectionRing) {
      const s = 1 + Math.sin(this.clock * 3) * 0.06;
      this.selectionRing.scale.set(s, s, 1);
      const mat = this.selectionRing.material as THREE.MeshBasicMaterial;
      mat.opacity = 0.7 + Math.sin(this.clock * 3) * 0.3;
      mat.transparent = true;
    }

    const starMat = this.starField.material as THREE.PointsMaterial;
    starMat.opacity = 0.62 + Math.sin(this.clock * 1.5) * 0.16;
    this.starField.rotation.z += dt * 0.004;
    const starBrightMat = this.starFieldBright.material as THREE.PointsMaterial;
    starBrightMat.opacity = 0.74 + Math.sin(this.clock * 2.2 + 0.7) * 0.2;
    this.starFieldBright.rotation.z -= dt * 0.0025;

    this.animateAsteroids(dt);
    this.animateFleetRoutes();
    this.animateCombatMarkers(dt);
    this.animateCombatBursts(dt);
    this.animateClickPulses(dt);
    this.animateProductionRings(dt);
    this.animatePan(dt);

    if (this.selectedPlanet) this.updateOverlayPosition();
  }

  private animateFleetRoutes(): void {
    for (const route of this.routeVisuals) {
      const from = route.start;
      const to = route.end;
      const dx = to.x - from.x;
      const dy = to.y - from.y;
      const progress = this.routeProgress(route.route.progress);
      const routeShown = this.shouldShowRoute(route.route);
      const shipX = from.x + dx * progress;
      const shipY = from.y + dy * progress;
      const shipAngle = Math.atan2(dy, dx);
      const ships = route.route.ships ?? 1;
      const activeFactor = route.route.active === false ? 0.55 : 1;

      // ---- Traveled line (route-toggle gated) ----
      const traveledAttr = route.traveledLine.geometry.getAttribute('position') as THREE.BufferAttribute;
      traveledAttr.setXYZ(0, from.x, from.y, -0.04);
      traveledAttr.setXYZ(1, shipX, shipY, -0.04);
      traveledAttr.needsUpdate = true;

      // ---- Dashes along remaining path (route-toggle gated) ----
      const dashAttrs = route.laneDashes.geometry.getAttribute('position') as THREE.BufferAttribute;
      const N = route.dashCount;
      const dashSpeed = 0.22 + (route.route.speed ?? 1) * 0.012;
      const phase = (this.clock * dashSpeed) % 1;
      const dashLen = 0.9 / (N * 2);
      const remX = to.x - shipX;
      const remY = to.y - shipY;

      for (let i = 0; i < N; i++) {
        const t0 = ((i / N) + phase) % 1;
        const t1 = Math.min(1, t0 + dashLen);
        if (t0 > 0.96) {
          dashAttrs.setXYZ(i * 2,     -99999, -99999, 0);
          dashAttrs.setXYZ(i * 2 + 1, -99999, -99999, 0);
        } else {
          dashAttrs.setXYZ(i * 2,     shipX + remX * t0, shipY + remY * t0, 0.08);
          dashAttrs.setXYZ(i * 2 + 1, shipX + remX * t1, shipY + remY * t1, 0.08);
        }
      }
      dashAttrs.needsUpdate = true;

      const traveledMat = route.traveledLine.material as THREE.LineBasicMaterial;
      const dashMat = route.laneDashes.material as THREE.LineBasicMaterial;
      if (routeShown) {
        const blinkSpeed = this.routeBlinkSpeed(route.route.speed);
        const blink = (Math.sin(this.clock * blinkSpeed) + 1) / 2;
        traveledMat.opacity = (0.35 + blink * 0.15) * activeFactor;
        dashMat.opacity = (0.75 + blink * 0.2) * activeFactor;
      } else {
        traveledMat.opacity = 0;
        dashMat.opacity = 0;
      }

      // ---- Ship — ALWAYS VISIBLE, direction locked ----
      route.ship.position.set(shipX, shipY, 0.22);
      route.ship.rotation.z = shipAngle;
      route.ship.visible = true;

      const blink2 = (Math.sin(this.clock * this.routeBlinkSpeed(route.route.speed)) + 1) / 2;
      const pulse = 0.92 + blink2 * 0.24;
      const baseWorldScale = this.fleetScale(ships);
      const maxWorldFromScreen = 28 * this.zoom;
      const clampedScale = Math.min(baseWorldScale, maxWorldFromScreen);
      const finalScale = clampedScale * pulse * (route.route.active === false ? 0.85 : 1);
      route.ship.scale.setScalar(finalScale);

      // ---- Glow — ALWAYS VISIBLE, pulses ----
      const glowPulse = 0.8 + Math.sin(this.clock * 2.2 + route.thrustPhase) * 0.3;
      const glowRadius = this.fleetGlowRadius(ships) * finalScale;
      route.shipGlow.position.set(shipX, shipY, 0.15);
      route.shipGlow.scale.setScalar(glowPulse);
      // Resize geometry only if scale changes significantly? No — just use mesh scale.
      const glowMat = route.shipGlow.material as THREE.MeshBasicMaterial;
      glowMat.opacity = (ships > 100 ? 0.42 : ships > 30 ? 0.32 : 0.22) * glowPulse * activeFactor;

      // Threat ring: extra outer danger ring for large fleets
      // (built into the glow — we just scale it more)
      if (ships > 100) {
        route.shipGlow.scale.setScalar(glowPulse * 1.3);
      }

      // ---- Exhaust particles — ALWAYS VISIBLE ----
      const exhaustAttr = route.exhaustParticles.geometry.getAttribute('position') as THREE.BufferAttribute;
      const exhaustCount = 10;
      const backX = -Math.cos(shipAngle);
      const backY = -Math.sin(shipAngle);
      const perpX = -backY;
      const perpY = backX;
      const exhaustLen = glowRadius * 2.5;

      for (let i = 0; i < exhaustCount; i++) {
        const t = ((i / exhaustCount) + (this.clock * 1.6 + route.thrustPhase)) % 1;
        const dist = t * exhaustLen;
        const jitter = Math.sin(i * 1.7 + this.clock * 4.0 + route.thrustPhase) * glowRadius * 0.35;
        exhaustAttr.setXYZ(
          i,
          shipX + backX * dist + perpX * jitter,
          shipY + backY * dist + perpY * jitter,
          0.14,
        );
      }
      exhaustAttr.needsUpdate = true;
      const exhaustMat = route.exhaustParticles.material as THREE.PointsMaterial;
      exhaustMat.opacity = (0.5 + Math.sin(this.clock * 3.5 + route.thrustPhase) * 0.35) * activeFactor;
    }
  }

  private updateRouteVisibility(): void {
    for (const route of this.routeVisuals) {
      const shown = this.shouldShowRoute(route.route);
      // Route lines follow toggle
      route.traveledLine.visible = shown;
      route.laneDashes.visible = shown;
      // Ship, glow, exhaust are ALWAYS visible regardless of toggle
      route.ship.visible = true;
      route.shipGlow.visible = true;
      route.exhaustParticles.visible = true;

      const traveledMat = route.traveledLine.material as THREE.LineBasicMaterial;
      const dashMat = route.laneDashes.material as THREE.LineBasicMaterial;
      const activeFactor = route.route.active === false ? 0.45 : 1;
      traveledMat.opacity = shown ? 0.40 * activeFactor : 0;
      dashMat.opacity = shown ? 0.80 * activeFactor : 0;
    }
  }

  private routeProgress(progress?: number): number {
    if (typeof progress !== 'number' || Number.isNaN(progress)) return 0;
    return Math.max(0, Math.min(1, progress));
  }

  private routeBlinkSpeed(fleetSpeed?: number): number {
    const safe = Math.max(0.2, fleetSpeed ?? 1);
    return Math.min(2.6, 0.65 + safe * 0.18);
  }

  private fleetScale(ships?: number): number {
    const safeShips = Math.max(1, ships ?? 1);
    return Math.min(2.8, 0.6 + Math.sqrt(safeShips) * 0.12);
  }

  private fleetGlowRadius(ships: number): number {
    if (ships > 100) return 3.2;
    if (ships > 30)  return 2.0;
    return 1.2;
  }

  private fleetThreatLevel(ships: number): 'low' | 'medium' | 'high' {
    if (ships > 100) return 'high';
    if (ships > 30)  return 'medium';
    return 'low';
  }

  private createFleetShip(route: ThreeFleetRoute): THREE.Group {
    const ship = new THREE.Group();
    const ships = route.ships ?? 1;
    const threat = this.fleetThreatLevel(ships);
    const hullColor = this.hexColor(route.color);

    // Hull — cone pointing along +X (travel direction; ship.rotation.z = shipAngle rotates +X → destination)
    const hullRadius = threat === 'high' ? 0.55 : threat === 'medium' ? 0.46 : 0.38;
    const hullLength = threat === 'high' ? 2.0  : threat === 'medium' ? 1.7  : 1.4;
    const hullGeo = new THREE.ConeGeometry(hullRadius, hullLength, threat === 'high' ? 6 : 10);
    hullGeo.rotateZ(-Math.PI / 2); // orient cone apex toward +X (travel direction)
    const hullEmissiveIntensity = threat === 'high' ? 0.55 : threat === 'medium' ? 0.38 : 0.22;
    const hullMat = new THREE.MeshPhongMaterial({
      color: hullColor,
      emissive: hullColor,
      emissiveIntensity: hullEmissiveIntensity,
      shininess: threat === 'high' ? 30 : 70,
    });
    const hull = new THREE.Mesh(hullGeo, hullMat);
    hull.position.z = 0.2;
    ship.add(hull);

    // Cockpit (sensor dome) — at the nose (+X)
    const cockpitR = hullRadius * 0.44;
    const cockpitGeo = new THREE.SphereGeometry(cockpitR, 10, 8);
    const cockpitColor = threat === 'high' ? 0xff6b6b : threat === 'medium' ? 0xfbbf24 : 0x93c5fd;
    const cockpitMat = new THREE.MeshPhongMaterial({
      color: 0xffffff,
      emissive: cockpitColor,
      emissiveIntensity: 0.7,
      transparent: true,
      opacity: 0.92,
    });
    const cockpit = new THREE.Mesh(cockpitGeo, cockpitMat);
    cockpit.position.set(hullLength * 0.28, 0, 0.3);
    ship.add(cockpit);

    // Wings — span along Y (perpendicular to travel direction +X)
    const wingSpan = threat === 'high' ? 1.8 : threat === 'medium' ? 1.4 : 1.1;
    const wingGeo = new THREE.BoxGeometry(0.1, wingSpan, 0.32);
    const wingMat = new THREE.MeshPhongMaterial({
      color: 0xd1d5db,
      emissive: threat === 'high' ? 0x7f1d1d : 0x1e293b,
      emissiveIntensity: 0.3,
      shininess: 50,
    });
    const wing = new THREE.Mesh(wingGeo, wingMat);
    wing.position.z = 0.18;
    ship.add(wing);

    // Engine glow — at the tail (-X)
    const engineColor = threat === 'high' ? 0xff4400 : threat === 'medium' ? 0xfbbf24 : 0x38bdf8;
    const engineGeo = new THREE.SphereGeometry(hullRadius * 0.38, 10, 8);
    const engineMat = new THREE.MeshBasicMaterial({
      color: engineColor,
      transparent: true,
      opacity: 0.95,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    });
    const engine = new THREE.Mesh(engineGeo, engineMat);
    engine.position.set(-hullLength * 0.55, 0, 0.18);
    ship.add(engine);

    // Threat ring for large fleets (hexagon silhouette)
    if (threat === 'high') {
      const dangerGeo = new THREE.RingGeometry(1.1, 1.38, 6);
      const dangerMat = new THREE.MeshBasicMaterial({
        color: 0xff3333,
        transparent: true,
        opacity: 0.75,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const dangerRing = new THREE.Mesh(dangerGeo, dangerMat);
      dangerRing.position.z = 0.05;
      ship.add(dangerRing);
    } else if (threat === 'medium') {
      const warnGeo = new THREE.RingGeometry(0.85, 1.02, 8);
      const warnMat = new THREE.MeshBasicMaterial({
        color: 0xfbbf24,
        transparent: true,
        opacity: 0.5,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const warnRing = new THREE.Mesh(warnGeo, warnMat);
      warnRing.position.z = 0.04;
      ship.add(warnRing);
    }

    ship.scale.setScalar(this.fleetScale(ships));
    ship.traverse(obj => { obj.userData['fleetRoute'] = route; });
    return ship;
  }

  private computeRemainingTurns(route: ThreeFleetRoute, start: THREE.Vector3, end: THREE.Vector3): number {
    const totalDist = start.distanceTo(end);
    const progress = this.routeProgress(route.progress);
    const remainingDist = totalDist * (1 - progress);
    if (route.speed && route.speed > 0 && remainingDist > 0) {
      return Math.max(1, Math.min(16, Math.round(remainingDist / route.speed)));
    }
    return Math.max(1, Math.min(8, Math.round((1 - progress) * 6 + 1)));
  }

  private shouldShowRoute(route: ThreeFleetRoute): boolean {
    if (this.showAllRoutes)
      return true;
    if (!this.routeFocusPlanet)
      return false;
    return route.origin === this.routeFocusPlanet || route.destination === this.routeFocusPlanet;
  }

  private animateAsteroids(dt: number): void {
    for (const asteroid of this.asteroidFlybys) {
      asteroid.mesh.position.addScaledVector(asteroid.velocity, dt);
      asteroid.mesh.rotation.x += asteroid.spin.x * dt;
      asteroid.mesh.rotation.y += asteroid.spin.y * dt;
      asteroid.mesh.rotation.z += asteroid.spin.z * dt;

      if (asteroid.mesh.position.x > asteroid.bounds) {
        asteroid.mesh.position.x = -asteroid.bounds;
      }
      if (asteroid.mesh.position.y < -asteroid.bounds) {
        asteroid.mesh.position.y = asteroid.bounds;
      }
    }
  }

  private spawnClickPulse(x: number, y: number): void {
    const geo = new THREE.RingGeometry(0.35, 0.55, 40);
    const mat = new THREE.MeshBasicMaterial({
      color: 0xf8fafc,
      transparent: true,
      opacity: 0.9,
      side: THREE.DoubleSide,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set(x, y, 0.4);
    this.scene.add(mesh);
    this.clickPulses.push({ mesh, life: 0, ttl: 0.65 });
  }

  private animateClickPulses(dt: number): void {
    for (let i = this.clickPulses.length - 1; i >= 0; i--) {
      const pulse = this.clickPulses[i]!;
      pulse.life += dt;
      const progress = pulse.life / pulse.ttl;
      pulse.mesh.scale.setScalar(1 + progress * 5.5);
      pulse.mesh.rotation.z += dt * 0.8;
      const material = pulse.mesh.material as THREE.MeshBasicMaterial;
      material.opacity = 1 - progress;
      if (progress >= 1) {
        this.scene.remove(pulse.mesh);
        pulse.mesh.geometry.dispose();
        material.dispose();
        this.clickPulses.splice(i, 1);
      }
    }
  }

  private animateCombatMarkers(dt: number): void {
    for (const marker of this.combatMarkers) {
      const pulse = (Math.sin(this.clock * (marker.type === 'battle' ? 6.4 : 8.2) + marker.phase) + 1) / 2;
      const flicker = marker.type === 'battle'
        ? 0.7 + pulse * 0.45
        : 0.6 + Math.abs(Math.sin(this.clock * 11 + marker.phase * 1.3)) * 0.5;

      marker.primary.rotation.z += dt * (marker.type === 'battle' ? 1.2 : -0.9);
      marker.secondary.rotation.z -= dt * (marker.type === 'battle' ? 0.8 : 0.5);
      marker.primary.scale.setScalar(marker.baseScale * (1 + pulse * 0.2));
      marker.secondary.scale.setScalar(1 + pulse * (marker.type === 'battle' ? 0.28 : 0.18));

      const primaryMat = marker.primary.material as THREE.MeshBasicMaterial;
      const secondaryMat = marker.secondary.material as THREE.MeshBasicMaterial;
      primaryMat.opacity = (marker.type === 'battle' ? 0.42 : 0.35) * flicker;
      secondaryMat.opacity = (marker.type === 'battle' ? 0.32 : 0.28) * flicker;
    }
  }

  private animateCombatBursts(dt: number): void {
    for (let i = this.combatBursts.length - 1; i >= 0; i--) {
      const burst = this.combatBursts[i]!;
      burst.life += dt;
      const progress = burst.life / burst.ttl;
      const mesh = burst.mesh;
      const material = mesh.material as THREE.MeshBasicMaterial;
      const scaleBoost = burst.type === 'battle' ? 2.8 : 2.1;
      mesh.scale.setScalar(1 + progress * scaleBoost);
      mesh.rotation.z += dt * (burst.type === 'battle' ? 2.2 : 1.6);
      material.opacity = (1 - progress) * (burst.type === 'battle' ? 0.95 : 0.8);

      if (progress >= 1) {
        this.scene.remove(mesh);
        mesh.geometry.dispose();
        material.dispose();
        this.combatBursts.splice(i, 1);
      }
    }
  }

  private animateProductionRings(dt: number): void {
    for (const ring of this.productionRings) {
      ring.mesh.rotation.z += ring.orbitSpeed * dt;
    }
  }

  private animatePan(dt: number): void {
    if (!this.panTarget) return;
    this.panAnimT = Math.min(1, this.panAnimT + dt * 3.5);
    const t = 1 - Math.pow(1 - this.panAnimT, 3); // ease-out cubic
    this.panX = this.panAnimFrom.x + (this.panTarget.x - this.panAnimFrom.x) * t;
    this.panY = this.panAnimFrom.y + (this.panTarget.y - this.panAnimFrom.y) * t;
    this.updateCamera();
    if (this.panAnimT >= 1) this.panTarget = null;
  }

  private disposeSceneObjects(): void {
    for (const object of this.cleanupMeshes) {
      this.scene.remove(object);
      const disposable = object as THREE.Object3D & {
        geometry?: { dispose: () => void };
        material?: THREE.Material | THREE.Material[];
      };
      if (disposable.geometry) {
        disposable.geometry.dispose();
      }
      if (disposable.material) {
        if (Array.isArray(disposable.material)) {
          disposable.material.forEach(material => material.dispose());
        } else {
          disposable.material.dispose();
        }
      }
    }
    for (const pulse of this.clickPulses) {
      this.scene.remove(pulse.mesh);
      pulse.mesh.geometry.dispose();
      (pulse.mesh.material as THREE.Material).dispose();
    }
    this.cleanupMeshes = [];
    this.planetVisuals = [];
    this.routeVisuals = [];
    this.combatMarkers = [];
    for (const burst of this.combatBursts) {
      this.scene.remove(burst.mesh);
      burst.mesh.geometry.dispose();
      (burst.mesh.material as THREE.Material).dispose();
    }
    this.combatBursts = [];
    this.productionRings = [];
    this.clickPulses = [];
    this.disposeAsteroidFlybys();
  }

  private disposeAsteroidFlybys(): void {
    this.asteroidFlybys = [];
  }

  private render(): void {
    this.renderer.render(this.scene, this.camera);
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}
