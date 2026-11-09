import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { cop, methodLabel, voucher } from './api'

type Voucher = Awaited<ReturnType<typeof voucher>>

export function VoucherPage() {
  const [params] = useSearchParams()
  const [data, setData] = useState<Voucher | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    const pid = params.get('pid')
    if (!pid) {
      setError('Falta el comprobante.')
      return
    }
    voucher(pid).then(setData).catch((err: Error) => setError(err.message))
  }, [params])

  const title = data?.status === 'APPROVED' ? 'Compra aprobada' : data?.status === 'PENDING' ? 'Pago en proceso' : 'Compra rechazada'

  return (
    <main className="page">
      <section className="card">
        <p className="kicker">Comprobante</p>
        <h1>{error ? 'Sin comprobante' : title}</h1>
        {error && <p className="error">{error}</p>}
        {data && (
          <table className="rows">
            <tbody>
              <tr><td>Producto</td><td>{data.product}</td></tr>
              <tr><td>Monto</td><td>{cop(data.amountCents)}</td></tr>
              <tr><td>Método</td><td>{methodLabel(data.method)}</td></tr>
              <tr><td>Referencia</td><td>{data.reference}</td></tr>
              <tr><td>Fecha</td><td>{new Date(data.createdAt).toLocaleString('es-CO')}</td></tr>
              <tr><td>Correo</td><td>{data.email}</td></tr>
              <tr><td>Estado</td><td>{data.status}</td></tr>
            </tbody>
          </table>
        )}
      </section>
    </main>
  )
}
