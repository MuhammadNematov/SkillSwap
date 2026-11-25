import { LiveKitRoom, RoomAudioRenderer, StartAudio } from '@livekit/components-react'
import { DisconnectReason } from 'livekit-client'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { JoinToken } from '../api/calls'
import { USE_MOCKS } from '../api/config'
import { liveKitServerUrl } from '../utils/livekitUrl'
import CallStage, { type CallChrome } from './call/CallStage'
import CallRoomMock from './CallRoomMock'
import Icon from './Icon'
import type { JoinPrefs } from './PreJoin'

type Failure = 'connect' | 'lost'

// Видеокомната на LiveKit в режиме фокуса: на весь видимый экран (100dvh) поверх сайта, боковое меню и шапка скрыты, страница не прокручивается.
// Токен приходит из POST /sessions/{id}/join-token (см. pages/Call), адрес сервера: liveKitServerUrl (в разработке от адреса страницы).
// Сам вышел (кнопка «Выйти») — onLeave, страница предложит вернуться. Не удалось подключиться или связь оборвалась — ошибка и «Повторить» (новый токен).
export default function CallRoom({
  data,
  prefs,
  chrome,
  onLeave,
  onRetry,
}: {
  data: JoinToken
  prefs: JoinPrefs
  chrome: CallChrome
  onLeave: () => void
  onRetry: () => Promise<unknown>
}) {
  const { t } = useTranslation()
  const [notice, setNotice] = useState<string | null>(null)
  const [failure, setFailure] = useState<Failure | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [retrying, setRetrying] = useState(false)
  const connected = useRef(false)

  // Режим фокуса: страница под звонком не прокручивается
  useEffect(() => {
    // И html, и body: в iOS Safari одного body недостаточно
    const html = document.documentElement.style.overflow
    const body = document.body.style.overflow
    window.scrollTo(0, 0)
    document.documentElement.style.overflow = 'hidden'
    document.body.style.overflow = 'hidden'
    return () => {
      document.documentElement.style.overflow = html
      document.body.style.overflow = body
    }
  }, [])

  if (USE_MOCKS) return <CallRoomMock data={data} onLeave={onLeave} />

  async function retry() {
    setRetrying(true)
    try {
      await onRetry()
      connected.current = false
      setNotice(null)
      setFailure(null)
      setAttempt((a) => a + 1)
    } finally {
      setRetrying(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex h-[100dvh] flex-col bg-[#0f0d1e] text-white" data-testid="call-screen">
      {failure ? (
        <div className="grid flex-1 place-items-center p-4">
          <div className="w-full max-w-md space-y-4 rounded-2xl bg-white/5 p-6 text-center ring-1 ring-white/10" role="alert" data-testid="call-failure">
            <span className="mx-auto grid h-12 w-12 place-items-center rounded-full bg-red-500/15 text-red-300">
              <Icon name="videoOff" className="h-6 w-6" />
            </span>
            <p className="font-semibold">{failure === 'connect' ? t('call.connectFailed') : t('call.connectionLost')}</p>
            <p className="text-sm text-white/70">{t('call.connectFailedHint')}</p>
            <div className="flex flex-wrap justify-center gap-3">
              <button className="btn-primary" disabled={retrying} onClick={retry}>
                {retrying ? t('common.loading') : t('call.retry')}
              </button>
              <button className="inline-flex items-center justify-center rounded-lg border border-white/20 px-5 py-2.5 text-sm font-semibold hover:bg-white/10" onClick={onLeave}>
                {t('call.leave')}
              </button>
            </div>
          </div>
        </div>
      ) : (
        <LiveKitRoom
          key={attempt}
          serverUrl={liveKitServerUrl(data.url)}
          token={data.token}
          connect
          video={prefs.video}
          audio={prefs.audio}
          options={{ adaptiveStream: true, dynacast: true }}
          onConnected={() => {
            connected.current = true
          }}
          onDisconnected={(reason) => {
            // Вышел сам — экран «Вы вышли»; всё остальное (не подключились, сервер недоступен, обрыв) — ошибка с повтором
            if (reason === DisconnectReason.CLIENT_INITIATED) onLeave()
            else setFailure(connected.current ? 'lost' : 'connect')
          }}
          onMediaDeviceFailure={() => setNotice(t('call.deviceFailure'))}
          onError={() => {
            if (!connected.current) setFailure('connect')
            else setNotice(t('call.connectionError'))
          }}
          style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}
        >
          <CallStage chrome={{ ...chrome, warning: notice ?? chrome.warning }} />
          <RoomAudioRenderer />
          {/* Браузер может запретить звук без жеста пользователя: тогда появляется кнопка включения звука */}
          <StartAudio label={t('call.room.startAudio')} className="fixed left-1/2 top-16 z-[60] -translate-x-1/2 rounded-full bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-lg" />
        </LiveKitRoom>
      )}
    </div>
  )
}
