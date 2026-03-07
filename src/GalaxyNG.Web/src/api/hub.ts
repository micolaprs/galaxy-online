import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';

let _conn: HubConnection | null = null;

export function getHubConnection(): HubConnection {
  if (!_conn) {
    _conn = new HubConnectionBuilder()
      .withUrl('/hubs/game')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return _conn;
}

export async function ensureConnected(): Promise<HubConnection> {
  const conn = getHubConnection();
  if (conn.state === HubConnectionState.Disconnected) {
    await conn.start();
  }
  return conn;
}
