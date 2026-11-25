import { VideoTrack, isTrackReference, useIsMuted, useIsSpeaking, type TrackReferenceOrPlaceholder } from '@livekit/components-react'
import { Track, type Participant } from 'livekit-client'
import { useTranslation } from 'react-i18next'
import Icon from '../Icon'

// Собеседник крупно: видео целиком (object-fit: contain) на тёмном фоне, чтобы лицо не обрезалось; при выключенной камере — буква и имя.
// Рамка подсвечивается, пока собеседник говорит; рядом с именем значок выключенного микрофона.
export default function RemoteView({
  participant,
  video,
  labelRight = false,
}: {
  participant: Participant | undefined
  video: TrackReferenceOrPlaceholder | undefined
  labelRight?: boolean // имя справа, когда своё окно стоит в левом верхнем углу
}) {
  const { t } = useTranslation()
  if (!participant) {
    return (
      <div className="absolute inset-0 grid place-items-center" data-testid="remote-waiting">
        <div className="flex flex-col items-center gap-3 text-white/80">
          <span className="h-3 w-3 animate-ping rounded-full bg-emerald-400" aria-hidden />
          <p className="text-sm">{t('call.room.partnerWaiting')}</p>
        </div>
      </div>
    )
  }
  return <RemoteParticipant participant={participant} video={video} labelRight={labelRight} />
}

function RemoteParticipant({ participant, video, labelRight }: { participant: Participant; video: TrackReferenceOrPlaceholder | undefined; labelRight: boolean }) {
  const { t } = useTranslation()
  const speaking = useIsSpeaking(participant)
  const micMuted = useIsMuted({ participant, source: Track.Source.Microphone })
  const name = participant.name || participant.identity
  const hasVideo = !!video && isTrackReference(video) && !video.publication.isMuted && !!video.publication.track

  return (
    <div className="absolute inset-0" data-testid="remote-view" data-speaking={speaking}>
      {hasVideo ? (
        <VideoTrack trackRef={video} className="h-full w-full bg-black object-contain" data-testid="remote-video" />
      ) : (
        <div className="grid h-full w-full place-items-center bg-[#15122b]">
          <div className="flex flex-col items-center gap-3">
            <span className="grid h-24 w-24 place-items-center rounded-full bg-brand-600 text-4xl font-bold text-white sm:h-32 sm:w-32 sm:text-5xl">
              {name.trim().charAt(0).toUpperCase() || '?'}
            </span>
            <span className="text-sm text-white/80">{t('call.room.camIsOff')}</span>
          </div>
        </div>
      )}
      {/* Подсветка говорящего: рамка поверх видео, не сдвигает раскладку */}
      {/* Имя наверху слева: внизу панель кнопок, справа сверху на телефоне своё окно */}
      <div className={`pointer-events-none absolute inset-0 rounded-[inherit] transition ${speaking ? 'shadow-[inset_0_0_0_3px_rgb(52_211_153)]' : ''}`} aria-hidden />
      <div data-testid="remote-name" className={`absolute top-3 z-10 flex max-w-[60%] items-center ${labelRight ? 'right-3' : 'left-3'}`} data-side={labelRight ? 'right' : 'left'}><div className="flex min-w-0 items-center gap-1.5 rounded-lg bg-black/55 px-2.5 py-1 text-sm font-medium text-white backdrop-blur">
        <span className="truncate">{name}</span>
        {micMuted && (
          <span title={t('call.room.micMuted')} aria-label={t('call.room.micMuted')} data-testid="remote-mic-muted">
            <Icon name="micOff" className="h-4 w-4 text-red-300" />
          </span>
        )}
      </div>
      </div>
    </div>
  )
}
