import '@livekit/components-styles'
import { LiveKitRoom, VideoConference, RoomAudioRenderer } from '@livekit/components-react'
import type { JoinToken } from '../api/calls'
import { USE_MOCKS } from '../api/config'
import CallRoomMock from './CallRoomMock'

// Видеокомната на LiveKit. Токен и адрес сервера приходят из POST /sessions/{id}/join-token (см. pages/Call).
// При подключении к реальному серверу url — адрес LiveKit (wss://…), token — одноразовый JWT.
export default function CallRoom({ data, onLeave }: { data: JoinToken; onLeave: () => void }) {
  if (USE_MOCKS) return <CallRoomMock data={data} onLeave={onLeave} />
  return (
    <div className="flex-1 overflow-hidden rounded-3xl border border-ink-line bg-[#17143d]" data-lk-theme="default" style={{ minHeight: 420 }}>
      <LiveKitRoom serverUrl={data.url} token={data.token} connect video audio onDisconnected={onLeave} style={{ height: '100%' }}>
        <VideoConference />
        <RoomAudioRenderer />
      </LiveKitRoom>
    </div>
  )
}
