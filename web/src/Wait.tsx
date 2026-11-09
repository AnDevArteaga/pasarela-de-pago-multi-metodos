import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { pollAsync, voucher } from './api'

export function WaitPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const pid = params.get('pid') || ''
  const [text, setText] = useState('Estamos confirmando el pago.')

  useEffect(() => {
    if (!pid) return
    let stop = false
    const tick = async () => {
      const link = await pollAsync(pid)
      if (stop) return
      if (link.url && link.status === 'PENDING') {
        window.location.href = link.url
        return
      }
      const state = await voucher(pid)
      if (stop) return
      if (state.status !== 'PENDING') {
        navigate(`/voucher?pid=${pid}`)
        return
      }
      setText(state.method === 'NEQUI'
        ? 'Confirma el pago en la app de Nequi.'
        : state.method === 'CARD'
          ? 'La transacción se está resolviendo.'
          : 'Esperando el link del banco.')
    }
    const timer = window.setInterval(() => { void tick() }, 1500)
    void tick()
    return () => { stop = true; window.clearInterval(timer) }
  }, [pid, navigate])

  return (
    <main className="page">
      <section className="card">
        <p className="kicker">Espera</p>
        <h1>Un momento</h1>
        <p className="muted">{text}</p>
      </section>
    </main>
  )
}
