// Адрес сервера LiveKit для подключения к видеокомнате.
// В разработке он строится из адреса страницы: wss://{location.host}/livekit (http-страница даёт ws://). Сигнальный канал идёт через прокси Vite
// (/livekit → localhost:7880, vite.config.ts), поэтому адрес работает одинаково на https://localhost:5173 и по адресу Mac в сети, и смена IP его не ломает
// (как относительные ссылки на файлы). Вне разработки берётся адрес из ответа бэкенда (настройка LiveKit:Url).
export function liveKitServerUrl(fromServer: string, location: Pick<Location, 'protocol' | 'host'> = window.location, dev: boolean = import.meta.env.DEV): string {
  if (!dev) return fromServer
  return `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/livekit`
}
