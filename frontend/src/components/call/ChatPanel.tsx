import type { ReceivedChatMessage } from '@livekit/components-react'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import Icon from '../Icon'

// Чат урока (канал данных LiveKit, как и раньше в VideoConference): сообщения видны только во время урока и не сохраняются.
export default function ChatPanel({
  messages,
  onSend,
  sending,
  onClose,
  dragHandle,
}: {
  messages: ReceivedChatMessage[]
  onSend: (text: string) => Promise<unknown>
  sending: boolean
  onClose: () => void
  dragHandle?: React.ReactNode
}) {
  const { t, i18n } = useTranslation()
  const [text, setText] = useState('')
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight })
  }, [messages.length])

  async function submit(e: FormEvent) {
    e.preventDefault()
    const value = text.trim()
    if (!value || sending) return
    setText('')
    await onSend(value)
  }

  return (
    <div className="flex h-full min-h-0 flex-col bg-surface text-ink" data-testid="chat-panel">
      {dragHandle}
      <div className="flex items-center justify-between gap-2 border-b border-ink-line px-4 py-3">
        <h2 className="font-bold">{t('call.room.chatTitle')}</h2>
        <button type="button" className="rounded-lg p-1.5 text-ink-soft hover:bg-brand-50" aria-label={t('call.room.close')} title={t('call.room.close')} onClick={onClose}>
          <Icon name="x" />
        </button>
      </div>
      <div ref={listRef} className="min-h-0 flex-1 space-y-2 overflow-y-auto overscroll-contain px-4 py-3">
        <p className="rounded-lg bg-brand-50 px-3 py-2 text-xs text-ink-soft">{t('call.room.chatNote')}</p>
        {messages.length === 0 && <p className="py-6 text-center text-sm text-ink-soft">{t('call.room.chatEmpty')}</p>}
        {messages.map((m) => {
          const mine = !!m.from?.isLocal
          return (
            <div key={m.id} className={`flex flex-col ${mine ? 'items-end' : 'items-start'}`}>
              <span className="mb-0.5 text-[11px] text-ink-soft">
                {mine ? t('call.room.you') : m.from?.name || m.from?.identity} · {new Date(m.timestamp).toLocaleTimeString(i18n.resolvedLanguage, { hour: '2-digit', minute: '2-digit' })}
              </span>
              <p className={`max-w-[85%] whitespace-pre-wrap break-words rounded-2xl px-3 py-2 text-sm ${mine ? 'rounded-br-md bg-brand-600 text-white' : 'rounded-bl-md bg-brand-100 text-ink'}`}>{m.message}</p>
            </div>
          )
        })}
      </div>
      <form onSubmit={submit} className="flex items-center gap-2 border-t border-ink-line p-3">
        <input
          className="input h-11 min-w-0 flex-1 py-0 text-base sm:text-sm"
          value={text}
          maxLength={1000}
          placeholder={t('call.room.placeholder')}
          aria-label={t('call.room.placeholder')}
          enterKeyHint="send"
          onChange={(e) => setText(e.target.value)}
        />
        <button className="btn-primary h-11 w-11 shrink-0 p-0" disabled={!text.trim() || sending} aria-label={t('call.room.send')} title={t('call.room.send')}>
          <Icon name="send" />
        </button>
      </form>
    </div>
  )
}
