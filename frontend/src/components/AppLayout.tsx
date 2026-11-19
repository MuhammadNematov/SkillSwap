import { useQuery } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { getExchangeRequests, getExchanges } from '../api/exchanges'
import { useAuth } from '../hooks/useAuth'
import { useUnreadCount } from '../hooks/useNotifications'
import Avatar from './Avatar'
import Icon, { type IconName } from './Icon'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'

interface NavItem {
  to: string
  key: string
  icon: IconName
  end?: boolean
  badge?: number
}

function SideLink({ item }: { item: NavItem }) {
  const { t } = useTranslation()
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        `flex h-11 items-center gap-3 rounded-xl px-3 text-sm font-semibold transition ${
          isActive ? 'bg-brand-100 text-brand-600' : 'text-ink-soft hover:bg-brand-50 hover:text-ink'
        }`
      }
    >
      <Icon name={item.icon} />
      <span className="flex-1">{t(`nav.${item.key}`)}</span>
      {!!item.badge && (
        <span className="grid h-5 min-w-[1.25rem] place-items-center rounded-full bg-brand-100 px-1 text-[11px] font-bold text-brand-600">
          {item.badge > 9 ? '9+' : item.badge}
        </span>
      )}
    </NavLink>
  )
}

const greetingKey = () => {
  const h = new Date().getHours()
  return h < 5 ? 'evening' : h < 12 ? 'morning' : h < 18 ? 'afternoon' : 'evening'
}

// Оболочка личного кабинета по дизайну: белая боковая панель с разделами + верхняя панель. На телефоне меню выезжает.
export default function AppLayout() {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  const unread = useUnreadCount()
  const navigate = useNavigate()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [query, setQuery] = useState('')

  // Бейджи в меню: ждущие ответа заявки и непрочитанные сообщения (те же запросы, что на страницах: кэш общий)
  const incoming = useQuery({ queryKey: ['requests', 'in'], queryFn: () => getExchangeRequests('in') })
  const exchanges = useQuery({ queryKey: ['exchanges'], queryFn: getExchanges })
  const pending = (incoming.data ?? []).filter((r) => r.status === 'Pending').length
  const unreadMessages = (exchanges.data ?? []).reduce((sum, e) => sum + (e.unreadMessages ?? 0), 0)

  const menu: NavItem[] = [
    { to: '/app', key: 'home', icon: 'home', end: true },
    { to: '/app/matches', key: 'matches', icon: 'search' },
    { to: '/app/exchanges', key: 'exchanges', icon: 'swap', badge: pending },
    { to: '/app/messages', key: 'messages', icon: 'message', badge: unreadMessages },
    { to: '/app/calendar', key: 'calendar', icon: 'calendar' },
  ]
  const activity: NavItem[] = [
    { to: '/app/notifications', key: 'notifications', icon: 'bell', badge: unread },
    { to: '/app/reviews', key: 'reviews', icon: 'star' },
    { to: '/app/profile', key: 'profile', icon: 'user' },
    { to: '/app/settings', key: 'settings', icon: 'settings' },
  ]

  // После перехода по ссылке закрываем меню на телефоне
  useEffect(() => setMenuOpen(false), [location.pathname])

  function onSearch(e: FormEvent) {
    e.preventDefault()
    const q = query.trim()
    navigate(q ? `/app/matches?q=${encodeURIComponent(q)}` : '/app/matches')
  }

  const label = 'px-3 pb-1.5 pt-2 text-[11px] font-bold uppercase tracking-[0.12em] text-ink-faint'

  return (
    <div className="min-h-screen lg:flex">
      {menuOpen && <div className="fixed inset-0 z-30 bg-ink/40 lg:hidden" onClick={() => setMenuOpen(false)} />}

      <aside
        className={`fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-ink-line bg-white transition-transform lg:sticky lg:top-0 lg:h-screen lg:translate-x-0 ${
          menuOpen ? 'translate-x-0' : '-translate-x-full'
        }`}
      >
        <div className="flex h-[76px] items-center justify-between px-6">
          <Link to="/app">
            <Logo className="h-8" />
          </Link>
          <button className="lg:hidden" aria-label={t('shell.close')} onClick={() => setMenuOpen(false)}>
            <Icon name="x" />
          </button>
        </div>

        <nav className="flex-1 space-y-1 overflow-y-auto px-4 py-2" aria-label={t('shell.menu')}>
          <p className={label}>{t('shell.sectionMenu')}</p>
          {menu.map((item) => (
            <SideLink key={item.to} item={item} />
          ))}
          <p className={`${label} pt-5`}>{t('shell.sectionActivity')}</p>
          {activity.map((item) => (
            <SideLink key={item.to} item={item} />
          ))}
        </nav>

        <div className="flex items-center gap-3 border-t border-ink-line p-3">
          <Avatar name={user?.name ?? ''} src={user?.avatarUrl} className="h-11 w-11" />
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-bold text-ink">{user?.name}</p>
            <p className="truncate text-xs text-ink-soft">{user?.email}</p>
          </div>
          <button
            className="grid h-9 w-9 place-items-center rounded-lg text-ink-soft hover:bg-brand-50"
            aria-label={t('common.logout')}
            title={t('common.logout')}
            onClick={logout}
          >
            <Icon name="logout" />
          </button>
        </div>
      </aside>

      <div className="min-w-0 flex-1">
        <header className="sticky top-0 z-20 flex flex-wrap items-center gap-x-6 gap-y-3 border-b border-ink-line bg-brand-50/90 px-4 py-3.5 backdrop-blur sm:px-8">
          <button
            className="rounded-lg p-2 text-ink-soft hover:bg-brand-100 lg:hidden"
            aria-label={t('shell.menu')}
            onClick={() => setMenuOpen(true)}
          >
            <Icon name="menu" />
          </button>

          <div className="hidden shrink-0 sm:block">
            <p className="text-xs text-ink-soft">{t(`shell.greeting_${greetingKey()}`)}</p>
            <p className="text-[15px] font-bold">{user?.name?.split(' ')[0]}</p>
          </div>

          <form onSubmit={onSearch} className="relative min-w-[200px] max-w-[520px] flex-1">
            <span className="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-ink-faint">
              <Icon name="search" className="h-[18px] w-[18px]" />
            </span>
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t('shell.searchPlaceholder')}
              aria-label={t('shell.searchPlaceholder')}
              className="input h-11 rounded-2xl pl-11"
            />
          </form>

          <div className="ml-auto flex items-center gap-2">
            <LanguageSwitcher />
            <Link to="/app/calendar" className="btn-primary hidden h-11 sm:inline-flex">
              <Icon name="calendar" className="h-4 w-4" />
              {t('shell.scheduleSession')}
            </Link>
            <Link
              to="/app/messages"
              aria-label={t('nav.messages')}
              className="relative grid h-11 w-11 place-items-center rounded-xl border border-ink-line bg-white text-ink hover:border-brand-300"
            >
              <Icon name="message" />
              {unreadMessages > 0 && (
                <span className="absolute -right-1 -top-1 grid h-[18px] min-w-[18px] place-items-center rounded-full border-2 border-white bg-brand-600 px-1 text-[10px] font-bold text-white">
                  {unreadMessages > 9 ? '9+' : unreadMessages}
                </span>
              )}
            </Link>
            <Link
              to="/app/notifications"
              aria-label={t('nav.notifications')}
              className="relative grid h-11 w-11 place-items-center rounded-xl border border-ink-line bg-white text-ink hover:border-brand-300"
            >
              <Icon name="bell" />
              {unread > 0 && (
                <span className="absolute -right-1 -top-1 grid h-[18px] min-w-[18px] place-items-center rounded-full border-2 border-white bg-brand-600 px-1 text-[10px] font-bold text-white">
                  {unread > 9 ? '9+' : unread}
                </span>
              )}
            </Link>
            <Link to="/app/profile" aria-label={t('nav.profile')}>
              <Avatar name={user?.name ?? ''} src={user?.avatarUrl} className="h-11 w-11" />
            </Link>
          </div>
        </header>

        <main className="mx-auto w-full max-w-[1200px] p-4 sm:p-8">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
