import { api } from '../api/client.js';
import type { Session } from '../types/api.js';

const HELP = `; GalaxyNG Orders — one per line, semicolon = comment
; ── Planet ──────────────────────────────────────────
; p <planet> <CAP|MAT|DRIVE|WEAPONS|SHIELDS|CARGO|type>
; r <origin> <CAP|COL|MAT|EMP> [destination]
; n <planet> <new_name>
; ── Ships ────────────────────────────────────────────
; d <name> <drive> <attacks> <weapons> <shields> <cargo>
; ── Groups ───────────────────────────────────────────
; s <group#> <planet>    ; send
; l <group#> <CAP|COL|MAT>  ; load
; u <group#>             ; unload
; b <group#> <ships>     ; break off
; g <group#>             ; upgrade
; x <group#>             ; scrap
; ── Diplomacy ────────────────────────────────────────
; a <race>   ; ally      w <race>   ; war
`;

export class OrdersEditor {
  private el: HTMLElement;
  private textarea!: HTMLTextAreaElement;
  private statusEl!: HTMLElement;
  private session: Session | null = null;
  private gameId: string = '';

  constructor(container: HTMLElement) {
    this.el = container;
    this.render();
  }

  setContext(gameId: string, session: Session): void {
    this.gameId  = gameId;
    this.session = session;
  }

  getOrders(): string { return this.textarea.value; }

  setOrders(text: string): void { this.textarea.value = text; }

  private render(): void {
    this.el.innerHTML = `
      <div class="orders-editor">
        <div class="orders-toolbar">
          <button id="btn-help" class="btn btn-sm">Help</button>
          <button id="btn-forecast" class="btn btn-sm btn-secondary">Forecast</button>
          <button id="btn-submit" class="btn btn-primary">End Turn ⏎</button>
        </div>
        <textarea id="orders-text" spellcheck="false" placeholder="Enter orders…"></textarea>
        <div id="orders-status" class="orders-status"></div>
      </div>
    `;

    this.textarea = this.el.querySelector('#orders-text')!;
    this.statusEl = this.el.querySelector('#orders-status')!;

    this.el.querySelector('#btn-help')!.addEventListener('click', () => {
      this.textarea.value = HELP + (this.textarea.value || '');
    });

    this.el.querySelector('#btn-forecast')!.addEventListener('click', () => this.doForecast());
    this.el.querySelector('#btn-submit')!.addEventListener('click', () => this.doSubmit());

    this.textarea.addEventListener('keydown', e => {
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        this.doSubmit();
      }
    });
  }

  private async doForecast(): Promise<void> {
    if (!this.session) return;
    this.setStatus('Fetching forecast…', 'info');
    try {
      const forecast = await api.getForecast(this.gameId, this.session, this.textarea.value);
      showModal('Forecast', `<pre>${escapeHtml(forecast)}</pre>`);
      this.setStatus('Forecast ready.', 'ok');
    } catch (e) {
      this.setStatus(`Error: ${e}`, 'error');
    }
  }

  private async doSubmit(): Promise<void> {
    if (!this.session) return;
    this.setStatus('Submitting…', 'info');
    try {
      await api.submitOrders(this.gameId, this.session, this.textarea.value, true);
      this.setStatus('✓ Orders submitted as FINAL.', 'ok');
    } catch (e) {
      this.setStatus(`Error: ${e}`, 'error');
    }
  }

  private setStatus(msg: string, type: 'ok' | 'error' | 'info'): void {
    this.statusEl.textContent = msg;
    this.statusEl.className   = `orders-status ${type}`;
  }
}

function escapeHtml(s: string): string {
  return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function showModal(title: string, html: string): void {
  const existing = document.getElementById('modal-overlay');
  existing?.remove();

  const overlay = document.createElement('div');
  overlay.id    = 'modal-overlay';
  overlay.innerHTML = `
    <div class="modal">
      <div class="modal-header">
        <span>${title}</span>
        <button class="btn-close" id="modal-close">✕</button>
      </div>
      <div class="modal-body">${html}</div>
    </div>
  `;
  document.body.appendChild(overlay);
  overlay.addEventListener('click', e => {
    if (e.target === overlay || (e.target as HTMLElement).id === 'modal-close')
      overlay.remove();
  });
}
