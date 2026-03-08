import { api } from '../api/client.js';
import type { AiSummaryEntry } from '../types/api.js';
import { sanitizeUiText } from '../utils/uiText.js';

export class GalaxySummaryPanel {
  private el: HTMLElement;
  private gameId: string;
  private currentTurn: number;
  private summaries: AiSummaryEntry[] = [];
  private generating = false;

  constructor(container: HTMLElement, gameId: string, currentTurn: number) {
    this.el          = container;
    this.gameId      = gameId;
    this.currentTurn = currentTurn;
    void this.load();
  }

  updateTurn(turn: number): void {
    const changed = turn !== this.currentTurn;
    this.currentTurn = turn;

    // Reload not only on turn switch, but also while summary for current turn is missing.
    if (changed || !this.summaries.some(s => s.turn === this.currentTurn)) {
      void this.load();
    }
  }

  private async load(): Promise<void> {
    this.render(true);
    try {
      this.summaries = await api.getAiSummaries(this.gameId);
      this.render(false);
    } catch {
      this.render(false);
    }
  }

  private render(loading: boolean): void {
    const currentSummary = this.summaries.find(s => s.turn === this.currentTurn);
    const latestAvailableSummary =
      currentSummary ??
      this.summaries
        .filter(s => s.turn <= this.currentTurn)
        .sort((a, b) => b.turn - a.turn)[0];
    const summaryByTurn = new Map(this.summaries.map(s => [s.turn, s] as const));
    const historyTurns = Array.from({ length: Math.max(0, this.currentTurn - 1) }, (_, i) => this.currentTurn - 1 - i);

    let openedHistoryItem = false;
    const histHtml = historyTurns.map(turn => {
      const s = summaryByTurn.get(turn);
      const ready = !!s;
      const badge = `<span class="gs-status ${ready ? 'ready' : 'pending'}">${ready ? 'готова' : 'в процессе'}</span>`;
      const openAttr = !openedHistoryItem ? ' open' : '';
      openedHistoryItem = true;
      if (!s) {
        return `
      <details class="gs-history-item"${openAttr}>
        <summary class="gs-history-header">Ход ${turn} ${badge}</summary>
        <div class="gs-history-text muted">Сводка для этого хода ещё не сгенерирована.</div>
      </details>
    `;
      }
      return `
      <details class="gs-history-item"${openAttr}>
        <summary class="gs-history-header">Ход ${s.turn} ${badge} <span class="muted">${timeAgo(s.generatedAt)}</span></summary>
        <div class="gs-history-text">${esc(sanitizeUiText(s.summary))}</div>
      </details>
    `;
    }).join('');

    const currentReady = !!currentSummary;
    const currentStatus = `<span class="gs-status ${currentReady ? 'ready' : 'pending'}">${currentReady ? 'готова' : 'в процессе'}</span>`;

    this.el.innerHTML = `
      <div class="gs-current">
        <div class="gs-current-header">
          <span class="gs-turn-label">Ход ${this.currentTurn} ${currentStatus}</span>
          <button class="btn btn-sm btn-primary" id="gs-gen-btn"
            ${this.generating ? 'disabled' : ''}>
            ${this.generating ? '⏳ Анализирую…' : '↻ Обновить'}
          </button>
        </div>
        ${loading
          ? '<div class="gs-loading">Загрузка…</div>'
          : latestAvailableSummary
            ? `<div class="gs-summary-text">${esc(sanitizeUiText(latestAvailableSummary.summary))}</div>
               <div class="muted gs-gen-time">Сгенерировано ${timeAgo(latestAvailableSummary.generatedAt)} (ход ${latestAvailableSummary.turn})</div>`
            : '<div class="gs-empty">Сводка ещё не готова. Попробуй обновить через пару секунд.</div>'
        }
      </div>
      ${historyTurns.length > 0 ? `
        <div class="gs-history">
          <div class="gs-section-title">История сводок</div>
          ${histHtml}
        </div>
      ` : ''}
    `;

    this.el.querySelector('#gs-gen-btn')!
      .addEventListener('click', () => void this.generate());
  }

  private async generate(): Promise<void> {
    if (this.generating) return;
    this.generating = true;
    this.render(false);
    try {
      const summary = await api.generateGalaxySummary(this.gameId);
      this.summaries = this.summaries.filter(s => s.turn !== this.currentTurn);
      this.summaries.push({
        turn: this.currentTurn,
        summary,
        generatedAt: new Date().toISOString(),
      });
    } catch {
      // will stay empty, user can retry
    }
    this.generating = false;
    this.render(false);
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
}

function timeAgo(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (secs < 60)   return `${secs}с назад`;
  if (secs < 3600) return `${Math.floor(secs / 60)}м назад`;
  return `${Math.floor(secs / 3600)}ч назад`;
}
