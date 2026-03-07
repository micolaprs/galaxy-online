import type { Session } from '../types/api.js';

const KEY = 'galaxyng_session';

export function saveSession(s: Session): void {
  localStorage.setItem(KEY, JSON.stringify(s));
}

export function loadSession(): Session | null {
  const raw = localStorage.getItem(KEY);
  return raw ? (JSON.parse(raw) as Session) : null;
}

export function clearSession(): void {
  localStorage.removeItem(KEY);
}

export function sessionFromUrl(): Session | null {
  const p = new URLSearchParams(location.search);
  const gameId = p.get('game');
  const race   = p.get('race');
  const pw     = p.get('pw');
  if (gameId && race && pw) return { gameId, raceName: race, password: pw, serverUrl: location.origin };
  return null;
}
