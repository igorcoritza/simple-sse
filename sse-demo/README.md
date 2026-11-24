# SSE demo: Angular + .NET 10

Una demo mínima de **Server-Sent Events (SSE)**: el backend en .NET 10 empuja mensajes al
frontend en Angular en tiempo real, sin que el navegador tenga que preguntar. Incluye además
un caso realista: un **job en segundo plano con Hangfire** que informa de su progreso al
navegador a través de **Redis pub/sub** y SSE.

```
sse-demo/
├── backend/            API mínima en .NET 10: stream SSE, envío de mensajes y encolado de jobs
├── worker/             Servidor Hangfire que ejecuta los jobs (y su dashboard)
├── shared/             Contrato del job y del evento, compartido por backend y worker
├── frontend/           App Angular que escucha el stream con EventSource
├── apphost/            .NET Aspire AppHost: orquesta todo el entorno local
├── servicedefaults/    Configuración común de Aspire (OpenTelemetry, health checks)
└── docker-compose.yml  Redis, por si quieres arrancarlo sin Aspire
```

## Arquitectura

```
                    POST /api/jobs                 Hangfire (storage en Redis)
 ┌──────────┐  ───────────────────▶ ┌─────────┐  ─────────────────────────▶ ┌──────────┐
 │ Angular  │                       │   API   │                             │  Worker  │
 │ frontend │  ◀─────────────────── │ backend │  ◀───────────────────────── │ Hangfire │
 └──────────┘   SSE /api/events     └─────────┘   Redis pub/sub             └──────────┘
                (evento "job")                    canal sse-demo:job-events
```

1. El navegador hace `POST /api/jobs`. La API **encola** el job en Hangfire y responde al
   momento (`202 Accepted`) con el id del job.
2. El **worker** (otro proceso) recoge el job de la cola y lo ejecuta paso a paso.
3. En cada paso, el worker **publica** un `JobEvent` en el canal de Redis
   `sse-demo:job-events`. No sabe nada de navegadores ni de SSE.
4. La API está **suscrita** a ese canal (`RedisJobEventsListener`). Cada mensaje que llega lo
   reenvía a todos los navegadores conectados como evento SSE `job`.
5. Angular recibe el evento y actualiza la barra de progreso.

¿Por qué Redis pub/sub? Porque la API y el worker son procesos distintos (podrían estar en
máquinas distintas, o haber varias instancias de la API). Pub/sub les permite hablar sin
conocerse: el worker publica y cualquier instancia de la API suscrita recibe el mensaje.
Redis además sirve como almacenamiento de Hangfire, así que solo hace falta un servicio.

## ¿Qué es SSE?

SSE es un estándar web para que **el servidor envíe datos al cliente** sobre una única
conexión HTTP que se queda abierta.

1. El navegador abre la conexión con `new EventSource(url)`.
2. El servidor responde con `Content-Type: text/event-stream` y **no cierra la respuesta**.
3. Cada vez que tiene algo nuevo, escribe un bloque de texto como este:

   ```
   event: tick
   data: {"type":"tick","text":"Tick #3 from the server","sentAt":"..."}
   retry: 3000

   ```

   - `event`: nombre del evento (el cliente lo escucha con `addEventListener('tick', ...)`).
   - `data`: el contenido (aquí, JSON).
   - `retry`: milisegundos que el navegador espera antes de reconectar si se cae la conexión.
   - Una línea en blanco cierra cada evento.

4. Si la conexión se corta, **el navegador reconecta solo**.

### SSE frente a WebSockets

| | SSE | WebSockets |
|---|---|---|
| Dirección | Servidor → cliente | Bidireccional |
| Protocolo | HTTP normal | Protocolo propio (`ws://`) |
| Reconexión | Automática | Hay que implementarla |
| Formato | Texto | Texto o binario |
| Casos típicos | Notificaciones, feeds, progreso, dashboards | Chats, juegos, colaboración en tiempo real |

Como SSE es unidireccional, en esta demo **enviar** un mensaje es un `POST` normal. El
servidor lo recibe y lo reparte a todos los clientes conectados a través del stream.

## Cómo funciona esta demo

### Backend (`backend/`)

- `GET /api/events`: el stream SSE. Usa `TypedResults.ServerSentEvents(...)`, que viene de
  serie en ASP.NET Core 10, a partir de un `IAsyncEnumerable<SseItem<T>>`.
- `POST /api/messages` con `{ "text": "..." }`: publica un mensaje para todos los clientes.
- `MessageBroadcaster`: guarda un `Channel` por cliente conectado y reparte cada mensaje a todos.
- `HeartbeatService`: un `BackgroundService` que publica un evento `tick` cada 5 segundos.
- `POST /api/jobs` con `{ "name": "...", "steps": 5 }`: encola un `IReportJob` en Hangfire.
  La API solo es **cliente** de Hangfire (no llama a `AddHangfireServer`), así que no ejecuta jobs.
- `RedisJobEventsListener`: se suscribe al canal de Redis y convierte cada mensaje en un evento SSE `job`.
- CORS permite `http://localhost:4200` (el servidor de desarrollo de Angular).

### Worker (`worker/`)

- Ejecuta el servidor de Hangfire (`AddHangfireServer`) con Redis como almacenamiento.
- `ReportJob`: simula un informe que tarda un segundo por paso y publica el progreso en Redis.
- Dashboard de Hangfire en <http://localhost:5260/hangfire> para ver la cola y los jobs terminados.

### Shared (`shared/`)

- `IReportJob`: la interfaz del job. Hangfire guarda en Redis solo la interfaz, el método y los
  argumentos; la API no necesita la implementación, y el worker la resuelve por inyección de dependencias.
- `JobEvent`: el mensaje que viaja por Redis y por SSE.
- `RedisConfig`: nombre del canal, prefijo de claves y opciones JSON comunes.

### Frontend (`frontend/`)

- `sse.service.ts`: envuelve `EventSource`, escucha los eventos `welcome`, `tick`, `custom` y
  `job`, y guarda los mensajes, los jobs y el estado de la conexión en *signals*.
- `app.ts` / `app.html`: muestra el estado, un panel para lanzar jobs con su barra de progreso,
  la lista de mensajes en vivo y un formulario para enviar mensajes.

## Cómo ejecutarlo

Requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 22 o superior y un
runtime de contenedores (Docker Desktop, OrbStack o Podman) arrancado, porque Redis corre en un
contenedor.

### Con .NET Aspire (recomendado)

Un solo comando arranca todo:

```bash
cd apphost
dotnet run
```

El AppHost (`apphost/AppHost.cs`) hace lo siguiente:

1. Descarga y arranca **Redis en un contenedor** (`AddRedis`), con **Redis Insight** para ver las
   claves y el canal de pub/sub.
2. Arranca la **API** y el **worker** y les inyecta la cadena de conexión de Redis
   (`ConnectionStrings__redis`). Ambos esperan a que Redis esté listo (`WaitFor`).
3. Ejecuta `npm install` y `npm start` en el **frontend** (Angular en `http://localhost:4200`)
   cuando la API ya está en marcha.
4. Abre el **dashboard de Aspire** (`http://localhost:15089`), con el estado de cada recurso, sus
   logs, y las trazas y métricas de OpenTelemetry. Ahí puedes seguir una petición `POST /api/jobs`
   desde la API hasta Redis.

La API y el worker usan `servicedefaults/` (`builder.AddServiceDefaults()`) para OpenTelemetry y
health checks, y la integración `Aspire.StackExchange.Redis` (`builder.AddRedisClient("redis")`)
para crear la conexión a Redis.

### Sin Aspire (a mano)

**1. Redis** (en `localhost:6379`):

```bash
docker compose up -d
```

**2. Backend** (escucha en `http://localhost:5250`):

```bash
cd backend
dotnet run
```

**3. Worker de Hangfire** (en otra terminal, dashboard en `http://localhost:5260/hangfire`):

```bash
cd worker
dotnet run
```

**4. Frontend** (en otra terminal, escucha en `http://localhost:4200`):

```bash
cd frontend
npm install
npm start
```

En este modo la cadena de conexión de Redis sale de `appsettings.json` de `backend/` y `worker/`
(`ConnectionStrings:redis`).

### Probar la demo

Abre <http://localhost:4200>. Verás un `tick` cada 5 segundos. Pulsa **Start job** y mira cómo
avanza la barra de progreso mientras el worker trabaja. Abre la página en dos pestañas: los
mensajes y el progreso de los jobs aparecen en las dos.

### Probar el stream sin el frontend

```bash
curl -N http://localhost:5250/api/events
```

Y en otra terminal:

```bash
curl -X POST http://localhost:5250/api/messages -H "Content-Type: application/json" -d '{"text":"hola"}'
curl -X POST http://localhost:5250/api/jobs -H "Content-Type: application/json" -d '{"name":"Ventas","steps":3}'
```

Para ver los mensajes que pasan por Redis (con Aspire, usa Redis Insight desde el dashboard; sin Aspire):

```bash
redis-cli SUBSCRIBE sse-demo:job-events
```

## Tests

```bash
cd frontend && npm test
```
