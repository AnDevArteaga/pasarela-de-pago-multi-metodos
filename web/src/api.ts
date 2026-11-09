export type Product = {
  id: number
  name: string
  description: string
  priceCents: number
  type: 'subscription' | 'one_time'
  billingInterval: string
  intervalCount: number
  graceDays: number
}

export type SessionView = {
  sessionId: string
  signed: boolean
  publicKey: string
  product: Product
  nextChargeAt: string | null
  legal: {
    privacyText: string
    recurringText: string
  }
  savedCard: { id: string; brand: string | null; lastFour: string | null } | null
}

export type PaymentView = {
  publicId: string
  status: string
  method: string
  asyncPaymentUrl: string | null
  amountCents: number
}

async function parse<T>(res: Response): Promise<T> {
  const body = await res.json().catch(() => ({}))
  if (!res.ok) {
    const error = new Error(body.error || 'No se pudo completar.') as Error & { status?: number; priceCents?: number }
    error.status = res.status
    error.priceCents = body.priceCents
    throw error
  }
  return body as T
}

export async function createSession(query: URLSearchParams) {
  return parse<{ sessionId: string }>(await fetch('/api/checkout/sessions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      productId: Number(query.get('id')),
      ref: query.get('ref'),
      ts: query.get('ts') ? Number(query.get('ts')) : null,
      sig: query.get('sig'),
    }),
  }))
}

export async function getSession(id: string) {
  return parse<SessionView>(await fetch(`/api/checkout/sessions/${id}`))
}

export async function banks(id: string) {
  return parse<{ code: string; name: string }[]>(await fetch(`/api/checkout/sessions/${id}/banks`))
}

export async function pay(id: string, body: unknown) {
  return parse<PaymentView>(await fetch(`/api/checkout/sessions/${id}/payments`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }))
}

export async function pollAsync(publicId: string) {
  return parse<{ url: string | null; status: string }>(await fetch(`/api/payments/${publicId}/async`))
}

export async function voucher(publicId: string) {
  return parse<{
    status: string
    product: string
    amountCents: number
    method: string
    reference: string
    createdAt: string
    email: string
  }>(await fetch(`/api/payments/${publicId}`))
}

export function cop(cents: number) {
  return new Intl.NumberFormat('es-CO', { style: 'currency', currency: 'COP', maximumFractionDigits: 0 }).format(cents / 100)
}

export function methodLabel(method: string) {
  if (method === 'CARD') return 'Tarjeta'
  if (method === 'PSE') return 'PSE'
  if (method === 'NEQUI') return 'Nequi'
  if (method === 'BANCOLOMBIA_TRANSFER') return 'Botón Bancolombia'
  return method
}
