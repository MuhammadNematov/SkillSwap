import { Navigate, Route, Routes } from 'react-router-dom'
import StaffLayout from '../../components/staff/StaffLayout'
import Notifications from '../Notifications'
import ModCaseDetail from './ModCaseDetail'
import ModOverview from './Overview'
import ModQueue from './ModQueue'

// Кабинет модератора: обзор, очередь, мои обращения в работе, апелляции на проверке, карточка обращения
export default function Moderation() {
  return (
    <Routes>
      <Route element={<StaffLayout />}>
        <Route index element={<ModOverview />} />
        <Route path="queue" element={<ModQueue scope="Queue" />} />
        <Route path="mine" element={<ModQueue scope="Mine" />} />
        <Route path="appeals" element={<ModQueue scope="Appeals" />} />
        <Route path="cases/:id" element={<ModCaseDetail />} />
        <Route path="notifications" element={<Notifications area="moderation" />} />
        <Route path="*" element={<Navigate to="/moderation" replace />} />
      </Route>
    </Routes>
  )
}
