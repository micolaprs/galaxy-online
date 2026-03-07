import { api } from '../api/client.js';
import type { SpectateData, SpectatePlayer, SpectateBattle, SpectateBombing } from '../types/api.js';
import { GalaxyMap, type MapPlanet } from './GalaxyMap.js';

interface TurnEvent {
  turn: number;
  at: string;
  battles: SpectateBattle[];
  bombings: SpectateBombing[];
}

// Fixed palette for player colors (up to 8 players)
const PLAYER_COLORS = ['#4ade80','#38bdf8','#f87171','#facc15','#a78bfa','#fb923c','#34d399','#e879f9'];

export class WatchView {
  private el: HTMLElement;
  private map!: GalaxyMap;
  private pollTimer: number | null = null;
  private gameId: string;
  private currentTurn = -1;
  private playerColorMap = new Map<string, string>();
  private turnLog: TurnEvent[] = [];

  onBack?: () => void;

  constructor(container: HTMLElement, gameId: string) {
    this.el     = container;
    this.gameId = gameId;
    this.render();
    void this.refresh();
    this.pollTimer = window.setInterval(() => void this.refresh(), 5_000);
  }

  destroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="wv-layout">
        <div class="wv-topbar">
          <button class="btn btn-sm btn-secondary" id="btn-back">← Games</button>
          <span class="wv-title" id="wv-title">Loading…</span>
          <span class="wv-turn" id="wv-turn"></span>
          <span class="wv-ago"  id="wv-ago"></span>
          <div class="wv-topbar-spacer"></div>
          <button class="btn btn-sm btn-warning" id="btn-run-turn">▶ Run Turn</button>
        </div>
        <div class="wv-body">
          <div class="wv-map-col">
            <canvas id="wv-canvas"></canvas>
          </div>
          <div class="wv-right-col">
            <div class="wv-section">
              <div class="wv-section-title">Players</div>
              <div id="wv-players"></div>
            </div>
            <div class="wv-section wv-log-section">
              <div class="wv-section-title">Turn Log</div>
              <div id="wv-log" class="wv-log"></div>
            </div>
          </div>
        </div>
      </div>
    `;

    // Size canvas to fill its container
    const canvas = this.el.querySelector<HTMLCanvasElement>('#wv-canvas')!;
    const resizeCanvas = () => {
      const col = canvas.parentElement!;
      canvas.width  = col.clientWidth  || 600;
      canvas.height = col.clientHeight || 600;
      this.map?.draw();
    };
    this.map = new GalaxyMap(canvas);
    setTimeout(resizeCanvas, 0);
    window.addEventListener('resize', resizeCanvas);

    this.el.querySelector('#btn-back')!.addEventListener('click', () => this.onBack?.());
    this.el.querySelector('#btn-run-turn')!.addEventListener('click', () => this.runTurn());
  }

  private async refresh(): Promise<void> {
    try {
      const data = await api.spectate(this.gameId);
      this.updateTitle(data);
      this.updatePlayerColors(data.players);
      this.updatePlayers(data);
      this.updateMap(data);
      if (data.turn !== this.currentTurn) {
        this.recordTurnEvent(data);
        this.currentTurn = data.turn;
      }
      this.renderLog();
    } catch (e) {
      this.el.querySelector('#wv-title')!.textContent = `Error: ${e}`;
    }
  }

  private updateTitle(data: SpectateData): void {
    this.el.querySelector('#wv-title')!.textContent = `${data.name} (${data.id})`;
    this.el.querySelector('#wv-turn')!.textContent  = `Turn ${data.turn}`;
    this.el.querySelector('#wv-ago')!.textContent   =
      data.lastTurnRunAt ? `last turn ${timeAgo(data.lastTurnRunAt)}` : 'no turns yet';
  }

  private updatePlayerColors(players: SpectatePlayer[]): void {
    players.forEach((p, i) => {
      if (!this.playerColorMap.has(p.id))
        this.playerColorMap.set(p.id, PLAYER_COLORS[i % PLAYER_COLORS.length]!);
    });
  }

  private updatePlayers(data: SpectateData): void {
    const el = this.el.querySelector('#wv-players')!;
    el.innerHTML = data.players.map(p => {
      const color   = this.playerColorMap.get(p.id) ?? '#888';
      const status  = p.isEliminated ? 'eliminated'
                    : p.submitted    ? 'submitted'
                    : 'waiting';
      const icon    = p.isBot ? '🤖' : '👤';
      const pct = data.planets.filter(pl => pl.ownerId === p.id).length;
      return `
        <div class="wv-player ${status}">
          <span class="wv-player-dot" style="background:${color}"></span>
          <span class="wv-player-icon">${icon}</span>
          <span class="wv-player-name">${esc(p.name)}</span>
          <span class="wv-player-planets">${pct}🌍</span>
          <span class="wv-player-status ${status}">${statusLabel(status)}</span>
          <span class="wv-player-tech">D${p.tech.drive.toFixed(1)} W${p.tech.weapons.toFixed(1)} S${p.tech.shields.toFixed(1)}</span>
        </div>
      `;
    }).join('');
  }

  private updateMap(data: SpectateData): void {
    const planets: MapPlanet[] = data.planets.map(p => {
      const color = p.ownerId ? (this.playerColorMap.get(p.ownerId) ?? '#888') : '#475569';
      return { name: p.name, x: p.x, y: p.y, size: p.size, owner: 'mine', color };
    });

    // Fit canvas size before setting data
    const canvas = this.el.querySelector<HTMLCanvasElement>('#wv-canvas')!;
    const col = canvas.parentElement!;
    if (canvas.width !== col.clientWidth || canvas.height !== col.clientHeight) {
      canvas.width  = col.clientWidth  || 600;
      canvas.height = col.clientHeight || 600;
    }

    this.map.setDataWithColors(data.galaxySize, planets);
  }

  private recordTurnEvent(data: SpectateData): void {
    if (data.turn === 0) return;
    this.turnLog.unshift({
      turn: data.turn,
      at:   data.lastTurnRunAt ?? new Date().toISOString(),
      battles:  [...data.battles],
      bombings: [...data.bombings],
    });
    if (this.turnLog.length > 20) this.turnLog.pop();
  }

  private renderLog(): void {
    const el = this.el.querySelector('#wv-log')!;
    if (this.turnLog.length === 0) {
      el.innerHTML = '<div class="wv-log-empty">Waiting for first turn…</div>';
      return;
    }
    el.innerHTML = this.turnLog.map(ev => {
      const events: string[] = [];
      ev.battles.forEach(b => {
        events.push(`⚔️ Battle at ${b.planetName}: ${b.participants.join(' vs ')} → ${b.winner} wins`);
      });
      ev.bombings.forEach(b => {
        const prev = b.previousOwner ? ` (was ${b.previousOwner})` : '';
        events.push(`💥 ${b.attackerRace} bombed ${b.planetName}${prev}`);
      });
      if (events.length === 0) events.push('🕊️ Peaceful turn');
      return `
        <div class="wv-log-entry">
          <div class="wv-log-turn">Turn ${ev.turn} <span class="wv-log-time">${timeAgo(ev.at)}</span></div>
          ${events.map(e => `<div class="wv-log-event">${e}</div>`).join('')}
        </div>
      `;
    }).join('');
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

// ---- GalaxyMap extension: support per-planet colors ----
// We need to pass custom colors. Patch MapPlanet to accept optional color.
declare module './GalaxyMap.js' {
  interface MapPlanet { color?: string; }
}

function statusLabel(s: string): string {
  return s === 'submitted' ? '✓ submitted'
       : s === 'eliminated' ? '✗ out'
       : '⏳ waiting';
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}

function timeAgo(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (secs < 60)   return `${secs}s ago`;
  if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
  return `${Math.floor(secs / 3600)}h ago`;
}
