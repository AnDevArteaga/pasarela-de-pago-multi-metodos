import { FormEvent, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

export function CancelPage() {
  const [params] = useSearchParams()
  const [message, setMessage] = useState('')

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    const res = await fetch('/api/public/cancel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        subscriptionId: params.get('subscription'),
        ref: params.get('ref'),
        ts: params.get('ts') ? Number(params.get('ts')) : null,
        sig: params.get('sig'),
        immediate: false,
      }),
    })
    const body = await res.json().catch(() => ({}))
    setMessage(res.ok ? 'La suscripción queda cancelada al final del periodo ya pagado.' : (body.error || 'No se pudo cancelar.'))
  }

  return (
    <main className="page">
      <section className="card">
        <p className="kicker">Suscripción</p>
        <h1>Cancelar</h1>
        <p className="muted">El acceso sigue hasta el fin del periodo que ya pagaste. No hay un cobro nuevo.</p>
        <form onSubmit={onSubmit}>
          <button className="primary" type="submit">Cancelar al fin del periodo</button>
        </form>
        {message && <p className="error">{message}</p>}
      </section>
    </main>
  )
}
