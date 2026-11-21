import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import { useNotificationsHub, useUnreadCount } from '../hooks/useNotifications'
import Icon from './Icon'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'

// Общая рамка панелей модератора и администратора: логотип, язык, имя, выход
export default function StaffShell({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  useNotificationsHub()
  const unread = useUnreadCount()
  const notificationsPath = user?.role === 'Moderator' ? '/moderation/notifications' : '/admin/notifications'
  return (
    <div className="min-h-screen">
      <header className="flex items-center justify-between gap-3 px-4 py-4 sm:px-8">
        <Logo />
        <div className="flex items-center gap-3">
          <LanguageSwitcher />
          <Link to={notificationsPath} className="relative rounded-lg p-2 text-slate-500 hover:bg-brand-50" aria-label={t('notifications.title')} title={t('notifications.title')}>
            <Icon name="bell" />
            {unread > 0 && <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-brand-600 px-1 text-[10px] font-bold text-white">{unread}</span>}
          </Link>
          <span className="hidden text-sm text-slate-600 sm:inline">{user?.name}</span>
          <button className="rounded-lg p-2 text-slate-500 hover:bg-brand-50" aria-label={t('common.logout')} title={t('common.logout')} onClick={logout}>
            <Icon name="logout" />
          </button>
        </div>
      </header>
      <main className={`mx-auto space-y-5 px-4 pb-16 sm:px-8 ${wide ? 'max-w-6xl' : 'max-w-4xl'}`}>{children}</main>
    </div>
  )
}
