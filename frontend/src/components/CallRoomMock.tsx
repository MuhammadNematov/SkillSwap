import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { JoinToken } from '../api/calls'

// Заглушка комнаты для режима моков (VITE_USE_MOCKS=true): LiveKit-сервера нет, подключаться некуда.
export default function CallRoomMock({ data, onLeave }: { data: JoinToken; onLeave: () => void }) {
  const { t } = useTranslation()
  const [mic, setMic] = useState(true)
  const [cam, setCam] = useState(true)

  return (
    <div className="flex flex-1 flex-col gap-4">
      <div className="grid flex-1 gap-4 sm:grid-cols-2">
        {[
          { src: '/characters/boy-phone.webp', name: t('call.you') },
          { src: '/characters/girl-phone.webp', name: t('call.partner') },
        ].map((p, i) => (
          <div key={i} className="relative min-h-[260px] overflow-hidden rounded-3xl border border-ink-line bg-[#17143d]">
            {i === 0 && !cam ? (
              <div className="flex h-full items-center justify-center text-sm text-white/70">{t('call.camOff')}</div>
            ) : (
              <img src={p.src} alt="" className="h-full w-full object-cover" draggable={false} />
            )}
            <span className="absolute bottom-3 left-3 rounded-full bg-black/50 px-3 py-1 text-xs font-semibold text-white">{p.name}</span>
          </div>
        ))}
      </div>
      <p className="text-center text-xs text-ink-soft">{t('call.mockNote', { room: data.roomName })}</p>
      <div className="flex flex-wrap items-center justify-center gap-3">
        <button className={mic ? 'btn-secondary' : 'btn-primary'} onClick={() => setMic((v) => !v)}>
          {mic ? t('call.micOn') : t('call.micOff')}
        </button>
        <button className={cam ? 'btn-secondary' : 'btn-primary'} onClick={() => setCam((v) => !v)}>
          {cam ? t('call.camOn') : t('call.camOff')}
        </button>
        <button className="btn-primary bg-[#DC3545] hover:bg-[#c42f3d]" onClick={onLeave}>
          {t('call.leave')}
        </button>
      </div>
    </div>
  )
}
