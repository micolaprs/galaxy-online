// ---- REST API shapes ----

export interface GameSummary {
  id: string;
  name: string;
  turn: number;
  playerCount: number;
  lastTurnRunAt: string | null;
}

export interface GameDetail {
  id: string;
  name: string;
  turn: number;
  galaxySize: number;
  players: PlayerSummary[];
  planetCount: number;
}

export interface PlayerSummary {
  name: string;
  tech: TechLevels;
  isBot: boolean;
  isEliminated: boolean;
  submitted: boolean;
}

export interface TechLevels {
  drive: number;
  weapons: number;
  shields: number;
  cargo: number;
}

export interface CreateGameResponse {
  gameId: string;
  joinLink: string;
  players: { id: string; name: string; password: string }[];
  turn: number;
}

export interface JoinedPlayer {
  id: string;
  name: string;
  password: string;
}

// Stored in localStorage
export interface Session {
  gameId: string;
  raceName: string;
  password: string;
  serverUrl: string;
}
