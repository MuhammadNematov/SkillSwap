import { Navigate, Route, Routes, useParams } from 'react-router-dom'
import AppLayout from './components/AppLayout'
import ProtectedRoute from './components/ProtectedRoute'
import Admin from './pages/Admin'
import AppHome from './pages/AppHome'
import Call from './pages/Call'
import Calendar from './pages/Calendar'
import Cases from './pages/Cases'
import CaseDetail from './pages/Cases/CaseDetail'
import ExchangeDetail from './pages/ExchangeDetail'
import Exchanges from './pages/Exchanges'
import ConfirmEmail from './pages/ConfirmEmail'
import ForgotPassword from './pages/ForgotPassword'
import Messages from './pages/Messages'
import ResetPassword from './pages/ResetPassword'
import Notifications from './pages/Notifications'
import Onboarding from './pages/Onboarding'
import Reviews from './pages/Reviews'
import Settings from './pages/Settings'
import StaffInvitation from './pages/StaffInvitation'
import Landing from './pages/Landing'
import Login from './pages/Login'
import Matches from './pages/Matches'
import Moderation from './pages/Moderation'
import NotFound from './pages/NotFound'
import Profile from './pages/Profile'
import Register from './pages/Register'

// Разделы личного кабинета: пока заглушки, настоящие страницы делаем по шагам
// Ссылки из писем бэкенда: /requests и /exchanges/{id} ведут в раздел приложения
function ExchangeLink() {
  const { id } = useParams()
  return <Navigate to={`/app/exchanges/${id}`} replace />
}

export default function App() {
  return (
    <Routes>
      {/* Публичные */}
      <Route path="/" element={<Landing />} />
      <Route path="/login" element={<Login />} />
      <Route path="/register" element={<Register />} />
      <Route path="/confirm-email" element={<ConfirmEmail />} />
      <Route path="/forgot-password" element={<ForgotPassword />} />
      <Route path="/staff-invitation/:token" element={<StaffInvitation />} />
      <Route path="/requests" element={<Navigate to="/app/exchanges?tab=incoming" replace />} />
      <Route path="/exchanges/:id" element={<ExchangeLink />} />
      <Route path="/reset-password" element={<ResetPassword />} />

      {/* Защита по ролям (раздел 11.2 ТЗ) */}
      <Route element={<ProtectedRoute roles={['User']} />}>
        <Route path="/app" element={<AppLayout />}>
          <Route index element={<AppHome />} />
          <Route path="onboarding" element={<Onboarding />} />
          <Route path="matches" element={<Matches />} />
          <Route path="exchanges" element={<Exchanges />} />
          <Route path="exchanges/:id" element={<ExchangeDetail />} />
          <Route path="messages" element={<Messages />} />
          <Route path="reviews" element={<Reviews />} />
          <Route path="cases" element={<Cases />} />
          <Route path="cases/:id" element={<CaseDetail />} />
          <Route path="settings" element={<Settings />} />
          <Route path="notifications" element={<Notifications />} />
          <Route path="calendar" element={<Calendar />} />
          <Route path="call/:sessionId" element={<Call />} />
          <Route path="profile" element={<Profile />} />
          <Route path="users/:id" element={<Profile />} />
          <Route path="*" element={<NotFound />} />
        </Route>
      </Route>
      <Route element={<ProtectedRoute roles={['Moderator']} />}>
        <Route path="/moderation/*" element={<Moderation />} />
      </Route>
      <Route element={<ProtectedRoute roles={['Admin', 'SuperAdmin']} />}>
        <Route path="/admin/*" element={<Admin />} />
      </Route>

      <Route path="*" element={<NotFound />} />
    </Routes>
  )
}
