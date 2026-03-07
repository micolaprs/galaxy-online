import * as THREE from 'three';
import { api } from '../api/client.js';
import type { PlanetDetail, TechLevels } from '../types/api.js';

type TechKey = keyof TechLevels;

interface TechOrbit {
  key: TechKey;
  ring: THREE.Mesh;
  node: THREE.Mesh;
  speed: number;
  radius: number;
  angle: number;
  tiltX: number;
  tiltY: number;
}

interface DevelopmentVisuals {
  shell: THREE.Mesh;
  nodes: THREE.Mesh[];
}

const TECH_META: Array<{ key: TechKey; label: string; color: string; accent: string }> = [
  { key: 'drive', label: 'Двигатели', color: '#38bdf8', accent: 'векторные трассы' },
  { key: 'weapons', label: 'Оружие', color: '#f87171', accent: 'орбитальные батареи' },
  { key: 'shields', label: 'Щиты', color: '#a78bfa', accent: 'ионный купол' },
  { key: 'cargo', label: 'Логистика', color: '#facc15', accent: 'грузовые петли' },
];

const DEVELOPMENT_STAGES = [
  { limit: 0.2, label: 'Форпост', hint: 'редкие огни и слабая инфраструктура' },
  { limit: 0.45, label: 'Колония', hint: 'строительные пояса и растущая сеть' },
  { limit: 0.7, label: 'Индустриальный мир', hint: 'яркие магистрали и плотная орбита' },
  { limit: 1.1, label: 'Техноядро', hint: 'перегруженная орбита и мощная энергетика' },
];

export class PlanetPanel {
  private el: HTMLElement;
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.PerspectiveCamera;
  private planetGroup!: THREE.Group;
  private planet3D!: THREE.Mesh;
  private glow!:     THREE.Mesh;
  private cloudLayer!: THREE.Mesh;
  private techOrbits: TechOrbit[] = [];
  private developmentVisuals: DevelopmentVisuals | null = null;
  private animFrame: number | null = null;
  private animationClock = 0;
  private currentTech: TechLevels | null = null;
  private activePlanetName: string | null = null;

  private gameId: string;
  private playerColorMap: Map<string, string>;
  private playerTechMap = new Map<string, TechLevels>();

  constructor(
    container: HTMLElement,
    gameId: string,
    playerColorMap: Map<string, string>,
  ) {
    this.gameId         = gameId;
    this.playerColorMap = playerColorMap;

    this.el = document.createElement('div');
    this.el.className = 'planet-panel hidden';
    container.appendChild(this.el);

    this.el.innerHTML = `
      <div class="pp-header">
        <span class="pp-title" id="pp-title">Planet</span>
        <button class="pp-close" id="pp-close">✕</button>
      </div>
      <div class="pp-body">
        <canvas id="pp-canvas" width="180" height="180"></canvas>
        <div class="pp-stats" id="pp-stats"></div>
      </div>
    `;

    this.el.querySelector('#pp-close')!.addEventListener('click', () => this.hide());
    this.init3D();
  }

  setPlayerTechMap(playerTechMap: Map<string, TechLevels>): void {
    this.playerTechMap = new Map(playerTechMap);
  }

  async show(planetName: string): Promise<void> {
    this.activePlanetName = planetName;
    this.el.classList.remove('hidden');
    this.el.querySelector('#pp-title')!.textContent = `⬡ ${planetName}`;
    this.el.querySelector('#pp-stats')!.innerHTML = '<div class="pp-loading">Загрузка…</div>';

    try {
      const d = await api.getPlanetDetail(this.gameId, planetName);
      if (this.activePlanetName !== planetName) return;
      this.currentTech = d.ownerId ? (this.playerTechMap.get(d.ownerId) ?? null) : null;
      this.renderStats(d);
      this.updatePlanetColor(d.ownerId ? (this.playerColorMap.get(d.ownerId) ?? '#888') : '#475569');
      this.updateDevelopmentState(d);
      this.updateTechOrbits(this.currentTech);
    } catch {
      this.el.querySelector('#pp-stats')!.innerHTML =
        '<div class="pp-loading error">Ошибка загрузки</div>';
    }
  }

  hide(): void {
    this.activePlanetName = null;
    this.el.classList.add('hidden');
  }

  destroy(): void {
    if (this.animFrame !== null) cancelAnimationFrame(this.animFrame);
    this.renderer.dispose();
  }

  // ---- 3D sphere ----

  private init3D(): void {
    const canvas = this.el.querySelector<HTMLCanvasElement>('#pp-canvas')!;
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(180, 180);
    this.renderer.setClearColor(0x000000, 0);

    this.scene  = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100);
    this.camera.position.set(0, 0, 4);

    // Ambient + key light
    this.scene.add(new THREE.AmbientLight(0x334466, 0.6));
    const key = new THREE.DirectionalLight(0xaaccff, 1.5);
    key.position.set(2, 3, 4);
    this.scene.add(key);

    this.planetGroup = new THREE.Group();
    this.planetGroup.rotation.z = 0.12;
    this.scene.add(this.planetGroup);

    // Planet sphere
    const geo = new THREE.SphereGeometry(1, 32, 24);
    const mat = new THREE.MeshPhongMaterial({
      color: 0x4ade80,
      shininess: 70,
      specular: 0x88bbff,
      emissive: 0x0c1628,
      emissiveIntensity: 0.12,
    });
    this.planet3D = new THREE.Mesh(geo, mat);
    this.planetGroup.add(this.planet3D);

    // Glow (additive outer shell)
    const glowGeo = new THREE.SphereGeometry(1.18, 32, 24);
    const glowMat = new THREE.MeshBasicMaterial({
      color: 0x4ade80, transparent: true, opacity: 0.12,
      side: THREE.FrontSide, blending: THREE.AdditiveBlending, depthWrite: false,
    });
    this.glow = new THREE.Mesh(glowGeo, glowMat);
    this.planetGroup.add(this.glow);

    const cloudGeo = new THREE.SphereGeometry(1.06, 32, 24);
    const cloudMat = new THREE.MeshPhongMaterial({
      color: 0xdbeafe,
      transparent: true,
      opacity: 0.12,
      shininess: 10,
      depthWrite: false,
    });
    this.cloudLayer = new THREE.Mesh(cloudGeo, cloudMat);
    this.planetGroup.add(this.cloudLayer);

    this.buildTechOrbits();

    this.animate();
  }

  private buildTechOrbits(): void {
    for (const meta of TECH_META) {
      const radius = 1.35 + this.techOrbits.length * 0.17;
      const ringGeo = new THREE.TorusGeometry(radius, 0.025, 12, 80);
      const ringMat = new THREE.MeshBasicMaterial({
        color: parseColor(meta.color),
        transparent: true,
        opacity: 0.14,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      });
      const ring = new THREE.Mesh(ringGeo, ringMat);
      const tiltX = 0.45 + this.techOrbits.length * 0.18;
      const tiltY = 0.1 + this.techOrbits.length * 0.14;
      ring.rotation.x = tiltX;
      ring.rotation.y = tiltY;
      this.planetGroup.add(ring);

      const nodeGeo = new THREE.SphereGeometry(0.06, 12, 10);
      const nodeMat = new THREE.MeshBasicMaterial({
        color: parseColor(meta.color),
        transparent: true,
        opacity: 0.8,
      });
      const node = new THREE.Mesh(nodeGeo, nodeMat);
      this.planetGroup.add(node);

      this.techOrbits.push({
        key: meta.key,
        ring,
        node,
        speed: 0.35 + this.techOrbits.length * 0.08,
        radius,
        angle: this.techOrbits.length * 0.8,
        tiltX,
        tiltY,
      });
    }
  }

  private animate(): void {
    this.animFrame = requestAnimationFrame(() => this.animate());
    this.animationClock += 0.016;
    this.planetGroup.rotation.y += 0.006;
    this.planet3D.rotation.y += 0.004;
    this.cloudLayer.rotation.y -= 0.0025;
    this.glow.scale.setScalar(1 + Math.sin(this.animationClock * 1.6) * 0.015);
    this.animateTechOrbits();
    this.animateDevelopmentState();
    this.renderer.render(this.scene, this.camera);
  }

  private updatePlanetColor(hexColor: string): void {
    const c = parseColor(hexColor);
    const planetMaterial = this.planet3D.material as THREE.MeshPhongMaterial;
    planetMaterial.color.setHex(c);
    planetMaterial.emissive.setHex(c);
    planetMaterial.emissiveIntensity = 0.12;
    (this.glow.material as THREE.MeshBasicMaterial).color.setHex(c);
  }

  // ---- Stats render ----

  private renderStats(d: PlanetDetail): void {
    const el = this.el.querySelector('#pp-stats')!;

    const prodLabel: Record<string, string> = {
      Capital: 'Капитал', Materials: 'Материалы', Drive: 'Двигатели',
      Weapons: 'Оружие', Shields: 'Щиты', Cargo: 'Грузовой отсек', Ship: 'Корабль',
    };

    const shipRows = d.groups.length === 0
      ? '<div class="pp-row"><span class="pp-label">Корабли:</span><span class="pp-value muted">нет</span></div>'
      : d.groups.map(g =>
          `<div class="pp-row">
            <span class="pp-label">${esc(g.ownerName)}:</span>
            <span class="pp-value">${g.ships}× ${esc(g.shipTypeName)}</span>
          </div>`
        ).join('');

    const devRatio = d.size > 0 ? clamp01((d.population + d.industry) / (d.size * 1.65)) : 0;
    const development = describeDevelopment(devRatio);
    const techMarkup = this.currentTech
      ? TECH_META.map(meta => {
          const level = this.currentTech?.[meta.key] ?? 0;
          const intensity = Math.min(100, Math.round((level / 10) * 100));
          const stage = techLevelLabel(level);
          return `
            <div class="pp-tech-row">
              <div class="pp-tech-head">
                <span class="pp-tech-name">
                  <span class="pp-tech-dot" style="--tech-color:${meta.color}"></span>
                  ${meta.label}
                </span>
                <span class="pp-tech-level">T${level}</span>
              </div>
              <div class="pp-tech-bar"><span style="--fill:${intensity}%;--tech-color:${meta.color}"></span></div>
              <div class="pp-tech-note">${stage} • ${meta.accent}</div>
            </div>
          `;
        }).join('')
      : '<div class="pp-tech-empty">Нет владельца или данные технологий недоступны.</div>';

    el.innerHTML = `
      <div class="pp-section">
        <div class="pp-row">
          <span class="pp-label">Владелец:</span>
          <span class="pp-value ${d.ownerName ? '' : 'muted'}">${esc(d.ownerName ?? 'никого')}</span>
        </div>
        ${d.isHome ? '<div class="pp-badge">🏠 Родная планета</div>' : ''}
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section">
        <div class="pp-row"><span class="pp-label">Размер:</span><span class="pp-value">${d.size}</span></div>
        <div class="pp-row"><span class="pp-label">Ресурсы:</span><span class="pp-value">${d.resources}</span></div>
        <div class="pp-row"><span class="pp-label">Население:</span><span class="pp-value">${Math.round(d.population)}</span></div>
        <div class="pp-row"><span class="pp-label">Промышленность:</span><span class="pp-value">${Math.round(d.industry)}</span></div>
        <div class="pp-row"><span class="pp-label">Производство:</span><span class="pp-value accent">${d.production.toFixed(1)}/ход</span></div>
        <div class="pp-dev-card">
          <div class="pp-dev-head">
            <span class="pp-dev-title">${development.label}</span>
            <span class="pp-dev-score">${Math.round(devRatio * 100)}%</span>
          </div>
          <div class="pp-dev-bar"><span style="--fill:${Math.round(devRatio * 100)}%"></span></div>
          <div class="pp-dev-note">${development.hint}</div>
        </div>
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section">
        <div class="pp-row"><span class="pp-label">Строит:</span><span class="pp-value">${prodLabel[d.producing] ?? d.producing}${d.shipTypeName ? ` (${esc(d.shipTypeName)})` : ''}</span></div>
        <div class="pp-row"><span class="pp-label">Капитал:</span><span class="pp-value">${Math.round(d.stockpiles.capital)}</span></div>
        <div class="pp-row"><span class="pp-label">Материалы:</span><span class="pp-value">${Math.round(d.stockpiles.materials)}</span></div>
        <div class="pp-row"><span class="pp-label">Колонисты:</span><span class="pp-value">${Math.round(d.stockpiles.colonists)}</span></div>
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section">
        <div class="pp-section-title">Технологический профиль</div>
        ${techMarkup}
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section pp-ships">
        <div class="pp-section-title">Флоты</div>
        ${shipRows}
      </div>
    `;
  }

  private updateDevelopmentState(d: PlanetDetail): void {
    if (this.developmentVisuals) {
      this.planetGroup.remove(this.developmentVisuals.shell);
      this.developmentVisuals.shell.geometry.dispose();
      (this.developmentVisuals.shell.material as THREE.Material).dispose();
      for (const node of this.developmentVisuals.nodes) {
        this.planetGroup.remove(node);
        node.geometry.dispose();
        (node.material as THREE.Material).dispose();
      }
    }

    const devRatio = d.size > 0 ? clamp01((d.population + d.industry) / (d.size * 1.65)) : 0;
    const shellGeo = new THREE.SphereGeometry(1.015, 28, 20);
    const shellMat = new THREE.MeshBasicMaterial({
      color: 0x7dd3fc,
      transparent: true,
      opacity: 0.05 + devRatio * 0.16,
      wireframe: true,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    });
    const shell = new THREE.Mesh(shellGeo, shellMat);
    this.planetGroup.add(shell);

    const nodeCount = 6 + Math.round(devRatio * 18);
    const nodes: THREE.Mesh[] = [];
    for (let i = 0; i < nodeCount; i++) {
      const nodeGeo = new THREE.SphereGeometry(0.018 + devRatio * 0.015, 8, 8);
      const nodeMat = new THREE.MeshBasicMaterial({
        color: 0xf8fafc,
        transparent: true,
        opacity: 0.25 + devRatio * 0.45,
      });
      const node = new THREE.Mesh(nodeGeo, nodeMat);
      const phi = Math.acos(1 - 2 * ((i + 0.5) / nodeCount));
      const theta = Math.PI * (1 + Math.sqrt(5)) * i;
      const radius = 1.02 + ((i % 3) * 0.012);
      node.position.set(
        radius * Math.sin(phi) * Math.cos(theta),
        radius * Math.cos(phi),
        radius * Math.sin(phi) * Math.sin(theta),
      );
      this.planetGroup.add(node);
      nodes.push(node);
    }

    this.developmentVisuals = { shell, nodes };
  }

  private animateDevelopmentState(): void {
    if (!this.developmentVisuals) return;

    this.developmentVisuals.shell.rotation.y += 0.0022;
    this.developmentVisuals.shell.rotation.x = 0.35 + Math.sin(this.animationClock * 0.5) * 0.05;
    for (let i = 0; i < this.developmentVisuals.nodes.length; i++) {
      const node = this.developmentVisuals.nodes[i]!;
      const material = node.material as THREE.MeshBasicMaterial;
      material.opacity = 0.18 + ((Math.sin(this.animationClock * 2.2 + i) + 1) * 0.2);
      node.scale.setScalar(0.9 + ((Math.sin(this.animationClock * 1.7 + i * 0.4) + 1) * 0.12));
    }
  }

  private updateTechOrbits(tech: TechLevels | null): void {
    for (const orbit of this.techOrbits) {
      const level = tech?.[orbit.key] ?? 0;
      const energy = level / 10;
      const ringMat = orbit.ring.material as THREE.MeshBasicMaterial;
      const nodeMat = orbit.node.material as THREE.MeshBasicMaterial;
      ringMat.opacity = 0.06 + energy * 0.42;
      nodeMat.opacity = 0.15 + energy * 0.85;
      orbit.ring.scale.setScalar(0.96 + energy * 0.1);
      orbit.node.scale.setScalar(0.7 + energy * 0.9);
    }
  }

  private animateTechOrbits(): void {
    for (const orbit of this.techOrbits) {
      const level = this.currentTech?.[orbit.key] ?? 0;
      const energy = level / 10;
      orbit.angle += orbit.speed * (0.4 + energy * 0.9) * 0.016;
      orbit.ring.rotation.z += 0.002 + energy * 0.004;
      orbit.ring.rotation.x = orbit.tiltX + Math.sin(this.animationClock * 0.8 + orbit.radius) * 0.05;
      orbit.ring.rotation.y = orbit.tiltY + Math.cos(this.animationClock * 0.7 + orbit.radius) * 0.05;

      const localPos = new THREE.Vector3(
        Math.cos(orbit.angle) * orbit.radius,
        Math.sin(orbit.angle * 1.4) * 0.18,
        Math.sin(orbit.angle) * orbit.radius,
      );
      localPos.applyEuler(orbit.ring.rotation);
      orbit.node.position.copy(localPos);
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}

function parseColor(hexColor: string): number {
  return parseInt(hexColor.replace('#', ''), 16);
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value));
}

function describeDevelopment(devRatio: number): { label: string; hint: string } {
  return DEVELOPMENT_STAGES.find(stage => devRatio <= stage.limit) ?? DEVELOPMENT_STAGES[DEVELOPMENT_STAGES.length - 1]!;
}

function techLevelLabel(level: number): string {
  if (level <= 2) return 'база';
  if (level <= 5) return 'стабильный контур';
  if (level <= 8) return 'прорыв';
  return 'доминирование';
}
