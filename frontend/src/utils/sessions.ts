import type { DeviceType } from '../api/types'
import type { IconName } from '../components/Icon'

// Адрес «своего» компьютера: так видит клиента сервер за локальным прокси (разработка) или при входе с этой же машины
export function isLocalIp(ip: string | null | undefined): boolean {
  if (!ip) return false
  const v = ip.trim().toLowerCase().replace(/^::ffff:/, '')
  return v === '::1' || v === 'localhost' || /^127(\.\d{1,3}){3}$/.test(v)
}

// Значок по виду устройства; неизвестное рисуем компьютером
export function deviceIcon(type: DeviceType | undefined): IconName {
  return type === 'Phone' ? 'phone' : type === 'Tablet' ? 'tablet' : 'laptop'
}

// «24 ноября» и «16:48» отдельно: между ними в русском «в», в английском «at», поэтому склеивает перевод
export function sessionMoment(iso: string, lang: string): { day: string; time: string } {
  const d = new Date(iso)
  return {
    day: new Intl.DateTimeFormat(lang, { day: 'numeric', month: 'long' }).format(d),
    time: new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit' }).format(d),
  }
}
