import { FormEvent, useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { cop, createSession, getSession, pay, type SessionView } from './api'
import { tokenize } from './wompi'

const METHODS = [
  { id: 'CARD', label: 'Tarjeta' },
  { id: 'PSE', label: 'PSE' },
  { id: 'BANCOLOMBIA_TRANSFER', label: 'Bancolombia' },
  { id: 'NEQUI', label: 'Nequi' },
] as const

export function PayPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const [session, setSession] = useState<SessionView | null>(null)
  const [sessionId, setSessionId] = useState('')
  const [method, setMethod] = useState<(typeof METHODS)[number]['id']>('CARD')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [useSaved, setUseSaved] = useState(false)
  const [acceptPrivacy, setAcceptPrivacy] = useState(false)
  const [acceptRecurring, setAcceptRecurring] = useState(false)

  useEffect(() => {
    let gone = false
    createSession(params)
      .then((created) => getSession(created.sessionId).then((view) => ({ created, view })))
      .then(({ created, view }) => {
        if (gone) return
        setSessionId(created.sessionId)
        setSession(view)
        if (view.savedCard) setUseSaved(true)
      })
      .catch((err: Error) => setError(err.message))
    return () => { gone = true }
  }, [params])

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!session) return
    setBusy(true)
    setError('')
    const form = new FormData(event.currentTarget)
    const key = crypto.randomUUID()
    try {
      let cardToken: string | undefined
        const payingCard = subscription || method === 'CARD'
        if (payingCard && !useSaved) {
          cardToken = await tokenize(session.publicKey)
        }
      const result = await pay(sessionId, {
        idempotencyKey: key,
        amountCents: session.product.priceCents,
        method: session.product.type === 'subscription' ? 'CARD' : method,
        useSavedSource: useSaved && method === 'CARD',
        savedSourceId: useSaved ? session.savedCard?.id : null,
        cardToken,
        installments: 1,
        payer: {
          email: String(form.get('email') || ''),
          fullName: String(form.get('fullName') || ''),
          phone: String(form.get('phone') || ''),
          legalId: String(form.get('legalId') || ''),
          legalIdType: String(form.get('legalIdType') || 'CC'),
        },
        financialInstitutionCode: String(form.get('bank') || ''),
        userType: Number(form.get('userType') || 0),
        nequiPhone: String(form.get('nequiPhone') || ''),
        acceptPrivacy,
        acceptRecurring,
      })
      sessionStorage.setItem('pasarela.payment', result.publicId)
      if (result.asyncPaymentUrl) {
        window.location.href = result.asyncPaymentUrl
        return
      }
      navigate(`/esperando?pid=${result.publicId}`)
    } catch (err) {
      const known = err as Error & { status?: number; priceCents?: number }
      if (known.status === 409 && known.priceCents && session) {
        setSession({ ...session, product: { ...session.product, priceCents: known.priceCents } })
        setError('El precio cambió. Revisa el valor y confirma de nuevo.')
      } else {
        setError(known.message)
      }
    } finally {
      setBusy(false)
    }
  }

  if (!session) {
    return <main className="page"><section className="card"><p className="muted">{error || 'Cargando…'}</p></section></main>
  }

  const subscription = session.product.type === 'subscription'
  const showCard = subscription || method === 'CARD'
  const next = session.nextChargeAt ? new Date(session.nextChargeAt).toLocaleDateString('es-CO') : ''

  return (
    <main className="page">
      <section className="card">
        <p className="kicker">Pago</p>
        <h1>{session.product.name}</h1>
        <p className="muted">{session.product.description}</p>
        <p className="price">{cop(session.product.priceCents)}</p>
        {subscription && (
          <p className="legal">
            {session.legal.recurringText} La próxima fecha de cobro, si hoy se aprueba, es {next}.
          </p>
        )}
        <form onSubmit={onSubmit}>
          {!subscription && (
            <div className="methods">
              {METHODS.map((item) => (
                <button key={item.id} type="button" aria-pressed={method === item.id} onClick={() => setMethod(item.id)}>
                  {item.label}
                </button>
              ))}
            </div>
          )}
          <label htmlFor="email">Correo</label>
          <input id="email" name="email" type="email" required />
          <label htmlFor="fullName">Nombre</label>
          <input id="fullName" name="fullName" required />
          {(method === 'PSE' || method === 'NEQUI' || method === 'BANCOLOMBIA_TRANSFER') && !subscription && (
            <>
              <label htmlFor="phone">Teléfono</label>
              <input id="phone" name="phone" required />
            </>
          )}
          {method === 'PSE' && !subscription && <PseFields sessionId={sessionId} />}
          {method === 'NEQUI' && !subscription && (
            <>
              <label htmlFor="nequiPhone">Celular Nequi</label>
              <input id="nequiPhone" name="nequiPhone" inputMode="numeric" pattern="\d{10}" required />
            </>
          )}
          {showCard && session.savedCard && (
            <label className="check">
              <input type="checkbox" checked={useSaved} onChange={(e) => setUseSaved(e.target.checked)} />
              <span>Usar la tarjeta {session.savedCard.brand} terminada en {session.savedCard.lastFour}</span>
            </label>
          )}
          <p className="legal">{session.legal.privacyText}</p>
          <label className="check">
            <input type="checkbox" checked={acceptPrivacy} onChange={(e) => setAcceptPrivacy(e.target.checked)} required />
            <span>Acepto el tratamiento de datos.</span>
          </label>
          {subscription && (
            <label className="check">
              <input type="checkbox" checked={acceptRecurring} onChange={(e) => setAcceptRecurring(e.target.checked)} required />
              <span>Acepto el cobro recurrente.</span>
            </label>
          )}
          {error && <p className="error">{error}</p>}
          <button className="primary" type="submit" disabled={busy}>
            {busy ? 'Procesando…' : showCard && !useSaved ? 'Continuar con Wompi' : 'Pagar'}
          </button>
        </form>
      </section>
    </main>
  )
}

function PseFields({ sessionId }: { sessionId: string }) {
  const [list, setList] = useState<{ code: string; name: string }[]>([])
  useEffect(() => {
    fetch(`/api/checkout/sessions/${sessionId}/banks`).then((r) => r.json()).then(setList).catch(() => setList([]))
  }, [sessionId])
  return (
    <>
      <label htmlFor="userType">Tipo de persona</label>
      <select id="userType" name="userType" defaultValue="0">
        <option value="0">Natural</option>
        <option value="1">Jurídica</option>
      </select>
      <label htmlFor="legalIdType">Tipo de documento</label>
      <select id="legalIdType" name="legalIdType" defaultValue="CC">
        <option>CC</option>
        <option>CE</option>
        <option>NIT</option>
      </select>
      <label htmlFor="legalId">Documento</label>
      <input id="legalId" name="legalId" required />
      <label htmlFor="bank">Banco</label>
      <select id="bank" name="bank" required defaultValue="">
        <option value="" disabled>Elige un banco</option>
        {list.map((bank) => <option key={bank.code} value={bank.code}>{bank.name}</option>)}
      </select>
    </>
  )
}
