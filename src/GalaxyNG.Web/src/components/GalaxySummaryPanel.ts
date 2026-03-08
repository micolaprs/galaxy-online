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
    if (turn !== this.currentTurn) {
      this.currentTurn = turn;
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
    const history        = this.summaries
      .filter(s => s.turn !== this.currentTurn)
      .sort((a, b) => b.turn - a.turn);

    const histHtml = history.map(s => `
      <details class="gs-history-item">
        <summary class="gs-history-header">Ход ${s.turn} <span class="muted">${timeAgo(s.generatedAt)}</span></summary>
        <div class="gs-history-text">${esc(sanitizeUiText(s.summary))}</div>
      </details>
    `).join('');

    this.el.innerHTML = `
      <div class="gs-current">
        <div class="gs-current-header">
          <span class="gs-turn-label">Ход ${this.currentTurn}</span>
          <button class="btn btn-sm btn-primary" id="gs-gen-btn"
            ${this.generating ? 'disabled' : ''}>
            ${this.generating ? '⏳ Анализирую…' : '✨ Сводка от ИИ'}
          </button>
        </div>
        ${loading
          ? '<div class="gs-loading">Загрузка…</div>'
          : currentSummary
            ? `<div class="gs-summary-text">${esc(sanitizeUiText(currentSummary.summary))}</div>
               <div class="muted gs-gen-time">Сгенерировано ${timeAgo(currentSummary.generatedAt)}</div>`
            : '<div class="gs-empty">Нажми «Сводка от ИИ», чтобы получить анализ текущего хода.</div>'
        }
      </div>
      ${history.length > 0 ? `
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
