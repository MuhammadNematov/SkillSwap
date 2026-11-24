// Обновление access-токена (POST /auth/refresh), которое не разлогинивает по пустякам и не множит запросы.
//
// Правила:
//  1. Разлогинивает только ответ сервера 401 или 403 (сеанс недействителен): бросается RefreshFatalError.
//     429 (лимит), сетевой сбой и 5xx не повод выходить: ждём Retry-After (или нарастающую паузу 1, 2, 4, 8 с) и повторяем.
//     Если и после повторов не вышло, наружу уходит исходная ошибка, а не RefreshFatalError: сеанс остаётся, пользователь не вышел.
//  2. Одновременные обновления объединяются в одно (single-flight) внутри вкладки.
//  3. Между вкладками обновление идёт под общей блокировкой (Web Locks), а результат рассылается по BroadcastChannel: refresh-токен ротируется,
//     и два одновременных запроса с одним cookie выглядели бы как повторное использование (сервер отозвал бы весь сеанс). Вкладка, которая получила
//     блокировку после соседней, берёт её свежий результат (если ему меньше REUSE_MS), а не шлёт ещё один запрос.
// Зависимости передаются снаружи, чтобы логика проверялась тестами без браузера (refreshCoordinator.test.ts).

export class RefreshFatalError extends Error {
  readonly status: number
  constructor(status: number) {
    super(`Сеанс недействителен (${status})`)
    this.name = 'RefreshFatalError'
    this.status = status
  }
}

export interface RefreshFailure {
  status?: number // HTTP-статус ответа; нет, если ответа не было (сеть)
  retryAfterMs?: number // из заголовка Retry-After
}

export interface LockManagerLike {
  request<R>(name: string, callback: () => Promise<R>): Promise<R>
}

export interface ChannelLike {
  postMessage(message: unknown): void
  onmessage: ((event: { data: unknown }) => void) | null
}

export interface RefreshDeps<T> {
  request: () => Promise<T>
  classify: (error: unknown) => RefreshFailure
  sleep?: (ms: number) => Promise<void>
  now?: () => number
  locks?: LockManagerLike | null
  channel?: ChannelLike | null
}

const LOCK_NAME = 'skillswap-auth-refresh'
const MAX_RETRIES = 4 // после первой попытки; без Retry-After паузы 1, 2, 4, 8 с
const MAX_WAIT_MS = 65_000 // лимит refresh на сервере: окно 60 с, дольше ждать незачем
const REUSE_MS = 10_000

export function createRefreshCoordinator<T>(deps: RefreshDeps<T>) {
  const sleep = deps.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)))
  const now = deps.now ?? Date.now
  let inflight: Promise<T> | null = null
  let fromOtherTab: { value: T; at: number } | null = null // результат соседней вкладки; свой не переиспользуется

  if (deps.channel) {
    deps.channel.onmessage = (event) => {
      const data = event.data as { type?: string; value?: T } | null
      if (data?.type === 'refreshed' && data.value !== undefined) fromOtherTab = { value: data.value, at: now() }
    }
  }

  async function attempt(): Promise<T> {
    for (let i = 0; ; i++) {
      try {
        return await deps.request()
      } catch (error) {
        const failure = deps.classify(error)
        if (failure.status === 401 || failure.status === 403) throw new RefreshFatalError(failure.status)
        if (i >= MAX_RETRIES) throw error
        const wait = failure.retryAfterMs !== undefined ? Math.min(Math.max(failure.retryAfterMs, 1000), MAX_WAIT_MS) : 1000 * 2 ** i
        await sleep(wait)
      }
    }
  }

  async function run(): Promise<T> {
    const body = async () => {
      if (fromOtherTab && now() - fromOtherTab.at < REUSE_MS) return fromOtherTab.value // соседняя вкладка только что обновила токен
      const value = await attempt()
      deps.channel?.postMessage({ type: 'refreshed', value })
      return value
    }
    return deps.locks ? deps.locks.request(LOCK_NAME, body) : body()
  }

  return {
    refresh(): Promise<T> {
      inflight ??= run().finally(() => {
        inflight = null
      })
      return inflight
    },
  }
}
