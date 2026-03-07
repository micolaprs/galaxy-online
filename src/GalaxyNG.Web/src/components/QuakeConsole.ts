import { ensureConnected } from '../api/hub.js';
import type { LogEntry } from '../types/api.js';

const MAX_LINES = 500;

const LEVEL_CSS: Record<string, string> = {
  Trace:       'qc-trace',
  Debug:       'qc-debug',
  Information: 'qc-info',
  Warning:     'qc-warn',
  Error:       'qc-error',
  Critical:    'qc-error',
};

/**
 * Quake-style drop-down console.
 * Toggle with the tilde key (~).
 * Subscribes to server-logs SignalR group and streams ALL backend logs.
 */
export class QuakeConsole {
  private overlay: HTMLElement;
  private outputEl: HTMLElement;
  private lines: string[] = [];
  private open = false;
  private connected = false;

  constructor() {
    this.overlay  = this.buildDOM();
    this.outputEl = this.overlay.querySelector('.qc-output')!;
    document.body.appendChild(this.overlay);

    // Toggle on ~ (tilde) — key code 192 (Backquote)
    window.addEventListener('keydown', (e) => {
      if (e.key === '`' || e.key === '~' || e.code === 'Backquote') {
        // Don't steal focus from text inputs
        const tag = (e.target as HTMLElement).tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA') return;
        e.preventDefault();
        this.toggle();
      }
    });

    void this.connectHub();
  }

  private buildDOM(): HTMLElement {
    const overlay = document.createElement('div');
    overlay.className = 'qc-overlay qc-closed';
    overlay.innerHTML = `
      <div class="qc-header">
        <span class="qc-title">▶ GalaxyNG Server Console</span>
        <span class="qc-hint">Press <kbd>~</kbd> to toggle</span>
        <div class="qc-header-actions">
          <button class="qc-btn" id="qc-clear">Clear</button>
          <button class="qc-btn" id="qc-close">✕</button>
        </div>
      </div>
      <div class="qc-output"></div>
    `;

    overlay.querySelector('#qc-clear')!.addEventListener('click', () => {
      this.lines = [];
      this.outputEl.innerHTML = '';
    });
    overlay.querySelector('#qc-close')!.addEventListener('click', () => this.close());

    return overlay;
  }

  private async connectHub(): Promise<void> {
    try {
      const hub = await ensureConnected();
      await hub.invoke('JoinLogsGroup');

      hub.on('LogEntry', (entry: LogEntry) => {
        this.appendLine(entry);
      });

      hub.on('LogsJoined', () => {
        this.appendRaw('qc-info', 'System', 'Connected to server log stream.');
      });

      this.connected = true;
    } catch (e) {
      this.appendRaw('qc-error', 'Console', `Failed to connect to log stream: ${e}`);
    }
  }

  private appendLine(entry: LogEntry): void {
    const css = LEVEL_CSS[entry.level] ?? 'qc-info';
    const html = `<div class="qc-line ${css}"><span class="qc-time">${entry.time}</span><span class="qc-cat">[${esc(entry.category)}]</span><span class="qc-msg">${esc(entry.message)}</span></div>`;
    this.pushHtml(html);
  }

  private appendRaw(css: string, cat: string, msg: string): void {
    const now = new Date().toLocaleTimeString('en-GB', { hour12: false, fractionalSecondDigits: 3 });
    const html = `<div class="qc-line ${css}"><span class="qc-time">${now}</span><span class="qc-cat">[${esc(cat)}]</span><span class="qc-msg">${esc(msg)}</span></div>`;
    this.pushHtml(html);
  }

  private pushHtml(html: string): void {
    this.lines.push(html);
    if (this.lines.length > MAX_LINES) this.lines.shift();

    // Only touch DOM when visible — avoids layout thrash while closed
    if (this.open) {
      this.outputEl.insertAdjacentHTML('beforeend', html);
      // Trim DOM to match lines array
      while (this.outputEl.childElementCount > MAX_LINES)
        this.outputEl.removeChild(this.outputEl.firstChild!);
      this.outputEl.scrollTop = this.outputEl.scrollHeight;
    }
  }

  toggle(): void {
    this.open ? this.close() : this.show();
  }

  show(): void {
    this.open = true;
    this.overlay.classList.remove('qc-closed');
    this.overlay.classList.add('qc-open');
    // Batch-render everything accumulated while closed
    this.outputEl.innerHTML = this.lines.join('');
    this.outputEl.scrollTop = this.outputEl.scrollHeight;
  }

  close(): void {
    this.open = false;
    this.overlay.classList.remove('qc-open');
    this.overlay.classList.add('qc-closed');
    // Clear DOM — will be rebuilt cheaply on next open()
    this.outputEl.innerHTML = '';
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
