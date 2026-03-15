// ---- REST API shapes ----

export interface GameSummary {
  id: string;
  name: string;
  turn: number;
  playerCount: number;
  lastTurnRunAt: string | null;
  maxTurns?: number;
  isFinished?: boolean;
  winnerName?: string | null;
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
  maxTurns?: number;
  isFinished?: boolean;
  winnerPlayerId?: string | null;
  winnerName?: string | null;
  finishReason?: string | null;
  galaxySize: number;
  lastTurnRunAt: string | null;
  autoRunOnAllSubmitted: boolean;
  players: SpectatePlayer[];
  planets: SpectatePlanet[];
  battles: SpectateBattle[];
  bombings: SpectateBombing[];
  fleetRoutes: SpectateFleetRoute[];
  diplomacy: SpectateDiplomacy;
}

export interface SpectateDiplomacy {
  globalMessages: SpectateChatMessage[];
  privateChats: SpectatePrivateChat[];
}

export interface SpectateChatMessage {
  id: string;
  turn: number;
  sentAt: string;
  senderId: string;
  senderName: string;
  text: string;
}

export interface SpectatePrivateChat {
  channelId: string;
  playerAId: string;
  playerAName: string;
  playerBId: string;
  playerBName: string;
  overlapPlanets: string[];
  messages: SpectateChatMessage[];
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
export interface BattleShot { attackerRace: string; defenderRace: string; killed: boolean; }
export interface SpectateBattle  {
  planetName: string; winner: string; participants: string[];
  protocol?: BattleShot[];
  initialShips?: Record<string, number>;
}
export interface SpectateBombing {
  planetName: string;
  attackerRace: string;
  previousOwner: string | null;
  oldPopulation?: number;
  oldIndustry?: number;
}
export interface BattleRecordDetail {
  planetName: string; winner: string; participants: string[];
  protocol: BattleShot[];
  initialShips: Record<string, number>;
}
export interface BattleSummary {
  planetName: string; winner: string; participants: string[];
  initialShips: Record<string, number>;
  shotCount: number;
}
export interface SpectateFleetRoute {
  ownerId: string;
  fleetName: string;
  origin: string;
  destination: string;
  ships: number;
  active?: boolean;
  speed?: number;
  progress?: number;
}

export interface FinalRaceResult {
  playerId: string;
  race: string;
  isWinner: boolean;
  isEliminated: boolean;
  planets: number;
  population: number;
  industry: number;
  ships: number;
  techTotal: number;
  achievements: string[];
}

export interface FinalGameReport {
  gameId: string;
  gameName: string;
  finishedTurn: number;
  maxTurns: number;
  winnerPlayerId?: string | null;
  winnerName?: string | null;
  finishReason?: string | null;
  races: FinalRaceResult[];
  timeline: string[];
}

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
  battleSummaries?: BattleSummary[];
}

// Player's orders for a turn
export interface TurnPlayerOrders {
  turn: number; race: string; orders: string; reasoning: string; summary?: string;
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
