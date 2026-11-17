import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import Avatar from './Avatar'
import Icon, { type IconName } from './Icon'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'

interface NavItem {
  to: string
  key: string
  icon: IconName
  end?: boolean
}

const MAIN_NAV: NavItem[] = [
  { to: '/app', key: 'home', icon: 'home', end: true },
  { to: '/app/matches', key: 'matches', icon: 'search' },
  { to: '/app/exchanges', key: 'exchanges', icon: 'swap' },
  { to: '/app/messages', key: 'messages', icon: 'message' },
  { to: '/app/calendar', key: 'calendar', icon: 'calendar' },
  { to: '/app/reviews', key: 'reviews', icon: 'star' },
  { to: '/app/notifications', key: 'notifications', icon: 'bell' },
]

const SECONDARY_NAV: NavItem[] = [
  { to: '/app/profile', key: 'profile', icon: 'user' },
  { to: '/app/settings', key: 'settings', icon: 'settings' },
]

function SideLink({ item }: { item: NavItem }) {
  const { t } = useTranslation()
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        `flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm font-medium transition ${
          isActive ? 'bg-white/80 text-brand-700 shadow-sm' : 'text-slate-600 hover:bg-white/60'
        }`
      }
    >
      <Icon name={item.icon} />
      {t(`nav.${item.key}`)}
    </NavLink>
  )
}

// Оболочка личного кабинета: боковое меню + верхняя панель. На телефоне меню выезжает.
export default function AppLayout() {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [query, setQuery] = useState('')

  // После перехода по ссылке закрываем меню на телефоне
  useEffect(() => setMenuOpen(false), [location.pathname])

  function onSearch(e: FormEvent) {
    e.preventDefault()
    const q = query.trim()
    navigate(q ? `/app/matches?q=${encodeURIComponent(q)}` : '/app/matches')
  }

  return (
    <div className="min-h-screen lg:flex">
      {menuOpen && (
        <div className="fixed inset-0 z-30 bg-slate-900/40 lg:hidden" onClick={() => setMenuOpen(false)} />
      )}

      <aside
        className={`fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-white/60 bg-white/80 p-4 shadow-glass backdrop-blur-2xl transition-transform lg:sticky lg:top-0 lg:h-screen lg:translate-x-0 lg:bg-white/50 ${
          menuOpen ? 'translate-x-0' : '-translate-x-full'
        }`}
      >
        <div className="flex items-center justify-between px-2 py-2">
          <Link to="/app">
            <Logo className="h-8" />
          </Link>
          <button className="lg:hidden" aria-label={t('shell.close')} onClick={() => setMenuOpen(false)}>
            <Icon name="x" />
          </button>
        </div>

        <nav className="mt-6 flex-1 space-y-1 overflow-y-auto">
          {MAIN_NAV.map((item) => (
            <SideLink key={item.to} item={item} />
          ))}
          <div className="my-3 border-t border-white/70" />
          {SECONDARY_NAV.map((item) => (
            <SideLink key={item.to} item={item} />
          ))}
        </nav>
      </aside>

      <div className="min-w-0 flex-1">
        <header className="sticky top-0 z-20 flex items-center gap-3 border-b border-white/60 bg-white/50 px-4 py-3 backdrop-blur-xl sm:px-6">
          <button
            className="rounded-lg p-2 text-slate-600 hover:bg-white/60 lg:hidden"
            aria-label={t('shell.menu')}
            onClick={() => setMenuOpen(true)}
          >
            <Icon name="menu" />
          </button>

          <form onSubmit={onSearch} className="relative max-w-md flex-1">
            <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">
              <Icon name="search" className="h-4 w-4" />
            </span>
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={t('shell.searchPlaceholder')}
              className="input pl-9"
            />
          </form>

          <div className="ml-auto flex items-center gap-2 sm:gap-3">
            <LanguageSwitcher />
            <Link
              to="/app/notifications"
              className="rounded-lg p-2 text-slate-600 hover:bg-white/60"
              aria-label={t('nav.notifications')}
            >
              <Icon name="bell" />
            </Link>
            <Link to="/app/profile" className="flex items-center gap-2">
              <Avatar name={user?.name ?? ''} src={user?.avatarUrl} className="h-9 w-9" />
              <span className="hidden text-sm font-medium text-slate-700 sm:inline">{user?.name}</span>
            </Link>
            <button
              className="rounded-lg p-2 text-slate-500 hover:bg-white/60"
              aria-label={t('common.logout')}
              title={t('common.logout')}
              onClick={logout}
            >
              <Icon name="logout" />
            </button>
          </div>
        </header>

        <main className="p-4 sm:p-6 lg:p-8">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
