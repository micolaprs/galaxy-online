import { api } from '../api/client.js';
import { ensureConnected } from '../api/hub.js';
import type { SpectateData, SpectatePlayer, SpectateBattle, SpectateBombing, BotStatusEvent } from '../types/api.js';
import { GalaxyMapThree, type ThreePlanet } from './GalaxyMapThree.js';
import { PlanetPanel } from './PlanetPanel.js';
import { PlayerHistoryPanel } from './PlayerHistoryPanel.js';
import { GalaxySummaryPanel } from './GalaxySummaryPanel.js';
import type { HubConnection } from '@microsoft/signalr';

interface TurnEvent {
  turn: number;
  at: string;
  battles: SpectateBattle[];
  bombings: SpectateBombing[];
}

// Fixed palette for player colors (up to 8 players)
const PLAYER_COLORS = [
  '#4ade80','#38bdf8','#f87171','#facc15',
  '#a78bfa','#fb923c','#34d399','#e879f9',
];

const STATUS_LABEL: Record<string, string> = {
  idle:             '💤 idle',
  waiting:          '⏳ waiting',
  'reading-report': '📖 reading report',
  thinking:         '🧠 thinking…',
  validating:       '🔍 validating',
  submitting:       '📤 submitting',
  submitted:        '✓ submitted',
  error:            '❌ error',
};

const STATUS_CSS: Record<string, string> = {
  idle:             'idle',
  waiting:          'waiting',
  'reading-report': 'active',
  thinking:         'thinking',
  validating:       'active',
  submitting:       'active',
  submitted:        'submitted',
  error:            'eliminated',
};

export class WatchView {
  private el: HTMLElement;
  private map!: GalaxyMapThree;
  private planetPanel!: PlanetPanel;
  private playerHistoryPanel!: PlayerHistoryPanel;
  private galaxySummaryPanel!: GalaxySummaryPanel;

  private pollTimer: number | null = null;
  private gameId: string;
  private currentTurn = -1;
  private playerColorMap = new Map<string, string>();
  private turnLog: TurnEvent[] = [];
  private botStatuses = new Map<string, BotStatusEvent>();
  private hub: HubConnection | null = null;
  private lastData: SpectateData | null = null;
  private activeRightTab: 'players' | 'summary' = 'players';

  onBack?: () => void;

  constructor(container: HTMLElement, gameId: string) {
    this.el     = container;
    this.gameId = gameId;
    this.render();
    void this.refresh();
    this.pollTimer = window.setInterval(() => void this.refresh(), 5_000);
    void this.connectHub();
  }

  destroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.map?.destroy();
    this.planetPanel?.destroy();
    if (this.hub) void this.hub.invoke('LeaveGameGroup', this.gameId).catch(() => {});
  }

  // ---- Hub ----

  private async connectHub(): Promise<void> {
    try {
      this.hub = await ensureConnected();
      await this.hub.invoke('JoinGameGroup', this.gameId);

      this.hub.on('BotStatusUpdate', (ev: BotStatusEvent) => {
        this.botStatuses.set(ev.raceName, ev);
        if (this.activeRightTab === 'players') this.renderPlayers(this.lastData);
      });

      this.hub.on('TurnComplete', () => { void this.refresh(); });
      this.hub.on('PlayerSubmitted', () => { void this.refresh(); });
    } catch (e) {
      console.warn('WatchView: SignalR failed, using polling only', e);
    }
  }

  // ---- Layout ----

  private render(): void {
    this.el.innerHTML = `
      <div class="wv-layout">
        <div class="wv-topbar">
          <button class="btn btn-sm btn-secondary" id="btn-back">← Игры</button>
          <span class="wv-title" id="wv-title">Загрузка…</span>
          <span class="wv-turn"  id="wv-turn"></span>
          <span class="wv-ago"   id="wv-ago"></span>
          <div class="wv-topbar-spacer"></div>
          <button class="btn btn-sm btn-warning" id="btn-run-turn">▶ Ход</button>
        </div>
        <div class="wv-body">
          <div class="wv-map-col" id="wv-map-col">
            <!-- Three.js canvas injected here -->
            <!-- Planet panel overlay -->
          </div>
          <div class="wv-right-col">
            <div class="wv-right-tabs">
              <button class="wv-rtab active" data-tab="players">Игроки</button>
              <button class="wv-rtab" data-tab="summary">Галактика</button>
            </div>
            <div class="wv-right-body">
              <div class="wv-tab-content active" id="tab-players">
                <!-- Player list -->
              </div>
              <div class="wv-tab-content" id="tab-summary">
                <!-- Galaxy summary -->
              </div>
            </div>
          </div>
        </div>
      </div>
    `;

    // Tab switching
    this.el.querySelectorAll<HTMLElement>('.wv-rtab').forEach(btn => {
      btn.addEventListener('click', () => {
        this.el.querySelectorAll('.wv-rtab').forEach(b => b.classList.remove('active'));
        this.el.querySelectorAll('.wv-tab-content').forEach(c => c.classList.remove('active'));
        btn.classList.add('active');
        const tab = btn.dataset['tab'] as 'players' | 'summary';
        this.activeRightTab = tab;
        this.el.querySelector(`#tab-${tab}`)!.classList.add('active');
      });
    });

    // Three.js map
    const mapCol = this.el.querySelector<HTMLElement>('#wv-map-col')!;
    this.map = new GalaxyMapThree(mapCol);

    // Planet panel (overlay inside map col)
    this.planetPanel = new PlanetPanel(mapCol, this.gameId, this.playerColorMap);

    // Player history panel
    const playersTab = this.el.querySelector<HTMLElement>('#tab-players')!;
    this.playerHistoryPanel = new PlayerHistoryPanel(
      playersTab, this.gameId, this.playerColorMap, this.botStatuses);

    // Galaxy summary panel (lazy init after first data)
    this.map.onPlanetClick = (name) => this.planetPanel.show(name);

    this.el.querySelector('#btn-back')!.addEventListener('click', () => this.onBack?.());
    this.el.querySelector('#btn-run-turn')!.addEventListener('click', () => void this.runTurn());

    // Resize observer for Three.js canvas
    const ro = new ResizeObserver(() => this.map.resize());
    ro.observe(mapCol);
  }

  // ---- Data refresh ----

  private async refresh(): Promise<void> {
    try {
      const data = await api.spectate(this.gameId);
      this.lastData = data;
      this.updateTitle(data);
      this.updatePlayerColors(data.players);
      this.renderPlayers(data);
      this.updateMap(data);

      if (data.turn !== this.currentTurn) {
        this.recordTurnEvent(data);
        this.currentTurn = data.turn;

        // Lazy-init galaxy summary panel on first data
        const summaryTab = this.el.querySelector<HTMLElement>('#tab-summary')!;
        if (!this.galaxySummaryPanel) {
          this.galaxySummaryPanel = new GalaxySummaryPanel(summaryTab, this.gameId, data.turn);
        } else {
          this.galaxySummaryPanel.updateTurn(data.turn);
        }
      }
    } catch (e) {
      this.el.querySelector('#wv-title')!.textContent = `Ошибка: ${e}`;
    }
  }

  private updateTitle(data: SpectateData): void {
    this.el.querySelector('#wv-title')!.textContent = `${data.name} (${data.id})`;
    this.el.querySelector('#wv-turn')!.textContent  = `Ход ${data.turn}`;
    this.el.querySelector('#wv-ago')!.textContent   =
      data.lastTurnRunAt ? `ход ${timeAgo(data.lastTurnRunAt)}` : 'ходов нет';
    this.el.querySelector<HTMLButtonElement>('#btn-run-turn')!.style.display =
      data.autoRunOnAllSubmitted ? 'none' : '';
  }

  private updatePlayerColors(players: SpectatePlayer[]): void {
    players.forEach((p, i) => {
      if (!this.playerColorMap.has(p.id))
        this.playerColorMap.set(p.id, PLAYER_COLORS[i % PLAYER_COLORS.length]!);
    });
  }

  private renderPlayers(data: SpectateData | null): void {
    if (!data || this.activeRightTab !== 'players') return;
    this.playerHistoryPanel.updatePlayers(data.players);
  }

  private updateMap(data: SpectateData): void {
    const planets: ThreePlanet[] = data.planets.map(p => ({
      name:    p.name,
      x:       p.x,
      y:       p.y,
      size:    p.size,
      ownerId: p.ownerId,
      hasShips: (p as any).hasShips ?? false,
      color:   p.ownerId
        ? (this.playerColorMap.get(p.ownerId) ?? '#888')
        : '#334466',
    }));
    this.map.setData(data.galaxySize, planets);
  }

  private recordTurnEvent(data: SpectateData): void {
    if (data.turn === 0) return;
    this.turnLog.unshift({
      turn:     data.turn,
      at:       data.lastTurnRunAt ?? new Date().toISOString(),
      battles:  [...data.battles],
      bombings: [...data.bombings],
    });
    if (this.turnLog.length > 20) this.turnLog.pop();
  }

  private async runTurn(): Promise<void> {
    const btn = this.el.querySelector<HTMLButtonElement>('#btn-run-turn')!;
    btn.disabled = true;
    try {
      await api.runTurn(this.gameId);
      await this.refresh();
    } finally {
      btn.disabled = false;
    }
  }
}

function timeAgo(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (secs < 60)   return `${secs}с назад`;
  if (secs < 3600) return `${Math.floor(secs / 60)}м назад`;
  return `${Math.floor(secs / 3600)}ч назад`;
}
