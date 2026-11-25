import { useRoomContext, useTrackToggle } from '@livekit/components-react'
import { Track } from 'livekit-client'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import Icon, { type IconName } from '../Icon'

// Демонстрация экрана есть в браузерах ноутбука; на телефонах (iOS, большинство Android) getDisplayMedia нет — кнопку не показываем
const canShareScreen = () => typeof navigator !== 'undefined' && !!navigator.mediaDevices && 'getDisplayMedia' in navigator.mediaDevices && !/Android|iPhone|iPad|iPod/i.test(navigator.userAgent)

function RoundButton({
  label,
  icon,
  active,
  danger,
  onClick,
  disabled,
  badge,
  pressed,
  testId,
}: {
  label: string
  icon: IconName
  active: boolean // включено (микрофон, камера) — светлая кнопка; выключено — красная
  danger?: boolean
  onClick: () => void
  disabled?: boolean
  badge?: ReactNode
  pressed?: boolean
  testId?: string
}) {
  const tone = danger ? 'bg-red-600 text-white hover:bg-red-500' : active ? 'bg-white/15 text-white hover:bg-white/25' : 'bg-red-500 text-white hover:bg-red-400'
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      aria-pressed={pressed}
      data-testid={testId}
      disabled={disabled}
      onClick={onClick}
      className={`relative grid h-12 shrink-0 place-items-center rounded-full transition focus:outline-none focus-visible:ring-2 focus-visible:ring-white disabled:opacity-50 sm:h-14 ${danger ? 'w-16 sm:w-20' : 'w-12 sm:w-14'} ${tone}`}
    >
      <Icon name={icon} className="h-6 w-6" />
      {badge}
    </button>
  )
}

// Панель кнопок звонка по центру внизу: микрофон, камера, экран, чат (бейдж непрочитанных), выйти. Подсказки (title) и aria-pressed у каждой кнопки.
export default function ControlBar({ chatOpen, unread, onToggleChat }: { chatOpen: boolean; unread: number; onToggleChat: () => void }) {
  const { t } = useTranslation()
  const room = useRoomContext()
  const mic = useTrackToggle({ source: Track.Source.Microphone })
  const cam = useTrackToggle({ source: Track.Source.Camera })
  const screen = useTrackToggle({ source: Track.Source.ScreenShare })

  return (
    <div className="flex items-center justify-center gap-2 sm:gap-3" role="toolbar" aria-label={t('call.room.controls')}>
      <RoundButton testId="btn-mic" label={mic.enabled ? t('call.room.micOn') : t('call.room.micOff')} icon={mic.enabled ? 'mic' : 'micOff'} active={mic.enabled} pressed={mic.enabled} disabled={mic.pending} onClick={() => void mic.toggle()} />
      <RoundButton testId="btn-cam" label={cam.enabled ? t('call.room.camOn') : t('call.room.camOff')} icon={cam.enabled ? 'video' : 'videoOff'} active={cam.enabled} pressed={cam.enabled} disabled={cam.pending} onClick={() => void cam.toggle()} />
      {canShareScreen() && (
        <RoundButton
          testId="btn-screen"
          label={screen.enabled ? t('call.room.screenOn') : t('call.room.screenOff')}
          icon="screen"
          active
          pressed={screen.enabled}
          disabled={screen.pending}
          onClick={() => void screen.toggle()}
        />
      )}
      <RoundButton
        testId="btn-chat"
        label={t('call.room.chat')}
        icon="message"
        active
        pressed={chatOpen}
        onClick={onToggleChat}
        badge={
          unread > 0 && !chatOpen ? (
            <span className="absolute -right-0.5 -top-0.5 grid h-5 min-w-5 place-items-center rounded-full bg-brand-500 px-1 text-[11px] font-bold text-white ring-2 ring-[#0f0d1e]" data-testid="chat-badge">
              {unread > 9 ? '9+' : unread}
            </span>
          ) : undefined
        }
      />
      <RoundButton testId="btn-leave" label={t('call.room.leave')} icon="phoneOff" active danger onClick={() => void room.disconnect()} />
    </div>
  )
}
