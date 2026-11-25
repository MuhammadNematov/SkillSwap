import assert from 'node:assert/strict'
import { test } from 'node:test'
import { liveKitServerUrl } from './livekitUrl.ts'

test('в разработке адрес строится из адреса страницы: localhost, адрес в сети, http', () => {
  assert.equal(liveKitServerUrl('wss://192.168.0.63:5173/livekit', { protocol: 'https:', host: 'localhost:5173' }, true), 'wss://localhost:5173/livekit')
  assert.equal(liveKitServerUrl('wss://172.20.10.3:5173/livekit', { protocol: 'https:', host: '192.168.0.220:5173' }, true), 'wss://192.168.0.220:5173/livekit')
  assert.equal(liveKitServerUrl('ws://localhost:7880', { protocol: 'http:', host: 'localhost:5173' }, true), 'ws://localhost:5173/livekit')
})

test('вне разработки адрес берётся из ответа бэкенда как есть', () => {
  assert.equal(liveKitServerUrl('wss://livekit.example.com', { protocol: 'https:', host: 'skillswap.example.com' }, false), 'wss://livekit.example.com')
})
