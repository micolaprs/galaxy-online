import { api } from '../api/client.js';
import type { GameSummary } from '../types/api.js';

export class GameList {
  private el: HTMLElement;
  private pollTimer: number | null = null;

  onWatch?:  (gameId: string) => void;
  onNewGame?: () => void;

  constructor(container: HTMLElement) {
    this.el = container;
    this.render();
    void this.refresh();
    this.pollTimer = window.setInterval(() => void this.refresh(), 5_000);
  }

  destroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="gl-layout">
        <div class="gl-header">
          <span class="logo-sm">🌌 GalaxyNG</span>
          <button class="btn btn-primary" id="btn-new-game">+ New Game</button>
        </div>
        <div class="gl-body">
          <div class="gl-title">Active Games</div>
          <div id="gl-table-wrap">
            <div class="gl-loading">Loading…</div>
          </div>
        </div>
      </div>
    `;
    this.el.querySelector('#btn-new-game')!
      .addEventListener('click', () => this.onNewGame?.());
  }

  private async refresh(): Promise<void> {
    try {
      const games = await api.listGames();
      this.renderTable(games);
    } catch {
      const wrap = this.el.querySelector('#gl-table-wrap')!;
      wrap.innerHTML = '<div class="gl-loading error">Cannot connect to server.</div>';
    }
  }

  private renderTable(games: GameSummary[]): void {
    const wrap = this.el.querySelector('#gl-table-wrap')!;
    if (games.length === 0) {
      wrap.innerHTML = '<div class="gl-loading muted">No games yet. Create one!</div>';
      return;
    }

    const rows = games.map(g => {
      const ago = g.lastTurnRunAt ? timeAgo(g.lastTurnRunAt) : '—';
      return `
        <tr class="gl-row" data-id="${g.id}">
          <td class="gl-cell gl-name">${esc(g.name)}</td>
          <td class="gl-cell gl-gid">${g.id}</td>
          <td class="gl-cell gl-turn">T${g.turn}</td>
          <td class="gl-cell gl-players">${g.playerCount} players</td>
          <td class="gl-cell gl-ago">${ago}</td>
          <td class="gl-cell gl-action">
            <button class="btn btn-sm btn-watch" data-id="${g.id}">Watch →</button>
          </td>
        </tr>
      `;
    }).join('');

    wrap.innerHTML = `
      <table class="gl-table">
        <thead>
          <tr>
            <th>Name</th><th>ID</th><th>Turn</th>
            <th>Players</th><th>Last turn</th><th></th>
          </tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    `;

    wrap.querySelectorAll<HTMLButtonElement>('.btn-watch').forEach(btn => {
      btn.addEventListener('click', () => this.onWatch?.(btn.dataset['id']!));
    });
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}

function timeAgo(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (secs < 60)  return `${secs}s ago`;
  if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
  return `${Math.floor(secs / 3600)}h ago`;
}
