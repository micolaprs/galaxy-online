import './style.css';
import { Lobby }          from './components/Lobby.js';
import { GameView }       from './components/GameView.js';
import { GameList }       from './components/GameList.js';
import { WatchView }      from './components/WatchView.js';
import { GameReplayView } from './components/GameReplayView.js';
import { QuakeConsole }   from './components/QuakeConsole.js';
import { loadSession, saveSession, sessionFromUrl } from './api/session.js';
import type { Session } from './types/api.js';

const app = document.getElementById('app')!;
let currentView: { destroy(): void } | null = null;

function mount(view: { destroy(): void }): void {
  currentView?.destroy();
  currentView = view;
}

function showGameList(): void {
  app.innerHTML = '';
  const list = new GameList(app);
  list.onWatch   = (id)   => showWatchView(id);
  list.onNewGame = ()     => showLobby();
  list.onReplay  = (data) => showReplayView(data);
  mount(list);
  history.pushState({}, '', '/');
}

function showReplayView(rawData: unknown): void {
  app.innerHTML = '';
  const rv = new GameReplayView(app, rawData);
  rv.onBack = () => showGameList();
  mount(rv);
  history.pushState({}, '', '/');
}

function showLobby(): void {
  app.innerHTML = '';
  const lobby = new Lobby(app);
  lobby.onJoined = (session, gameId) => enterGame(session, gameId);
  lobby.onBack   = () => showGameList();
  mount(lobby);
}

function showWatchView(gameId: string): void {
  app.innerHTML = '';
  const wv = new WatchView(app, gameId);
  wv.onBack = () => showGameList();
  mount(wv);
  history.pushState({}, '', `/?watch=${gameId}`);
}

function enterGame(session: Session, gameId: string): void {
  saveSession(session);
  app.innerHTML = '';
  const gv = new GameView(app, gameId, session);
  (gv as any).onBack = () => showGameList();
  mount(gv);
  history.pushState({}, '', `/?game=${gameId}&race=${encodeURIComponent(session.raceName)}`);
}

// Boot
function init(): void {
  const params = new URLSearchParams(location.search);

  // ?watch=GAMEID — go straight to observer
  const watchId = params.get('watch');
  if (watchId) { showWatchView(watchId); return; }

  // ?game=... — try to resume session or go to game view
  const urlSession = sessionFromUrl();
  if (urlSession) {
    saveSession(urlSession);
    enterGame(urlSession, urlSession.gameId);
    return;
  }

  const saved = loadSession();
  if (saved && params.get('game')) {
    enterGame(saved, saved.gameId);
    return;
  }

  // Default: game list
  showGameList();
}

// Global Quake-style console — always available via ~ key
new QuakeConsole();

init();
