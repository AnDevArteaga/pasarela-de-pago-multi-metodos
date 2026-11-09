import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { CancelPage } from './Cancel'
import { PayPage } from './Pay'
import { VoucherPage } from './Voucher'
import { WaitPage } from './Wait'

export function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/pagar" element={<PayPage />} />
        <Route path="/esperando" element={<WaitPage />} />
        <Route path="/voucher" element={<VoucherPage />} />
        <Route path="/cancelar" element={<CancelPage />} />
        <Route path="*" element={<PayPage />} />
      </Routes>
    </BrowserRouter>
  )
}
