import { Route, Routes } from 'react-router-dom'
import StaffShell from '../../components/StaffShell'
import ModCaseDetail from './ModCaseDetail'
import ModQueue from './ModQueue'

// Панель модератора: очередь обращений и карточка обращения
export default function Moderation() {
  return (
    <StaffShell>
      <Routes>
        <Route index element={<ModQueue />} />
        <Route path="cases/:id" element={<ModCaseDetail />} />
      </Routes>
    </StaffShell>
  )
}
