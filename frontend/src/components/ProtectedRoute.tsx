import { Navigate, Outlet, useLocation } from 'react-router-dom'
import type { Role } from '../api/types'
import { useAuth } from '../hooks/useAuth'
import { homeForRole } from '../utils/roles'
import Spinner from './Spinner'

// Скрытие страниц на фронтенде — только удобство. Настоящие проверки прав делает сервер (ТЗ, раздел 10).
export default function ProtectedRoute({ roles }: { roles?: Role[] }) {
  const { user, loading } = useAuth()
  const location = useLocation()

  if (loading) return <Spinner fullScreen />

  if (!user) {
    return <Navigate to="/login" replace state={{ from: location }} />
  }

  if (roles && !roles.includes(user.role)) {
    return <Navigate to={homeForRole(user.role)} replace />
  }

  // Без анкеты подбор и обмены недоступны: сначала онбординг (ТЗ 5)
  if (user.role === 'User' && !user.skillsComplete && location.pathname !== '/app/onboarding') {
    return <Navigate to="/app/onboarding" replace />
  }

  return <Outlet />
}
