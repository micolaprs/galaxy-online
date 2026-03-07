import { api } from '../api/client.js';
import type { TurnHistoryEntry, TurnPlayerOrders } from '../types/api.js';
import type { SpectatePlayer } from '../types/api.js';

export class PlayerHistoryPanel {
  private el: HTMLElement;
  private gameId: string;
  private players: SpectatePlayer[] = [];
  private playerColorMap: Map<string, string>;

  private selectedRace: string | null = null;
  private history: TurnHistoryEntry[] = [];
  private selectedTurn: number | null = null;
  private turnDetail: TurnPlayerOrders | null = null;
  private turnSummary: string | null = null;
  private loadingSummary = false;

  constructor(
    container: HTMLElement,
    gameId: string,
    playerColorMap: Map<string, string>,
  ) {
    this.gameId         = gameId;
    this.playerColorMap = playerColorMap;
    this.el             = container;
  }

  updatePlayers(players: SpectatePlayer[]): void {
    this.players = players;
    if (!this.selectedRace) this.renderPlayerList();
  }

  private renderPlayerList(): void {
    const colorDots = this.players.map(p => {
      const color = this.playerColorMap.get(p.id) ?? '#888';
      return `
        <div class="ph-player ${p.isEliminated ? 'eliminated' : ''}"
             data-race="${esc(p.name)}" style="--dot:${color}">
          <span class="ph-dot"></span>
          <span class="ph-icon">${p.isBot ? '🤖' : '👤'}</span>
          <span class="ph-name">${esc(p.name)}</span>
          <span class="ph-count muted">${p.planetCount}🌍</span>
        </div>
      `;
    }).join('');

    this.el.innerHTML = `
      <div class="ph-title">Выбери игрока:</div>
      <div class="ph-player-list">${colorDots}</div>
    `;

    this.el.querySelectorAll<HTMLElement>('.ph-player').forEach(row => {
      row.addEventListener('click', () => {
        const race = row.dataset['race']!;
        void this.selectPlayer(race);
      });
    });
  }

  private async selectPlayer(race: string): Promise<void> {
    this.selectedRace  = race;
    this.selectedTurn  = null;
    this.turnDetail    = null;
    this.turnSummary   = null;

    this.el.innerHTML = `
      <div class="ph-nav">
        <button class="ph-back" id="ph-back-players">← Игроки</button>
        <span class="ph-heading">${esc(race)}</span>
      </div>
      <div class="ph-loading">Загрузка истории…</div>
    `;
    this.el.querySelector('#ph-back-players')!
      .addEventListener('click', () => {
        this.selectedRace = null;
        this.renderPlayerList();
      });

    try {
      this.history = await api.getHistory(this.gameId);
      this.renderTurnList();
    } catch {
      this.el.querySelector('.ph-loading')!.textContent = 'Ошибка загрузки истории';
    }
  }

  private renderTurnList(): void {
    const race = this.selectedRace!;

    const rows = this.history
      .filter(h => h.players.includes(race))
      .map(h => {
        const battles  = h.battleCount  > 0 ? `⚔️${h.battleCount}` : '';
        const bombings = h.bombingCount > 0 ? `💥${h.bombingCount}` : '';
        const tags = [battles, bombings].filter(Boolean).join(' ');
        return `
          <div class="ph-turn ${this.selectedTurn === h.turn ? 'selected' : ''}"
               data-turn="${h.turn}">
            <span class="ph-turn-num">Ход ${h.turn}</span>
            <span class="ph-turn-tags">${tags}</span>
          </div>
        `;
      }).join('');

    const emptyMsg = rows ? '' : '<div class="ph-empty">Нет истории ходов</div>';

    // Keep nav, replace content below it
    const content = this.el.querySelector('.ph-content') ??
      (() => {
        const d = document.createElement('div');
        d.className = 'ph-content';
        this.el.appendChild(d);
        return d;
      })();

    content.innerHTML = rows
      ? `<div class="ph-turn-list">${rows}</div>`
      : emptyMsg;

    content.querySelectorAll<HTMLElement>('.ph-turn').forEach(row => {
      row.addEventListener('click', () => {
        const turn = parseInt(row.dataset['turn']!);
        void this.selectTurn(turn);
      });
    });
  }

  private async selectTurn(turn: number): Promise<void> {
    this.selectedTurn  = turn;
    this.turnDetail    = null;
    this.turnSummary   = null;
    this.loadingSummary = false;

    this.renderTurnDetail();

    try {
      this.turnDetail = await api.getTurnPlayerOrders(this.gameId, turn, this.selectedRace!);
      this.renderTurnDetail();
    } catch {
      this.showTurnDetailError('Ошибка загрузки приказов');
    }
  }

  private renderTurnDetail(): void {
    // Replace content area
    let content = this.el.querySelector<HTMLElement>('.ph-content');
    if (!content) {
      content = document.createElement('div');
      content.className = 'ph-content';
      this.el.appendChild(content);
    }

    if (!this.turnDetail) {
      content.innerHTML = `
        <div class="ph-turn-back">
          <button class="ph-back" id="ph-back-turns">← Ходы</button>
          <span class="ph-heading">Ход ${this.selectedTurn}</span>
        </div>
        <div class="ph-loading">Загрузка…</div>
      `;
      content.querySelector('#ph-back-turns')!
        .addEventListener('click', () => { this.selectedTurn = null; this.renderTurnList(); });
      return;
    }

    const d = this.turnDetail;
    const summarySection = this.turnSummary
      ? `<div class="ph-summary-text">${esc(this.turnSummary)}</div>`
      : this.loadingSummary
        ? '<div class="ph-loading">ИИ анализирует…</div>'
        : `<button class="btn btn-sm btn-primary" id="ph-gen-summary">✨ Краткая сводка (ИИ)</button>`;

    const events = [...d.battles.map(b => `⚔️ ${b}`), ...d.bombings.map(b => `💥 ${b}`)];
    const eventsHtml = events.length
      ? events.map(e => `<div class="ph-event">${esc(e)}</div>`).join('')
      : '<div class="ph-event muted">Мирный ход</div>';

    content.innerHTML = `
      <div class="ph-turn-back">
        <button class="ph-back" id="ph-back-turns">← Ходы</button>
        <span class="ph-heading">Ход ${d.turn} — ${esc(d.race)}</span>
      </div>
      <div class="ph-detail-scroll">
        <div class="ph-section-title">Аналитика</div>
        <div class="ph-summary-wrap" id="ph-summary-wrap">
          ${summarySection}
        </div>
        <div class="ph-section-title">События хода</div>
        <div class="ph-events">${eventsHtml}</div>
        <div class="ph-section-title">Приказы игрока</div>
        <pre class="ph-orders">${esc(d.orders || '(нет приказов)')}</pre>
      </div>
    `;

    content.querySelector('#ph-back-turns')!
      .addEventListener('click', () => { this.selectedTurn = null; this.renderTurnList(); });

    const genBtn = content.querySelector<HTMLButtonElement>('#ph-gen-summary');
    if (genBtn) {
      genBtn.addEventListener('click', () => void this.generateSummary());
    }
  }

  private async generateSummary(): Promise<void> {
    if (this.loadingSummary || !this.selectedTurn) return;
    this.loadingSummary = true;
    this.renderTurnDetail();

    try {
      this.turnSummary = await api.getTurnSummary(
        this.gameId, this.selectedTurn, this.selectedRace!);
    } catch {
      this.turnSummary = 'Не удалось получить сводку от ИИ.';
    }
    this.loadingSummary = false;
    this.renderTurnDetail();
  }

  private showTurnDetailError(msg: string): void {
    const content = this.el.querySelector('.ph-content');
    if (content) {
      const loading = content.querySelector('.ph-loading');
      if (loading) loading.textContent = msg;
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}
