import type { BattleRecordDetail, ShipDesignSnapshot } from '../types/api.js';
import { BattleVisualizer } from './BattleVisualizer.js';

interface ReplayTurn {
  turn: number;
  runAt: string;
  battles: BattleRecordDetail[];
}

// Accepts the raw parsed game.json (camelCase keys from GameStore)
interface GameJsonData {
  id?: string;
  name?: string;
  turn?: number;
  players?: Record<string, { name?: string }>;
  turnHistory?: Array<{
    turn: number;
    runAt?: string;
    battleRecords?: Array<{
      planetName: string;
      winner: string;
      participants: string[];
      protocol: Array<{ attackerRace: string; defenderRace: string; killed: boolean }>;
      initialShips?: Record<string, number>;
      shipDesigns?: Record<string, ShipDesignSnapshot>;
    }>;
  }>;
}

const FALLBACK_COLORS = [
  '#4ade80','#38bdf8','#f87171','#facc15','#a78bfa','#fb923c','#34d399','#e879f9',
];

export class GameReplayView {
  private el: HTMLElement;
  private turns: ReplayTurn[];
  private playerColorMap = new Map<string, string>();
  private gameInfo: { id: string; name: string; totalTurns: number };
  private activeVisualizer: BattleVisualizer | null = null;

  // Auto-play state
  private autoPlayQueue: Array<{ turn: number; battle: BattleRecordDetail }> = [];
  private autoPlayIndex = 0;

  onBack?: () => void;

  constructor(container: HTMLElement, rawData: unknown) {
    this.el = container;
    const data = rawData as GameJsonData;
    this.turns = this.parseTurns(data);
    this.gameInfo = {
      id: data.id ?? '?',
      name: data.name ?? 'Игра без названия',
      totalTurns: data.turn ?? (this.turns.at(-1)?.turn ?? 0),
    };
    this.buildColorMap(data);
    this.render();
  }

  destroy(): void {
    this.activeVisualizer?.destroy();
    this.autoPlayQueue = [];
  }

  private parseTurns(data: GameJsonData): ReplayTurn[] {
    return (data.turnHistory ?? [])
      .filter(h => (h.battleRecords ?? []).length > 0)
      .map(h => ({
        turn: h.turn,
        runAt: h.runAt ?? '',
        battles: (h.battleRecords ?? []).map(b => ({
          planetName:   b.planetName,
          winner:       b.winner,
          participants: b.participants,
          protocol:     b.protocol,
          initialShips: b.initialShips ?? {},
          shipDesigns:  b.shipDesigns,
        })),
      }))
      .sort((a, b) => a.turn - b.turn);
  }

  private buildColorMap(data: GameJsonData): void {
    // Try to use player names from the game object first
    const playerNames: string[] = [];
    if (data.players) {
      for (const p of Object.values(data.players)) {
        if (p.name) playerNames.push(p.name);
      }
    }
    // Supplement with participant names from battles
    const allRaces = new Set<string>(playerNames);
    for (const t of this.turns) {
      for (const b of t.battles) {
        for (const p of b.participants) allRaces.add(p);
      }
    }
    let idx = 0;
    for (const race of allRaces) {
      this.playerColorMap.set(race, FALLBACK_COLORS[idx % FALLBACK_COLORS.length]!);
      idx++;
    }
  }

  private get totalBattles(): number {
    return this.turns.reduce((n, t) => n + t.battles.length, 0);
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="grv-layout">
        <div class="grv-topbar">
          <button class="btn btn-sm btn-secondary" id="grv-back">← Назад</button>
          <span class="grv-title">📼 ${esc(this.gameInfo.name)}</span>
          <span class="grv-id">${esc(this.gameInfo.id)}</span>
          <span class="grv-meta">${this.turns.length} ходов · ${this.totalBattles} сражений</span>
          <div class="grv-spacer"></div>
          ${this.totalBattles > 0
            ? `<button class="btn btn-sm btn-primary" id="grv-play-all">▶▶ Воспроизвести всё</button>`
            : ''}
        </div>
        <div class="grv-body">
          <div class="grv-turns" id="grv-turns">
            ${this.turns.length === 0
              ? '<div class="grv-empty">Файл не содержит записей сражений.</div>'
              : this.turns.map(t => this.renderTurnBlock(t)).join('')}
          </div>
        </div>
      </div>
      <div class="grv-overlay hidden" id="grv-overlay">
        <div class="grv-modal">
          <div class="grv-modal-head">
            <span class="grv-modal-title" id="grv-modal-title">Сражение</span>
            <button class="btn-close" id="grv-close">×</button>
          </div>
          <div class="grv-modal-body" id="grv-modal-body"></div>
        </div>
      </div>
    `;

    this.el.querySelector('#grv-back')!.addEventListener('click', () => this.onBack?.());
    this.el.querySelector('#grv-close')!.addEventListener('click', () => this.closeVisualizer());
    this.el.querySelector('#grv-overlay')!.addEventListener('click', ev => {
      if (ev.target === ev.currentTarget) this.closeVisualizer();
    });
    this.el.querySelector('#grv-play-all')?.addEventListener('click', () => this.startAutoPlay());

    this.el.querySelectorAll<HTMLElement>('.grv-replay-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const turnNo    = parseInt(btn.dataset['turn']   ?? '0');
        const battleIdx = parseInt(btn.dataset['battle'] ?? '0');
        const turn = this.turns.find(t => t.turn === turnNo);
        const battle = turn?.battles[battleIdx];
        if (battle) this.openVisualizer(turnNo, battle);
      });
    });
  }

  private renderTurnBlock(t: ReplayTurn): string {
    const date = t.runAt ? new Date(t.runAt).toLocaleString() : '';
    const cards = t.battles.map((b, i) => {
      const badges = b.participants.map(p => {
        const color = this.playerColorMap.get(p) ?? '#94a3b8';
        return `<span class="grv-badge" style="border-color:${color};color:${color}">${esc(p)}</span>`;
      }).join('');
      const initCounts = Object.entries(b.initialShips)
        .map(([r, n]) => `${esc(r)}: ${n}`)
        .join(' vs ');
      const shots = b.protocol.length;
      const replayBtn = shots > 0
        ? `<button class="grv-replay-btn" data-turn="${t.turn}" data-battle="${i}">▶ Реплей <span class="grv-shot-count">${shots} выстр.</span></button>`
        : `<span class="grv-no-replay">нет данных выстрелов</span>`;
      return `
        <div class="grv-battle-card">
          <div class="grv-battle-planet">⚔ ${esc(b.planetName)}</div>
          <div class="grv-battle-badges">${badges}</div>
          ${initCounts ? `<div class="grv-battle-counts">${initCounts}</div>` : ''}
          <div class="grv-battle-footer">
            <span class="grv-battle-winner">🏆 ${esc(b.winner === 'Draw' || b.winner === 'None' ? 'Ничья' : b.winner)}</span>
            ${replayBtn}
          </div>
        </div>
      `;
    }).join('');

    return `
      <div class="grv-turn">
        <div class="grv-turn-head">
          <span class="grv-turn-no">Ход ${t.turn}</span>
          ${date ? `<span class="grv-turn-date">${esc(date)}</span>` : ''}
          <span class="grv-turn-count">${t.battles.length} сражений</span>
        </div>
        <div class="grv-turn-battles">${cards}</div>
      </div>
    `;
  }

  private openVisualizer(turn: number, battle: BattleRecordDetail): void {
    this.closeVisualizer();
    this.activeVisualizer = new BattleVisualizer(battle, this.playerColorMap);

    const overlay = this.el.querySelector<HTMLElement>('#grv-overlay')!;
    const body    = this.el.querySelector<HTMLElement>('#grv-modal-body')!;
    const title   = this.el.querySelector<HTMLElement>('#grv-modal-title')!;

    title.textContent = `Ход ${turn} · ${battle.planetName}`;
    body.innerHTML    = '';
    body.appendChild(this.activeVisualizer.element);
    overlay.classList.remove('hidden');

    // Wire auto-play callback: advance to next when done
    this.activeVisualizer.onPlaybackEnd = () => this.autoAdvance();
  }

  private closeVisualizer(): void {
    this.activeVisualizer?.destroy();
    this.activeVisualizer = null;
    this.el.querySelector('#grv-overlay')?.classList.add('hidden');
    const body = this.el.querySelector('#grv-modal-body');
    if (body) body.innerHTML = '';
  }

  // ---- Auto-play all battles ----

  private startAutoPlay(): void {
    this.autoPlayQueue = [];
    for (const t of this.turns) {
      for (const b of t.battles) {
        if (b.protocol.length > 0) {
          this.autoPlayQueue.push({ turn: t.turn, battle: b });
        }
      }
    }
    this.autoPlayIndex = 0;
    this.openAndPlayCurrent();
  }

  private openAndPlayCurrent(): void {
    if (this.autoPlayIndex >= this.autoPlayQueue.length) {
      // All done
      return;
    }
    const item = this.autoPlayQueue[this.autoPlayIndex]!;
    this.openVisualizer(item.turn, item.battle);
    this.activeVisualizer!.setSpeed(20);
    this.activeVisualizer!.play();
  }

  private autoAdvance(): void {
    this.autoPlayIndex++;
    if (this.autoPlayIndex < this.autoPlayQueue.length) {
      // Brief pause before next battle, then open
      window.setTimeout(() => this.openAndPlayCurrent(), 800);
    } else {
      // Reached last battle — keep visualizer open so user can review
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
