import { api } from '../api/client.js';
import type { TurnHistoryEntry, TurnPlayerOrders, SpectatePlayer, BotStatusEvent } from '../types/api.js';
import { sanitizeUiText } from '../utils/uiText.js';

const STATUS_LABEL: Record<string, string> = {
  idle:             '💤 idle',
  waiting:          '⏳ ждёт хода',
  'reading-report': '📖 читает отчёт',
  thinking:         '🧠 думает…',
  validating:       '🔍 проверяет',
  submitting:       '📤 отправляет',
  submitted:        '✓ отправил',
  error:            '❌ ошибка',
};

const STATUS_CSS: Record<string, string> = {
  idle:             'idle',
  waiting:          'waiting',
  'reading-report': 'active',
  thinking:         'thinking',
  validating:       'active',
  submitting:       'active',
  submitted:        'submitted',
  error:            'error',
};

export class PlayerHistoryPanel {
  private el: HTMLElement;
  private gameId: string;
  private players: SpectatePlayer[] = [];
  private playerColorMap: Map<string, string>;
  private botStatuses: Map<string, BotStatusEvent>;

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
    botStatuses: Map<string, BotStatusEvent>,
  ) {
    this.gameId         = gameId;
    this.playerColorMap = playerColorMap;
    this.botStatuses    = botStatuses;
    this.el             = container;
  }

  updatePlayers(players: SpectatePlayer[]): void {
    this.players = players;
    if (!this.selectedRace) this.renderPlayerList();
  }

  private renderPlayerList(): void {
    const colorDots = this.players.map(p => {
      const color = this.playerColorMap.get(p.id) ?? '#888';

      // Determine status label + css class
      // p.submitted (from server) takes priority over transient bot status
      let statusKey = '';
      if (p.isEliminated) {
        statusKey = '';
      } else if (p.submitted) {
        statusKey = 'submitted';
      } else if (p.isBot) {
        const ev = this.botStatuses.get(p.name);
        statusKey = ev ? ev.status : 'waiting';
      }
      const statusLabel = STATUS_LABEL[statusKey] ?? '';
      const statusCss   = STATUS_CSS[statusKey]   ?? '';
      const statusHtml  = statusLabel
        ? `<span class="ph-status ph-status-${statusCss}">${statusLabel}</span>`
        : '';

      const ev = p.isBot ? (this.botStatuses.get(p.name) ?? null) : null;
      const thinkingHtml = ev?.thinking
        ? `<details class="ph-thinking">
             <summary class="ph-thinking-toggle">💭 Размышления</summary>
             <pre class="ph-thinking-text">${esc(sanitizeUiText(ev.thinking))}</pre>
           </details>`
        : '';

      return `
        <div class="ph-player ${p.isEliminated ? 'eliminated' : ''}"
             data-race="${esc(p.name)}" style="--dot:${color}">
          <div class="ph-player-row">
            <span class="ph-dot"></span>
            <span class="ph-icon">${p.isBot ? '🤖' : '👤'}</span>
            <span class="ph-name">${esc(p.name)}</span>
            ${statusHtml}
            <span class="ph-count muted">${p.planetCount}🌍</span>
          </div>
          ${thinkingHtml}
        </div>
      `;
    }).join('');

    this.el.innerHTML = `
      <div class="ph-title">Выбери игрока:</div>
      <div class="ph-player-list">${colorDots}</div>
    `;

    this.el.querySelectorAll<HTMLElement>('.ph-player-row').forEach(row => {
      row.addEventListener('click', () => {
        const race = row.closest<HTMLElement>('.ph-player')!.dataset['race']!;
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
      ? `<div class="ph-summary-text">${esc(sanitizeUiText(this.turnSummary))}</div>`
      : this.loadingSummary
        ? '<div class="ph-loading">ИИ анализирует…</div>'
        : `<button class="btn btn-sm btn-primary" id="ph-gen-summary">✨ Краткая сводка (ИИ)</button>`;

    const events = [...d.battles.map(b => `⚔️ ${b}`), ...d.bombings.map(b => `💥 ${b}`)];
    const eventsHtml = events.length
      ? events.map(e => `<div class="ph-event">${esc(e)}</div>`).join('')
      : '<div class="ph-event muted">Мирный ход</div>';

    // Parse reasoning + orders from either saved history or live LLM response
    const reasoning = sanitizeUiText(d.reasoning || '');
    const orderLines = extractGameCommands(d.orders || '');
    const ordersHtml = orderLines.length
      ? orderLines.map(l => `<div class="ph-order-line">${esc(l)}</div>`).join('')
      : '<div class="ph-event muted">(нет приказов)</div>';

    const reasoningHtml = reasoning
      ? `<details class="ph-reasoning" open>
           <summary class="ph-reasoning-toggle">💭 Размышления бота</summary>
           <pre class="ph-reasoning-text">${esc(reasoning)}</pre>
         </details>`
      : '';

    content.innerHTML = `
      <div class="ph-turn-back">
        <button class="ph-back" id="ph-back-turns">← Ходы</button>
        <span class="ph-heading">Ход ${d.turn} — ${esc(d.race)}</span>
      </div>
      <div class="ph-detail-scroll">
        <div class="ph-section-title">События хода</div>
        <div class="ph-events">${eventsHtml}</div>
        <div class="ph-section-title">Отданные команды</div>
        <div class="ph-orders-list">${ordersHtml}</div>
        ${reasoningHtml}
        <div class="ph-section-title">ИИ-анализ</div>
        <div class="ph-summary-wrap" id="ph-summary-wrap">
          ${summarySection}
        </div>
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

/** Returns only game command lines, stripping @ message blocks. */
function extractGameCommands(orders: string): string[] {
  const result: string[] = [];
  let inMessage = false;
  for (const raw of orders.split('\n')) {
    const line = raw.trim();
    if (!line) continue;
    if (line.startsWith('@')) {
      inMessage = !inMessage;
      continue;
    }
    if (!inMessage) result.push(line);
  }
  return result;
}
