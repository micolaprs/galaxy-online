import { api } from '../api/client.js';
import { saveSession } from '../api/session.js';
import type { Session } from '../types/api.js';

export class Lobby {
  private el: HTMLElement;
  onJoined?: (session: Session, gameId: string) => void;
  onBack?:   () => void;
  destroy(): void {}

  constructor(container: HTMLElement) {
    this.el = container;
    this.render();
  }

  private render(): void {
    this.el.innerHTML = `
      <div class="lobby">
        <div class="lobby-nav">
          <button class="btn btn-sm btn-secondary" id="btn-lobby-back">← Games</button>
        </div>
        <h1 class="logo">🌌 GalaxyNG</h1>
        <p class="tagline">Simultaneous-turn interstellar strategy</p>

        <div class="lobby-tabs">
          <button class="tab active" data-tab="create">New Game</button>
          <button class="tab" data-tab="join">Join Game</button>
        </div>

        <div id="tab-create" class="tab-content">
          <label>Game name <input id="game-name" type="text" value="MyGame" /></label>
          <label>Your race <input id="race-name" type="text" value="Terrans" /></label>
          <label>Password  <input id="race-pw"   type="text" value="secret" /></label>
          <label>Galaxy size <input id="galaxy-size" type="number" value="200" min="50" max="500"/></label>
          <div class="bot-row">
            <label><input id="add-bot" type="checkbox" checked /> Add 1 LLM bot</label>
          </div>
          <button id="btn-create" class="btn btn-primary">Create Game</button>
          <div id="create-result" class="result-box hidden"></div>
        </div>

        <div id="tab-join" class="tab-content hidden">
          <label>Game ID   <input id="join-game-id" type="text" placeholder="ABCD1234" /></label>
          <label>Race name <input id="join-race"    type="text" placeholder="Terrans" /></label>
          <label>Password  <input id="join-pw"      type="text" placeholder="secret" /></label>
          <button id="btn-join" class="btn btn-primary">Join</button>
          <div id="join-error" class="error hidden"></div>
        </div>
      </div>
    `;

    // Tab switching
    this.el.querySelectorAll<HTMLButtonElement>('.tab').forEach(btn => {
      btn.addEventListener('click', () => {
        this.el.querySelectorAll('.tab').forEach(b => b.classList.remove('active'));
        this.el.querySelectorAll('.tab-content').forEach(c => c.classList.add('hidden'));
        btn.classList.add('active');
        this.el.querySelector(`#tab-${btn.dataset['tab']}`)!.classList.remove('hidden');
      });
    });

    this.el.querySelector('#btn-lobby-back')!.addEventListener('click', () => this.onBack?.());
    this.el.querySelector('#btn-create')!.addEventListener('click', () => this.createGame());
    this.el.querySelector('#btn-join')!.addEventListener('click', () => this.joinGame());

    // Pre-fill from URL ?game=
    const urlGame = new URLSearchParams(location.search).get('game');
    if (urlGame) {
      (this.el.querySelector('#join-game-id') as HTMLInputElement).value = urlGame;
      this.el.querySelector<HTMLButtonElement>('[data-tab="join"]')!.click();
    }
  }

  private async createGame(): Promise<void> {
    const name      = (this.el.querySelector('#game-name')  as HTMLInputElement).value.trim();
    const raceName  = (this.el.querySelector('#race-name')  as HTMLInputElement).value.trim();
    const password  = (this.el.querySelector('#race-pw')    as HTMLInputElement).value.trim();
    const galaxySize= Number((this.el.querySelector('#galaxy-size') as HTMLInputElement).value);
    const addBot    = (this.el.querySelector('#add-bot') as HTMLInputElement).checked;

    const players: Array<{ name: string; password: string; isBot: boolean }> = [
      { name: raceName, password, isBot: false },
    ];
    if (addBot) players.push({ name: 'BotRace', password: 'botpw', isBot: true });

    const btn = this.el.querySelector('#btn-create') as HTMLButtonElement;
    btn.disabled = true;
    btn.textContent = 'Creating…';

    try {
      const res     = await api.createGame({ name, players, galaxySize });
      const session : Session = { gameId: res.gameId, raceName, password, serverUrl: location.origin };
      saveSession(session);

      const joinUrl = `${location.origin}/?game=${res.gameId}`;
      const result  = this.el.querySelector('#create-result')!;
      result.classList.remove('hidden');
      result.innerHTML = `
        <p>✓ Game <strong>${res.gameId}</strong> created! Turn 0.</p>
        <p>Share this link with players:</p>
        <div class="join-link">
          <input type="text" readonly value="${joinUrl}" id="join-link-input"/>
          <button class="btn btn-sm" id="btn-copy">Copy</button>
        </div>
        <button class="btn btn-primary" id="btn-enter">Enter Game →</button>
      `;
      result.querySelector('#btn-copy')!.addEventListener('click', () => {
        navigator.clipboard.writeText(joinUrl);
        (result.querySelector('#btn-copy') as HTMLButtonElement).textContent = 'Copied!';
      });
      result.querySelector('#btn-enter')!.addEventListener('click', () => {
        this.onJoined?.(session, res.gameId);
      });
    } catch (e) {
      alert(`Failed to create game: ${e}`);
    } finally {
      btn.disabled = false;
      btn.textContent = 'Create Game';
    }
  }

  private async joinGame(): Promise<void> {
    const gameId   = (this.el.querySelector('#join-game-id') as HTMLInputElement).value.trim();
    const raceName = (this.el.querySelector('#join-race')    as HTMLInputElement).value.trim();
    const password = (this.el.querySelector('#join-pw')      as HTMLInputElement).value.trim();
    const errEl    = this.el.querySelector('#join-error')!;

    if (!gameId || !raceName || !password) {
      errEl.textContent = 'All fields required.';
      errEl.classList.remove('hidden');
      return;
    }

    try {
      // Verify the game exists
      await api.getGame(gameId);
      const session: Session = { gameId, raceName, password, serverUrl: location.origin };
      saveSession(session);
      this.onJoined?.(session, gameId);
    } catch {
      errEl.textContent = 'Game not found or auth failed.';
      errEl.classList.remove('hidden');
    }
  }
}
