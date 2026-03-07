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
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  color: string;
  fleetName?: string;
  ships?: number;
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
  baseLine: THREE.Line;
  laneDots: THREE.Points;
  start: THREE.Vector3;
  end: THREE.Vector3;
  speed: number;
}

export class GalaxyMapThree {
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.OrthographicCamera;
  private starField!: THREE.Points;

  // Planet meshes & metadata
  private cleanupMeshes: THREE.Object3D[] = [];
  private planetVisuals: PlanetVisual[] = [];
  private asteroidFlybys: AsteroidFlyby[] = [];
  private clickPulses: ClickPulse[] = [];
  private planets: ThreePlanet[] = [];
  private fleetRoutes: ThreeFleetRoute[] = [];
  private routeVisuals: FleetRouteVisual[] = [];
  private galaxySize = 200;

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
  onMapClick?: () => void;

  constructor(private container: HTMLElement) {
    this.init();
  }

  // ---- Public API ----

  setData(galaxySize: number, planets: ThreePlanet[], fleetRoutes: ThreeFleetRoute[] = []): void {
    this.galaxySize = galaxySize;
    this.planets    = planets;
    this.fleetRoutes = fleetRoutes;
    this.buildPlanetMeshes();
    this.fitToView();
    this.render();
  }

  select(name: string | null): void {
    this.selectedName = name;
    this.updateSelectionRing();
    this.render();
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
    const sizes     = new Float32Array(STAR_COUNT);
    for (let i = 0; i < STAR_COUNT; i++) {
      positions[i * 3]     = (Math.random() - 0.5) * 800;
      positions[i * 3 + 1] = (Math.random() - 0.5) * 800;
      positions[i * 3 + 2] = -5;
      sizes[i] = Math.random() * 1.5 + 0.3;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geo.setAttribute('size',     new THREE.BufferAttribute(sizes, 1));

    const mat = new THREE.PointsMaterial({
      color: 0xffffff, sizeAttenuation: true,
      size: 0.6, transparent: true, opacity: 0.55,
    });
    this.starField = new THREE.Points(geo, mat);
    this.scene.add(this.starField);
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

      const haloGeo = new THREE.RingGeometry(radius * 1.28, radius * 1.6, 40);
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
        const dotGeo = new THREE.SphereGeometry(radius * 0.35, 8, 6);
        const dotMat = new THREE.MeshBasicMaterial({ color: 0x38bdf8 });
        const dot    = new THREE.Mesh(dotGeo, dotMat);
        dot.position.set(p.x + radius * 1.2, -(p.y - radius * 1.2), 0.5);
        this.scene.add(dot);
        this.cleanupMeshes.push(dot);
      }
    }

    this.buildAsteroidFlybys();
    this.buildFleetRouteMeshes();
    this.updateSelectionRing();
  }

  private buildFleetRouteMeshes(): void {
    this.routeVisuals = [];

    for (const route of this.fleetRoutes) {
      const start = new THREE.Vector3(route.x1, -route.y1, -0.04);
      const end = new THREE.Vector3(route.x2, -route.y2, -0.04);
      const points = [start, end];

      const baseGeometry = new THREE.BufferGeometry().setFromPoints(points);
      const baseMaterial = new THREE.LineBasicMaterial({
        color: this.hexColor(route.color),
        transparent: true,
        opacity: 0.32,
      });
      const baseLine = new THREE.Line(baseGeometry, baseMaterial);
      this.scene.add(baseLine);
      this.cleanupMeshes.push(baseLine);

      const dotPositions = new Float32Array(ROUTE_POINT_COUNT * 3);
      const laneGeometry = new THREE.BufferGeometry();
      laneGeometry.setAttribute('position', new THREE.BufferAttribute(dotPositions, 3));
      const laneMaterial = new THREE.PointsMaterial({
        color: this.hexColor(route.color),
        size: 0.9,
        transparent: true,
        opacity: 0.95,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const laneDots = new THREE.Points(laneGeometry, laneMaterial);
      laneDots.position.z = 0.08;
      this.scene.add(laneDots);
      this.cleanupMeshes.push(laneDots);

      this.routeVisuals.push({
        baseLine,
        laneDots,
        start,
        end,
        speed: 0.35 + Math.random() * 0.25,
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
    return Math.max(0.8, Math.min(3.5, (size / 1000) * 5));
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
    const pad  = gs * 0.08;
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

    // Only test planet meshes (skip dots/rings)
    const targets = this.planetVisuals.map(visual => visual.mesh);
    const hits    = this.raycaster.intersectObjects(targets);

    if (hits.length > 0) {
      const name = hits[0].object.userData['planet'] as string;
      this.selectedName = name;
      this.updateSelectionRing();
      this.spawnClickPulse(hits[0].point.x, hits[0].point.y);
      this.render();
      this.onPlanetClick?.(name, e.clientX, e.clientY);
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
    starMat.opacity = 0.45 + Math.sin(this.clock * 0.7) * 0.1;

    this.animateAsteroids(dt);
    this.animateFleetRoutes();
    this.animateClickPulses(dt);

    if (this.selectedPlanet) this.updateOverlayPosition();
  }

  private animateFleetRoutes(): void {
    for (const route of this.routeVisuals) {
      const attrs = route.laneDots.geometry.getAttribute('position') as THREE.BufferAttribute;
      const from = route.start;
      const to = route.end;
      const dx = to.x - from.x;
      const dy = to.y - from.y;
      const phase = (this.clock * route.speed) % 1;

      for (let i = 0; i < ROUTE_POINT_COUNT; i++) {
        const t = ((i / ROUTE_POINT_COUNT) + phase) % 1;
        attrs.setXYZ(i, from.x + dx * t, from.y + dy * t, 0.08);
      }
      attrs.needsUpdate = true;
    }
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

  private disposeSceneObjects(): void {
    for (const object of this.cleanupMeshes) {
      this.scene.remove(object);
      if (object instanceof THREE.Mesh) {
        object.geometry.dispose();
        if (Array.isArray(object.material)) {
          object.material.forEach(material => material.dispose());
        } else {
          object.material.dispose();
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
