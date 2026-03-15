import * as THREE from 'three';
import type { BattleRecordDetail, ShipDesignSnapshot } from '../types/api.js';

// ---- Constants ----

const MAX_VIS = 40;

// ---- Helpers ----

function hexColor(hex: string): number {
  return parseInt(hex.replace('#', ''), 16);
}

function lightenHex(hex: string, amt: number): number {
  const c = parseInt(hex.replace('#', ''), 16);
  const r = Math.min(255, Math.round(((c >> 16) & 0xff) + (255 - ((c >> 16) & 0xff)) * amt));
  const g = Math.min(255, Math.round(((c >> 8) & 0xff) + (255 - ((c >> 8) & 0xff)) * amt));
  const b = Math.min(255, Math.round((c & 0xff) + (255 - (c & 0xff)) * amt));
  return (r << 16) | (g << 8) | b;
}

/**
 * Build a ship shape procedurally from its design parameters.
 *
 * Axes: +X = nose direction (facing right), Y = lateral.
 *
 * Design influence:
 *   drive    → hull length & engine width
 *   weapons  → cannon barrel length (per barrel)
 *   shields  → hull width / armor bulk
 *   attacks  → number of gun barrels
 *   cargo    → widened mid-section belly
 */
function makeShipShape(design: ShipDesignSnapshot): THREE.Shape {
  const mass    = design.drive + design.weapons + design.shields + design.cargo
                  + Math.max(0, design.attacks - 1) * design.weapons / 2;
  const norm    = (v: number) => mass > 0 ? v / mass : 0;

  // Normalised fractions (0..1)
  const driveF   = Math.min(norm(design.drive),   1);
  const weapF    = Math.min(norm(design.weapons),  1);
  const shieldF  = Math.min(norm(design.shields),  1);
  const cargoF   = Math.min(norm(design.cargo),    1);

  // Hull proportions (world-space units, ship ~20 wide)
  const hullLen  = 10 + driveF * 8;          // 10..18
  const halfW    = 5  + shieldF * 6;         // 5..11  (armor bulk)
  const belly    = 1  + cargoF  * 4;         // 1..5   (cargo belly offset)
  const noseX    = hullLen;
  const tailX    = -(hullLen * 0.55);
  const engineW  = 3  + driveF  * 4;         // engine nozzle half-width

  const s = new THREE.Shape();
  // Hull outline (nose → top fin → tail notch → bottom fin)
  s.moveTo(noseX, 0);
  s.lineTo(noseX * 0.35, halfW * 0.55);
  s.quadraticCurveTo(tailX * 0.3, halfW + belly, tailX * 0.6, engineW * 1.15);
  s.lineTo(tailX, engineW);
  s.lineTo(tailX - 1, 0);
  s.lineTo(tailX, -engineW);
  s.lineTo(tailX * 0.6, -engineW * 1.15);
  s.quadraticCurveTo(tailX * 0.3, -(halfW + belly), noseX * 0.35, -halfW * 0.55);
  s.closePath();
  return s;
}

/**
 * Build gun barrels as separate line segments attached to the hull.
 * Returns array of [startX, startY, endX, endY] quads.
 */
function makeGunBarrels(design: ShipDesignSnapshot): Array<[number, number, number, number]> {
  const mass    = design.drive + design.weapons + design.shields + design.cargo
                  + Math.max(0, design.attacks - 1) * design.weapons / 2;
  const weapF   = mass > 0 ? Math.min(design.weapons / mass, 1) : 0;
  const shieldF = mass > 0 ? Math.min(design.shields / mass, 1) : 0;
  const driveF  = mass > 0 ? Math.min(design.drive / mass, 1) : 0;
  const hullLen = 10 + driveF * 8;
  const halfW   = 5  + shieldF * 6;

  const count   = Math.min(design.attacks, 4);    // max 4 visible barrels
  const barrelL = 3 + weapF * 14;                 // 3..17 — bigger weapons = longer barrels
  const barrelY = halfW * 0.45;                    // attach point lateral offset
  const attachX = hullLen * 0.4;                   // attach at ~40% from nose

  const barrels: Array<[number, number, number, number]> = [];
  if (count === 0 || design.weapons <= 0) return barrels;

  if (count === 1) {
    // Single centre-line cannon
    barrels.push([attachX, 0, attachX + barrelL, 0]);
  } else {
    // Evenly distribute ±Y
    for (let i = 0; i < count; i++) {
      const t  = count === 1 ? 0 : (i / (count - 1) - 0.5) * 2;  // -1..+1
      const y  = t * barrelY * 1.2;
      barrels.push([attachX, y, attachX + barrelL, y]);
    }
  }
  return barrels;
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ---- Interfaces ----

interface VisShip {
  id: number; race: string; color: string; side: number;
  body: THREE.Mesh<THREE.ShapeGeometry, THREE.MeshBasicMaterial>;
  glow: THREE.Mesh<THREE.CircleGeometry, THREE.MeshBasicMaterial>;
  barrels: THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>[];
  group: THREE.Group;
  baseX: number; baseY: number;
  alive: boolean; opacity: number; flash: number; dying: number;
  sway: number; swayA: number;
  groupExtra?: number;
  extraEl?: HTMLElement;
}

interface VisProj {
  group: THREE.Group;
  line: THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
  head: THREE.Mesh<THREE.CircleGeometry, THREE.MeshBasicMaterial>;
  sx: number; sy: number; tx: number; ty: number;
  progress: number; speed: number;
  colorStr: string;
  killed: boolean; done: boolean; targetShipId: number;
}

interface ExplPart { x: number; y: number; vx: number; vy: number; life: number }

interface VisExpl {
  ring: THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
  points: THREE.Points<THREE.BufferGeometry, THREE.PointsMaterial>;
  partsBuf: Float32Array;
  parts: ExplPart[];
  x: number; y: number; r: number; maxR: number; op: number;
  done: boolean;
}

interface VisFloat {
  el: HTMLElement; x: number; y: number; vy: number; op: number; done: boolean;
}

// ---- BattleVisualizer ----

export class BattleVisualizer {
  private record: BattleRecordDetail;
  private playerColorMap: Map<string, string>;
  private states: Array<Record<string, number>>;

  // Playback
  private currentShot = 0;
  private playing     = false;
  private speed       = 1;
  private playTimer: number | null = null;
  private scrubberVisible = false;

  // DOM
  private el!: HTMLElement;
  private overlayEl!: HTMLElement;
  private logEl!: HTMLElement;
  private shotLabelEl!: HTMLElement;
  private scrubberEl!: HTMLInputElement;
  private scrubberPanelEl!: HTMLElement;
  private playBtnEl!: HTMLButtonElement;
  private hudEls = new Map<string, { countEl: HTMLElement; barFill: HTMLElement }>();

  // Three.js
  private renderer!: THREE.WebGLRenderer;
  private scene!: THREE.Scene;
  private camera!: THREE.OrthographicCamera;
  private animFrame: number | null = null;
  private t = 0;

  // Disposables for static scene elements
  private starPoints!: THREE.Points;
  private dividerMesh!: THREE.Mesh;

  // Dynamic scene objects
  private ships: VisShip[] = [];
  private projectiles: VisProj[] = [];
  private explosions: VisExpl[] = [];
  private floatingTexts: VisFloat[] = [];

  /** Called when auto-playback reaches the last shot and stops. */
  onPlaybackEnd?: () => void;

  private static readonly FALLBACK_COLORS = [
    '#4ade80','#38bdf8','#f87171','#facc15','#a78bfa','#fb923c','#34d399','#e879f9',
  ];

  // Virtual canvas size; camera covers [-450..+450] x [-200..+200]
  readonly CW = 900;
  readonly CH = 400;

  constructor(record: BattleRecordDetail, playerColorMap: Map<string, string>) {
    this.record         = record;
    this.playerColorMap = playerColorMap;
    this.states         = this.buildStates();
    this.el             = this.createDOM();
    this.initThree();
    this.buildStaticScene();
    this.initShips();
    this.buildHUDOverlay();
    this.renderAt(0, false);
    this.startLoop();
  }

  get element(): HTMLElement { return this.el; }

  destroy(): void {
    this.stopPlay(false);
    if (this.animFrame !== null) { cancelAnimationFrame(this.animFrame); this.animFrame = null; }

    for (const p of this.projectiles) this.disposeProj(p);
    for (const e of this.explosions)  this.disposeExpl(e);
    for (const s of this.ships) {
      this.scene.remove(s.group);
      s.body.geometry.dispose();
      s.body.material.dispose();
      s.glow.geometry.dispose();
      s.glow.material.dispose();
      for (const b of s.barrels) { b.geometry.dispose(); b.material.dispose(); }
    }
    this.floatingTexts.forEach(f => f.el.remove());

    this.starPoints.geometry.dispose();
    (this.starPoints.material as THREE.Material).dispose();
    this.dividerMesh.geometry.dispose();
    (this.dividerMesh.material as THREE.Material).dispose();
    this.scene.remove(this.starPoints);
    this.scene.remove(this.dividerMesh);

    this.renderer.dispose();
    // renderer.domElement already inside the container div which gets cleared by parent
  }

  play(): void { this.startPlay(); }

  setSpeed(n: number): void {
    this.speed = n;
    this.el.querySelectorAll<HTMLElement>('.bv-speed').forEach(b => {
      b.classList.toggle('active', parseInt(b.dataset['speed'] ?? '1') === n);
    });
  }

  // ---- State ----

  private buildStates(): Array<Record<string, number>> {
    const initial = { ...this.record.initialShips };
    if (Object.keys(initial).length === 0) {
      for (const race of this.record.participants) initial[race] = 0;
      for (const shot of this.record.protocol) {
        if (shot.killed) initial[shot.defenderRace] = (initial[shot.defenderRace] ?? 0) + 1;
      }
    }
    const states: Array<Record<string, number>> = [{ ...initial }];
    const alive = { ...initial };
    for (const shot of this.record.protocol) {
      if (shot.killed) alive[shot.defenderRace] = Math.max(0, (alive[shot.defenderRace] ?? 0) - 1);
      states.push({ ...alive });
    }
    return states;
  }

  // ---- DOM ----

  private createDOM(): HTMLElement {
    const el = document.createElement('div');
    el.className = 'bv-container';
    const total = this.record.protocol.length;
    const winnerLabel = (this.record.winner === 'Draw' || this.record.winner === 'None')
      ? 'Ничья' : this.record.winner;

    el.innerHTML = `
      <div class="bv-header">
        <div class="bv-title">⚔ ${esc(this.record.planetName)}</div>
        <div class="bv-winner-badge">🏆 ${esc(winnerLabel)}</div>
      </div>
      <div class="bv-canvas-wrap">
        <div id="bv-renderer" class="bv-renderer"></div>
        <div id="bv-hud-overlay" class="bv-hud-overlay"></div>
      </div>
      <div class="bv-scrubber-wrap">
        <div class="bv-scrubber-toprow">
          <span class="bv-shot-label" id="bv-shot-label">Выстрел 0 / ${total}</span>
        </div>
        <div class="bv-scrubber-panel hidden" id="bv-scrubber-panel">
          <div class="bv-scrubber-track-wrap">
            <input type="range" id="bv-scrubber" class="bv-scrubber-input"
              min="0" max="${total}" value="0" step="1">
            <div class="bv-scrubber-kill-marks" id="bv-scrubber-kill-marks"></div>
          </div>
          <div class="bv-scrubber-nums" id="bv-scrubber-nums"></div>
        </div>
      </div>
      <div class="bv-controls">
        <button class="bv-ctrl-btn" id="bv-first" title="В начало">⏮</button>
        <button class="bv-ctrl-btn" id="bv-prev"  title="Назад">⏴</button>
        <button class="bv-ctrl-btn bv-play-btn" id="bv-play" title="Воспроизвести">▶</button>
        <button class="bv-ctrl-btn" id="bv-next"  title="Вперёд">⏵</button>
        <button class="bv-ctrl-btn" id="bv-last"  title="В конец">⏭</button>
        <button class="bv-ctrl-btn" id="bv-timeline" title="Хронология (перемотка)">⋮⋮</button>
        <div class="bv-speed-btns">
          <button class="bv-speed active" data-speed="1">×1</button>
          <button class="bv-speed" data-speed="2">×2</button>
          <button class="bv-speed" data-speed="5">×5</button>
          <button class="bv-speed" data-speed="20">×20</button>
        </div>
      </div>
      <div class="bv-log-wrap">
        <div class="bv-log-title">Журнал боя</div>
        <div class="bv-log" id="bv-log"></div>
      </div>
    `;

    this.el = el;
    this.logEl           = el.querySelector('#bv-log')!;
    this.shotLabelEl     = el.querySelector('#bv-shot-label')!;
    this.scrubberEl      = el.querySelector('#bv-scrubber') as HTMLInputElement;
    this.scrubberPanelEl = el.querySelector('#bv-scrubber-panel')!;
    this.playBtnEl       = el.querySelector('#bv-play') as HTMLButtonElement;
    this.overlayEl       = el.querySelector('#bv-hud-overlay')!;

    this.populateScrubberMarkers(el, total);

    el.querySelector('#bv-first')!.addEventListener('click', () => this.goTo(0));
    el.querySelector('#bv-prev')!.addEventListener('click',  () => this.step(-1));
    el.querySelector('#bv-play')!.addEventListener('click',  () => this.togglePlay());
    el.querySelector('#bv-next')!.addEventListener('click',  () => this.step(1));
    el.querySelector('#bv-last')!.addEventListener('click',  () => this.goTo(total));
    el.querySelector('#bv-timeline')!.addEventListener('click', () => this.toggleScrubber());

    this.scrubberEl.addEventListener('input', () => {
      this.stopPlay(false);
      this.currentShot = parseInt(this.scrubberEl.value);
      this.renderAt(this.currentShot, false);
    });

    el.querySelectorAll<HTMLElement>('.bv-speed').forEach(btn => {
      btn.addEventListener('click', () => {
        el.querySelectorAll('.bv-speed').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        this.speed = parseInt(btn.dataset['speed'] ?? '1');
      });
    });

    return el;
  }

  // ---- Three.js init ----

  private initThree(): void {
    const container = this.el.querySelector<HTMLElement>('#bv-renderer')!;

    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(this.CW, this.CH);
    this.renderer.setClearColor(0x020816);
    container.appendChild(this.renderer.domElement);

    this.scene  = new THREE.Scene();
    // Camera: left=-450, right=450, top=200, bottom=-200, near=0.1, far=1000
    this.camera = new THREE.OrthographicCamera(-450, 450, 200, -200, 0.1, 1000);
    this.camera.position.set(0, 0, 10);
  }

  private buildStaticScene(): void {
    // Stars
    const starCount = 200;
    const starPos   = new Float32Array(starCount * 3);
    for (let i = 0; i < starCount; i++) {
      starPos[i*3]   = (Math.random() - 0.5) * 900;
      starPos[i*3+1] = (Math.random() - 0.5) * 400;
      starPos[i*3+2] = -5;
    }
    const starGeo = new THREE.BufferGeometry();
    starGeo.setAttribute('position', new THREE.BufferAttribute(starPos, 3));
    const starMat = new THREE.PointsMaterial({
      color: 0xc8d8ff, size: 1.5, sizeAttenuation: false,
      transparent: true, opacity: 0.7,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    this.starPoints = new THREE.Points(starGeo, starMat);
    this.scene.add(this.starPoints);

    // Center divider glow
    const divGeo = new THREE.PlaneGeometry(160, 400);
    const divMat = new THREE.MeshBasicMaterial({
      color: 0x38bdf8, transparent: true, opacity: 0.035,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    this.dividerMesh = new THREE.Mesh(divGeo, divMat);
    this.dividerMesh.position.set(0, 0, -1);
    this.scene.add(this.dividerMesh);
  }

  // ---- Fleet helpers ----

  private getFleetAnchor(side: number): { x: number; y: number } {
    switch (side) {
      case 0:  return { x: -280, y:   0 };
      case 1:  return { x:  280, y:   0 };
      case 2:  return { x:    0, y: 110 };
      default: return { x:    0, y: -110 };
    }
  }

  private getFleetAngle(side: number): number {
    switch (side) {
      case 0:  return 0;
      case 1:  return Math.PI;
      case 2:  return Math.PI / 2;
      default: return -Math.PI / 2;
    }
  }

  private layoutFleet(count: number, side: number): Array<{ x: number; y: number }> {
    if (count === 0) return [];
    const anchor = this.getFleetAnchor(side);
    const cellW  = 26;
    const cellH  = 20;
    const padX   = 6;
    const padY   = 5;
    const cols   = Math.min(count, Math.ceil(Math.sqrt(count * 1.5)));
    const rows   = Math.ceil(count / cols);
    const totalW = cols * (cellW + padX) - padX;
    const totalH = rows * (cellH + padY) - padY;
    const startX = anchor.x - totalW / 2;
    const startY = anchor.y + totalH / 2;
    const positions: Array<{ x: number; y: number }> = [];
    for (let i = 0; i < count; i++) {
      const col = i % cols;
      const row = Math.floor(i / cols);
      positions.push({
        x: startX + col * (cellW + padX) + cellW / 2,
        y: startY - row * (cellH + padY) - cellH / 2,
      });
    }
    return positions;
  }

  private getColor(race: string, idx: number): string {
    return this.playerColorMap.get(race)
      ?? BattleVisualizer.FALLBACK_COLORS[idx % BattleVisualizer.FALLBACK_COLORS.length]!;
  }

  // ---- Ship init ----

  private initShips(): void {
    let shipId = 0;

    this.record.participants.forEach((race, raceIdx) => {
      const side       = raceIdx % 4;
      const color      = this.getColor(race, raceIdx);
      const angle      = this.getFleetAngle(side);
      const initial    = this.record.initialShips[race] ?? 0;
      const visCount   = Math.min(initial, MAX_VIS);
      const positions  = this.layoutFleet(visCount, side);
      const showExtra  = initial > MAX_VIS;
      const extraCount = initial - MAX_VIS;

      // Ship design for this race (fallback: generic warship)
      const design: ShipDesignSnapshot = this.record.shipDesigns?.[race]
        ?? { weapons: 2, shields: 1, drive: 2, cargo: 0, attacks: 1 };

      // Pre-build shared geometries per race (same design for all ships in fleet)
      const shipShape = makeShipShape(design);
      const gunBarrelDefs = makeGunBarrels(design);

      // Scale factor: keep ships at consistent visual size (~1 "cell")
      const mass       = design.drive + design.weapons + design.shields + design.cargo
                         + Math.max(0, design.attacks - 1) * design.weapons / 2;
      const hullLen    = 10 + (mass > 0 ? Math.min(design.drive / mass, 1) : 0) * 8;
      const scale      = 10 / Math.max(hullLen, 4);   // normalise so ~10 units wide

      // Engine glow size scales with drive
      const driveF     = mass > 0 ? Math.min(design.drive / mass, 1) : 0;
      const glowR      = 4 + driveF * 6;
      const tailX      = -(hullLen * 0.55);

      positions.forEach((pos, posIdx) => {
        // Hull body
        const bodyGeo = new THREE.ShapeGeometry(shipShape);
        const bodyMat = new THREE.MeshBasicMaterial({
          color: hexColor(color), transparent: true, opacity: 1,
          blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
        });
        const body = new THREE.Mesh(bodyGeo, bodyMat);

        // Engine glow (at tail)
        const glowGeo = new THREE.CircleGeometry(glowR, 12);
        const glowMat = new THREE.MeshBasicMaterial({
          color: hexColor(color), transparent: true, opacity: 0.45,
          blending: THREE.AdditiveBlending, depthWrite: false,
        });
        const glow = new THREE.Mesh(glowGeo, glowMat);
        glow.position.set(tailX - 1, 0, -0.1);

        const group = new THREE.Group();
        group.add(body);
        group.add(glow);

        // Gun barrels (bright accent lines)
        const barrels: THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>[] = [];
        for (const [bx0, by0, bx1, by1] of gunBarrelDefs) {
          const bGeo = new THREE.BufferGeometry();
          bGeo.setAttribute('position', new THREE.BufferAttribute(
            new Float32Array([bx0, by0, 0.2, bx1, by1, 0.2]), 3));
          const bMat = new THREE.LineBasicMaterial({
            color: 0xffffff, transparent: true, opacity: 0.85,
            blending: THREE.AdditiveBlending, depthWrite: false,
          });
          const barrel = new THREE.Line(bGeo, bMat);
          group.add(barrel);
          barrels.push(barrel);
        }

        group.scale.set(scale, scale, 1);
        group.position.set(pos.x, pos.y, 1);
        group.rotation.z = angle;
        this.scene.add(group);

        const isGroupShip = showExtra && posIdx === 0;
        const ship: VisShip = {
          id: shipId++, race, color, side,
          body, glow, barrels, group,
          baseX: pos.x, baseY: pos.y,
          alive: true, opacity: 1, flash: 0, dying: 0,
          sway: Math.random() * Math.PI * 2,
          swayA: 1.5 + Math.random() * 1.5,
          groupExtra: isGroupShip ? extraCount : undefined,
        };

        if (isGroupShip) {
          const labelEl = document.createElement('div');
          labelEl.style.cssText = [
            'position:absolute',
            'background:rgba(0,0,0,0.7)',
            `border:1px solid ${color}`,
            `color:${color}`,
            'font:bold 9px monospace',
            'padding:1px 4px',
            'border-radius:3px',
            'pointer-events:none',
            'transform:translate(-50%,-50%)',
            'white-space:nowrap',
          ].join(';');
          labelEl.textContent = `+${extraCount}`;
          this.overlayEl.appendChild(labelEl);
          ship.extraEl = labelEl;
        }

        this.ships.push(ship);
      });
    });
  }

  // ---- HUD overlay ----

  private buildHUDOverlay(): void {
    this.record.participants.forEach((race, raceIdx) => {
      const side   = raceIdx % 4;
      const color  = this.getColor(race, raceIdx);
      const anchor = this.getFleetAnchor(side);

      // Label above fleet (or below for side 3)
      const labelWorldY = side === 3 ? anchor.y - 80 : anchor.y + 80;
      const sx = ((anchor.x + 450) / 900) * 100;
      const sy = ((200 - labelWorldY) / 400) * 100;

      const div = document.createElement('div');
      div.style.cssText = `position:absolute;left:${sx}%;top:${sy}%;transform:translate(-50%,-50%);text-align:center;pointer-events:none;`;

      const initial = this.record.initialShips[race] ?? 0;
      div.innerHTML = `
        <div style="font:bold 11px monospace;color:${color};text-shadow:0 0 8px ${color}">${esc(race)}</div>
        <div class="bv-hud-count" style="font:10px monospace;color:rgba(200,220,255,0.7)">${initial}/${initial}</div>
        <div style="width:80px;height:5px;background:rgba(255,255,255,0.08);border-radius:2px;margin:2px auto 0">
          <div class="bv-hud-bar" style="height:100%;border-radius:2px;background:${color};width:100%"></div>
        </div>
      `;
      this.overlayEl.appendChild(div);
      this.hudEls.set(race, {
        countEl: div.querySelector('.bv-hud-count')! as HTMLElement,
        barFill: div.querySelector('.bv-hud-bar')! as HTMLElement,
      });
    });
  }

  private updateHUD(): void {
    this.record.participants.forEach((race, raceIdx) => {
      const initial  = this.record.initialShips[race] ?? 0;
      const alive    = this.ships.filter(s => s.race === race && s.alive).length;
      const ratio    = initial > 0 ? alive / initial : 0;
      const color    = this.getColor(race, raceIdx);
      const barColor = ratio > 0.5 ? color : ratio > 0.25 ? '#facc15' : '#f87171';
      const hud = this.hudEls.get(race);
      if (!hud) return;
      hud.countEl.textContent = `${alive}/${initial}`;
      hud.barFill.style.width      = `${ratio * 100}%`;
      hud.barFill.style.background = barColor;
    });
  }

  // ---- Update ships ----

  private updateShips(): void {
    for (const ship of this.ships) {
      if (ship.opacity <= 0 && ship.dying === 0) {
        ship.group.visible = false;
        if (ship.extraEl) ship.extraEl.style.opacity = '0';
        continue;
      }

      ship.group.visible = true;

      if (ship.dying > 0) {
        ship.opacity = Math.max(0, 1 - ship.dying / 30);
        ship.dying++;
        if (ship.dying >= 30) { ship.opacity = 0; ship.dying = 0; }
      }

      const swayY = Math.sin(this.t * 0.04 + ship.sway) * ship.swayA;
      ship.group.position.set(ship.baseX, ship.baseY + swayY, 1);

      const colorVal = ship.flash > 0 ? lightenHex(ship.color, ship.flash * 0.85) : hexColor(ship.color);
      ship.body.material.color.setHex(colorVal);
      ship.body.material.opacity = ship.opacity;
      ship.glow.material.opacity = ship.opacity * 0.45;
      for (const b of ship.barrels) b.material.opacity = ship.opacity * 0.85;

      if (ship.flash > 0) ship.flash = Math.max(0, ship.flash - 0.07);

      if (ship.extraEl && ship.opacity > 0.5) {
        const wx = ship.group.position.x;
        const wy = ship.group.position.y - 12;
        ship.extraEl.style.left    = `${((wx + 450) / 900) * 100}%`;
        ship.extraEl.style.top     = `${((200 - wy) / 400) * 100}%`;
        ship.extraEl.style.opacity = String(ship.opacity);
      }
    }
  }

  // ---- Projectiles ----

  private spawnProjectile(attackerRace: string, defenderRace: string, killed: boolean): void {
    const attackers = this.ships.filter(s => s.race === attackerRace && s.alive);
    const defenders = this.ships.filter(s => s.race === defenderRace && s.alive);
    if (attackers.length === 0 || defenders.length === 0) return;

    const src = attackers[Math.floor(Math.random() * attackers.length)]!;
    const dst = killed
      ? defenders[defenders.length - 1]!
      : defenders[Math.floor(Math.random() * defenders.length)]!;

    const color = this.getColor(attackerRace, this.record.participants.indexOf(attackerRace));
    const speed = this.speed <= 1 ? 0.018 : 0.036;

    const sx = src.group.position.x;
    const sy = src.group.position.y;
    const tx = dst.group.position.x;
    const ty = dst.group.position.y;

    // Line: 2 dynamic points (tail → head)
    const lineGeo = new THREE.BufferGeometry();
    lineGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array([
      sx, sy, 2,
      sx, sy, 2,
    ]), 3));
    const lineMat = new THREE.LineBasicMaterial({
      color: hexColor(color), transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const line = new THREE.Line(lineGeo, lineMat);

    const headGeo = new THREE.CircleGeometry(2.5, 8);
    const headMat = new THREE.MeshBasicMaterial({
      color: 0xffffff, transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const head = new THREE.Mesh(headGeo, headMat);
    head.position.set(sx, sy, 2.1);

    const group = new THREE.Group();
    group.add(line);
    group.add(head);
    this.scene.add(group);

    this.projectiles.push({
      group, line, head, sx, sy, tx, ty,
      progress: 0, speed,
      colorStr: color,
      killed, done: false, targetShipId: dst.id,
    });
  }

  private updateProjectiles(): void {
    for (const proj of this.projectiles) {
      if (proj.done) continue;
      proj.progress += proj.speed;

      const threshold = proj.killed ? 1 : 0.6;
      if (proj.progress >= threshold) {
        if (proj.killed) this.onProjectileHit(proj);
        else {
          const mx = proj.sx + (proj.tx - proj.sx) * 0.6;
          const my = proj.sy + (proj.ty - proj.sy) * 0.6;
          this.spawnExplosion(mx, my, 14, proj.colorStr, 0);
        }
        proj.done = true;
        this.disposeProj(proj);
        continue;
      }

      const p    = proj.progress;
      const tail = Math.max(0, p - 0.08);
      const alpha = proj.killed ? 1 : Math.max(0, 1 - p / 0.6);

      const cx = proj.sx + (proj.tx - proj.sx) * p;
      const cy = proj.sy + (proj.ty - proj.sy) * p;
      const tx = proj.sx + (proj.tx - proj.sx) * tail;
      const ty = proj.sy + (proj.ty - proj.sy) * tail;

      const attr = proj.line.geometry.attributes['position'] as THREE.BufferAttribute;
      attr.setXYZ(0, tx, ty, 2);
      attr.setXYZ(1, cx, cy, 2);
      attr.needsUpdate = true;

      proj.head.position.set(cx, cy, 2.1);
      proj.line.material.opacity = alpha;
      proj.head.material.opacity = alpha;
    }
    this.projectiles = this.projectiles.filter(p => !p.done);
  }

  private onProjectileHit(proj: VisProj): void {
    const target = this.ships.find(s => s.id === proj.targetShipId);
    if (proj.killed && target) {
      target.dying = 1;
      target.alive = false;
      const defColor = this.getColor(target.race, this.record.participants.indexOf(target.race));
      this.spawnExplosion(proj.tx, proj.ty, 32, defColor, 10);
      this.spawnFloat(proj.tx, proj.ty, '💥 -1', '#f87171');
    } else {
      this.spawnExplosion(proj.tx, proj.ty, 14, proj.colorStr, 0);
    }
  }

  private disposeProj(proj: VisProj): void {
    this.scene.remove(proj.group);
    proj.line.geometry.dispose();
    proj.line.material.dispose();
    proj.head.geometry.dispose();
    proj.head.material.dispose();
  }

  // ---- Explosions ----

  private spawnExplosion(x: number, y: number, maxR: number, color: string, numParticles: number): void {
    const ringGeo = new THREE.RingGeometry(0.85, 1, 32);
    const ringMat = new THREE.MeshBasicMaterial({
      color: hexColor(color), transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
    });
    const ring = new THREE.Mesh(ringGeo, ringMat);
    ring.position.set(x, y, 2);
    ring.scale.set(2, 2, 1);
    this.scene.add(ring);

    const n = Math.max(1, numParticles);
    const partsBuf = new Float32Array(n * 3);
    const parts: ExplPart[] = [];
    for (let i = 0; i < numParticles; i++) {
      const a = (Math.PI * 2 * i) / numParticles + Math.random() * 0.5;
      const spd = 1.5 + Math.random() * 2.5;
      parts.push({ x, y, vx: Math.cos(a) * spd, vy: Math.sin(a) * spd, life: 1 });
      partsBuf[i*3]   = x;
      partsBuf[i*3+1] = y;
      partsBuf[i*3+2] = 2.5;
    }

    const pointsGeo = new THREE.BufferGeometry();
    pointsGeo.setAttribute('position', new THREE.BufferAttribute(partsBuf, 3));
    const pointsMat = new THREE.PointsMaterial({
      color: hexColor(color), size: 3, sizeAttenuation: false,
      transparent: true, opacity: 1,
      blending: THREE.AdditiveBlending, depthWrite: false,
    });
    const points = new THREE.Points(pointsGeo, pointsMat);
    this.scene.add(points);

    this.explosions.push({ ring, points, partsBuf, parts, x, y, r: 2, maxR, op: 1, done: false });
  }

  private updateExplosions(): void {
    for (const expl of this.explosions) {
      if (expl.done) continue;
      expl.r  += (expl.maxR - expl.r) * 0.12;
      expl.op -= 0.035;
      if (expl.op <= 0) {
        expl.done = true;
        this.disposeExpl(expl);
        continue;
      }

      expl.ring.scale.set(expl.r, expl.r, 1);
      expl.ring.material.opacity = expl.op;

      for (let i = 0; i < expl.parts.length; i++) {
        const p = expl.parts[i]!;
        p.x += p.vx; p.y += p.vy; p.vy += 0.08; p.life -= 0.04;
        expl.partsBuf[i*3]   = p.x;
        expl.partsBuf[i*3+1] = p.y;
        expl.partsBuf[i*3+2] = 2.5;
      }
      if (expl.parts.length > 0) {
        const attr = expl.points.geometry.attributes['position'] as THREE.BufferAttribute;
        attr.needsUpdate = true;
        expl.points.material.opacity = expl.op;
      }
    }
    this.explosions = this.explosions.filter(e => !e.done);
  }

  private disposeExpl(expl: VisExpl): void {
    this.scene.remove(expl.ring);
    this.scene.remove(expl.points);
    expl.ring.geometry.dispose();
    expl.ring.material.dispose();
    expl.points.geometry.dispose();
    expl.points.material.dispose();
  }

  // ---- Floating texts ----

  private spawnFloat(x: number, y: number, text: string, color: string): void {
    const el = document.createElement('div');
    el.style.cssText = [
      'position:absolute',
      `color:${color}`,
      'font:bold 12px monospace',
      `text-shadow:0 0 6px ${color}`,
      'pointer-events:none',
      'transform:translate(-50%,-50%)',
      'white-space:nowrap',
    ].join(';');
    el.textContent = text;
    this.overlayEl.appendChild(el);
    this.floatingTexts.push({ el, x, y, vy: -0.6, op: 1, done: false });
  }

  private updateFloats(): void {
    for (const fl of this.floatingTexts) {
      if (fl.done) continue;
      fl.y  += fl.vy;
      fl.op -= 0.022;
      if (fl.op <= 0) {
        fl.done = true;
        fl.el.remove();
        continue;
      }
      fl.el.style.left    = `${((fl.x + 450) / 900) * 100}%`;
      fl.el.style.top     = `${((200 - fl.y) / 400) * 100}%`;
      fl.el.style.opacity = String(fl.op);
    }
    this.floatingTexts = this.floatingTexts.filter(f => !f.done);
  }

  // ---- Animation loop ----

  private startLoop(): void {
    const loop = () => {
      this.t++;
      // Twinkle stars via opacity
      const starMat = this.starPoints.material as THREE.PointsMaterial;
      starMat.opacity = 0.4 + 0.3 * Math.abs(Math.sin(this.t * 0.015));
      this.updateShips();
      this.updateProjectiles();
      this.updateExplosions();
      this.updateFloats();
      this.updateHUD();
      this.renderer.render(this.scene, this.camera);
      this.animFrame = requestAnimationFrame(loop);
    };
    this.animFrame = requestAnimationFrame(loop);
  }

  // ---- Sync visual state ----

  private syncVisualState(shotIdx: number): void {
    const state = this.states[shotIdx] ?? this.states[this.states.length - 1]!;
    this.record.participants.forEach(race => {
      const aliveCount = state[race] ?? 0;
      const raceShips  = this.ships.filter(s => s.race === race);
      raceShips.forEach((ship, idx) => {
        if (idx < aliveCount) {
          ship.alive = true; ship.opacity = 1; ship.dying = 0; ship.flash = 0;
        } else {
          ship.alive = false; ship.opacity = 0; ship.dying = 0;
        }
      });
    });
  }

  // ---- renderAt ----

  renderAt(shotIdx: number, animate: boolean): void {
    const total = this.record.protocol.length;
    this.shotLabelEl.textContent = `Выстрел ${shotIdx} / ${total}`;
    this.scrubberEl.value = String(shotIdx);
    const pct = total > 0 ? (shotIdx / total) * 100 : 0;
    this.scrubberEl.style.setProperty('--bv-pct', `${pct}%`);

    if (!animate || this.speed > 2) {
      this.syncVisualState(shotIdx);
      for (const p of this.projectiles) this.disposeProj(p);
      this.projectiles = [];
      for (const e of this.explosions) this.disposeExpl(e);
      this.explosions = [];
      for (const f of this.floatingTexts) f.el.remove();
      this.floatingTexts = [];

      if (animate && this.speed > 2 && shotIdx > 0) {
        const shot      = this.record.protocol[shotIdx - 1]!;
        const prevAlive = this.states[shotIdx - 1]?.[shot.defenderRace] ?? 0;
        const nowAlive  = this.states[shotIdx]?.[shot.defenderRace] ?? 0;
        if (shot.killed && prevAlive > nowAlive) {
          const raceShips = this.ships.filter(s => s.race === shot.defenderRace);
          const dying = raceShips[nowAlive];
          if (dying) dying.flash = 1;
        }
      }
    } else {
      if (shotIdx > 0) {
        const shot = this.record.protocol[shotIdx - 1]!;
        this.spawnProjectile(shot.attackerRace, shot.defenderRace, shot.killed);
        if (shot.killed) {
          const defShips = this.ships.filter(s => s.race === shot.defenderRace && s.alive);
          const target   = defShips[defShips.length - 1];
          if (target) target.alive = false;
        }
      }
    }

    this.updateLog(shotIdx);
  }

  private updateLog(upTo: number): void {
    const start = Math.max(0, upTo - 10);
    const shots = this.record.protocol.slice(start, upTo);
    this.logEl.innerHTML = shots.map(s => {
      const cls    = s.killed ? 'bv-log-kill' : 'bv-log-miss';
      const result = s.killed ? '💥 уничтожен' : '✗ промах';
      return `<div class="bv-log-entry ${cls}">
        <span class="bv-log-attacker">${esc(s.attackerRace)}</span>
        <span class="bv-log-arrow"> → </span>
        <span class="bv-log-defender">${esc(s.defenderRace)}</span>
        <span class="bv-log-result">${result}</span>
      </div>`;
    }).join('');
    this.logEl.scrollTop = this.logEl.scrollHeight;
  }

  // ---- Scrubber ----

  private populateScrubberMarkers(el: HTMLElement, total: number): void {
    if (total === 0) return;
    const killMarksEl = el.querySelector('#bv-scrubber-kill-marks')!;
    const numsEl      = el.querySelector('#bv-scrubber-nums')!;

    this.record.protocol.forEach((shot, idx) => {
      if (shot.killed) {
        const tick = document.createElement('div');
        tick.className  = 'bv-scrubber-kill-tick';
        tick.style.left = `${((idx + 1) / total) * 100}%`;
        tick.title      = `${shot.defenderRace} уничтожен (выстрел ${idx + 1})`;
        killMarksEl.appendChild(tick);
      }
    });

    const step = total <= 20 ? 5 : total <= 100 ? 25 : total <= 500 ? 100 : 250;
    for (let i = 0; i <= total; i += step) {
      const label = document.createElement('div');
      label.className   = 'bv-scrubber-num';
      label.style.left  = `${(i / total) * 100}%`;
      label.textContent = String(i);
      label.title       = `Перейти к выстрелу ${i}`;
      label.addEventListener('click', () => this.goTo(i));
      numsEl.appendChild(label);
    }
  }

  private toggleScrubber(): void {
    this.scrubberVisible = !this.scrubberVisible;
    this.scrubberPanelEl.classList.toggle('hidden', !this.scrubberVisible);
    this.el.querySelector<HTMLElement>('#bv-timeline')?.classList.toggle('active', this.scrubberVisible);
  }

  // ---- Playback ----

  private goTo(idx: number): void {
    this.stopPlay(false);
    this.currentShot = Math.max(0, Math.min(idx, this.record.protocol.length));
    this.renderAt(this.currentShot, false);
  }

  private step(delta: number): void {
    this.stopPlay(false);
    this.currentShot = Math.max(0, Math.min(this.currentShot + delta, this.record.protocol.length));
    this.renderAt(this.currentShot, true);
  }

  private togglePlay(): void {
    if (this.playing) this.stopPlay(false);
    else this.startPlay();
  }

  private startPlay(): void {
    if (this.currentShot >= this.record.protocol.length) this.currentShot = 0;
    this.playing = true;
    this.playBtnEl.textContent = '⏸';
    this.scheduleNext();
  }

  private stopPlay(fireCallback: boolean): void {
    this.playing = false;
    if (this.playBtnEl) this.playBtnEl.textContent = '▶';
    if (this.playTimer !== null) { window.clearTimeout(this.playTimer); this.playTimer = null; }
    if (fireCallback) this.onPlaybackEnd?.();
  }

  private scheduleNext(): void {
    if (!this.playing) return;
    this.playTimer = window.setTimeout(() => {
      if (!this.playing) return;
      this.currentShot++;
      this.renderAt(this.currentShot, true);
      if (this.currentShot >= this.record.protocol.length) this.stopPlay(true);
      else this.scheduleNext();
    }, Math.max(20, 350 / this.speed));
  }
}
