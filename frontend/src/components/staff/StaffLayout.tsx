import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../../hooks/useAuth'
import { useNotificationsHub, useUnreadCount } from '../../hooks/useNotifications'
import { useAdminCounts, useModCounts } from '../../hooks/useStaffCounts'
import Avatar from '../Avatar'
import Icon, { type IconName } from '../Icon'
import LanguageSwitcher from '../LanguageSwitcher'
import Logo from '../Logo'
import Tag from './Tag'

interface NavItem {
  to: string
  key: string // staffUi.nav.<key>
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
        `flex h-11 items-center gap-3 rounded-xl px-3 text-sm font-semibold transition ${isActive ? 'bg-brand-100 text-brand-600' : 'text-ink-soft hover:bg-brand-50 hover:text-ink'}`
      }
    >
      <Icon name={item.icon} />
      <span className="flex-1">{t(`staffUi.nav.${item.key}`)}</span>
      {!!item.badge && (
        <span
          className="grid h-5 min-w-[1.25rem] place-items-center rounded-full bg-brand-600 px-1 text-[11px] font-bold text-white"
          aria-label={t('staffUi.newWork', { count: item.badge })}
        >
          {item.badge > 99 ? '99+' : item.badge}
        </span>
      )}
    </NavLink>
  )
}

// Общий каркас кабинетов сотрудников в стиле пользовательской части: левое меню с бейджами (на телефоне выезжает по кнопке),
// сверху заголовок раздела и роль меткой. Содержимое раздела приходит через <Outlet />.
export default function StaffLayout() {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  useNotificationsHub()
  const unread = useUnreadCount()

  const isMod = user?.role === 'Moderator'
  const base = isMod ? '/moderation' : '/admin'
  const adminCounts = useAdminCounts(!isMod)
  const modCounts = useModCounts(isMod)

  const items: NavItem[] = isMod
    ? [
        { to: '/moderation', key: 'overview', icon: 'dashboard', end: true },
        { to: '/moderation/queue', key: 'queue', icon: 'inbox', badge: modCounts.queue.data },
        { to: '/moderation/mine', key: 'mine', icon: 'folder', badge: modCounts.mine.data },
        { to: '/moderation/appeals', key: 'modAppeals', icon: 'appeal', badge: modCounts.appeals.data },
        { to: '/moderation/notifications', key: 'notifications', icon: 'bell', badge: unread },
      ]
    : [
        { to: '/admin', key: 'overview', icon: 'dashboard', end: true },
        { to: '/admin/cases', key: 'cases', icon: 'scale', badge: adminCounts.openCases.data },
        { to: '/admin/bans', key: 'bans', icon: 'ban', badge: adminCounts.bans.data },
        { to: '/admin/users', key: 'users', icon: 'users' },
        { to: '/admin/suggestions', key: 'suggestions', icon: 'lightbulb', badge: adminCounts.suggestions.data },
        { to: '/admin/skills', key: 'skills', icon: 'star' },
        { to: '/admin/categories', key: 'categories', icon: 'tag' },
        { to: '/admin/staff', key: 'staff', icon: 'shield' },
        { to: '/admin/notifications', key: 'notifications', icon: 'bell', badge: unread },
      ]

  // Заголовок раздела: самый длинный пункт меню, с которого начинается адрес (обращение /cases/:id попадает в «Обращения»)
  const path = location.pathname.replace(/\/$/, '') || base
  const current = items
    .filter((i) => (i.end ? path === i.to : path === i.to || path.startsWith(`${i.to}/`)))
    .sort((a, b) => b.to.length - a.to.length)[0]
  const caseDetail = /\/cases\/[^/]+$/.test(path)
  const title = caseDetail ? t(isMod ? 'staffUi.nav.queue' : 'staffUi.nav.cases') : t(`staffUi.nav.${current?.key ?? 'overview'}`)

  useEffect(() => setMenuOpen(false), [location.pathname])

  const role = user?.role ?? 'Moderator'
  const roleLabel = t(`staffUi.role.${role}`)

  return (
    <div className="min-h-screen lg:flex">
      {menuOpen && <div className="fixed inset-0 z-30 bg-black/40 lg:hidden" onClick={() => setMenuOpen(false)} />}

      <aside
        className={`fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-ink-line bg-surface transition-transform lg:sticky lg:top-0 lg:h-screen lg:translate-x-0 ${menuOpen ? 'translate-x-0' : '-translate-x-full'}`}
      >
        <div className="flex h-[76px] items-center justify-between px-6">
          <Link to={base}>
            <Logo className="h-8" />
          </Link>
          <button className="rounded-lg p-1 lg:hidden" aria-label={t('shell.close')} onClick={() => setMenuOpen(false)}>
            <Icon name="x" />
          </button>
        </div>

        <nav className="flex-1 space-y-1 overflow-y-auto px-4 py-2" aria-label={t('shell.menu')}>
          {items.map((item) => (
            <SideLink key={item.to} item={item} />
          ))}
        </nav>

        <div className="space-y-3 border-t border-ink-line p-3">
          <div className="sm:hidden">
            <LanguageSwitcher />
          </div>
          <div className="flex items-center gap-3">
            <Avatar name={user?.name ?? ''} src={user?.avatarUrl} className="h-11 w-11" />
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-bold text-ink">{user?.name}</p>
              <p className="truncate text-xs text-ink-soft">{user?.email}</p>
            </div>
            <button className="grid h-9 w-9 place-items-center rounded-lg text-ink-soft hover:bg-brand-50" aria-label={t('common.logout')} title={t('common.logout')} onClick={logout}>
              <Icon name="logout" />
            </button>
          </div>
        </div>
      </aside>

      <div className="min-w-0 flex-1">
        <header className="sticky top-0 z-20 flex items-center gap-3 border-b border-ink-line bg-brand-50/90 px-4 py-3 backdrop-blur sm:px-8">
          <button className="rounded-lg p-2 text-ink-soft hover:bg-brand-100 lg:hidden" aria-label={t('shell.menu')} onClick={() => setMenuOpen(true)}>
            <Icon name="menu" />
          </button>
          <h1 className="min-w-0 flex-1 truncate text-lg font-bold text-ink sm:text-2xl">{title}</h1>
          <Tag tone="brand">{roleLabel}</Tag>
          <div className="hidden sm:block">
            <LanguageSwitcher />
          </div>
          <Link
            to={`${base}/notifications`}
            aria-label={t('notifications.title')}
            className="relative grid h-11 w-11 shrink-0 place-items-center rounded-xl border border-ink-line bg-surface text-ink hover:border-brand-300"
          >
            <Icon name="bell" />
            {unread > 0 && (
              <span className="absolute -right-1 -top-1 grid h-[18px] min-w-[18px] place-items-center rounded-full border-2 border-surface bg-brand-600 px-1 text-[10px] font-bold text-white">
                {unread > 9 ? '9+' : unread}
              </span>
            )}
          </Link>
        </header>

        <main className="mx-auto w-full max-w-[1200px] space-y-5 p-4 sm:p-8">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
