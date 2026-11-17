import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import Logo from './Logo'
import LanguageSwitcher from './LanguageSwitcher'

interface Props {
  title: string
  subtitle?: string
  children: ReactNode
}

// Общая рамка для страниц входа и регистрации
export default function AuthLayout({ title, subtitle, children }: Props) {
  return (
    <div className="flex min-h-screen flex-col">
      <header className="flex items-center justify-between px-6 py-5">
        <Link to="/">
          <Logo />
        </Link>
        <LanguageSwitcher />
      </header>
      <main className="flex flex-1 items-center justify-center px-4 pb-16">
        <div className="card w-full max-w-md p-8">
          <h1 className="text-2xl font-bold text-slate-900">{title}</h1>
          {subtitle && <p className="mt-1 text-sm text-slate-500">{subtitle}</p>}
          <div className="mt-6">{children}</div>
        </div>
      </main>
    </div>
  )
}
