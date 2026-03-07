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

// Spectate (public, no auth)
export interface SpectateData {
  id: string;
  name: string;
  turn: number;
  galaxySize: number;
  lastTurnRunAt: string | null;
  autoRunOnAllSubmitted: boolean;
  players: SpectatePlayer[];
  planets: SpectatePlanet[];
  battles: SpectateBattle[];
  bombings: SpectateBombing[];
}
export interface SpectatePlayer {
  id: string; name: string; isBot: boolean;
  submitted: boolean; isEliminated: boolean;
  tech: TechLevels; planetCount: number;
}
export interface SpectatePlanet {
  name: string; x: number; y: number;
  size: number; ownerId: string | null; population: number;
}
export interface SpectateBattle  { planetName: string; winner: string; participants: string[]; }
export interface SpectateBombing { planetName: string; attackerRace: string; previousOwner: string | null; }

// Bot real-time status (via SignalR)
export interface BotStatusEvent {
  raceName: string;
  status: 'idle' | 'waiting' | 'reading-report' | 'thinking' | 'validating' | 'submitting' | 'submitted' | 'error' | string;
  detail: string | null;
  thinking: string | null;
  time: string;
}

// Server log entry (via SignalR)
export interface LogEntry {
  level: 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical' | string;
  category: string;
  message: string;
  time: string;
}

// Planet detail (public spectate, no auth)
export interface PlanetDetail {
  name: string; x: number; y: number; size: number; resources: number;
  population: number; industry: number; ownerId: string | null; ownerName: string | null;
  isHome: boolean; production: number; producing: string; shipTypeName: string | null;
  stockpiles: { capital: number; materials: number; colonists: number };
  groups: Array<{ ships: number; shipTypeName: string; ownerName: string; ownerId: string }>;
}

// Turn history
export interface TurnHistoryEntry {
  turn: number; runAt: string;
  players: string[]; battleCount: number; bombingCount: number;
  battles: string[]; bombings: string[];
}

// Player's orders for a turn
export interface TurnPlayerOrders {
  turn: number; race: string; orders: string; reasoning: string;
  battles: string[]; bombings: string[];
}

// AI summary entry
export interface AiSummaryEntry {
  turn: number; summary: string; generatedAt: string;
}

// Stored in localStorage
export interface Session {
  gameId: string;
  raceName: string;
  password: string;
  serverUrl: string;
}
