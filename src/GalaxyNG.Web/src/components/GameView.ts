import { api } from '../api/client.js';
import type { Session, SpectateData, SpectatePlayer, SpectatePlanet } from '../types/api.js';
import { GalaxyMapThree, type ThreePlanet, type ThreeFleetRoute, type ThreeCombatEvents } from './GalaxyMapThree.js';
import { OrdersEditor } from './OrdersEditor.js';

const PLAYER_COLORS = [
  '#4ade80','#38bdf8','#f87171','#facc15',
  '#a78bfa','#fb923c','#34d399','#e879f9',
];

export class GameView {
  private el: HTMLElement;
  private map!: GalaxyMapThree;
  private editor!: OrdersEditor;
  private reportEl!: HTMLElement;
  private planetListEl!: HTMLElement;
  private statusBar!: HTMLElement;
  private session: Session;
  private gameId: string;
  private pollTimer: number | null = null;
  private currentTurn = -1;
  private playerColorMap = new Map<string, string>();

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
    this.map?.destroy();
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="game-layout">
        <div class="sidebar">
          <div class="sidebar-header">
            <span id="game-id-label" class="game-id"></span>
            <span id="turn-label" class="turn-label"></span>
          </div>
          <div id="galaxy-map-container" class="galaxy-map-container"></div>
          <div id="planet-info" class="planet-info">
            <em>Click a planet</em>
          </div>
        </div>
        <div class="main-panel">
          <div class="panel-tabs">
            <button class="ptab active" data-panel="orders">Orders</button>
            <button class="ptab" data-panel="report">Turn Report</button>
            <button class="ptab" data-panel="planets">Планеты</button>
          </div>
          <div id="panel-orders" class="panel-content">
            <div id="orders-container"></div>
          </div>
          <div id="panel-report" class="panel-content hidden">
            <pre id="report-text" class="report-text"></pre>
          </div>
          <div id="panel-planets" class="panel-content hidden">
            <div id="planet-list" class="planet-list"></div>
          </div>
        </div>
      </div>
      <div class="status-bar" id="status-bar"></div>
    `;

    const mapContainer = this.el.querySelector<HTMLElement>('#galaxy-map-container')!;
    this.map = new GalaxyMapThree(mapContainer);
    this.map.onPlanetClick = name => this.showPlanetInfo(name);

    const ordersContainer = this.el.querySelector<HTMLElement>('#orders-container')!;
    this.editor = new OrdersEditor(ordersContainer);
    this.editor.setContext(this.gameId, this.session);

    this.reportEl     = this.el.querySelector('#report-text')!;
    this.planetListEl = this.el.querySelector('#planet-list')!;
    this.statusBar    = this.el.querySelector('#status-bar')!;

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

    this.statusBar.innerHTML = `
      <span id="status-msg">Loading…</span>
      <button class="btn btn-sm btn-warning" id="btn-run-turn">▶ Run Turn</button>
    `;
    this.el.querySelector('#btn-run-turn')!.addEventListener('click', () => void this.runTurn());
  }

  private async refresh(): Promise<void> {
    try {
      const data = await api.spectate(this.gameId);
      this.el.querySelector('#turn-label')!.textContent = `Turn ${data.turn}`;
      this.setStatus(`Turn ${data.turn} | Players: ${data.players.map(p =>
        `${p.name}${p.submitted ? '✓' : ''}`).join(', ')}`);

      this.updatePlayerColors(data.players);
      this.updateMap(data);

      if (data.turn !== this.currentTurn) {
        this.currentTurn = data.turn;
        await this.loadReport();
      }
    } catch (e) {
      this.setStatus(`Error refreshing: ${e}`);
    }
  }

  private updatePlayerColors(players: SpectatePlayer[]): void {
    players.forEach((p, i) => {
      if (!this.playerColorMap.has(p.id))
        this.playerColorMap.set(p.id, PLAYER_COLORS[i % PLAYER_COLORS.length]!);
    });
  }

  private updateMap(data: SpectateData): void {
    const planets: ThreePlanet[] = data.planets.map(p => ({
      name:       p.name,
      x:          p.x,
      y:          p.y,
      size:       p.size,
      ownerId:    p.ownerId,
      population: p.population,
      color: p.ownerId
        ? (this.playerColorMap.get(p.ownerId) ?? '#888')
        : '#334466',
    }));

    const planetByName = new Map(planets.map(p => [p.name, p] as const));
    const fleetRoutes: ThreeFleetRoute[] = (data.fleetRoutes ?? [])
      .map(route => {
        const from = planetByName.get(route.origin);
        const to   = planetByName.get(route.destination);
        if (!from || !to) return null;
        return {
          ownerId:     route.ownerId,
          ownerName:   data.players.find(p => p.id === route.ownerId)?.name ?? route.ownerId,
          origin:      route.origin,
          destination: route.destination,
          x1: from.x, y1: from.y,
          x2: to.x,   y2: to.y,
          color:    this.playerColorMap.get(route.ownerId) ?? '#94a3b8',
          fleetName: route.fleetName,
          ships:    route.ships,
          active:   route.active ?? true,
          speed:    typeof route.speed === 'number' ? route.speed : undefined,
          progress: typeof route.progress === 'number' ? route.progress : undefined,
        };
      })
      .filter((r): r is ThreeFleetRoute => r !== null);

    const combatEvents: ThreeCombatEvents = {
      turn: data.turn,
      battlePlanets:  data.battles.map(b => b.planetName),
      bombingPlanets: data.bombings.map(b => b.planetName),
    };

    this.map.setData(data.galaxySize, planets, fleetRoutes, combatEvents);
    this.map.setRouteDisplay(true, null);
    this.renderPlanetList(data);
  }

  private renderPlanetList(data: SpectateData): void {
    const byOwner = new Map<string | null, SpectatePlanet[]>();
    for (const p of data.planets) {
      const key = p.ownerId ?? null;
      if (!byOwner.has(key)) byOwner.set(key, []);
      byOwner.get(key)!.push(p);
    }

    const playerById = new Map(data.players.map(p => [p.id, p]));

    const renderGroup = (ownerId: string | null, planets: SpectatePlanet[]): string => {
      const player   = ownerId ? playerById.get(ownerId) : null;
      const color    = ownerId ? (this.playerColorMap.get(ownerId) ?? '#888') : '#4b5563';
      const label    = player ? player.name : 'Нейтральные';
      const totalPop = planets.reduce((s, p) => s + p.population, 0);
      const rows     = planets
        .slice()
        .sort((a, b) => b.population - a.population)
        .map(p => `
          <tr>
            <td>${p.name}</td>
            <td class="pl-num">${Math.round(p.size)}</td>
            <td class="pl-num">${Math.round(p.population)}</td>
          </tr>`)
        .join('');
      return `
        <div class="pl-group">
          <div class="pl-group-header" style="border-left: 3px solid ${color}">
            <span style="color:${color}">${label}</span>
            <span class="pl-stats">${planets.length} планет · ${Math.round(totalPop)} поп</span>
          </div>
          <table class="pl-table">
            <thead><tr><th>Планета</th><th class="pl-num">Размер</th><th class="pl-num">Население</th></tr></thead>
            <tbody>${rows}</tbody>
          </table>
        </div>`;
    };

    // Players sorted by planet count desc, then neutrals
    const playerGroups = data.players
      .filter(p => byOwner.has(p.id))
      .sort((a, b) => (byOwner.get(b.id)?.length ?? 0) - (byOwner.get(a.id)?.length ?? 0))
      .map(p => renderGroup(p.id, byOwner.get(p.id)!))
      .join('');

    const neutralGroup = byOwner.has(null)
      ? renderGroup(null, byOwner.get(null)!)
      : '';

    this.planetListEl.innerHTML = playerGroups + neutralGroup;
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
