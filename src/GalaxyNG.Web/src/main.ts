import './style.css';
import { Lobby } from './components/Lobby.js';
import { GameView } from './components/GameView.js';
import { loadSession, saveSession, sessionFromUrl } from './api/session.js';
import type { Session } from './types/api.js';

const app = document.getElementById('app')!;
let currentView: GameView | null = null;

function enterGame(session: Session, gameId: string): void {
  currentView?.destroy();
  saveSession(session);
  // Update URL without reload
  history.pushState({}, '', `/?game=${gameId}&race=${encodeURIComponent(session.raceName)}`);
  app.innerHTML = '';
  currentView = new GameView(app, gameId, session);
}

function showLobby(): void {
  currentView?.destroy();
  currentView = null;
  app.innerHTML = '';
  const lobby  = new Lobby(app);
  lobby.onJoined = (session, gameId) => enterGame(session, gameId);
}

// Boot
function init(): void {
  // Try URL params first (join link)
  const urlSession = sessionFromUrl();
  if (urlSession) {
    saveSession(urlSession);
    enterGame(urlSession, urlSession.gameId);
    return;
  }

  // Try saved session
  const saved = loadSession();
  if (saved) {
    enterGame(saved, saved.gameId);
    return;
  }

  showLobby();
}

init();
