# Pasarela

El código se puede ver. Cualquier otro uso necesita autorización del titular. Ver [LICENSE](LICENSE).

Pasarela de pagos aislada. Cobra con Wompi y avisa al origen. No lee la base de Máquina ni la de ningún otro producto.

## Qué corre

Un contenedor sirve el front (React) y la API (.NET 8). Postgres va aparte. En Railway: una réplica, App Sleeping apagado, healthcheck en `/health`.

```bash
docker compose up --build
```

Abre `http://localhost:8090/health`. El puerto del host es 8090 para no chocar con otros servicios locales; el contenedor sigue escuchando en 8080. Con `SEED_DEMO=true` hay un origen demo y dos productos. En test, `GET /api/dev/sign?productId=1&reference=usuario-1` devuelve la ruta firmada de `/pagar`. Ese endpoint responde 404 si `APP_ENV=production`.

## Ambientes

| `APP_ENV` | Llaves Wompi | Rate limit |
| --- | --- | --- |
| `test` | `pub_test_` / `prv_test_` (sandbox) | no |
| `production` | llaves reales | 10 cobros/min/IP y 5 fallos/hora/IP |

`TRUSTED_PROXIES=railway` (o `*`) hace que la API tome la IP de `X-Forwarded-For`. Sin eso usa la IP de la conexión.

## Variables

Ver `.env.example`. Los secretos de cada origen (`signing_secret`, `notify_secret`, `mail_from`) viven en la tabla `origins`, no en el entorno.

`WOMPI_INTEGRITY_SECRET` firma el cobro. `WOMPI_EVENTS_SECRET` verifica el webhook. No se mezclan.

Webhook público: `{PUBLIC_BASE_URL}/api/webhooks/wompi`.

## Firma del checkout

`HMAC-SHA256` de `{id}.{ref}.{ts}` con el `signing_secret` del origen. El punto es parte del mensaje. `ts` es unix en segundos, máximo 15 minutos atrás y 1 minuto hacia el futuro.

```
/pagar?id=1&ref=usuario-1&ts=1700000000&sig=<hex>
```

La pantalla manda el monto solo para compararlo. El servidor cobra el precio leído en la base. Si no coincide, responde 409 y no cobra.

## Admin

`Authorization: Bearer {ADMIN_TOKEN}`

- `POST /api/admin/origins` con `name`, `signingSecret`, `notifySecret`, `mailFrom`
- `POST /api/admin/products` con `originId`, `name`, `priceCents`, `type` (`subscription` o `one_time`), `notifyUrl`, `notifyKind`
- `GET /api/admin/products`

`notify_kind` tiene que ser igual al tipo. Un producto de un solo pago no puede avisar a `/api/subscriptions/webhook`.

## Correo

El outbox manda el correo con Resend (`RESEND_API_KEY`). El remitente es `origins.mail_from`, por ejemplo `Pagos Hernan Elejalde Amin <pagos@dominio-verificado>`. El dominio tiene que tener SPF y DKIM en Resend.

## Cambios que Máquina tiene que hacer después

Esta pasarela no modifica Máquina. Máquina sigue siendo dueña del acceso. Cuando reciba el aviso:

- Guardar `event_id` y ignorar un evento repetido o más viejo.
- Si el aviso es de suscripción, exigir `current_period_end` y negar el acceso si ya pasó.
- Un estado `canceled` mantiene el acceso hasta `current_period_end`, no lo corta en el momento.
- Validar `x-signature` y `x-timestamp` (HMAC del cuerpo con `notify_secret`). Mientras migra, también llega `x-webhook-secret`.
- Resolver el producto por `external_reference`. El id de producto de esta pasarela no es un id de Máquina.
- El pago único necesita un endpoint distinto de `POST /api/subscriptions/webhook`.

El cuerpo del aviso va en snake_case: `user_id`, `status` (`active`, `inactive`, `canceled`), `external_reference`, `external_subscription_id`, `current_period_end`, `payment_id`, `payment_status`, `amount_in_cents`, `event`, `event_id`, `occurred_at`.
