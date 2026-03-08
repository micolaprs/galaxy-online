import { api } from '../api/client.js';
import { ensureConnected } from '../api/hub.js';
import type {
  SpectateData, SpectatePlayer, SpectateBattle, SpectateBombing,
  BotStatusEvent, TechLevels, SpectateChatMessage, SpectatePrivateChat, FinalGameReport,
} from '../types/api.js';
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
  private turnLog: TurnEvent[] = [];
  private botStatuses = new Map<string, BotStatusEvent>();
  private hub: HubConnection | null = null;
  private lastData: SpectateData | null = null;
  private activeRightTab: 'players' | 'summary' | 'combat' = 'players';
  private diplomacyCollapsed = false;
  private showAllRoutes = true;
  private routeFocusPlanet: string | null = null;
  private activeFleetRoute: ThreeFleetRoute | null = null;
  private finalReport: FinalGameReport | null = null;
  private finalReportAutoOpened = false;

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
              <button class="wv-rtab" data-tab="combat">Сражения</button>
              <button class="wv-rtab" data-tab="summary">Галактика</button>
            </div>
            <div class="wv-right-body">
              <div class="wv-tab-content active" id="tab-players">
                <!-- Player list -->
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
        const tab = btn.dataset['tab'] as 'players' | 'summary' | 'combat';
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
      this.updateMap(data);
      this.renderDiplomacy(data);

      const turnChanged = data.turn !== this.currentTurn;
      if (turnChanged) {
        this.recordTurnEvent(data);
        this.map.triggerTurnCombatBursts({
          turn: data.turn,
          battlePlanets: data.battles.map(b => b.planetName),
          bombingPlanets: data.bombings.map(b => b.planetName),
        });
        this.currentTurn = data.turn;
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

  private renderCombatIntel(): void {
    const tab = this.el.querySelector<HTMLElement>('#tab-combat');
    if (!tab) return;

    if (this.turnLog.length === 0) {
      tab.innerHTML = '<div class="wv-combat-empty">Пока нет завершённых ходов с боевыми событиями.</div>';
      return;
    }

    const entries = this.turnLog
      .filter(entry => entry.battles.length > 0 || entry.bombings.length > 0)
      .slice(0, 20);

    if (entries.length === 0) {
      tab.innerHTML = '<div class="wv-combat-empty">Сражений и бомбардировок пока не зафиксировано.</div>';
      return;
    }

    tab.innerHTML = entries.map(entry => {
      const battles = entry.battles.map(b => {
        const participants = b.participants.length > 0 ? b.participants.join(' vs ') : 'неизвестные участники';
        const participantBadges = b.participants.map(p => {
          const color = [...this.playerColorMap.entries()].find(([, v]) => v && this.lastData?.players.find(pl => pl.name === p && this.playerColorMap.get(pl.id) === v))?.[1] ?? '#94a3b8';
          return `<span class="wv-combat-badge" style="border-color:${esc(color)}">${esc(p)}</span>`;
        }).join('');
        return `
          <div class="wv-combat-event battle wv-combat-clickable" data-planet="${esc(b.planetName)}">
            <div class="wv-combat-kind">⚔ Орбитальный бой</div>
            <div class="wv-combat-main">${esc(b.planetName)} <span class="wv-combat-nav-hint">→ на карте</span></div>
            <div class="wv-combat-badges">${participantBadges || esc(participants)}</div>
            <div class="wv-combat-sub winner">🏆 Победитель: ${esc(b.winner)}</div>
          </div>
        `;
      }).join('');

      const bombings = entry.bombings.map(b => `
        <div class="wv-combat-event bombing wv-combat-clickable" data-planet="${esc(b.planetName)}">
          <div class="wv-combat-kind">💥 Бомбардировка</div>
          <div class="wv-combat-main">${esc(b.planetName)} <span class="wv-combat-nav-hint">→ на карте</span></div>
          <div class="wv-combat-sub">Атакующий: ${esc(b.attackerRace)}</div>
          <div class="wv-combat-sub">${esc(b.previousOwner ? `Владелец до удара: ${b.previousOwner}` : 'Ранее нейтральная цель')}</div>
        </div>
      `).join('');

      return `
        <section class="wv-combat-turn">
          <div class="wv-combat-turn-head">
            <span class="wv-combat-turn-no">Ход ${entry.turn}</span>
            <span class="wv-combat-turn-time">${esc(new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }))}</span>
          </div>
          <div class="wv-combat-events">${battles}${bombings}</div>
        </section>
      `;
    }).join('');

    // Add click handlers
    tab.querySelectorAll<HTMLElement>('.wv-combat-clickable').forEach(el => {
      el.addEventListener('click', () => {
        const planet = el.dataset['planet'];
        if (planet) {
          this.navigateToPlanet(planet);
          // Switch to map view by switching right tab away if needed
        }
      });
    });
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
    if (privateChats.length === 0) {
      privateEl.innerHTML = '<div class="wv-dip-empty">Личные каналы откроются при пересечении зон видимости рас.</div>';
      return;
    }

    privateEl.innerHTML = privateChats
      .map(chat => this.renderPrivateChat(chat))
      .join('');
  }

  private renderPrivateChat(chat: SpectatePrivateChat): string {
    const messages = chat.messages.length > 0
      ? chat.messages.slice(-10).map(msg => this.renderMessageLine(msg)).join('')
      : '<div class="wv-dip-empty">Канал открыт. Сообщений пока нет.</div>';

    const overlapLabel = chat.overlapPlanets.length > 0
      ? `Зона контакта: ${chat.overlapPlanets.join(', ')}`
      : 'Зона контакта обнаружена';

    return `
      <div class="wv-dip-channel">
        <div class="wv-dip-channel-head">${esc(`${chat.playerAName} ↔ ${chat.playerBName}`)}</div>
        <div class="wv-dip-channel-overlap">${esc(overlapLabel)}</div>
        <div class="wv-dip-messages small">${messages}</div>
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
