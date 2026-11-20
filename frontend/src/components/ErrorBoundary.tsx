import { Component, type ReactNode } from 'react'
import i18n from '../i18n'

// Ловит ошибку рендера страницы, чтобы вместо пустого экрана показать сообщение
export default class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  componentDidCatch(error: unknown) {
    console.error(error)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return (
      <div className="card mx-auto max-w-lg space-y-3 text-center">
        <h2 className="text-lg font-bold text-slate-900">{i18n.t('errors.pageCrashed')}</h2>
        <p className="text-sm text-slate-500">{i18n.t('errors.pageCrashedHint')}</p>
        <button className="btn-primary" onClick={() => window.location.reload()}>
          {i18n.t('errors.reload')}
        </button>
      </div>
    )
  }
}
