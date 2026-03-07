import * as THREE from 'three';
import { api } from '../api/client.js';
import type { PlanetDetail } from '../types/api.js';

export class PlanetPanel {
  private el: HTMLElement;
  private renderer!: THREE.WebGLRenderer;
  private scene!:    THREE.Scene;
  private camera!:   THREE.PerspectiveCamera;
  private planet3D!: THREE.Mesh;
  private glow!:     THREE.Mesh;
  private animFrame: number | null = null;

  private gameId: string;
  private playerColorMap: Map<string, string>;

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

  async show(planetName: string): Promise<void> {
    this.el.classList.remove('hidden');
    this.el.querySelector('#pp-title')!.textContent = `⬡ ${planetName}`;
    this.el.querySelector('#pp-stats')!.innerHTML = '<div class="pp-loading">Загрузка…</div>';

    try {
      const d = await api.getPlanetDetail(this.gameId, planetName);
      this.renderStats(d);
      this.updatePlanetColor(d.ownerId ? (this.playerColorMap.get(d.ownerId) ?? '#888') : '#475569');
    } catch {
      this.el.querySelector('#pp-stats')!.innerHTML =
        '<div class="pp-loading error">Ошибка загрузки</div>';
    }
  }

  hide(): void {
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

    // Planet sphere
    const geo = new THREE.SphereGeometry(1, 32, 24);
    const mat = new THREE.MeshPhongMaterial({ color: 0x4ade80, shininess: 60 });
    this.planet3D = new THREE.Mesh(geo, mat);
    this.scene.add(this.planet3D);

    // Glow (additive outer shell)
    const glowGeo = new THREE.SphereGeometry(1.18, 32, 24);
    const glowMat = new THREE.MeshBasicMaterial({
      color: 0x4ade80, transparent: true, opacity: 0.12,
      side: THREE.FrontSide, blending: THREE.AdditiveBlending, depthWrite: false,
    });
    this.glow = new THREE.Mesh(glowGeo, glowMat);
    this.scene.add(this.glow);

    this.animate();
  }

  private animate(): void {
    this.animFrame = requestAnimationFrame(() => this.animate());
    this.planet3D.rotation.y += 0.008;
    this.glow.rotation.y     += 0.008;
    this.renderer.render(this.scene, this.camera);
  }

  private updatePlanetColor(hexColor: string): void {
    const c = parseInt(hexColor.replace('#', ''), 16);
    (this.planet3D.material as THREE.MeshPhongMaterial).color.setHex(c);
    (this.planet3D.material as THREE.MeshPhongMaterial).emissive.setHex(c);
    (this.planet3D.material as THREE.MeshPhongMaterial).emissiveIntensity = 0.1;
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
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section">
        <div class="pp-row"><span class="pp-label">Строит:</span><span class="pp-value">${prodLabel[d.producing] ?? d.producing}${d.shipTypeName ? ` (${esc(d.shipTypeName)})` : ''}</span></div>
        <div class="pp-row"><span class="pp-label">Капитал:</span><span class="pp-value">${Math.round(d.stockpiles.capital)}</span></div>
        <div class="pp-row"><span class="pp-label">Материалы:</span><span class="pp-value">${Math.round(d.stockpiles.materials)}</span></div>
        <div class="pp-row"><span class="pp-label">Колонисты:</span><span class="pp-value">${Math.round(d.stockpiles.colonists)}</span></div>
      </div>
      <div class="pp-divider"></div>
      <div class="pp-section pp-ships">
        <div class="pp-section-title">Флоты</div>
        ${shipRows}
      </div>
    `;
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}
