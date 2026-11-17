import { Route, Routes } from 'react-router-dom'
import AppLayout from './components/AppLayout'
import ProtectedRoute from './components/ProtectedRoute'
import Admin from './pages/Admin'
import AppHome from './pages/AppHome'
import Landing from './pages/Landing'
import Login from './pages/Login'
import Moderation from './pages/Moderation'
import NotFound from './pages/NotFound'
import Placeholder from './pages/Placeholder'
import Register from './pages/Register'

// Разделы личного кабинета: пока заглушки, настоящие страницы делаем по шагам
const APP_SECTIONS = ['matches', 'exchanges', 'messages', 'calendar', 'reviews', 'notifications', 'profile', 'settings']

export default function App() {
  return (
    <Routes>
      {/* Публичные */}
      <Route path="/" element={<Landing />} />
      <Route path="/login" element={<Login />} />
      <Route path="/register" element={<Register />} />

      {/* Защита по ролям (раздел 11.2 ТЗ) */}
      <Route element={<ProtectedRoute roles={['User']} />}>
        <Route path="/app" element={<AppLayout />}>
          <Route index element={<AppHome />} />
          {APP_SECTIONS.map((s) => (
            <Route key={s} path={s} element={<Placeholder titleKey={`nav.${s}`} />} />
          ))}
          <Route path="*" element={<NotFound />} />
        </Route>
      </Route>
      <Route element={<ProtectedRoute roles={['Moderator']} />}>
        <Route path="/moderation/*" element={<Moderation />} />
      </Route>
      <Route element={<ProtectedRoute roles={['Admin', 'Superadmin']} />}>
        <Route path="/admin/*" element={<Admin />} />
      </Route>

      <Route path="*" element={<NotFound />} />
    </Routes>
  )
}
