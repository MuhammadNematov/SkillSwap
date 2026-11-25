import '@livekit/components-styles'
import { LiveKitRoom, VideoConference, RoomAudioRenderer } from '@livekit/components-react'
import { DisconnectReason } from 'livekit-client'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { JoinToken } from '../api/calls'
import { USE_MOCKS } from '../api/config'
import { liveKitServerUrl } from '../utils/livekitUrl'
import CallRoomMock from './CallRoomMock'
import Icon from './Icon'
import type { JoinPrefs } from './PreJoin'

type Failure = 'connect' | 'lost'

// Видеокомната на LiveKit. Токен приходит из POST /sessions/{id}/join-token (см. pages/Call), адрес сервера: liveKitServerUrl (в разработке от адреса страницы).
// Сам вышел (кнопка «Выйти») — onLeave, страница предложит вернуться. Не удалось подключиться или связь оборвалась — не выкидываем молча, а показываем
// ошибку и кнопку «Повторить»: она берёт новый токен (onRetry) и подключается заново.
export default function CallRoom({
  data,
  prefs,
  onLeave,
  onRetry,
}: {
  data: JoinToken
  prefs: JoinPrefs
  onLeave: () => void
  onRetry: () => Promise<unknown>
}) {
  const { t } = useTranslation()
  const [notice, setNotice] = useState<string | null>(null)
  const [failure, setFailure] = useState<Failure | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [retrying, setRetrying] = useState(false)
  const connected = useRef(false)
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

  if (failure) {
    return (
      <div className="card mx-auto w-full max-w-md space-y-4 text-center" role="alert" data-testid="call-failure">
        <span className="mx-auto grid h-12 w-12 place-items-center rounded-full bg-red-50 text-red-700">
          <Icon name="video" className="h-6 w-6" />
        </span>
        <p className="font-semibold text-ink">{failure === 'connect' ? t('call.connectFailed') : t('call.connectionLost')}</p>
        <p className="text-sm text-ink-soft">{t('call.connectFailedHint')}</p>
        <div className="flex flex-wrap justify-center gap-3">
          <button className="btn-primary" disabled={retrying} onClick={retry}>
            {retrying ? t('common.loading') : t('call.retry')}
          </button>
          <button className="btn-secondary" onClick={onLeave}>
            {t('call.leave')}
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="flex flex-1 flex-col gap-3">
      {notice && <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">{notice}</div>}
      <div className="flex-1 overflow-hidden rounded-3xl border border-ink-line bg-[#17143d]" data-lk-theme="default" style={{ minHeight: 420 }}>
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
          style={{ height: '100%' }}
        >
          <VideoConference />
          <RoomAudioRenderer />
        </LiveKitRoom>
      </div>
    </div>
  )
}
