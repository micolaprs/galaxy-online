import { api } from '../api/client.js';
import type { Session } from '../types/api.js';
import { GalaxyMap, type MapPlanet } from './GalaxyMap.js';
import { OrdersEditor } from './OrdersEditor.js';

export class GameView {
  private el: HTMLElement;
  private map!: GalaxyMap;
  private editor!: OrdersEditor;
  private reportEl!: HTMLElement;
  private statusBar!: HTMLElement;
  private session: Session;
  private gameId: string;
  private pollTimer: number | null = null;
  private currentTurn = -1;

  constructor(container: HTMLElement, gameId: string, session: Session) {
    this.el      = container;
    this.gameId  = gameId;
    this.session = session;
    this.render();
    void this.refresh();
    this.startPolling();
  }

  destroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="game-layout">
        <div class="sidebar">
          <div class="sidebar-header">
            <span id="game-id-label" class="game-id"></span>
            <span id="turn-label" class="turn-label"></span>
          </div>
          <canvas id="galaxy-canvas" width="320" height="320"></canvas>
          <div id="planet-info" class="planet-info">
            <em>Click a planet</em>
          </div>
        </div>
        <div class="main-panel">
          <div class="panel-tabs">
            <button class="ptab active" data-panel="orders">Orders</button>
            <button class="ptab" data-panel="report">Turn Report</button>
          </div>
          <div id="panel-orders" class="panel-content">
            <div id="orders-container"></div>
          </div>
          <div id="panel-report" class="panel-content hidden">
            <pre id="report-text" class="report-text"></pre>
          </div>
        </div>
      </div>
      <div class="status-bar" id="status-bar"></div>
    `;

    // Map
    const canvas = this.el.querySelector<HTMLCanvasElement>('#galaxy-canvas')!;
    this.map = new GalaxyMap(canvas);
    this.map.onPlanetClick = name => this.showPlanetInfo(name);

    // Orders editor
    const ordersContainer = this.el.querySelector<HTMLElement>('#orders-container')!;
    this.editor = new OrdersEditor(ordersContainer);
    this.editor.setContext(this.gameId, this.session);

    this.reportEl  = this.el.querySelector('#report-text')!;
    this.statusBar = this.el.querySelector('#status-bar')!;

    // Panel tabs
    this.el.querySelectorAll<HTMLButtonElement>('.ptab').forEach(btn => {
      btn.addEventListener('click', () => {
        this.el.querySelectorAll('.ptab').forEach(b => b.classList.remove('active'));
        this.el.querySelectorAll('.panel-content').forEach(c => c.classList.add('hidden'));
        btn.classList.add('active');
        const panel = this.el.querySelector(`#panel-${btn.dataset['panel']}`)!;
        panel.classList.remove('hidden');
        if (btn.dataset['panel'] === 'report') void this.loadReport();
      });
    });

    this.el.querySelector('#game-id-label')!.textContent = `Game: ${this.gameId}`;

    // Run Turn button (host only — in status bar)
    this.statusBar.innerHTML = `
      <span id="status-msg">Loading…</span>
      <button class="btn btn-sm btn-warning" id="btn-run-turn">▶ Run Turn</button>
    `;
    this.el.querySelector('#btn-run-turn')!.addEventListener('click', () => this.runTurn());
  }

  private async refresh(): Promise<void> {
    try {
      const game = await api.getGame(this.gameId);
      this.el.querySelector('#turn-label')!.textContent = `Turn ${game.turn}`;
      this.setStatus(`Turn ${game.turn} | Players: ${game.players.map(p =>
        `${p.name}${p.submitted ? '✓' : ''}`).join(', ')}`);

      if (game.turn !== this.currentTurn) {
        this.currentTurn = game.turn;
        await this.loadMapData(game);
        await this.loadReport();
      }
    } catch (e) {
      this.setStatus(`Error refreshing: ${e}`);
    }
  }

  private async loadMapData(game: { galaxySize: number }): Promise<void> {
    // Parse report to build planet list (simple approach: use /api/games/{id})
    // For MVP we read the text report and extract planet lines
    try {
      const report  = await api.getReport(this.gameId, this.session);
      const planets = parsePlanetsFromReport(report, this.session.raceName);
      this.map.setData(game.galaxySize, planets);
    } catch { /* ignore map error */ }
  }

  private async loadReport(): Promise<void> {
    try {
      const report = await api.getReport(this.gameId, this.session);
      this.reportEl.textContent = report;
    } catch (e) {
      this.reportEl.textContent = `Failed to load report: ${e}`;
    }
  }

  private async runTurn(): Promise<void> {
    const btn = this.el.querySelector<HTMLButtonElement>('#btn-run-turn')!;
    btn.disabled = true;
    this.setStatus('Running turn…');
    try {
      await api.runTurn(this.gameId);
      this.setStatus('Turn complete!');
      await this.refresh();
    } catch (e) {
      this.setStatus(`Error: ${e}`);
    } finally {
      btn.disabled = false;
    }
  }

  private showPlanetInfo(name: string): void {
    this.map.select(name);
    const infoEl = this.el.querySelector('#planet-info')!;
    infoEl.innerHTML = `<strong>${name}</strong><br><em>See Turn Report for details.</em>`;
  }

  private setStatus(msg: string): void {
    const el = this.el.querySelector('#status-msg');
    if (el) el.textContent = msg;
  }

  private startPolling(): void {
    this.pollTimer = window.setInterval(() => void this.refresh(), 15_000);
  }
}

// ---- Parse planet positions from the text report ----
function parsePlanetsFromReport(report: string, myRace: string): MapPlanet[] {
  const planets: MapPlanet[] = [];
  const lines = report.split('\n');
  let inMyPlanets = false;
  let inAlien = false;
  let inUninhabited = false;

  for (const line of lines) {
    if (line.includes('YOUR PLANETS')) { inMyPlanets = true; inAlien = false; inUninhabited = false; continue; }
    if (line.includes('ALIEN PLANETS')) { inAlien = true; inMyPlanets = false; inUninhabited = false; continue; }
    if (line.includes('UNINHABITED PLANETS')) { inUninhabited = true; inMyPlanets = false; inAlien = false; continue; }
    if (line.startsWith('=')) { inMyPlanets = false; inAlien = false; inUninhabited = false; }

    if (inMyPlanets || inAlien || inUninhabited) {
      // Format: Name   X      Y     Size   ...
      const m = line.match(/^(\S+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/);
      if (!m) continue;
      const [, name, xs, ys, sizes] = m;
      if (!name || !xs || !ys || !sizes) continue;
      planets.push({
        name,
        x: parseFloat(xs),
        y: parseFloat(ys),
        size: parseFloat(sizes),
        owner: inMyPlanets ? 'mine' : inUninhabited ? 'neutral' : 'enemy',
        hasShips: false,
      });
    }
  }
  return planets;
}
