# CreditDesk · API de originación de créditos

[![CI](https://github.com/edu-tech220/credit-desk-api/actions/workflows/ci.yml/badge.svg)](https://github.com/edu-tech220/credit-desk-api/actions/workflows/ci.yml)

API REST en **ASP.NET Core 8** para el ciclo de vida de una **solicitud de crédito**: el solicitante arma su
expediente (montos, avales y documentos), lo envía y un analista lo toma, lo dictamina y lo dispersa.
Las reglas de negocio viven en el dominio, la consistencia se protege con concurrencia optimista e
índices en base de datos, y todo sale con pruebas automatizadas, Docker y CI.

## ¿Qué resuelve?

En un portal de autogestión de créditos los problemas reales no son el CRUD, sino:

- **Validar el expediente completo antes de que llegue a mesa de crédito**: monto y plazo dentro del producto,
  capacidad de pago (mensualidad vs. ingreso), avales cuando el monto lo exige y documentos obligatorios.
  Todos los errores se devuelven juntos para que el solicitante los corrija en una sola vuelta.
- **Que dos analistas no tomen la misma solicitud** ni que un usuario sobrescriba cambios que no vio.
- **Que un solicitante no tenga dos solicitudes abiertas del mismo producto**, aun con peticiones simultáneas.
- **Trazabilidad**: quién cambió el estado, cuándo y por qué.

## Flujo de estados

```
            submit                review              approve             disburse
  Draft ───────────▶ Submitted ───────────▶ InReview ──────────▶ Approved ───────────▶ Disbursed
    │                    │                     │
    │ cancel             │ cancel              │ reject (motivo ≥ 10 caracteres)
    ▼                    ▼                     ▼
 Cancelled           Cancelled              Rejected
```

| Regla | Dónde vive | Respuesta |
|---|---|---|
| Monto/plazo dentro de los límites del producto | `LoanProduct.CollectTermsErrors` | 422 |
| Mensualidad ≤ % máximo del ingreso (amortización francesa) | `LoanApplication.Submit` | 422 |
| Aval obligatorio arriba del umbral; ingreso conjunto de avales ≥ 1 mensualidad | `LoanApplication.Submit` | 422 |
| Documentos obligatorios del producto | `LoanApplication.Submit` | 422 |
| RFC válido, máximo 2 avales, sin duplicados | `LoanApplication.AddGuarantor` | 422 / 409 |
| Sólo se edita en borrador y sólo el dueño | `LoanApplication` | 409 / 403 |
| Transiciones permitidas (tabla única) | `LoanApplication.AllowedTransitions` | 409 |
| Sólo el analista asignado dictamina y dispersa | `LoanApplication.EnsureAssignedAnalyst` | 403 |
| Una solicitud abierta por solicitante y producto | servicio + índice único filtrado | 409 |
| Cambio concurrente / versión obsoleta | token `Version` + `If-Match` | 409 / 412 |

## Arquitectura

```
src/CreditDesk.Api
├── Domain/          Entidades y reglas: LoanApplication (agregado), LoanProduct, Amortization, excepciones
├── Data/            DbContext (EF Core), migraciones de SQL Server, seed de catálogo y analistas
├── Services/        LoanApplicationService: visibilidad, If-Match, persistencia y traducción de conflictos
├── Controllers/     Auth, Products (catálogo y simulador), Applications (consultas y comandos)
├── Contracts/       DTOs de entrada/salida (los entidades nunca se serializan directo)
├── Security/        JWT, PBKDF2, extensiones de claims
└── Infrastructure/  Filtro de excepciones → ProblemDetails, ETags, políticas de rate limiting
tests/CreditDesk.Tests
├── Unit/            Dominio puro y amortización (sin base de datos)
└── Integration/     API real con WebApplicationFactory + SQLite temporal
```

Un solo proyecto de API organizado por carpetas: para este alcance, separar en cuatro ensamblados
añadiría ceremonia sin aislar nada que hoy se necesite aislar. La frontera importante sí se respeta:
**el dominio no depende de EF ni de ASP.NET** y los controladores no cambian estados directamente.

## Decisiones técnicas

- **Modelo de dominio rico.** `LoanApplication` expone métodos (`Submit`, `StartReview`, `Approve`…) con setters
  privados; las transiciones salen de una sola tabla. Así una regla nueva se prueba con un test unitario, sin HTTP.
- **Concurrencia optimista portable.** En lugar de `rowversion` (exclusivo de SQL Server) se usa una columna
  `Version` (GUID) marcada como `IsConcurrencyToken` que el dominio renueva en cada cambio. Funciona igual en
  SQL Server y SQLite, y se expone como `ETag`. Los comandos aceptan `If-Match`: versión obsoleta → **412**;
  si la carrera ocurre entre la lectura y el `UPDATE`, EF lanza `DbUpdateConcurrencyException` → **409**.
- **Invariantes respaldadas por la base.** "Una solicitud abierta por producto" se valida en el servicio *y*
  con un índice único filtrado (`[IsOpen] = 1`), porque dos peticiones simultáneas pueden pasar la validación.
- **Errores consistentes (RFC 7807).** Cada excepción de dominio se traduce a `ProblemDetails` con un `code`
  estable (`invalid_transition`, `open_application_exists`, `concurrency_conflict`…). Entrada mal formada → 400
  (validación de modelo); reglas de negocio → 422. Se usa un filtro MVC para que los errores esperados no se
  registren como fallas del servidor.
- **Dinero en `decimal`.** La amortización francesa se calcula sin `double`, redondeando a centavos por periodo;
  el último pago absorbe el residuo para que el saldo cierre exactamente en 0 (probado con varios escenarios).
- **Privacidad por defecto.** Un solicitante que consulta una solicitud ajena recibe 404, no 403, para no revelar
  que existe. Los analistas no se registran por la API: se crean desde configuración.
- **Seguridad.** JWT con roles (`Applicant`, `Analyst`), contraseñas con PBKDF2-SHA256 (100 000 iteraciones, sal
  aleatoria, comparación en tiempo constante), rate limiting por IP en `/api/auth/*`, mismo mensaje para usuario
  inexistente y contraseña incorrecta. La llave JWT se exige al arrancar (≥ 32 bytes) y nunca está en el repo
  para entornos reales (sólo un valor de desarrollo local en `appsettings.Development.json`).
- **Migraciones versionadas para SQL Server**; SQLite (desarrollo y pruebas) crea el esquema desde el modelo.
  El CI falla si el modelo cambió sin generar migración (`has-pending-model-changes`).
- **Split queries** para cargar el agregado (avales, documentos y bitácora) sin producto cartesiano.
- **`TimeProvider`** inyectado en lugar de `DateTime.UtcNow` disperso.

## Endpoints

| Método | Ruta | Rol | Descripción |
|---|---|---|---|
| POST | `/api/auth/register` | — | Registro de solicitante |
| POST | `/api/auth/login` | — | Devuelve JWT |
| GET | `/api/products` | — | Catálogo |
| GET | `/api/products/{code}/quote?amount=&termMonths=` | — | Simulador con tabla de amortización |
| GET | `/api/applications?status=&productCode=&page=&pageSize=` | ambos | Paginado; el solicitante ve las suyas, el analista todas (`status=Submitted` en orden FIFO) |
| GET | `/api/applications/{id}` | ambos | Detalle con avales, documentos, bitácora y `ETag` |
| GET | `/api/applications/{id}/schedule` | ambos | Tabla de amortización |
| POST | `/api/applications` | Applicant | Crea borrador |
| PUT | `/api/applications/{id}/terms` | Applicant | Cambia monto, plazo e ingreso |
| POST / DELETE | `/api/applications/{id}/guarantors[/{guarantorId}]` | Applicant | Avales |
| PUT | `/api/applications/{id}/documents` | Applicant | Registra documento del expediente (reemplaza el del mismo tipo) |
| POST | `/api/applications/{id}/submit` · `/cancel` | Applicant | Envío / cancelación |
| POST | `/api/applications/{id}/review` · `/approve` · `/reject` · `/disburse` | Analyst | Mesa de crédito |

Productos sembrados: `PERSONAL` (aval arriba de 100 000), `NOMINA` (sin aval) y `PYME` (aval arriba de 250 000).

> Alcance: los documentos se registran como metadatos (tipo y nombre de archivo); el almacenamiento de
> archivos (blob storage, antivirus) queda fuera de este proyecto a propósito.

## Cómo correrlo

### Local (SQLite, sin dependencias)

```bash
dotnet run --project src/CreditDesk.Api --launch-profile http
# Swagger: http://localhost:5180/swagger
```

En `Development` se usa `creditdesk-dev.db` (SQLite) y se crea el analista `analista@creditdesk.local`
con la contraseña de desarrollo definida en `appsettings.Development.json`.

### Docker Compose (API + SQL Server 2022)

```bash
cp .env.example .env          # cambia las contraseñas y la llave JWT
docker compose up --build
# Swagger: http://localhost:8080/swagger
```

La API espera a que SQL Server esté sano (healthcheck), aplica migraciones y siembra el catálogo.

### Recorrido rápido

```bash
# 1. Registrar solicitante y guardar el token
TOKEN=$(curl -s -X POST localhost:8080/api/auth/register -H "Content-Type: application/json" \
  -d '{"email":"ana@example.com","fullName":"Ana López","password":"Secreta#2026"}' | jq -r .accessToken)

# 2. Crear borrador, adjuntar documentos y enviar
ID=$(curl -s -X POST localhost:8080/api/applications -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"productCode":"NOMINA","amount":30000,"termMonths":12,"monthlyIncome":15000}' | jq -r .id)
curl -s -X PUT localhost:8080/api/applications/$ID/documents -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"type":"OfficialId","fileName":"ine.pdf"}'
curl -s -X PUT localhost:8080/api/applications/$ID/documents -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"type":"ProofOfIncome","fileName":"nomina.pdf"}'
curl -s -X POST localhost:8080/api/applications/$ID/submit -H "Authorization: Bearer $TOKEN"
```

## Pruebas

```bash
dotnet test
```

55 pruebas que no requieren servicios externos:

- **Unitarias** (dominio y amortización): transiciones válidas e inválidas, validación agregada del expediente,
  capacidad de pago, avales, RFC, bitácora, cuadre exacto de la tabla de amortización.
- **Integración** con `WebApplicationFactory` contra la API real y un archivo SQLite temporal por fábrica:
  autenticación y roles, 404 para recursos ajenos, 409/412/422 con su `code`, paginación FIFO de la bandeja y
  una **carrera real**: 12 peticiones simultáneas de 3 analistas para tomar la misma solicitud → exactamente una
  gana y el resto recibe 409. (Se usa archivo y no `:memory:` para que las conexiones concurrentes compartan la base.)

## CI

`.github/workflows/ci.yml`: restore → build → test (con resultados `.trx` como artefacto) → verificación de
migraciones pendientes → build de la imagen Docker.

## Stack

C# 12 · .NET 8 · ASP.NET Core Web API · Entity Framework Core 8 · SQL Server 2022 · SQLite · JWT Bearer ·
xUnit · WebApplicationFactory · Swagger/OpenAPI · Docker · Docker Compose · GitHub Actions
