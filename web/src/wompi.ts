const SRC = 'https://checkout.wompi.co/widget.js'

type WidgetCtor = new (opts: { widgetOperation: string; publicKey: string }) => {
  open: (cb: (result: unknown) => void) => void
}

declare global {
  interface Window { WidgetCheckout?: WidgetCtor }
}

function load(): Promise<void> {
  if (window.WidgetCheckout) return Promise.resolve()
  return new Promise((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SRC
    script.async = true
    script.onload = () => resolve()
    script.onerror = () => reject(new Error('No se pudo abrir Wompi.'))
    document.head.appendChild(script)
  })
}

function findToken(value: unknown, depth = 0): string | null {
  if (value == null || depth > 6) return null
  if (typeof value === 'string') return /^tok_[A-Za-z0-9_-]+$/.test(value) ? value : null
  if (typeof value === 'object') {
    for (const nested of Object.values(value as Record<string, unknown>)) {
      const found = findToken(nested, depth + 1)
      if (found) return found
    }
  }
  return null
}

export async function tokenize(publicKey: string) {
  await load()
  const Ctor = window.WidgetCheckout
  if (!Ctor) throw new Error('El widget de Wompi no está disponible.')
  return new Promise<string>((resolve, reject) => {
    const widget = new Ctor({ widgetOperation: 'tokenize', publicKey })
    widget.open((result) => {
      const token = findToken(result)
      if (!token) reject(new Error('Wompi no devolvió el token.'))
      else resolve(token)
    })
  })
}
