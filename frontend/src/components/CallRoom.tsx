import '@livekit/components-styles'
import { LiveKitRoom, VideoConference, RoomAudioRenderer } from '@livekit/components-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { JoinToken } from '../api/calls'
import { USE_MOCKS } from '../api/config'
import CallRoomMock from './CallRoomMock'
import type { JoinPrefs } from './PreJoin'

// Видеокомната на LiveKit. Токен и адрес сервера приходят из POST /sessions/{id}/join-token (см. pages/Call).
// При подключении к реальному серверу url — адрес LiveKit (wss://…), token — одноразовый JWT.
export default function CallRoom({ data, prefs, onLeave }: { data: JoinToken; prefs: JoinPrefs; onLeave: () => void }) {
  const { t } = useTranslation()
  const [notice, setNotice] = useState<string | null>(null)
  if (USE_MOCKS) return <CallRoomMock data={data} onLeave={onLeave} />
  return (
    <div className="flex flex-1 flex-col gap-3">
      {notice && <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">{notice}</div>}
      <div className="flex-1 overflow-hidden rounded-3xl border border-ink-line bg-[#17143d]" data-lk-theme="default" style={{ minHeight: 420 }}>
        <LiveKitRoom
          serverUrl={data.url}
          token={data.token}
          connect
          video={prefs.video}
          audio={prefs.audio}
          options={{ adaptiveStream: true, dynacast: true }}
          onDisconnected={onLeave}
          onMediaDeviceFailure={() => setNotice(t('call.deviceFailure'))}
          onError={() => setNotice(t('call.connectionError'))}
          style={{ height: '100%' }}
        >
          <VideoConference />
          <RoomAudioRenderer />
        </LiveKitRoom>
      </div>
    </div>
  )
}
