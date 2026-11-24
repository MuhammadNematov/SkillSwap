// Крутилка загрузки. По умолчанию занимает часть высоты (внутри страницы или блока); fullScreen — на весь экран (проверка сеанса при старте).
export default function Spinner({ fullScreen = false }: { fullScreen?: boolean }) {
  return (
    <div className={`flex items-center justify-center py-10 ${fullScreen ? 'min-h-screen' : 'min-h-[30vh]'}`} role="status" aria-busy="true">
      <div className="h-10 w-10 animate-spin rounded-full border-4 border-brand-100 border-t-brand-600" />
    </div>
  )
}
