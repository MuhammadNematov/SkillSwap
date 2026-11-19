import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { getAccessToken } from './client'
import { API_URL, USE_MOCKS } from './config'

// Хабы лежат в корне сервера (/hubs/...), а не под /api/v1.
function hubUrl(path: string): string {
  try {
    return new URL(API_URL).origin + path
  } catch {
    return path // относительный API_URL: тот же origin (прокси)
  }
}

export function createHub(path: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(hubUrl(path), { accessTokenFactory: () => getAccessToken() ?? '' })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build()
}

export async function startHub(conn: HubConnection): Promise<boolean> {
  if (USE_MOCKS || !getAccessToken()) return false
  if (conn.state !== HubConnectionState.Disconnected) return conn.state === HubConnectionState.Connected
  try {
    await conn.start()
    return true
  } catch {
    return false // опрос остаётся запасным вариантом
  }
}

// ───────── Общее соединение чата (одно на приложение) ─────────
let chatHub: HubConnection | null = null
export function getChatHub(): HubConnection {
  chatHub ??= createHub('/hubs/chat')
  return chatHub
}
