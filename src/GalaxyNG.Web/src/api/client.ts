import type { CreateGameResponse, GameDetail, GameSummary, Session, SpectateData } from '../types/api.js';

export class ApiClient {
  constructor(private readonly base: string = '') {}

  // ---- Games ----

  async listGames(): Promise<GameSummary[]> {
    return this.get('/api/games');
  }

  async deleteAllGames(): Promise<void> {
    const r = await fetch(this.base + '/api/games', { method: 'DELETE' });
    if (!r.ok) throw new Error(`DELETE /api/games failed: ${r.status}`);
  }

  async getGame(id: string): Promise<GameDetail> {
    return this.get(`/api/games/${id}`);
  }

  async spectate(id: string): Promise<SpectateData> {
    return this.get(`/api/games/${id}/spectate`);
  }

  async createGame(body: {
    name: string;
    players: Array<{ name: string; password: string; isBot: boolean }>;
    galaxySize?: number;
    autoRun?: boolean;
  }): Promise<CreateGameResponse> {
    return this.post('/api/games', body);
  }

  // ---- Orders ----

  async submitOrders(gameId: string, session: Session, orders: string, final: boolean): Promise<void> {
    await this.post(`/api/games/${gameId}/orders`, {
      raceName: session.raceName,
      password: session.password,
      orders,
      final,
    });
  }

  // ---- Reports ----

  async getReport(gameId: string, session: Session): Promise<string> {
    const r = await fetch(
      `${this.base}/api/games/${gameId}/report/${encodeURIComponent(session.raceName)}?password=${encodeURIComponent(session.password)}`
    );
    if (!r.ok) throw new Error(`Report fetch failed: ${r.status}`);
    return r.text();
  }

  async getForecast(gameId: string, session: Session, orders: string): Promise<string> {
    const r = await fetch(
      `${this.base}/api/games/${gameId}/forecast/${encodeURIComponent(session.raceName)}` +
      `?password=${encodeURIComponent(session.password)}&orders=${encodeURIComponent(orders)}`
    );
    if (!r.ok) throw new Error(`Forecast fetch failed: ${r.status}`);
    return r.text();
  }

  // ---- Turn ----

  async runTurn(gameId: string): Promise<void> {
    await this.post(`/api/games/${gameId}/run-turn`, {});
  }

  // ---- Helpers ----

  private async get<T>(path: string): Promise<T> {
    const r = await fetch(this.base + path);
    if (!r.ok) throw new Error(`GET ${path} failed: ${r.status}`);
    return r.json() as Promise<T>;
  }

  private async post<T>(path: string, body: unknown): Promise<T> {
    const r = await fetch(this.base + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) {
      const err = await r.text().catch(() => r.statusText);
      throw new Error(`POST ${path} failed: ${r.status} — ${err}`);
    }
    // 201/204 might have no body
    if (r.status === 204) return undefined as T;
    return r.json() as Promise<T>;
  }
}

export const api = new ApiClient();
