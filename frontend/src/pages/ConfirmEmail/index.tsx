import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import AuthLayout from '../../components/AuthLayout'
import ConfirmEmailCode from '../../components/ConfirmEmailCode'

// Подтверждение почты кодом из письма: /confirm-email или /confirm-email?email=...
// Ссылок в письмах больше нет; старая ссылка (?userId=…&token=…) ведёт сюда же и просит ввести код.
export default function ConfirmEmail() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const oldLink = params.has('token')
  const [email, setEmail] = useState(params.get('email') ?? '')
  const [entered, setEntered] = useState(!!params.get('email'))
  const [confirmed, setConfirmed] = useState(false)

  return (
    <AuthLayout title={t('confirmEmail.title')}>
      {confirmed ? (
        <p className="text-sm text-slate-600">{t('confirmEmail.success')}</p>
      ) : (
        <>
          {oldLink && <p className="mb-4 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800">{t('confirmEmail.oldLink')}</p>}
          {entered ? (
            <>
              <p className="mb-5 text-sm text-slate-600">{t('confirmEmail.enterCode', { email })}</p>
              <ConfirmEmailCode email={email.trim()} onConfirmed={() => setConfirmed(true)} />
              <button type="button" className="mt-4 text-sm text-ink-soft hover:underline" onClick={() => setEntered(false)}>
                {t('confirmEmail.changeEmail')}
              </button>
            </>
          ) : (
            <form
              className="space-y-4"
              onSubmit={(e) => {
                e.preventDefault()
                if (email.trim()) setEntered(true)
              }}
            >
              <label className="block">
                <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('loginPage.email')}</span>
                <input className="input" type="email" autoComplete="email" autoFocus value={email} onChange={(e) => setEmail(e.target.value)} />
              </label>
              <button className="btn-primary h-12 w-full rounded-2xl text-[15px]" disabled={!email.trim()}>
                {t('confirmEmail.next')}
              </button>
            </form>
          )}
        </>
      )}
      <Link to="/login" className={`${confirmed ? 'btn-primary' : 'btn-secondary'} mt-6 w-full`}>
        {t('registerPage.toLogin')}
      </Link>
    </AuthLayout>
  )
}
