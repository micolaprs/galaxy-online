import * as THREE from 'three';

export interface ThreePlanet {
  name: string;
  x: number;
  y: number;
  size: number;
  color: string;
  ownerId: string | null;
  hasShips?: boolean;
}

const STAR_COUNT = 800;

export class GalaxyMapThree {
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.OrthographicCamera;
  private starField!: THREE.Points;

  // Planet meshes & metadata
  private planetMeshes: THREE.Mesh[]   = [];
  private planets: ThreePlanet[]        = [];
  private galaxySize = 200;

  // Selection ring
  private selectionRing: THREE.Mesh | null = null;
  private selectedName: string | null      = null;

  // Pan / zoom
  private panX = 0;
  private panY = 0;
  private zoom = 1;       // world units per screen pixel (decreasing = zoom in)
  private dragging = false;
  private dragStart = { x: 0, y: 0, panX: 0, panY: 0 };

  private raycaster = new THREE.Raycaster();
  private pointer   = new THREE.Vector2();

  private animFrame: number | null = null;

  /** Called when user clicks a planet. */
  onPlanetClick?: (name: string, screenX: number, screenY: number) => void;

  constructor(private container: HTMLElement) {
    this.init();
  }

  // ---- Public API ----

  setData(galaxySize: number, planets: ThreePlanet[]): void {
    this.galaxySize = galaxySize;
    this.planets    = planets;
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
    this.renderer.dispose();
    this.container.innerHTML = '';
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
    this.render();
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

  private setupLights(): void {
    const ambient = new THREE.AmbientLight(0x223355, 0.8);
    this.scene.add(ambient);

    const dir = new THREE.DirectionalLight(0x88bbff, 1.2);
    dir.position.set(50, 80, 100);
    this.scene.add(dir);
  }

  // ---- Planet meshes ----

  private buildPlanetMeshes(): void {
    // Remove old meshes
    for (const m of this.planetMeshes) {
      this.scene.remove(m);
      m.geometry.dispose();
      (m.material as THREE.Material).dispose();
    }
    this.planetMeshes = [];
    if (this.selectionRing) {
      this.scene.remove(this.selectionRing);
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
      this.planetMeshes.push(mesh);

      // Ship dot
      if (p.hasShips) {
        const dotGeo = new THREE.SphereGeometry(radius * 0.35, 8, 6);
        const dotMat = new THREE.MeshBasicMaterial({ color: 0x38bdf8 });
        const dot    = new THREE.Mesh(dotGeo, dotMat);
        dot.position.set(p.x + radius * 1.2, -(p.y - radius * 1.2), 0.5);
        this.scene.add(dot);
        this.planetMeshes.push(dot);   // include so it gets cleaned up
      }
    }

    this.updateSelectionRing();
  }

  private updateSelectionRing(): void {
    if (this.selectionRing) {
      this.scene.remove(this.selectionRing);
      this.selectionRing.geometry.dispose();
      (this.selectionRing.material as THREE.Material).dispose();
      this.selectionRing = null;
    }
    if (!this.selectedName) return;

    const planet = this.planets.find(p => p.name === this.selectedName);
    if (!planet) return;

    const r  = this.planetRadius(planet.size) + 0.8;
    const geo = new THREE.RingGeometry(r, r + 0.3, 32);
    const mat = new THREE.MeshBasicMaterial({ color: 0xfacc15, side: THREE.DoubleSide });
    this.selectionRing = new THREE.Mesh(geo, mat);
    this.selectionRing.position.set(planet.x, -planet.y, 0.2);
    this.scene.add(this.selectionRing);
  }

  private planetRadius(size: number): number {
    return Math.max(0.8, Math.min(3.5, (size / 1000) * 5));
  }

  private hexColor(css: string): number {
    return parseInt(css.replace('#', ''), 16);
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
    const rect = this.renderer.domElement.getBoundingClientRect();
    this.pointer.x =  ((e.clientX - rect.left)  / rect.width)  * 2 - 1;
    this.pointer.y = -((e.clientY - rect.top)   / rect.height) * 2 + 1;

    this.raycaster.setFromCamera(this.pointer, this.camera);

    // Only test planet meshes (skip dots/rings)
    const targets = this.planetMeshes.filter(m => m.userData['planet']);
    const hits    = this.raycaster.intersectObjects(targets);

    if (hits.length > 0) {
      const name = hits[0].object.userData['planet'] as string;
      this.selectedName = name;
      this.updateSelectionRing();
      this.render();
      this.onPlanetClick?.(name, e.clientX, e.clientY);
    }
  }

  // ---- Render ----

  private render(): void {
    this.renderer.render(this.scene, this.camera);
  }
}
