import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createRefreshCoordinator, RefreshFatalError, type ChannelLike, type LockManagerLike, type RefreshFailure } from './refreshCoordinator.ts'

class HttpError extends Error {
  status?: number
  retryAfterMs?: number
  constructor(status?: number, retryAfterMs?: number) {
    super(`http ${status}`)
    this.status = status
    this.retryAfterMs = retryAfterMs
  }
}
const classify = (e: unknown): RefreshFailure => ({ status: (e as HttpError).status, retryAfterMs: (e as HttpError).retryAfterMs })
const noSleep = () => {
  const waits: number[] = []
  return { waits, sleep: async (ms: number) => void waits.push(ms) }
}

// Блокировка как Web Locks: один владелец за раз, остальные ждут в очереди (общая для «вкладок»)
function fakeLocks(): LockManagerLike {
  let tail: Promise<unknown> = Promise.resolve()
  return {
    request<R>(_name: string, callback: () => Promise<R>): Promise<R> {
      const run = tail.then(callback, callback)
      tail = run.catch(() => undefined)
      return run
    },
  }
}
// Общая шина как BroadcastChannel: сообщение получают все, кроме отправителя, асинхронно
function fakeChannels() {
  const all: FakeChannel[] = []
  class FakeChannel implements ChannelLike {
    onmessage: ((event: { data: unknown }) => void) | null = null
    constructor() {
      all.push(this)
    }
    postMessage(message: unknown) {
      for (const c of all) if (c !== this) queueMicrotask(() => c.onmessage?.({ data: message }))
    }
  }
  return () => new FakeChannel()
}

test('одновременные обновления в одной вкладке объединяются в один запрос', async () => {
  let calls = 0
  const c = createRefreshCoordinator({ request: async () => ({ token: ++calls }), classify })
  const results = await Promise.all([c.refresh(), c.refresh(), c.refresh(), c.refresh(), c.refresh()])
  assert.equal(calls, 1)
  assert.ok(results.every((r) => r.token === 1))
  assert.deepEqual(await c.refresh(), { token: 2 }, 'после завершения следующее обновление идёт отдельным запросом')
})

test('429: ждёт Retry-After и повторяет, не разлогинивает', async () => {
  const { waits, sleep } = noSleep()
  let calls = 0
  const c = createRefreshCoordinator({
    request: async () => {
      if (++calls === 1) throw new HttpError(429, 7000)
      return 'ok'
    },
    classify,
    sleep,
  })
  assert.equal(await c.refresh(), 'ok')
  assert.deepEqual(waits, [7000])
  assert.equal(calls, 2)
})

test('429 без Retry-After: нарастающая пауза 1, 2, 4 с', async () => {
  const { waits, sleep } = noSleep()
  let calls = 0
  const c = createRefreshCoordinator({
    request: async () => {
      if (++calls <= 3) throw new HttpError(429)
      return 'ok'
    },
    classify,
    sleep,
  })
  assert.equal(await c.refresh(), 'ok')
  assert.deepEqual(waits, [1000, 2000, 4000])
})

test('Retry-After ограничен сверху и снизу', async () => {
  const { waits, sleep } = noSleep()
  let calls = 0
  const c = createRefreshCoordinator({
    request: async () => {
      calls++
      if (calls === 1) throw new HttpError(429, 600_000)
      if (calls === 2) throw new HttpError(429, 0)
      return 'ok'
    },
    classify,
    sleep,
  })
  await c.refresh()
  assert.deepEqual(waits, [65_000, 1000])
})

test('429 держится долго: после повторов наружу уходит исходная ошибка, а не «сеанс недействителен»', async () => {
  const { sleep } = noSleep()
  let calls = 0
  const c = createRefreshCoordinator({
    request: async () => {
      calls++
      throw new HttpError(429, 1000)
    },
    classify,
    sleep,
  })
  await assert.rejects(c.refresh(), (e) => e instanceof HttpError && e.status === 429 && !(e instanceof RefreshFatalError))
  assert.equal(calls, 5, 'первая попытка и четыре повтора')
})

for (const status of [401, 403]) {
  test(`${status}: сеанс недействителен, сразу RefreshFatalError без повторов`, async () => {
    const { waits, sleep } = noSleep()
    let calls = 0
    const c = createRefreshCoordinator({
      request: async () => {
        calls++
        throw new HttpError(status)
      },
      classify,
      sleep,
    })
    await assert.rejects(c.refresh(), (e) => e instanceof RefreshFatalError && e.status === status)
    assert.equal(calls, 1)
    assert.deepEqual(waits, [])
  })
}

test('сетевой сбой и 5xx повторяются, а не разлогинивают', async () => {
  const { waits, sleep } = noSleep()
  let calls = 0
  const c = createRefreshCoordinator({
    request: async () => {
      calls++
      if (calls === 1) throw new HttpError(undefined)
      if (calls === 2) throw new HttpError(503)
      return 'ok'
    },
    classify,
    sleep,
  })
  assert.equal(await c.refresh(), 'ok')
  assert.deepEqual(waits, [1000, 2000])
})

test('после ошибки следующее обновление не залипает на старой', async () => {
  let fail = true
  const c = createRefreshCoordinator({
    request: async () => {
      if (fail) throw new HttpError(401)
      return 'ok'
    },
    classify,
  })
  await assert.rejects(c.refresh())
  fail = false
  assert.equal(await c.refresh(), 'ok')
})

test('две вкладки одновременно: под общей блокировкой уходит один запрос, вторая берёт результат первой', async () => {
  const locks = fakeLocks()
  const channel = fakeChannels()
  let calls = 0
  const request = async () => {
    await new Promise((r) => setTimeout(r, 5))
    return { token: ++calls }
  }
  const tabA = createRefreshCoordinator({ request, classify, locks, channel: channel() })
  const tabB = createRefreshCoordinator({ request, classify, locks, channel: channel() })
  const [a, b] = await Promise.all([tabA.refresh(), tabB.refresh()])
  assert.equal(calls, 1, 'одно обновление на обе вкладки: иначе повторное использование refresh-токена отозвало бы сеанс')
  assert.deepEqual(a, b)
})

test('вкладка B не берёт устаревший результат вкладки A (старше 10 с)', async () => {
  const locks = fakeLocks()
  const channel = fakeChannels()
  let clock = 0
  let calls = 0
  const request = async () => ({ token: ++calls })
  const tabA = createRefreshCoordinator({ request, classify, locks, channel: channel(), now: () => clock })
  const tabB = createRefreshCoordinator({ request, classify, locks, channel: channel(), now: () => clock })
  await tabA.refresh()
  await new Promise((r) => setTimeout(r, 0))
  clock = 11_000
  assert.deepEqual(await tabB.refresh(), { token: 2 })
})

test('429 у вкладки-лидера: вторая вкладка ждёт блокировку и не добавляет свои запросы', async () => {
  const locks = fakeLocks()
  const channel = fakeChannels()
  const { sleep } = noSleep()
  let calls = 0
  const request = async () => {
    calls++
    if (calls === 1) throw new HttpError(429, 3000)
    return { token: calls }
  }
  const tabA = createRefreshCoordinator({ request, classify, locks, channel: channel(), sleep })
  const tabB = createRefreshCoordinator({ request, classify, locks, channel: channel(), sleep })
  const [a, b] = await Promise.all([tabA.refresh(), tabB.refresh()])
  assert.equal(calls, 2, 'отказ и один повтор на обе вкладки')
  assert.deepEqual(a, b)
})

test('без Web Locks и BroadcastChannel работает в пределах вкладки', async () => {
  let calls = 0
  const c = createRefreshCoordinator({ request: async () => ++calls, classify, locks: null, channel: null })
  assert.equal(await c.refresh(), 1)
})
