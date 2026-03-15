import { api } from '../api/client.js';
import { ensureConnected } from '../api/hub.js';
import type {
  SpectateData, SpectatePlayer, SpectateBattle, SpectateBombing, SpectatePlanet,
  BotStatusEvent, TechLevels, SpectateChatMessage, SpectatePrivateChat, FinalGameReport,
  TurnHistoryEntry, BattleSummary, BattleRecordDetail,
} from '../types/api.js';
import { BattleVisualizer } from './BattleVisualizer.js';
import {
  GalaxyMapThree,
  type ThreeCombatEvents,
  type ThreeFleetRoute,
  type ThreePlanet,
} from './GalaxyMapThree.js';
import { PlanetPanel } from './PlanetPanel.js';
import { PlayerHistoryPanel } from './PlayerHistoryPanel.js';
import { GalaxySummaryPanel } from './GalaxySummaryPanel.js';
import { QuakeConsole } from './QuakeConsole.js';
import { sanitizeUiText } from '../utils/uiText.js';
import type { HubConnection } from '@microsoft/signalr';

interface CombatEntry {
  turn: number;
  at: string;
  battles: BattleSummary[];
  battlesRaw: SpectateBattle[];      // from live spectate (current turn)
  bombings: SpectateBombing[];
  bombingStrings: string[];          // fallback narrative strings from history
}

// Fixed palette for player colors (up to 8 players)
const PLAYER_COLORS = [
  '#4ade80','#38bdf8','#f87171','#facc15',
  '#a78bfa','#fb923c','#34d399','#e879f9',
];


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
  private playerTechMap = new Map<string, TechLevels>();
  private combatLog: CombatEntry[] = [];
  private historyLoaded = false;
  private botStatuses = new Map<string, BotStatusEvent>();
  private hub: HubConnection | null = null;
  private lastData: SpectateData | null = null;
  private activeRightTab: 'players' | 'summary' | 'combat' | 'planets' = 'players';
  private planetSortCol: 'name' | 'size' | 'population' = 'population';
  private planetSortAsc = false;
  private diplomacyCollapsed = false;
  private showAllRoutes = true;
  private routeFocusPlanet: string | null = null;
  private activeFleetRoute: ThreeFleetRoute | null = null;
  private expandedChannels = new Set<string>();
  private lastPrivateChats: import('../types/api.js').SpectatePrivateChat[] = [];
  private finalReport: FinalGameReport | null = null;
  private finalReportAutoOpened = false;
  private activeVisualizer: BattleVisualizer | null = null;

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
        // Preserve reasoning text across subsequent status updates (validating → submitting etc.)
        // Clear it only when a new "reading-report" arrives (new turn)
        const prev = this.botStatuses.get(ev.raceName);
        if (!ev.thinking && prev?.thinking && ev.status !== 'reading-report') {
          ev = { ...ev, thinking: prev.thinking };
        }
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
          <button class="btn btn-sm btn-secondary" id="btn-final-report" style="display:none">Итоги</button>
          <button class="btn btn-sm btn-warning" id="btn-run-turn">▶ Ход</button>
        </div>
        <div class="wv-body">
          <div class="wv-map-col" id="wv-map-col">
            <!-- Three.js canvas injected here -->
            <!-- Planet panel overlay -->
            <button class="wv-route-toggle" id="wv-route-toggle">Маршруты: все</button>
            <div class="wv-fleet-panel hidden" id="wv-fleet-panel"></div>
            <div class="wv-dip-chat" id="wv-dip-chat">
              <button class="wv-dip-toggle" id="wv-dip-toggle">Дипломатия ▾</button>
              <div class="wv-dip-body" id="wv-dip-body">
                <section class="wv-dip-section">
                  <div class="wv-dip-heading">Общий канал</div>
                  <div class="wv-dip-messages" id="wv-dip-global"></div>
                </section>
                <section class="wv-dip-section">
                  <div class="wv-dip-heading">Личные каналы</div>
                  <div class="wv-dip-private" id="wv-dip-private"></div>
                </section>
              </div>
            </div>
          </div>
          <div class="wv-right-col">
            <div class="wv-right-tabs">
              <button class="wv-rtab active" data-tab="players">Игроки</button>
              <button class="wv-rtab" data-tab="planets">Планеты</button>
              <button class="wv-rtab" data-tab="combat">Сражения</button>
              <button class="wv-rtab" data-tab="summary">Галактика</button>
            </div>
            <div class="wv-right-body">
              <div class="wv-tab-content active" id="tab-players">
                <!-- Player list -->
              </div>
              <div class="wv-tab-content" id="tab-planets">
                <div class="planet-list" id="wv-planet-list"></div>
              </div>
              <div class="wv-tab-content" id="tab-combat">
                <!-- Combat intel -->
              </div>
              <div class="wv-tab-content" id="tab-summary">
                <!-- Galaxy summary -->
              </div>
            </div>
          </div>
        </div>
      </div>
      <div class="wv-battle-overlay hidden" id="wv-battle-overlay">
        <div class="wv-battle-modal">
          <div class="wv-battle-modal-head">
            <span class="wv-battle-modal-title" id="wv-battle-title">Сражение</span>
            <button class="btn-close" id="wv-battle-close">×</button>
          </div>
          <div class="wv-battle-modal-body" id="wv-battle-body"></div>
        </div>
      </div>
      <div class="wv-final-overlay hidden" id="wv-final-overlay">
        <div class="wv-final-modal">
          <div class="wv-final-head">
            <div class="wv-final-title" id="wv-final-title">Итоги партии</div>
            <button class="btn-close" id="wv-final-close">×</button>
          </div>
          <div class="wv-final-body" id="wv-final-body"></div>
        </div>
      </div>
    `;

    // Tab switching
    this.el.querySelectorAll<HTMLElement>('.wv-rtab').forEach(btn => {
      btn.addEventListener('click', () => {
        this.el.querySelectorAll('.wv-rtab').forEach(b => b.classList.remove('active'));
        this.el.querySelectorAll('.wv-tab-content').forEach(c => c.classList.remove('active'));
        btn.classList.add('active');
        const tab = btn.dataset['tab'] as 'players' | 'summary' | 'combat' | 'planets';
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
    this.map.onMapClick = () => {
      this.map.select(null);
      QuakeConsole.closeActive();
      this.planetPanel.hide();
      this.hideFleetPanel();
      this.routeFocusPlanet = null;
      this.applyRouteDisplayMode();
    };
    this.map.onPlanetClick = (name) => {
      this.routeFocusPlanet = name;
      this.applyRouteDisplayMode();
      this.hideFleetPanel();
      this.planetPanel.show(name);
    };
    this.map.onFleetClick = (route) => {
      this.activeFleetRoute = route;
      this.showFleetPanel(route);
    };

    this.el.querySelector('#btn-back')!.addEventListener('click', () => this.onBack?.());
    this.el.querySelector('#btn-run-turn')!.addEventListener('click', () => void this.runTurn());
    this.el.querySelector('#btn-final-report')!.addEventListener('click', () => this.openFinalReport());
    this.el.querySelector('#wv-final-close')!.addEventListener('click', () => this.closeFinalReport());
    this.el.querySelector('#wv-final-overlay')!.addEventListener('click', (ev) => {
      if (ev.target === ev.currentTarget) this.closeFinalReport();
    });
    this.el.querySelector('#wv-battle-close')!.addEventListener('click', () => this.closeBattleVisualizer());
    this.el.querySelector('#wv-battle-overlay')!.addEventListener('click', (ev) => {
      if (ev.target === ev.currentTarget) this.closeBattleVisualizer();
    });
    this.el.querySelector('#wv-dip-toggle')!.addEventListener('click', () => this.toggleDiplomacyChat());
    this.el.querySelector('#wv-route-toggle')!.addEventListener('click', () => {
      this.showAllRoutes = !this.showAllRoutes;
      this.updateRouteToggleLabel();
      this.applyRouteDisplayMode();
    });

    // Resize observer for Three.js canvas
    const ro = new ResizeObserver(() => this.map.resize());
    ro.observe(mapCol);
    this.updateRouteToggleLabel();
  }

  // ---- Data refresh ----

  private async refresh(): Promise<void> {
    try {
      const data = await api.spectate(this.gameId);
      this.lastData = data;
      this.updateTitle(data);
      this.updatePlayerColors(data.players);
      this.renderPlayers(data);
      this.renderPlanetList(data);
      this.updateMap(data);
      this.renderDiplomacy(data);

      const turnChanged = data.turn !== this.currentTurn;
      if (turnChanged) {
        this.recordLiveTurnEvent(data);
        this.map.triggerTurnCombatBursts({
          turn: data.turn,
          battlePlanets: data.battles.map(b => b.planetName),
          bombingPlanets: data.bombings.map(b => b.planetName),
        });
        this.currentTurn = data.turn;
      }
      if (!this.historyLoaded) {
        void this.loadCombatHistory();
      }
      this.renderCombatIntel();

      // Keep galaxy summary panel in sync on every refresh,
      // so delayed summary generation appears without waiting for next turn.
      const summaryTab = this.el.querySelector<HTMLElement>('#tab-summary')!;
      if (!this.galaxySummaryPanel) {
        this.galaxySummaryPanel = new GalaxySummaryPanel(summaryTab, this.gameId, data.turn);
      } else {
        this.galaxySummaryPanel.updateTurn(data.turn);
      }

      if (data.isFinished) {
        await this.ensureFinalReportLoaded(!this.finalReportAutoOpened);
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
    this.el.querySelector<HTMLButtonElement>('#btn-final-report')!.style.display =
      data.isFinished ? '' : 'none';
  }

  private updatePlayerColors(players: SpectatePlayer[]): void {
    players.forEach((p, i) => {
      if (!this.playerColorMap.has(p.id))
        this.playerColorMap.set(p.id, PLAYER_COLORS[i % PLAYER_COLORS.length]!);
      this.playerTechMap.set(p.id, p.tech);
    });
    this.planetPanel.setPlayerTechMap(this.playerTechMap);
  }

  private renderPlayers(data: SpectateData | null): void {
    if (!data || this.activeRightTab !== 'players') return;
    this.playerHistoryPanel.updatePlayers(data.players);
  }

  private renderPlanetList(data: SpectateData): void {
    const el = this.el.querySelector<HTMLElement>('#wv-planet-list');
    if (!el) return;

    const col = this.planetSortCol;
    const asc = this.planetSortAsc;

    const sortPlanets = (planets: SpectatePlanet[]) => planets.slice().sort((a, b) => {
      let cmp = 0;
      if (col === 'name')       cmp = a.name.localeCompare(b.name);
      else if (col === 'size')  cmp = a.size - b.size;
      else                      cmp = a.population - b.population;
      return asc ? cmp : -cmp;
    });

    const arrow = (c: typeof col) => c === col ? (asc ? ' ▲' : ' ▼') : '';

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
      const rows     = sortPlanets(planets).map(p => `
        <tr class="pl-row" data-planet="${esc(p.name)}">
          <td>${esc(p.name)}</td>
          <td class="pl-num">${Math.round(p.size)}</td>
          <td class="pl-num">${Math.round(p.population)}</td>
        </tr>`).join('');
      return `
        <div class="pl-group">
          <div class="pl-group-header" style="border-left:3px solid ${color}">
            <span style="color:${color}">${esc(label)}</span>
            <span class="pl-stats">${planets.length} · ${Math.round(totalPop)} поп</span>
          </div>
          <table class="pl-table">
            <thead><tr>
              <th class="pl-sort-th" data-col="name">Планета${arrow('name')}</th>
              <th class="pl-sort-th pl-num" data-col="size">Размер${arrow('size')}</th>
              <th class="pl-sort-th pl-num" data-col="population">Население${arrow('population')}</th>
            </tr></thead>
            <tbody>${rows}</tbody>
          </table>
        </div>`;
    };

    const playerGroups = data.players
      .filter(p => byOwner.has(p.id))
      .sort((a, b) => (byOwner.get(b.id)?.length ?? 0) - (byOwner.get(a.id)?.length ?? 0))
      .map(p => renderGroup(p.id, byOwner.get(p.id)!))
      .join('');

    const neutralGroup = byOwner.has(null) ? renderGroup(null, byOwner.get(null)!) : '';
    el.innerHTML = playerGroups + neutralGroup;

    el.querySelectorAll<HTMLElement>('.pl-sort-th').forEach(th => {
      th.addEventListener('click', () => {
        const c = th.dataset['col'] as typeof col;
        if (this.planetSortCol === c) this.planetSortAsc = !this.planetSortAsc;
        else { this.planetSortCol = c; this.planetSortAsc = c === 'name'; }
        if (this.lastData) this.renderPlanetList(this.lastData);
      });
    });

    el.querySelectorAll<HTMLElement>('.pl-row').forEach(row => {
      row.addEventListener('click', () => {
        const planet = row.dataset['planet'];
        if (planet) this.navigateToPlanet(planet);
      });
    });
  }

  private updateMap(data: SpectateData): void {
    const planets: ThreePlanet[] = data.planets.map(p => ({
      name:       p.name,
      x:          p.x,
      y:          p.y,
      size:       p.size,
      ownerId:    p.ownerId,
      hasShips:   (p as any).hasShips ?? false,
      population: p.population,
      color: p.ownerId
        ? (this.playerColorMap.get(p.ownerId) ?? '#888')
        : '#334466',
    }));

    const planetByName = new Map(planets.map(p => [p.name, p] as const));
    const fleetRoutes: ThreeFleetRoute[] = (data.fleetRoutes ?? [])
      .map(route => {
        const from = planetByName.get(route.origin);
        const to = planetByName.get(route.destination);
        if (!from || !to) return null;
        return {
          ownerId: route.ownerId,
          ownerName: data.players.find(p => p.id === route.ownerId)?.name ?? route.ownerId,
          origin: route.origin,
          destination: route.destination,
          x1: from.x,
          y1: from.y,
          x2: to.x,
          y2: to.y,
          color: this.playerColorMap.get(route.ownerId) ?? '#94a3b8',
          fleetName: route.fleetName,
          ships: route.ships,
          active: route.active ?? true,
          speed: typeof route.speed === 'number' ? route.speed : undefined,
          progress: typeof route.progress === 'number' ? route.progress : undefined,
        };
      })
      .filter((route): route is ThreeFleetRoute => route !== null);

    const combatEvents: ThreeCombatEvents = {
      turn: data.turn,
      battlePlanets: data.battles.map(b => b.planetName),
      bombingPlanets: data.bombings.map(b => b.planetName),
    };
    this.map.setData(data.galaxySize, planets, fleetRoutes, combatEvents);
    this.applyRouteDisplayMode();
  }

  private recordLiveTurnEvent(data: SpectateData): void {
    if (data.turn === 0) return;
    // Merge live spectate data into combatLog (current turn always uses live data)
    const existing = this.combatLog.findIndex(e => e.turn === data.turn);
    const entry: CombatEntry = {
      turn:           data.turn,
      at:             data.lastTurnRunAt ?? new Date().toISOString(),
      battles:        data.battles.map(b => ({
        planetName:  b.planetName,
        winner:      b.winner,
        participants: b.participants,
        initialShips: b.initialShips ?? {},
        shotCount:   b.protocol?.length ?? 0,
      })),
      battlesRaw:    [...data.battles],
      bombings:      [...data.bombings],
      bombingStrings: [],
    };
    if (existing >= 0) this.combatLog[existing] = entry;
    else this.combatLog.unshift(entry);
    this.combatLog.sort((a, b) => b.turn - a.turn);
  }

  private async loadCombatHistory(): Promise<void> {
    this.historyLoaded = true;
    try {
      const history = await api.getHistory(this.gameId);
      for (const h of history) {
        if (this.combatLog.some(e => e.turn === h.turn)) continue; // live data takes priority
        if (h.battleCount === 0 && h.bombingCount === 0) continue;
        this.combatLog.push(fromHistoryEntry(h));
      }
      this.combatLog.sort((a, b) => b.turn - a.turn);
      this.renderCombatIntel();
    } catch {
      // silently ignore — live session data is still shown
    }
  }

  private renderCombatIntel(): void {
    const tab = this.el.querySelector<HTMLElement>('#tab-combat');
    if (!tab) return;

    const entries = this.combatLog.filter(e => e.battles.length > 0 || e.bombings.length > 0).slice(0, 30);

    if (entries.length === 0) {
      tab.innerHTML = '<div class="wv-combat-empty">Сражений и бомбардировок пока не зафиксировано.</div>';
      return;
    }

    tab.innerHTML = entries.map(entry => {
      const battleCards = entry.battles.map(b => {
        const badges = b.participants.map(p => {
          const color = this.colorForRace(p);
          return `<span class="wv-combat-badge" style="border-color:${esc(color)}">${esc(p)}</span>`;
        }).join('');
        const initCounts = Object.entries(b.initialShips)
          .map(([r, n]) => `${esc(r)}:${n}`)
          .join(' vs ');
        const replayBtn = b.shotCount > 0
          ? `<button class="bv-replay-btn" data-turn="${entry.turn}" data-planet="${esc(b.planetName)}">▶ Реплей (${b.shotCount} выстр.)</button>`
          : '';
        return `
          <div class="wv-combat-event battle">
            <div class="wv-combat-kind">⚔ Орбитальный бой</div>
            <div class="wv-combat-main wv-combat-clickable" data-planet="${esc(b.planetName)}">
              ${esc(b.planetName)} <span class="wv-combat-nav-hint">→ на карте</span>
            </div>
            <div class="wv-combat-badges">${badges}</div>
            ${initCounts ? `<div class="wv-combat-sub">${initCounts}</div>` : ''}
            <div class="wv-combat-sub winner">🏆 ${esc(b.winner === 'Draw' ? 'Ничья' : b.winner)}</div>
            ${replayBtn}
          </div>
        `;
      }).join('');

      const bombingCards = entry.bombings.map(b => {
        const attackerColor = this.colorForRace(b.attackerRace);
        return `
          <div class="wv-combat-event bombing">
            <div class="wv-combat-kind">💥 Бомбардировка</div>
            <div class="wv-combat-main wv-combat-clickable" data-planet="${esc(b.planetName)}">
              ${esc(b.planetName)} <span class="wv-combat-nav-hint">→ на карте</span>
            </div>
            <div class="wv-combat-sub">Атакующий: ${esc(b.attackerRace)}</div>
            <div class="wv-combat-sub">${esc(b.previousOwner ? `Владелец до удара: ${b.previousOwner}` : 'Ранее нейтральная цель')}</div>
            <button class="bv-replay-btn bv-bombing-btn"
              data-planet="${esc(b.planetName)}"
              data-attacker="${esc(b.attackerRace)}"
              data-color="${esc(attackerColor)}"
              data-prev-owner="${esc(b.previousOwner ?? '')}"
              data-pop="${b.oldPopulation ?? ''}"
              >💥 Анимация бомбардировки</button>
          </div>
        `;
      }).join('');

      // Fallback string-based bombings (historical without structured data)
      const bombingFallback = entry.bombingStrings.length > 0 && entry.bombings.length === 0
        ? entry.bombingStrings.map(s => `<div class="wv-combat-event bombing"><div class="wv-combat-sub">${esc(s)}</div></div>`).join('')
        : '';

      return `
        <section class="wv-combat-turn">
          <div class="wv-combat-turn-head">
            <span class="wv-combat-turn-no">Ход ${entry.turn}</span>
            <span class="wv-combat-turn-time">${esc(new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }))}</span>
          </div>
          ${entry.battles.length > 0 ? `<div class="wv-combat-section-label">⚔ Сражения</div>` : ''}
          <div class="wv-combat-events">${battleCards}</div>
          ${(entry.bombings.length > 0 || bombingFallback) ? `<div class="wv-combat-section-label">💥 Бомбардировки</div>` : ''}
          <div class="wv-combat-events">${bombingCards}${bombingFallback}</div>
        </section>
      `;
    }).join('');

    // Planet nav click handlers
    tab.querySelectorAll<HTMLElement>('.wv-combat-clickable').forEach(el => {
      el.addEventListener('click', () => {
        const planet = el.dataset['planet'];
        if (planet) this.navigateToPlanet(planet);
      });
    });

    // Replay button handlers
    tab.querySelectorAll<HTMLElement>('.bv-replay-btn:not(.bv-bombing-btn)').forEach(btn => {
      btn.addEventListener('click', () => {
        const turn   = parseInt(btn.dataset['turn']  ?? '0');
        const planet = btn.dataset['planet'] ?? '';
        void this.openBattleVisualizer(turn, planet);
      });
    });

    // Bombing animation button handlers
    tab.querySelectorAll<HTMLElement>('.bv-bombing-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        this.closeBattleVisualizer();
        this.map.triggerBombingOnMap({
          planetName:    btn.dataset['planet'] ?? '',
          attackerRace:  btn.dataset['attacker'] ?? '',
          previousOwner: btn.dataset['prevOwner'] || null,
          attackerColor: btn.dataset['color'] ?? '#94a3b8',
          oldPopulation: btn.dataset['pop'] ? parseFloat(btn.dataset['pop']!) : undefined,
        });
      });
    });
  }

  private colorForRace(raceName: string): string {
    const entry = this.lastData?.players.find(p => p.name === raceName);
    return entry ? (this.playerColorMap.get(entry.id) ?? '#94a3b8') : '#94a3b8';
  }

  private async openBattleVisualizer(turn: number, planetName: string): Promise<void> {
    // Try live spectate data first (current turn)
    let record: BattleRecordDetail | null = null;
    const liveEntry = this.combatLog.find(e => e.turn === turn);
    if (liveEntry) {
      const raw = liveEntry.battlesRaw.find(b => b.planetName === planetName);
      if (raw?.protocol && raw.protocol.length > 0) {
        record = {
          planetName:   raw.planetName,
          winner:       raw.winner,
          participants: raw.participants,
          protocol:     raw.protocol,
          initialShips: raw.initialShips ?? {},
          shipDesigns:  raw.shipDesigns,
        };
      }
    }

    // Otherwise fetch from history endpoint
    if (!record) {
      try {
        const records = await api.getTurnBattleRecords(this.gameId, turn);
        record = records.find(r => r.planetName === planetName) ?? null;
      } catch {
        // silently fail
      }
    }

    if (!record || record.protocol.length === 0) {
      // No protocol available (old game data)
      return;
    }

    this.map?.stopBombingOnMap(false);
    this.closeBattleVisualizer();
    this.activeVisualizer = new BattleVisualizer(record, this.playerColorMap);

    const overlay = this.el.querySelector<HTMLElement>('#wv-battle-overlay')!;
    const body    = this.el.querySelector<HTMLElement>('#wv-battle-body')!;
    const title   = this.el.querySelector<HTMLElement>('#wv-battle-title')!;

    title.textContent = `Сражение · Ход ${turn}`;
    body.innerHTML = '';
    body.appendChild(this.activeVisualizer.element);
    overlay.classList.remove('hidden');
  }

  private closeBattleVisualizer(): void {
    this.map?.stopBombingOnMap(true);
    this.activeVisualizer?.destroy();
    this.activeVisualizer = null;
    const overlay = this.el.querySelector<HTMLElement>('#wv-battle-overlay');
    overlay?.classList.add('hidden');
    const body = this.el.querySelector<HTMLElement>('#wv-battle-body');
    if (body) body.innerHTML = '';
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

  private toggleDiplomacyChat(): void {
    this.diplomacyCollapsed = !this.diplomacyCollapsed;
    const chat = this.el.querySelector<HTMLElement>('#wv-dip-chat');
    const btn = this.el.querySelector<HTMLElement>('#wv-dip-toggle');
    if (!chat || !btn) return;

    chat.classList.toggle('collapsed', this.diplomacyCollapsed);
    btn.textContent = this.diplomacyCollapsed ? 'Дипломатия ▸' : 'Дипломатия ▾';
  }

  private navigateToPlanet(name: string): void {
    this.map.panToAndSelect(name);
    this.routeFocusPlanet = name;
    this.applyRouteDisplayMode();
    this.planetPanel.show(name);
  }

  private renderDiplomacy(data: SpectateData): void {
    const globalEl = this.el.querySelector<HTMLElement>('#wv-dip-global');
    const privateEl = this.el.querySelector<HTMLElement>('#wv-dip-private');
    if (!globalEl || !privateEl) return;

    const globalMessages = data.diplomacy?.globalMessages ?? [];
    if (globalMessages.length === 0) {
      globalEl.innerHTML = '<div class="wv-dip-empty">Пока нет дипломатических сообщений.</div>';
    } else {
      globalEl.innerHTML = globalMessages
        .slice(-20)
        .map(msg => this.renderMessageLine(msg))
        .join('');
      globalEl.scrollTop = globalEl.scrollHeight;
    }

    const privateChats = data.diplomacy?.privateChats ?? [];
    this.lastPrivateChats = privateChats;
    if (privateChats.length === 0) {
      privateEl.innerHTML = '<div class="wv-dip-empty">Личные каналы откроются при пересечении зон видимости рас.</div>';
      return;
    }

    this.renderPrivateChats(privateEl);
  }

  private renderPrivateChats(privateEl: HTMLElement): void {
    const sorted = [...this.lastPrivateChats].sort(
      (a, b) => b.messages.length - a.messages.length,
    );

    privateEl.innerHTML = sorted.map(chat => this.renderPrivateChat(chat)).join('');

    privateEl.querySelectorAll<HTMLElement>('.wv-dip-ch-toggle').forEach(header => {
      header.addEventListener('click', () => {
        const id = header.dataset['channelId']!;
        if (this.expandedChannels.has(id)) {
          this.expandedChannels.delete(id);
        } else {
          this.expandedChannels.add(id);
        }
        this.renderPrivateChats(privateEl);
      });
    });
  }

  private renderPrivateChat(chat: SpectatePrivateChat): string {
    const isExpanded = this.expandedChannels.has(chat.channelId);
    const msgCount = chat.messages.length;

    const overlapLabel = chat.overlapPlanets.length > 0
      ? `Зона контакта: ${chat.overlapPlanets.join(', ')}`
      : 'Зона контакта обнаружена';

    const messages = msgCount > 0
      ? chat.messages.slice(-10).map(msg => this.renderMessageLine(msg)).join('')
      : '<div class="wv-dip-empty">Канал открыт. Сообщений пока нет.</div>';

    const badge = msgCount > 0
      ? `<span class="wv-dip-ch-badge">${msgCount}</span>`
      : '';

    return `
      <div class="wv-dip-channel${isExpanded ? ' expanded' : ''}">
        <div class="wv-dip-ch-toggle" data-channel-id="${esc(chat.channelId)}">
          <span class="wv-dip-ch-names">${esc(`${chat.playerAName} ↔ ${chat.playerBName}`)}</span>
          ${badge}
          <span class="wv-dip-ch-arrow">${isExpanded ? '▾' : '▸'}</span>
        </div>
        <div class="wv-dip-ch-body">
          <div class="wv-dip-channel-overlap">${esc(overlapLabel)}</div>
          <div class="wv-dip-messages small">${messages}</div>
        </div>
      </div>
    `;
  }

  private renderMessageLine(msg: SpectateChatMessage): string {
    const sent = new Date(msg.sentAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const raceColor = this.playerColorMap.get(msg.senderId) ?? '#94a3b8';
    return `
      <div class="wv-dip-msg">
        <div class="wv-dip-msg-head">
          <span class="wv-dip-msg-race" style="color:${esc(raceColor)}">${esc(msg.senderName)}</span>
          <span class="wv-dip-msg-time">T${msg.turn} • ${esc(sent)}</span>
        </div>
        <div class="wv-dip-msg-text">${esc(sanitizeUiText(msg.text))}</div>
      </div>
    `;
  }

  private applyRouteDisplayMode(): void {
    this.map.setRouteDisplay(this.showAllRoutes, this.routeFocusPlanet);
  }

  private updateRouteToggleLabel(): void {
    const btn = this.el.querySelector<HTMLElement>('#wv-route-toggle');
    if (!btn) return;
    btn.textContent = this.showAllRoutes ? 'Маршруты: все' : 'Маршруты: выбранные';
  }

  private showFleetPanel(route: ThreeFleetRoute): void {
    const panel = this.el.querySelector<HTMLElement>('#wv-fleet-panel');
    if (!panel) return;
    const ownerColor = route.ownerId ? (this.playerColorMap.get(route.ownerId) ?? '#94a3b8') : '#94a3b8';
    const status = route.active === false ? 'Завершённый маршрут' : 'В гиперпространстве';
    panel.innerHTML = `
      <div class="wv-fleet-head">
        <span class="wv-fleet-dot" style="background:${ownerColor}"></span>
        <span class="wv-fleet-title">${esc(route.fleetName ?? 'Флот')}</span>
      </div>
      <div class="wv-fleet-row"><span>Раса</span><strong>${esc(route.ownerName ?? '—')}</strong></div>
      <div class="wv-fleet-row"><span>Кораблей</span><strong>${route.ships ?? 0}</strong></div>
      <div class="wv-fleet-row"><span>Маршрут</span><strong>${esc(`${route.origin} → ${route.destination}`)}</strong></div>
      <div class="wv-fleet-row"><span>Статус</span><strong>${esc(status)}</strong></div>
      <div class="wv-fleet-hint">Клик в любое место карты закрывает это окно.</div>
    `;
    panel.classList.remove('hidden');
  }

  private hideFleetPanel(): void {
    const panel = this.el.querySelector<HTMLElement>('#wv-fleet-panel');
    if (!panel) return;
    panel.classList.add('hidden');
    panel.innerHTML = '';
    this.activeFleetRoute = null;
  }

  private async ensureFinalReportLoaded(autoOpen: boolean): Promise<void> {
    if (!this.finalReport) {
      this.finalReport = await api.getFinalReport(this.gameId);
      this.renderFinalReport();
    }
    if (autoOpen) {
      this.finalReportAutoOpened = true;
      this.openFinalReport();
    }
  }

  private renderFinalReport(): void {
    const body = this.el.querySelector<HTMLElement>('#wv-final-body');
    const title = this.el.querySelector<HTMLElement>('#wv-final-title');
    if (!body || !title || !this.finalReport) return;
    const r = this.finalReport;
    title.textContent = `Итоги: ${r.gameName} (${r.gameId})`;

    const rows = r.races.map(row => `
      <tr class="${row.isWinner ? 'winner' : ''}">
        <td>${esc(row.race)}</td>
        <td>${row.planets}</td>
        <td>${row.population.toFixed(1)}</td>
        <td>${row.industry.toFixed(1)}</td>
        <td>${row.ships}</td>
        <td>${row.techTotal.toFixed(2)}</td>
        <td>${esc(row.achievements.join(' • '))}</td>
      </tr>
    `).join('');

    const timeline = r.timeline.map(line => `<li>${esc(sanitizeUiText(line))}</li>`).join('');
    body.innerHTML = `
      <div class="wv-final-meta">
        <strong>Победитель:</strong> ${esc(r.winnerName ?? 'не определён')}<br/>
        <strong>Финиш:</strong> ход ${r.finishedTurn} из ${r.maxTurns}<br/>
        <strong>Причина:</strong> ${esc(r.finishReason ?? '—')}
      </div>
      <div class="wv-final-table-wrap">
        <table class="wv-final-table">
          <thead>
            <tr>
              <th>Раса</th>
              <th>Планеты</th>
              <th>Население</th>
              <th>Индустрия</th>
              <th>Флот</th>
              <th>Тех</th>
              <th>Достижения</th>
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
      <div class="wv-final-story">
        <div class="wv-final-story-title">История партии</div>
        <ul>${timeline}</ul>
      </div>
    `;
  }

  private openFinalReport(): void {
    const overlay = this.el.querySelector<HTMLElement>('#wv-final-overlay');
    if (!overlay) return;
    overlay.classList.remove('hidden');
  }

  private closeFinalReport(): void {
    const overlay = this.el.querySelector<HTMLElement>('#wv-final-overlay');
    if (!overlay) return;
    overlay.classList.add('hidden');
  }
}

function fromHistoryEntry(h: TurnHistoryEntry): CombatEntry {
  // Convert server history entry to unified CombatEntry
  const battles: import('../types/api.js').BattleSummary[] =
    (h.battleSummaries ?? []).map(b => ({
      planetName:   b.planetName,
      winner:       b.winner,
      participants: b.participants,
      initialShips: b.initialShips ?? {},
      shotCount:    b.shotCount ?? 0,
    }));

  // Fallback: parse narrative strings into minimal structured data when battleSummaries not available
  // (for old game data that predates this feature)
  if (battles.length === 0 && h.battleCount > 0) {
    for (const s of h.battles) {
      const m = s.match(/Битва при (.+?): (.+?) → (.+?) побеждает/);
      if (m) {
        battles.push({
          planetName:   m[1]!,
          winner:       m[3]!,
          participants: m[2]!.split(' vs '),
          initialShips: {},
          shotCount:    0,
        });
      }
    }
  }

  // Extract bombings from narrative strings (for old data)
  const bombings: import('../types/api.js').SpectateBombing[] = [];
  if (h.bombingCount > 0) {
    for (const s of h.bombings) {
      const m = s.match(/(.+?) бомбардировал (.+?)(?:\s+\(был (.+?)\))?$/);
      if (m) {
        bombings.push({ planetName: m[2]!, attackerRace: m[1]!, previousOwner: m[3] ?? null });
      }
    }
  }

  return {
    turn:           h.turn,
    at:             h.runAt,
    battles,
    battlesRaw:     [],
    bombings,
    bombingStrings: bombings.length === 0 ? h.bombings : [],
  };
}

function timeAgo(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (secs < 60)   return `${secs}с назад`;
  if (secs < 3600) return `${Math.floor(secs / 60)}м назад`;
  return `${Math.floor(secs / 3600)}ч назад`;
}

function esc(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
