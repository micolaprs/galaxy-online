// BombingVisualizer.ts — Three.js bombing sequence

import * as THREE from 'three';

export interface BombingData {
  planetName:    string;
  attackerRace:  string;
  previousOwner: string | null;
  attackerColor: string;
  oldPopulation?: number;
}

// ---- Helpers ----

function hexColor(hex: string): number {
  return parseInt(hex.replace('#', ''), 16);
}

function makeShipShape(): THREE.Shape {
  const s = new THREE.Shape();
  s.moveTo(14, 0);
  s.lineTo(-4, -8);
  s.lineTo(-10, -5);
  s.lineTo(-8, 0);
  s.lineTo(-10, 5);
  s.lineTo(-4, 8);
  s.closePath();
  return s;
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Build an offscreen canvas texture for the planet surface */
function makePlanetTexture(size = 512): THREE.CanvasTexture {
  const c   = document.createElement('canvas');
  c.width   = size;
  c.height  = size;
  const ctx = c.getContext('2d')!;
  const cx  = size / 2;
  const cy  = size / 2;
  const r   = size / 2;

  // Surface gradient
  const surf = ctx.createRadialGradient(cx - r * 0.3, cy - r * 0.3, r * 0.1, cx, cy, r);
  surf.addColorStop(0,   '#2f7d4f');
  surf.addColorStop(0.5, '#1a5035');
  surf.addColorStop(1,   '#0d2e1e');
  ctx.fillStyle = surf;
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  ctx.fill();

  // Ocean patch
  const ocean = ctx.createLinearGradient(cx - r, cy, cx + r, cy);
  ocean.addColorStop(0,   'rgba(20,80,160,0.55)');
  ocean.addColorStop(0.5, 'rgba(10,50,120,0.35)');
  ocean.addColorStop(1,   'rgba(20,80,160,0.55)');
  ctx.fillStyle = ocean;
  ctx.beginPath();
  ctx.ellipse(cx, cy + r * 0.1, r * 0.58, r * 0.34, -0.3, 0, Math.PI * 2);
  ctx.fill();

  // Terrain spots
  const rng = new Uint32Array(1);
  for (let i = 0; i < 35; i++) {
    crypto.getRandomValues(rng);
    const a  = (rng[0]! / 0xffffffff) * Math.PI * 2;
    const d  = (rng[0]! / 0xffffffff) * r * 0.88;
    crypto.getRandomValues(rng);
    const sr = 6 + (rng[0]! / 0xffffffff) * 22;
    ctx.fillStyle = 'rgba(0,25,12,0.2)';
    ctx.beginPath();
    ctx.ellipse(cx + Math.cos(a) * d, cy + Math.sin(a) * d, sr, sr * 0.6, a, 0, Math.PI * 2);
    ctx.fill();
  }

  // Terminator shadow (right side)
  const term = ctx.createLinearGradient(cx + r * 0.35, cy, cx + r, cy);
  term.addColorStop(0, 'rgba(0,0,0,0)');
  term.addColorStop(1, 'rgba(0,0,0,0.62)');
  ctx.fillStyle = term;
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  ctx.fill();

  return new THREE.CanvasTexture(c);
}

// ---- Internal types ----

interface BombObj {
  mesh: THREE.Mesh;
  trail: THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
  sx: number; sy: number; tx: number; ty: number;
  progress: number; done: boolean;
}

interface BombExpl {
  ring: THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
  points: THREE.Points<THREE.BufferGeometry, THREE.PointsMaterial>;
  partsBuf: Float32Array;
  parts: Array<{ x: number; y: number; vx: number; vy: number; life: number }>;
  x: number; y: number; r: number; maxR: number; op: number; done: boolean;
}

// ---- BombingVisualizer ----

export class BombingVisualizer {
  private data: BombingData;

  // DOM
  private el:        HTMLElement;
  private overlayEl: HTMLElement;
  private popEl:     HTMLElement | null = null;

  // Three.js
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.OrthographicCamera;
  private animFrame: number | null = null;
  private t = 0;

  // Orbit
  private orbitPhases = [0, (Math.PI * 2) / 3, (Math.PI * 4) / 3];
  private orbitSpeed  = (Math.PI * 2) / (60 * 8);
  private shipGroups:  THREE.Group[] = [];

  // Bombs
  private bombs:      BombObj[]  = [];
  private explosions: BombExpl[] = [];
  private nextBombFrame = 60;
  private bombShipIdx   = 0;
  private impactCount   = 0;

  // Population counter
  private popDisplay: number;
  private popTarget:  number;

  // Canvas & camera extents
  readonly CW       = 900;
  readonly CH       = 420;
  // In Three.js world coords (origin = center of canvas)
  readonly PLANET_X =   0;
  readonly PLANET_Y =   0;   // centered
  readonly PLANET_R = 150;
  readonly ORBIT_A  = 230;
  readonly ORBIT_B  =  80;

  // Disposable scene items
  private planetMesh!:    THREE.Mesh;
  private planetTexture!: THREE.CanvasTexture;
  private starPoints!:    THREE.Points;
  private atmosphereMeshes: THREE.Mesh[] = [];
  private orbitLine!: THREE.LineLoop<THREE.BufferGeometry, THREE.LineBasicMaterial>;

  constructor(data: BombingData) {
    this.data       = data;
    this.popDisplay = data.oldPopulation ?? 0;
    this.popTarget  = this.popDisplay;
    this.el         = this.createDOM();
    this.initThree();
    this.buildScene();
    this.buildShips();
    this.startLoop();
  }

  get element(): HTMLElement { return this.el; }

  destroy(): void {
    if (this.animFrame !== null) { cancelAnimationFrame(this.animFrame); this.animFrame = null; }

    for (const b of this.bombs)      this.disposeBomp(b);
    for (const e of this.explosions) this.disposeExpl(e);

    this.starPoints.geometry.dispose();
    (this.starPoints.material as THREE.Material).dispose();
    this.planetTexture.dispose();
    this.planetMesh.geometry.dispose();
    (this.planetMesh.material as THREE.Material).dispose();
    this.atmosphereMeshes.forEach(m => {
      m.geometry.dispose(); (m.material as THREE.Material).dispose();
    });
    this.orbitLine.geometry.dispose();
    this.orbitLine.material.dispose();
    this.shipGroups.forEach(g => {
      g.traverse(obj => {
        if (obj instanceof THREE.Mesh) {
          obj.geometry.dispose(); (obj.material as THREE.Material).dispose();
        }
      });
      this.scene.remove(g);
    });

    this.renderer.dispose();
  }

  // ---- DOM ----

  private createDOM(): HTMLElement {
    const el = document.createElement('div');
    el.className = 'bv-container bv-bombing';

    const popHtml = this.data.oldPopulation != null
      ? `<div class="bv-bombing-stats" id="bv-bombing-stats">
           Население ${esc(this.data.planetName)}: <span id="bv-pop-counter">${Math.round(this.data.oldPopulation)}</span>
           → ~${Math.round(this.data.oldPopulation * 0.55)}
         </div>`
      : `<div class="bv-bombing-stats" id="bv-bombing-stats"></div>`;

    el.innerHTML = `
      <div class="bv-header">
        <div class="bv-title">💥 Бомбардировка</div>
        <div class="bv-bombing-info">
          <span class="bv-bombing-attacker">${esc(this.data.attackerRace)}</span>
          <span> → </span>
          <span class="bv-bombing-planet">${esc(this.data.planetName)}</span>
        </div>
      </div>
      <div class="bv-canvas-wrap">
        <div id="bv-renderer" class="bv-renderer"></div>
        <div id="bv-hud-overlay" class="bv-hud-overlay"></div>
      </div>
      ${popHtml}
    `;

    this.overlayEl = el.querySelector('#bv-hud-overlay')!;
    if (this.data.oldPopulation != null) {
      this.popEl = el.querySelector('#bv-pop-counter');
    }

    this.buildHUDOverlay(el);
    return el;
  }

  private buildHUDOverlay(el: HTMLElement): void {
    const overlay = el.querySelector<HTMLElement>('#bv-hud-overlay')!;

    // Attacker label — top left
    const atk = document.createElement('div');
    atk.style.cssText = [
      'position:absolute', 'left:16px', 'top:12px', 'pointer-events:none',
    ].join(';');
    atk.innerHTML = `
      <div style="font:bold 13px monospace;color:${this.data.attackerColor};text-shadow:0 0 8px ${this.data.attackerColor}">${esc(this.data.attackerRace)}</div>
      <div style="font:11px monospace;color:rgba(200,220,255,0.65)">Флот захватчика</div>
    `;
    overlay.appendChild(atk);

    // Planet label — top right
    const planet = document.createElement('div');
    planet.style.cssText = [
      'position:absolute', 'right:16px', 'top:12px',
      'text-align:right', 'pointer-events:none',
    ].join(';');
    planet.innerHTML = `
      <div style="font:bold 13px monospace;color:#38bdf8;text-shadow:0 0 8px #38bdf8">${esc(this.data.planetName)}</div>
      ${this.data.previousOwner
        ? `<div style="font:10px monospace;color:rgba(200,220,255,0.55)">Прежний: ${esc(this.data.previousOwner)}</div>`
        : ''}
    `;
    overlay.appendChild(planet);
  }

  // ---- Three.js init ----

  private initThree(): void {
    const container = this.el.querySelector<HTMLElement>('#bv-renderer')!;

    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(this.CW, this.CH);
    this.renderer.setClearColor(0x010811);
    container.appendChild(this.renderer.domElement);

    this.scene  = new THREE.Scene();
    // Camera zoomed in: planet is centered, [-340..340] x [-190..190]
    this.camera = new THREE.OrthographicCamera(-340, 340, 190, -190, 0.1, 1000);
    this.camera.position.set(0, 0, 10);
  }

  private buildScene(): void {
    // Stars
    const starCount = 180;
    const starPos   = new Float32Array(starCount * 3);
    for (let i = 0; i < starCount; i++) {
      starPos[i*3]   = (Math.random() - 0.5) * 680;
      starPos[i*3+1] = (Math.random() - 0.5) * 380;
      starPos[i*3+2] = -5;
    }
    const starGeo = new THREE.BufferGeometry();
    starGeo.setAttribute('position', new THREE.BufferAttribute(starPos, 3));
    const starMat = new THREE.PointsMaterial({
      color: 0xb8ccff, size: 1.5, sizeAttenuation: false,
      transparent: true, opacity: 0.7,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    this.starPoints = new THREE.Points(starGeo, starMat);
    this.scene.add(this.starPoints);

    // Atmosphere glow rings (before planet so planet renders on top)
    for (let i = 0; i < 4; i++) {
      const ar     = this.PLANET_R + 16 + i * 13;
      const ringGeo = new THREE.RingGeometry(ar - 8, ar, 48);
      const ringMat = new THREE.MeshBasicMaterial({
        color: 0x64dcff, transparent: true, opacity: 0.055 - i * 0.01,
        blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
      });
      const ring = new THREE.Mesh(ringGeo, ringMat);
      ring.position.set(this.PLANET_X, this.PLANET_Y, 0.5);
      this.scene.add(ring);
      this.atmosphereMeshes.push(ring);
    }

    // Planet disk (canvas texture)
    this.planetTexture = makePlanetTexture(512);
    const planetGeo = new THREE.CircleGeometry(this.PLANET_R, 64);
    const planetMat = new THREE.MeshBasicMaterial({ map: this.planetTexture, depthWrite: false });
    this.planetMesh = new THREE.Mesh(planetGeo, planetMat);
    this.planetMesh.position.set(this.PLANET_X, this.PLANET_Y, 0.8);
    this.scene.add(this.planetMesh);

    // Planet outline glow
    const outlineGeo = new THREE.RingGeometry(this.PLANET_R - 1, this.PLANET_R + 4, 64);
    const outlineMat = new THREE.MeshBasicMaterial({
      color: 0x50c878, transparent: true, opacity: 0.25,
      blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
    });
    const outline = new THREE.Mesh(outlineGeo, outlineMat);
    outline.position.set(this.PLANET_X, this.PLANET_Y, 0.9);
    this.scene.add(outline);
    this.atmosphereMeshes.push(outline);

    // Orbit ellipse (as line loop approximation)
    const orbitPts = 80;
    const orbitPos = new Float32Array(orbitPts * 3);
    for (let i = 0; i < orbitPts; i++) {
      const a = (Math.PI * 2 * i) / orbitPts;
      orbitPos[i*3]   = this.PLANET_X + Math.cos(a) * this.ORBIT_A;
      orbitPos[i*3+1] = this.PLANET_Y + Math.sin(a) * this.ORBIT_B;
      orbitPos[i*3+2] = 1.5;
    }
    const orbitGeo = new THREE.BufferGeometry();
    orbitGeo.setAttribute('position', new THREE.BufferAttribute(orbitPos, 3));
    const orbitMat = new THREE.LineBasicMaterial({
      color: 0x94a3b8, transparent: true, opacity: 0.2, depthWrite: false,
    });
    this.orbitLine = new THREE.LineLoop(orbitGeo, orbitMat);
    this.scene.add(this.orbitLine);
  }

  private buildShips(): void {
    const color    = hexColor(this.data.attackerColor);
    const shipShape = makeShipShape();

    for (let i = 0; i < 3; i++) {
      const bodyGeo = new THREE.ShapeGeometry(shipShape);
      const bodyMat = new THREE.MeshBasicMaterial({
        color, transparent: true, opacity: 0.9,
        blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
      });
      const body = new THREE.Mesh(bodyGeo, bodyMat);

      const glowGeo = new THREE.CircleGeometry(7, 10);
      const glowMat = new THREE.MeshBasicMaterial({
        color: 0xff9900, transparent: true, opacity: 0.4,
        blending: THREE.AdditiveBlending, depthWrite: false,
      });
      const glow = new THREE.Mesh(glowGeo, glowMat);
      glow.position.set(-10, 0, -0.1);

      const group = new THREE.Group();
      group.add(body);
      group.add(glow);
      group.scale.set(0.75, 0.75, 1);
      group.position.set(0, 0, 2);
      this.scene.add(group);
      this.shipGroups.push(group);
    }
  }

  // ---- Orbit math ----

  private getOrbitPos(phase: number): { x: number; y: number } {
    return {
      x: this.PLANET_X + Math.cos(phase) * this.ORBIT_A,
      y: this.PLANET_Y + Math.sin(phase) * this.ORBIT_B,
    };
  }

  private getOrbitTangentAngle(phase: number): number {
    const dx = -Math.sin(phase) * this.ORBIT_A;
    const dy =  Math.cos(phase) * this.ORBIT_B;
    return Math.atan2(dy, dx);
  }

  // ---- Bombs ----

  private fireBomb(shipIdx: number): void {
    const pos = this.getOrbitPos(this.orbitPhases[shipIdx]!);

    // Impact on visible surface of planet (full range)
    const impactAngle = Math.random() * Math.PI * 2;
    const tx = this.PLANET_X + Math.cos(impactAngle) * (this.PLANET_R - 18);
    const ty = this.PLANET_Y + Math.sin(impactAngle) * (this.PLANET_R - 18);

    // Bomb mesh
    const bGeo = new THREE.CircleGeometry(5, 8);
    const bMat = new THREE.MeshBasicMaterial({
      color: 0xff6020, transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const mesh = new THREE.Mesh(bGeo, bMat);
    mesh.position.set(pos.x, pos.y, 3);
    this.scene.add(mesh);

    // Trail
    const trailGeo = new THREE.BufferGeometry();
    trailGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array([
      pos.x, pos.y, 2.9, pos.x, pos.y, 2.9,
    ]), 3));
    const trailMat = new THREE.LineBasicMaterial({
      color: 0xff8030, transparent: true, opacity: 0.7,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const trail = new THREE.Line(trailGeo, trailMat);
    this.scene.add(trail);

    this.bombs.push({ mesh, trail, sx: pos.x, sy: pos.y, tx, ty, progress: 0, done: false });
  }

  private updateBombs(): void {
    for (const bomb of this.bombs) {
      if (bomb.done) continue;

      bomb.progress += 1 / 55;
      const p  = Math.min(1, bomb.progress);
      const cx = bomb.sx + (bomb.tx - bomb.sx) * p;
      const cy = bomb.sy + (bomb.ty - bomb.sy) * p;
      bomb.mesh.position.set(cx, cy, 3);

      // Trail: from 12% behind to current
      const tail = Math.max(0, p - 0.12);
      const tx2  = bomb.sx + (bomb.tx - bomb.sx) * tail;
      const ty2  = bomb.sy + (bomb.ty - bomb.sy) * tail;
      const attr = bomb.trail.geometry.attributes['position'] as THREE.BufferAttribute;
      attr.setXYZ(0, tx2, ty2, 2.9);
      attr.setXYZ(1, cx,  cy,  2.9);
      attr.needsUpdate = true;

      if (p >= 1) {
        bomb.done = true;
        this.onBombImpact(bomb);
        this.impactCount++;
        this.disposeBomp(bomb);
      }
    }
    this.bombs = this.bombs.filter(b => !b.done);
  }

  private onBombImpact(bomb: BombObj): void {
    // Surface explosion
    const n = 18;
    const partsBuf = new Float32Array(n * 3);
    const parts: BombExpl['parts'] = [];
    for (let i = 0; i < n; i++) {
      const a   = (Math.PI * 2 * i) / n + Math.random() * 0.4;
      const spd = 3.0 + Math.random() * 5;
      parts.push({ x: bomb.tx, y: bomb.ty, vx: Math.cos(a) * spd, vy: Math.sin(a) * spd, life: 1 });
      partsBuf[i*3] = bomb.tx; partsBuf[i*3+1] = bomb.ty; partsBuf[i*3+2] = 3.5;
    }

    const ringGeo = new THREE.RingGeometry(0.85, 1, 32);
    const ringMat = new THREE.MeshBasicMaterial({
      color: 0xff4400, transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
    });
    const ring = new THREE.Mesh(ringGeo, ringMat);
    ring.position.set(bomb.tx, bomb.ty, 3);
    ring.scale.set(6, 6, 1);
    this.scene.add(ring);

    const pointsGeo = new THREE.BufferGeometry();
    pointsGeo.setAttribute('position', new THREE.BufferAttribute(partsBuf, 3));
    const pointsMat = new THREE.PointsMaterial({
      color: 0xff8800, size: 4.5, sizeAttenuation: false,
      transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const points = new THREE.Points(pointsGeo, pointsMat);
    this.scene.add(points);

    this.explosions.push({
      ring, points, partsBuf, parts,
      x: bomb.tx, y: bomb.ty, r: 6, maxR: 55, op: 1, done: false,
    });

    // Population counter
    if (this.data.oldPopulation != null) {
      this.popTarget = Math.max(0, this.popTarget - this.data.oldPopulation * 0.09);
    }
  }

  private updateExplosions(): void {
    for (const expl of this.explosions) {
      if (expl.done) continue;
      expl.r  += (expl.maxR - expl.r) * 0.1;
      expl.op -= 0.025;
      if (expl.op <= 0) {
        expl.done = true;
        this.disposeExpl(expl);
        continue;
      }
      expl.ring.scale.set(expl.r, expl.r, 1);
      expl.ring.material.opacity = expl.op;

      for (let i = 0; i < expl.parts.length; i++) {
        const p = expl.parts[i]!;
        p.x += p.vx; p.y += p.vy; p.vy += 0.15; p.life -= 0.035;
        expl.partsBuf[i*3]   = p.x;
        expl.partsBuf[i*3+1] = p.y;
        expl.partsBuf[i*3+2] = 3.5;
      }
      const attr = expl.points.geometry.attributes['position'] as THREE.BufferAttribute;
      attr.needsUpdate = true;
      expl.points.material.opacity = expl.op;
    }
    this.explosions = this.explosions.filter(e => !e.done);
  }

  private disposeBomp(b: BombObj): void {
    this.scene.remove(b.mesh);
    this.scene.remove(b.trail);
    b.mesh.geometry.dispose(); (b.mesh.material as THREE.Material).dispose();
    b.trail.geometry.dispose(); b.trail.material.dispose();
  }

  private disposeExpl(e: BombExpl): void {
    this.scene.remove(e.ring);
    this.scene.remove(e.points);
    e.ring.geometry.dispose(); e.ring.material.dispose();
    e.points.geometry.dispose(); e.points.material.dispose();
  }

  // ---- Loop ----

  private startLoop(): void {
    const loop = () => {
      this.t++;
      this.updateScene();
      this.renderer.render(this.scene, this.camera);
      this.animFrame = requestAnimationFrame(loop);
    };
    this.animFrame = requestAnimationFrame(loop);
  }

  private updateScene(): void {
    // Twinkle stars
    const starMat = this.starPoints.material as THREE.PointsMaterial;
    starMat.opacity = 0.35 + 0.35 * Math.abs(Math.sin(this.t * 0.018));

    // Advance orbit
    for (let i = 0; i < 3; i++) {
      this.orbitPhases[i] = (this.orbitPhases[i]! + this.orbitSpeed) % (Math.PI * 2);
    }

    // Update ship positions & visibility
    for (let i = 0; i < 3; i++) {
      const phase = this.orbitPhases[i]!;
      const pos   = this.getOrbitPos(phase);
      const angle = this.getOrbitTangentAngle(phase);
      const group = this.shipGroups[i]!;

      // Hide when behind the planet (below PLANET_Y center minus threshold)
      if (pos.y < this.PLANET_Y - 30) {
        group.visible = false;
      } else {
        group.visible = true;
        group.position.set(pos.x, pos.y, 2);
        group.rotation.z = angle;
        // Engine pulse
        const pulse = 0.35 + 0.08 * Math.sin(this.t * 0.08 + i * 1.2);
        const glow  = group.children[1] as THREE.Mesh<THREE.CircleGeometry, THREE.MeshBasicMaterial>;
        if (glow) glow.material.opacity = pulse;
      }
    }

    // Bomb spawning
    if (this.t === this.nextBombFrame) {
      this.fireBomb(this.bombShipIdx % 3);
      this.bombShipIdx++;
      this.nextBombFrame = this.t + (this.impactCount > 0 && this.impactCount % 5 === 0 ? 160 : 80);
    }

    this.updateBombs();
    this.updateExplosions();

    // Population counter
    if (this.popEl && this.data.oldPopulation != null && this.popDisplay > this.popTarget) {
      this.popDisplay = Math.max(this.popTarget, this.popDisplay - (this.popDisplay - this.popTarget) * 0.05);
      this.popEl.textContent = String(Math.round(this.popDisplay));
    }
  }
}
