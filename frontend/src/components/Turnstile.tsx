import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { USE_MOCKS } from '../api/config'

// Виджет Cloudflare Turnstile (FR-5). Токен одноразовый: после каждого запроса вызываем reset().
// Подключается скриптом Cloudflare, отдельная библиотека не нужна.
const SITE_KEY = import.meta.env.VITE_TURNSTILE_SITE_KEY || '1x00000000000000000000AA' // тестовый ключ
const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'

interface TurnstileApi {
  render: (el: HTMLElement, opts: Record<string, unknown>) => string
  reset: (id?: string) => void
  remove: (id?: string) => void
}
declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

let scriptPromise: Promise<void> | null = null
function loadScript(): Promise<void> {
  if (window.turnstile) return Promise.resolve()
  scriptPromise ??= new Promise<void>((resolve, reject) => {
    const s = document.createElement('script')
    s.src = SCRIPT_SRC
    s.async = true
    s.onload = () => resolve()
    s.onerror = () => {
      scriptPromise = null
      reject(new Error('turnstile'))
    }
    document.head.appendChild(s)
  })
  return scriptPromise
}

export interface TurnstileHandle {
  reset: () => void
}

interface Props {
  onToken: (token: string | null) => void
}

const Turnstile = forwardRef<TurnstileHandle, Props>(function Turnstile({ onToken }, ref) {
  const { i18n } = useTranslation()
  const box = useRef<HTMLDivElement>(null)
  const widgetId = useRef<string>()
  const onTokenRef = useRef(onToken)
  onTokenRef.current = onToken

  useImperativeHandle(ref, () => ({
    reset() {
      onTokenRef.current(null)
      if (widgetId.current) window.turnstile?.reset(widgetId.current)
    },
  }))

  useEffect(() => {
    if (USE_MOCKS) return
    let cancelled = false
    loadScript()
      .then(() => {
        if (cancelled || !box.current || !window.turnstile) return
        widgetId.current = window.turnstile.render(box.current, {
          sitekey: SITE_KEY,
          language: i18n.resolvedLanguage === 'tg' ? 'tg' : (i18n.resolvedLanguage ?? 'ru'),
          callback: (token: string) => onTokenRef.current(token),
          'expired-callback': () => onTokenRef.current(null),
          'error-callback': () => onTokenRef.current(null),
        })
      })
      .catch(() => onTokenRef.current(null))
    return () => {
      cancelled = true
      if (widgetId.current) window.turnstile?.remove(widgetId.current)
      widgetId.current = undefined
    }
  }, [i18n.resolvedLanguage])

  // На заглушках капчи нет — бэкенд не вызывается
  if (USE_MOCKS) return null
  return <div ref={box} className="flex min-h-[65px] justify-center" />
})

export default Turnstile
