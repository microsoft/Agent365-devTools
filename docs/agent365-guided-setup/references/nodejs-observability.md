# Node.js — A365 Observability Reference

Authoritative package versions and code patterns for instrumenting A365 observability
into a Node.js agent. All samples mirror the official Microsoft Learn docs (updated 2026-04-30).

---

## npm Packages

| Package | Purpose |
|---------|---------|
| `@microsoft/agents-a365-observability` | Logger/exporter helpers such as `setLogger`, `ExporterEventNames`, and additional observability contracts |
| `@microsoft/agents-a365-observability-hosting` | `AgenticTokenCacheInstance`, `BaggageBuilderUtils`, `BaggageMiddleware`, `ObservabilityHostingManager`, `ScopeUtils` |
| `@microsoft/agents-a365-runtime` | `getObservabilityAuthenticationScope()`, `ClusterCategory` |

**Unified distro entry point:**

| Package | Purpose |
|---------|---------|
| `@microsoft/opentelemetry` (v0.1.0-beta.1) | Required entry point: `useMicrosoftOpenTelemetry()`, `shutdownMicrosoftOpenTelemetry()`, all scope types (`BaggageBuilder`, `InvokeAgentScope`, `InferenceScope`, `ExecuteToolScope`), `AgentDetails`, and all contract types |
| `@azure/msal-node` (^3.6.0) | MSAL `ConfidentialClientApplication` with `fmiPath` for the FMI token chain |
| `@azure/identity` (^4.6.0) | `ManagedIdentityCredential` for MSI-based token acquisition |

Install commands:
```bash
# Required for all agents
npm install @microsoft/opentelemetry@0.1.0-beta.1
npm install @microsoft/agents-a365-observability
npm install @microsoft/agents-a365-runtime

# Required for AI Teammate agents (hosting path)
npm install @microsoft/agents-a365-observability-hosting

# S2S token service dependencies
npm install @azure/msal-node @azure/identity

# Optional auto-instrumentation extensions
npm install @microsoft/agents-a365-observability-extensions-openai
npm install @microsoft/agents-a365-observability-extensions-langchain
```

The unified distro `useMicrosoftOpenTelemetry()` entry point is used for both OBO and S2S flows.

Minimum Node.js: **18.x** (LTS). TypeScript: **5.x** recommended.

---

## Entry Point — Observability Init (before any LLM imports)

### Configuration

Initialize the unified distro before importing the rest of your app so LangChain auto-instrumentation can patch libraries.

```typescript
// A365 Observability — best-effort instrumentation (verify against official sample)
// index.ts — must be called BEFORE importing other modules
import { configDotenv } from 'dotenv';
configDotenv();

import { useMicrosoftOpenTelemetry } from '@microsoft/opentelemetry';
import { tokenResolver } from './token-cache';
import { AgenticTokenCacheInstance } from '@microsoft/agents-a365-observability-hosting';

useMicrosoftOpenTelemetry({
  a365: {
    enabled: true,
    // Option 1: Custom token resolver with local cache (sample default when Use_Custom_Resolver=true)
    tokenResolver: process.env.Use_Custom_Resolver === 'true'
      ? (agentId: string, tenantId: string) => tokenResolver(agentId, tenantId) ?? ''
      : (agentId: string, tenantId: string) => AgenticTokenCacheInstance.getObservabilityToken(agentId, tenantId) ?? '',
  },
  // instrumentationOptions is optional — omit unless you need framework-specific auto-instrumentation.
  // The @microsoft/agents-a365-observability-extensions-langchain package has a peer dep conflict
  // with @langchain/core@^0.3.0, so manual scopes (InvokeAgentScope, InferenceScope, etc.) are preferred.
});
```

> **Auto-instrumentation:** `instrumentationOptions: { langchain: {} }` is optional and only useful
> if `@microsoft/agents-a365-observability-extensions-langchain` is installed (requires `@langchain/core@^1.1.32`).
> For most agents, manual scopes are sufficient and avoid the peer dependency conflict.

### S2S configuration (`authMode: S2S`)

S2S observability is supported for Node.js. The token service uses a **3-hop FMI (Federated Managed Identity) token chain**:

```
Blueprint (client_credentials / MSI)
  → Hop 1+2: FMI token (api://AzureADTokenExchange/.default with fmiPath=agentId)
    → Agent Identity token
      → Hop 3: Observability API token (scope=api://9b975845-388f-4429-889e-eab1ef63949c/.default)
```

No OBO user token is required.

> **Auth strategy** is controlled by `AGENT365_USE_MANAGED_IDENTITY`:
>   - `true` (production) — MSI → Blueprint FIC → Agent Identity → API
>   - `false` (local dev) — Client Secret → Blueprint FIC → Agent Identity → API

> **IMPORTANT — MSAL `fmiPath` limitation (as of 2026-04-30):** In the
> `acquireTokenByClientCredential()` flow used by the **client-secret path**, published versions
> of `@azure/msal-node` (v3.x or v5.x) do not serialize a caller-supplied `fmiPath` to the
> token endpoint. Passing `fmiPath` via `acquireTokenByClientCredential()` with `as any` results
> in `AADSTS82008: All agentic applications requesting a token exchange token must include the
> fmipath parameter`. **Workaround:** For the client-secret path (`acquireT1ViaClientSecret`),
> use a direct HTTP POST to the `/oauth2/v2.0/token` endpoint with `fmi_path` as a form
> parameter. The MSI path (`acquireT1ViaMsi`) is still expected to work because it obtains the
> Blueprint/FMI token through `ManagedIdentityCredential` (a `client_assertion` flow, not a
> `client_credentials` + secret flow), rather than relying on MSAL to serialize `fmiPath` on a
> standard client-credential request. This workaround will be removed once MSAL ships native
> `fmiPath` support for the client-secret credential path.

> **Note:** `a365 setup all` attempts to grant `Agent365.Observability.OtelWrite` to the Agent Identity SP, but this requires **Global Administrator** privileges. If the assignment fails (403), a Global Admin must manually grant the role via Entra portal — otherwise trace exports will return HTTP 403.

> **IMPORTANT — SDK `useS2SEndpoint` bug (v0.1.0-beta.1):** The `@microsoft/opentelemetry`
> distro does **not** pass `useS2SEndpoint` to `Agent365Exporter`. The exporter defaults
> `useS2SEndpoint` to `false`, sending spans to `/observability/` instead of
> `/observabilityService/`. S2S tokens are rejected (HTTP 401) on the non-S2S endpoint.
> **Workaround:** Create a custom `Agent365Exporter` with `useS2SEndpoint: true` via
> `spanProcessors` and do **not** pass `a365` options to the distro (see Step 3 entry point).
> Also set `ENABLE_A365_OBSERVABILITY_EXPORTER=false` in `.env` — this env var has highest
> precedence and overrides programmatic `enabled: false`, re-creating the broken built-in exporter.

> **Auto-instrumentation note:** The `instrumentationOptions: { langchain: {} }` option is
> **not required** for autonomous agents. The distro attempts OpenAI Agents auto-instrumentation
> by default (logs a benign `ERR_MODULE_NOT_FOUND` warning for `@openai/agents` if not installed).
> The optional `@microsoft/agents-a365-observability-extensions-langchain` package has a peer
> dependency on `@langchain/core@^1.1.32` which conflicts with `@langchain/core@^0.3.0` used
> by most LangChain projects — skip it and use manual scopes instead.

#### Step 1 — Create `observability/token-cache.ts`

Simple in-memory token cache shared by the token service and the OTel exporter:

```typescript
// observability/token-cache.ts
// A365 Observability — best-effort instrumentation (verify against official sample)

interface CacheEntry {
  token: string;
  expiresAt: number; // Unix ms
}

const EXPIRY_BUFFER_MS = 5 * 60 * 1000; // 5 minutes

const cache = new Map<string, CacheEntry>();

export function cacheToken(agentId: string, tenantId: string, token: string, expiresInMs: number = 60 * 60 * 1000): void {
  const key = `${agentId}:${tenantId}`;
  cache.set(key, {
    token,
    expiresAt: Date.now() + expiresInMs,
  });
}

export function getCachedToken(agentId: string, tenantId: string): string | null {
  const key = `${agentId}:${tenantId}`;
  const entry = cache.get(key);

  if (!entry) {
    return null;
  }

  if (Date.now() + EXPIRY_BUFFER_MS >= entry.expiresAt) {
    cache.delete(key);
    return null;
  }

  return entry.token;
}

/**
 * Token resolver called by the A365 Observability exporter when exporting telemetry.
 */
export const tokenResolver = (agentId: string, tenantId: string): string | null => {
  return getCachedToken(agentId, tenantId);
};
```

#### Step 2 — Create `observability/observability-token-service.ts`

Background token acquisition via MSAL 3-hop FMI chain:

```typescript
// observability/observability-token-service.ts
// A365 Observability — best-effort instrumentation (verify against official sample)
// A365 auth mode: S2S — 3-hop FMI token chain (MSAL)
//   Hop 1+2: Blueprint (MSI or client secret) → T1 via FMI path → Agent Identity
//   Hop 3:   Agent Identity uses T1 as assertion → Observability API token

import { ConfidentialClientApplication } from '@azure/msal-node';
import { ManagedIdentityCredential } from '@azure/identity';
import { cacheToken } from './token-cache';

const FMI_SCOPES = ['api://AzureADTokenExchange/.default'];
const OBSERVABILITY_SCOPES = ['api://9b975845-388f-4429-889e-eab1ef63949c/.default'];
const REFRESH_INTERVAL_MS = 50 * 60 * 1000; // 50 minutes

export interface TokenServiceConfig {
  tenantId: string;
  agentId: string;
  blueprintClientId: string;
  blueprintClientSecret: string;
  useManagedIdentity: boolean;
}

export function startTokenService(config: TokenServiceConfig): ReturnType<typeof setInterval> {
  console.log(`[A365 Observability] Token service started (useManagedIdentity=${config.useManagedIdentity}).`);

  const run = async () => {
    try {
      await acquireAndRegisterToken(config);
    } catch (error) {
      console.warn(`[A365 Observability] Failed to acquire token; will retry in ${REFRESH_INTERVAL_MS / 1000}s.`, error);
    }
  };

  // Acquire immediately, then on interval
  run();
  return setInterval(run, REFRESH_INTERVAL_MS);
}

async function acquireAndRegisterToken(config: TokenServiceConfig): Promise<void> {
  const authority = `https://login.microsoftonline.com/${config.tenantId}`;

  // Hop 1+2: Blueprint → T1 via FMI path
  const t1Token = config.useManagedIdentity
    ? await acquireT1ViaMsi(authority, config.blueprintClientId, config.agentId)
    : await acquireT1ViaClientSecret(authority, config.blueprintClientId, config.blueprintClientSecret, config.agentId);

  // Hop 3: Agent Identity uses T1 → Observability API token
  const identityApp = new ConfidentialClientApplication({
    auth: {
      clientId: config.agentId,
      authority,
      clientAssertion: t1Token,
    },
  });

  const obsResult = await identityApp.acquireTokenByClientCredential({
    scopes: OBSERVABILITY_SCOPES,
  });

  if (!obsResult?.accessToken) {
    throw new Error('Failed to acquire observability token: no access token returned');
  }

  const expiresInMs = obsResult.expiresOn
    ? obsResult.expiresOn.getTime() - Date.now()
    : 55 * 60 * 1000;
  cacheToken(config.agentId, config.tenantId, obsResult.accessToken, expiresInMs);
  console.log(`[A365 Observability] Token registered for agent ${config.agentId}.`);
}

async function acquireT1ViaMsi(authority: string, blueprintClientId: string, agentId: string): Promise<string> {
  // ManagedIdentityCredential.getToken uses a resource URI (no /.default suffix).
  const credential = new ManagedIdentityCredential();
  const msiToken = await credential.getToken('api://AzureADTokenExchange');

  const blueprintApp = new ConfidentialClientApplication({
    auth: {
      clientId: blueprintClientId,
      authority,
      clientAssertion: msiToken.token,
    },
  });

  const result = await blueprintApp.acquireTokenByClientCredential({
    scopes: FMI_SCOPES,
    azureRegion: undefined,
    fmiPath: agentId,
  } as any); // fmiPath is available in MSAL Node but not yet in stable types

  if (!result?.accessToken) {
    throw new Error('FMI T1 via MSI failed: no access token returned');
  }
  return result.accessToken;
}

async function acquireT1ViaClientSecret(authority: string, blueprintClientId: string, blueprintClientSecret: string, agentId: string): Promise<string> {
  // Direct HTTP request — @azure/msal-node does not yet serialize fmiPath to the token endpoint.
  // Use native fetch to POST with fmi_path form parameter until MSAL ships support.
  const tokenUrl = `${authority}/oauth2/v2.0/token`;
  const params = new URLSearchParams({
    client_id: blueprintClientId,
    client_secret: blueprintClientSecret,
    scope: FMI_SCOPES[0],
    grant_type: 'client_credentials',
    fmi_path: agentId,
  });

  const response = await fetch(tokenUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: params.toString(),
  });

  if (!response.ok) {
    const errorBody = await response.text();
    throw new Error(`FMI T1 via client secret failed (${response.status}): ${errorBody}`);
  }

  const data = await response.json() as { access_token?: string };
  if (!data.access_token) {
    throw new Error('FMI T1 via client secret failed: no access_token in response');
  }
  return data.access_token;
}
```

#### Step 3 — Wire in entry point (`index.ts`)

```typescript
// authMode: S2S — service principal, no user OBO.
import { configDotenv } from 'dotenv';
configDotenv();

import {
  useMicrosoftOpenTelemetry,
  shutdownMicrosoftOpenTelemetry,
  Agent365Exporter,
  A365SpanProcessor,
} from '@microsoft/opentelemetry';
import type { AgentDetails, CallerDetails, UserDetails } from '@microsoft/opentelemetry';
import { BatchSpanProcessor } from '@opentelemetry/sdk-trace-base';

import { tokenResolver } from './observability/token-cache';
import { startTokenService } from './observability/observability-token-service';

// ── Configuration ────────────────────────────────────────────────────────────
const TENANT_ID = process.env.AGENT365_TENANT_ID || '';
const AGENT_ID = process.env.AGENT365_AGENT_ID || '';
const BLUEPRINT_ID = process.env.AGENT365_BLUEPRINT_ID || '';
const CLIENT_ID = process.env.AGENT365_CLIENT_ID || '';
const CLIENT_SECRET = process.env.AGENT365_CLIENT_SECRET || '';
const AGENT_NAME = process.env.AGENT365_AGENT_NAME || 'my-agent';
const AGENT_DESCRIPTION = process.env.AGENT365_AGENT_DESCRIPTION || '';
const SPONSOR_USER_ID = process.env.agent365Observability__sponsorUserId || CLIENT_ID || '';
const SPONSOR_USER_NAME = process.env.agent365Observability__sponsorUserName || AGENT_NAME;
const SPONSOR_USER_EMAIL = process.env.agent365Observability__sponsorUserEmail || '';
const USE_MANAGED_IDENTITY = (process.env.AGENT365_USE_MANAGED_IDENTITY || 'true').toLowerCase() === 'true';
const USE_S2S_ENDPOINT = (process.env.AGENT365_USE_S2S_ENDPOINT || 'false').toLowerCase() === 'true';

function hasA365Credentials(): boolean {
  const requiredValues = [TENANT_ID, AGENT_ID, CLIENT_ID];
  const hasRequired = requiredValues.every(v => v && !v.startsWith('<<'));
  if (!hasRequired) return false;
  if (USE_MANAGED_IDENTITY) return true;
  return !!CLIENT_SECRET && !CLIENT_SECRET.startsWith('<<');
}

const A365_ENABLED = hasA365Credentials();

// ── Agent Details ────────────────────────────────────────────────────────────
export const agentDetails: AgentDetails = {
  agentId: AGENT_ID || 'local-dev',
  agentName: AGENT_NAME,
  agentDescription: AGENT_DESCRIPTION,
  agentBlueprintId: BLUEPRINT_ID,
  tenantId: TENANT_ID || 'local-dev',
};

export const userDetails: UserDetails = {
  userId: SPONSOR_USER_ID || 'unknown',
  userName: SPONSOR_USER_NAME || 'Blueprint Sponsor',
  userEmail: SPONSOR_USER_EMAIL,
};

export const callerDetails: CallerDetails = {
  userDetails,
};

// ── Observability ────────────────────────────────────────────────────────────
// Microsoft OpenTelemetry distro with A365 exporter.
// Token resolver reads from in-memory cache populated by the background token service.
//
// SDK workaround (v0.1.0-beta.1): The distro does not pass `useS2SEndpoint`
// to Agent365Exporter. When AGENT365_USE_S2S_ENDPOINT=true, we supply our own
// A365SpanProcessor + Agent365Exporter via `spanProcessors` instead.
// IMPORTANT: Set ENABLE_A365_OBSERVABILITY_EXPORTER=false in .env to prevent
// the env var from overriding the programmatic `enabled` setting.
const a365TokenResolver = (agentId: string, tenantId: string) =>
  tokenResolver(agentId, tenantId) ?? '';

const s2sSpanProcessors = A365_ENABLED && USE_S2S_ENDPOINT
  ? [
      new A365SpanProcessor(),
      new BatchSpanProcessor(
        new Agent365Exporter({
          useS2SEndpoint: true,
          tokenResolver: a365TokenResolver,
        })
      ),
    ]
  : [];

useMicrosoftOpenTelemetry({
  // When using S2S workaround, don't pass a365 options (avoids duplicate exporter
  // or noisy console fallback). Otherwise let the distro create its own exporter.
  a365: A365_ENABLED && !USE_S2S_ENDPOINT
    ? {
        enabled: true,
        tokenResolver: a365TokenResolver,
      }
    : undefined,
  spanProcessors: s2sSpanProcessors,
});

// ... import app modules AFTER observability init ...

// Start background token service after server is listening
const tokenServiceInterval = A365_ENABLED
  ? startTokenService({
      tenantId: TENANT_ID,
      agentId: AGENT_ID,
      blueprintClientId: CLIENT_ID,
      blueprintClientSecret: CLIENT_SECRET,
      useManagedIdentity: USE_MANAGED_IDENTITY,
    })
  : undefined;

// Graceful shutdown:
function shutdown(signal: string) {
  console.log(`${signal} received — shutting down`);
  if (tokenServiceInterval) {
    clearInterval(tokenServiceInterval);
  }
  shutdownMicrosoftOpenTelemetry().finally(() => process.exit(0));
}
process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));
```

#### S2S environment variables

```dotenv
# Agent 365 Observability — S2S
AGENT365_TENANT_ID=
AGENT365_AGENT_ID=
AGENT365_BLUEPRINT_ID=
AGENT365_CLIENT_ID=
AGENT365_CLIENT_SECRET=
AGENT365_AGENT_NAME=my-agent
AGENT365_AGENT_DESCRIPTION=
agent365Observability__sponsorUserId=<<Blueprint ID>>
agent365Observability__sponsorUserName=<<Blueprint Name>>
agent365Observability__sponsorUserEmail=<<Blueprint Sponsor Email>>
AGENT365_USE_MANAGED_IDENTITY=true
AGENT365_USE_S2S_ENDPOINT=true
# IMPORTANT: Must be false when using the S2S workaround (AGENT365_USE_S2S_ENDPOINT=true),
# because this env var overrides the programmatic `enabled` setting in A365Configuration.
# The custom Agent365Exporter with useS2SEndpoint handles export instead.
ENABLE_A365_OBSERVABILITY_EXPORTER=false
```

Message handler baggage setup is **identical** to `user-delegated` / `agentic-identity` — only the token resolver and credential source differ. Do **not** call `AgenticTokenCacheInstance.RefreshObservabilityToken` for S2S agents.

---

## Adapter — BaggageMiddleware

Register `BaggageMiddleware` to auto-populate baggage from every incoming `TurnContext`.
This removes the need to call `BaggageBuilder` manually in each activity handler.

```typescript
import { BaggageMiddleware } from '@microsoft/agents-a365-observability-hosting';

// Option 1: Register middleware directly on the adapter
adapter.use(new BaggageMiddleware());
// The middleware skips async replies (ContinueConversation) to avoid overwriting baggage.
```

```typescript
import { ObservabilityHostingManager } from '@microsoft/agents-a365-observability-hosting';

// Option 2: Use ObservabilityHostingManager for composite configuration
const manager = new ObservabilityHostingManager();
manager.configure(adapter, { enableBaggage: true });
```

---

## Message Handler — Token Refresh + BaggageBuilder

For OBO / user-delegated / agentic-identity flows, the official sample now builds the baggage scope from `TurnContext`, optionally adds `sessionDescription(...)`, preloads the exporter token, then runs the agent logic inside `baggageScope.run(...)`.

The sample supports **two token refresh patterns**:
- **Option 1 (sample default when `Use_Custom_Resolver=true`)** — exchange the OBO token yourself and cache it with `createAgenticTokenCacheKey(...)`
- **Option 2** — call `AgenticTokenCacheInstance.RefreshObservabilityToken(...)`

```typescript
// A365 Observability — best-effort instrumentation (verify against official sample)
import { BaggageBuilder } from '@microsoft/opentelemetry';
import { AgenticTokenCacheInstance, BaggageBuilderUtils } from '@microsoft/agents-a365-observability-hosting';
import { getObservabilityAuthenticationScope } from '@microsoft/agents-a365-runtime';
import tokenCache, { createAgenticTokenCacheKey } from './token-cache';

// Inside your AgentApplication subclass / message handler:
async function handleMessage(turnContext: TurnContext, state: ApplicationTurnState) {
  const baggageScope = BaggageBuilderUtils.fromTurnContext(
    new BaggageBuilder(),
    turnContext
  ).sessionDescription('Initial onboarding session')
    .build();

  await preloadObservabilityToken(turnContext);

  try {
    await baggageScope.run(async () => {
      // ... your LangChain invocation, tool calls, streaming, etc. ...
    });
  } finally {
    baggageScope.dispose();
  }
}

async function preloadObservabilityToken(turnContext: TurnContext): Promise<void> {
  const agentId = turnContext.activity?.recipient?.agenticAppId ?? '';
  const tenantId = turnContext.activity?.recipient?.tenantId ?? '';

  if (process.env.Use_Custom_Resolver === 'true') {
    // Option 1: Custom cache
    const aauToken = await agentApplication.authorization.exchangeToken(turnContext, 'agentic', {
      scopes: getObservabilityAuthenticationScope()
    });
    const cacheKey = createAgenticTokenCacheKey(agentId, tenantId);
    tokenCache.set(cacheKey, aauToken?.token || '');
  } else {
    // Option 2: Built-in cache
    await AgenticTokenCacheInstance.RefreshObservabilityToken(
      agentId,
      tenantId,
      turnContext,
      agentApplication.authorization,
      getObservabilityAuthenticationScope()
    );
  }
}
```

> If you already registered `BaggageMiddleware`, you can usually skip the manual `BaggageBuilderUtils.fromTurnContext(...)` call, but the per-turn token preload/refresh step is still required for OBO export.

---

## Manual Instrumentation Scopes

> **Store publishing requirement:** `InvokeAgentScope`, `InferenceScope`, and `ExecuteToolScope`
> are **required** for store validation. Missing any one causes store validation failure.

> **Import source:** Import all scope types (`InvokeAgentScope`, `InferenceScope`, `ExecuteToolScope`, `BaggageBuilder`, `AgentDetails`, etc.) from `@microsoft/opentelemetry`.

```typescript
import {
  BaggageBuilder,
  InvokeAgentScope,
  InferenceScope,
  ExecuteToolScope,
  InferenceOperationType,
} from '@microsoft/opentelemetry';
import type {
  AgentDetails,
  InferenceDetails,
  InvokeAgentScopeDetails,
  A365Request,
  ToolCallDetails,
} from '@microsoft/opentelemetry';
```

### InvokeAgentScope

```typescript
import {
  InvokeAgentScope,
  InvokeAgentScopeDetails,
  AgentDetails,
  CallerDetails,
  UserDetails,
  Channel,
  Request,
  ServiceEndpoint,
} from '@microsoft/opentelemetry';

// Use the same agentDetails and request instances across all scopes in a request.
const agentDetails: AgentDetails = {
  agentId: 'agent-456',
  agentName: 'Email Assistant',
  agentDescription: 'An AI agent powered by Azure OpenAI',
  agentAUID: 'auid-123',
  agentEmail: 'agent@contoso.com',  // note: interface field is agentAUID (uppercase UID)
  agentBlueprintId: 'blueprint-789',
  tenantId: 'tenant-123',
};

const scopeDetails: InvokeAgentScopeDetails = {
  endpoint: { host: 'myagent.contoso.com', port: 443 } as ServiceEndpoint,
};

const request: Request = {
  content: 'Please help me organize my emails',
  sessionId: 'session-42',
  conversationId: 'conv-xyz',
  channel: { name: 'msteams' } as Channel,
};

const callerDetails: CallerDetails = {
  userDetails: {
    userId: 'user-123',
    userEmail: 'jane.doe@contoso.com',
    userName: 'Jane Doe',
  } as UserDetails,
};

const scope = InvokeAgentScope.start(request, scopeDetails, agentDetails, callerDetails);

try {
  await scope.withActiveSpanAsync(async () => {
    scope.recordInputMessages(['Please help me organize my emails']);

    const response = await invokeAgent(request.content);

    scope.recordOutputMessages(['I found 15 urgent emails', 'Here is your organized inbox']);
  });
} catch (error) {
  scope.recordError(error as Error);
  throw error;
} finally {
  scope.dispose();
}
```

> **TIP:** For S2S autonomous agents, export `callerDetails` and `userDetails` from the entry
> point module so all scope files can import them alongside `agentDetails`.
> Read sponsor details from env vars:
> - `agent365Observability__sponsorUserId` (fallback: `clientId`)
> - `agent365Observability__sponsorUserName` (fallback: `agentName`)
> - `agent365Observability__sponsorUserEmail`

#### InvokeAgentScope with ScopeUtils (hosting path — auto-populates from TurnContext)

```typescript
import { InvokeAgentScopeDetails, AgentDetails, ServiceEndpoint } from '@microsoft/opentelemetry';
import { ScopeUtils } from '@microsoft/agents-a365-observability-hosting';

const agentDetails: AgentDetails = { agentId: 'agent-456' };
const scopeDetails: InvokeAgentScopeDetails = {
  endpoint: { host: 'myagent.contoso.com', port: 443 } as ServiceEndpoint,
};

const scope = ScopeUtils.populateInvokeAgentScopeFromTurnContext(
  agentDetails,
  scopeDetails,
  context,     // TurnContext
  authToken    // authentication token string
);

try {
  await scope.withActiveSpanAsync(async () => {
    const response = await invokeAgent(context.activity.text);
    scope.recordOutputMessages([response]);
  });
} finally {
  scope.dispose();
}
```

### ExecuteToolScope

```typescript
import { ExecuteToolScope, ToolCallDetails } from '@microsoft/opentelemetry';

// Use the same agentDetails, userDetails, and request instances from InvokeAgentScope above.

const toolDetails: ToolCallDetails = {
  toolName: 'email-search',
  arguments: JSON.stringify({ query: 'from:boss@company.com', limit: 10 }),
  toolCallId: 'tool-call-456',
  description: 'Search emails by criteria',
  toolType: 'function',
  endpoint: {
    host: 'tools.contoso.com',
    port: 8080,
    protocol: 'https'
  },
};

const scope = ExecuteToolScope.start(request, toolDetails, agentDetails, userDetails);

try {
  return await scope.withActiveSpanAsync(async () => {
    const result = await searchEmails(toolDetails.arguments);
    scope.recordResponse(result);
    return result;
  });
} catch (error) {
  scope.recordError(error as Error);
  throw error;
} finally {
  scope.dispose();
}
```

#### ExecuteToolScope with ScopeUtils

```typescript
import { ToolCallDetails } from '@microsoft/opentelemetry';
import { ScopeUtils } from '@microsoft/agents-a365-observability-hosting';

const toolDetails: ToolCallDetails = {
  toolName: 'email-search',
  arguments: JSON.stringify({ query: 'from:boss@company.com' }),
  toolCallId: 'tool-call-456',
  toolType: 'function',
};

const scope = ScopeUtils.populateExecuteToolScopeFromTurnContext(
  toolDetails,
  context,     // TurnContext
  authToken    // authentication token string
);

try {
  await scope.withActiveSpanAsync(async () => {
    const result = await searchEmails(toolDetails.arguments);
    scope.recordResponse(JSON.stringify(result));
  });
} finally {
  scope.dispose();
}
```

### InferenceScope

#### Example

```typescript
// A365 Observability — best-effort instrumentation (verify against official sample)
import {
  InferenceScope,
  InferenceOperationType,
} from '@microsoft/opentelemetry';
import type {
  AgentDetails,
  InferenceDetails,
  Request,
  UserDetails,
} from '@microsoft/opentelemetry';

const inferenceDetails: InferenceDetails = {
  operationName: InferenceOperationType.CHAT,
  model: 'gpt-4o-mini',
};

const request: Request = {
  conversationId: context.activity?.conversation?.id || `conv-${Date.now()}`,
};

const agentDetails: AgentDetails = {
  agentId: context.activity?.recipient?.agenticAppId || agentName,
  agentName,
  tenantId: context.activity?.recipient?.tenantId || 'sample-tenant',
};

const userDetails: UserDetails = {
  userId: process.env.agent365Observability__sponsorUserId || context.activity?.from?.id || 'blueprint-app-id',
  userName: process.env.agent365Observability__sponsorUserName || context.activity?.from?.name || agentName,
  userEmail: process.env.agent365Observability__sponsorUserEmail || '',
};

let response = '';
const scope = InferenceScope.start(request, inferenceDetails, agentDetails, userDetails);
try {
  await scope.withActiveSpanAsync(async () => {
    response = await invokeAgent(prompt);
    scope.recordOutputMessages([response]);
    scope.recordInputMessages([prompt]);
    scope.recordInputTokens(45);
    scope.recordOutputTokens(78);
    scope.recordFinishReasons(['stop']);
  });
} catch (error) {
  scope.recordError(error as Error);
  throw error;
} finally {
  scope.dispose();
}
```

#### InferenceScope with ScopeUtils

```typescript
import { InferenceDetails, InferenceOperationType } from '@microsoft/opentelemetry';
import { ScopeUtils } from '@microsoft/agents-a365-observability-hosting';

const inferenceDetails: InferenceDetails = {
  operationName: InferenceOperationType.CHAT,
  model: 'gpt-4o-mini',
  providerName: 'azure-openai',
};

const scope = ScopeUtils.populateInferenceScopeFromTurnContext(
  inferenceDetails,
  context,     // TurnContext
  authToken    // authentication token string
);

try {
  await scope.withActiveSpanAsync(async () => {
    const response = await callLLM();
    scope.recordOutputMessages([response.text]);
    scope.recordInputTokens(response.usage.inputTokens);
    scope.recordOutputTokens(response.usage.outputTokens);
  });
} finally {
  scope.dispose();
}
```

### OutputScope (async scenarios)

```typescript
import { OutputScope, OutputResponse, SpanDetails } from '@microsoft/opentelemetry';

// Use the same agentDetails, userDetails, and request instances from InvokeAgentScope above.

// Get the parent context from the originating scope
const parentContext = invokeScope.getSpanContext();

const response: OutputResponse = {
  messages: ['Here is your organized inbox with 15 urgent emails.'],
};

const scope = OutputScope.start(
  request,
  response,
  agentDetails,
  userDetails,
  { parentContext } as SpanDetails
);

// Output messages are recorded automatically from the response
scope.dispose();
```

---

## Advanced: Custom Token Resolver

```typescript
import { useMicrosoftOpenTelemetry } from '@microsoft/opentelemetry';
import { AgenticTokenCacheInstance } from '@microsoft/agents-a365-observability-hosting';
import { tokenResolver } from './token-cache'; // your custom resolver

useMicrosoftOpenTelemetry({
  a365: {
    enabled: true,
    tokenResolver:
      process.env.Use_Custom_Resolver === 'true'
        ? (agentId: string, tenantId: string) => tokenResolver(agentId, tenantId) ?? ''
        : (agentId: string, tenantId: string) =>
            AgenticTokenCacheInstance.getObservabilityToken(agentId, tenantId) ?? '',
  },
});
```

---

## Auto-Instrumentation Extensions

### OpenAI Agents SDK

> **Peer dependency:** `@microsoft/agents-a365-observability-extensions-openai` requires
> `@openai/agents ^0.7.0` (the **OpenAI Agents SDK**) — this is NOT the `openai` npm package
> and NOT `@azure/openai`. Install the peer dep first:
> ```bash
> npm install @openai/agents@^0.7.0
> npm install @microsoft/agents-a365-observability-extensions-openai
> ```

```typescript
import { OpenAIAgentsTraceInstrumentor } from '@microsoft/agents-a365-observability-extensions-openai';

// Assumes useMicrosoftOpenTelemetry(...) already ran in your entry point.
const instrumentor = new OpenAIAgentsTraceInstrumentor({
  enabled: true,
  tracerName: 'openai-agents-tracer',
  tracerVersion: '1.0.0'
});

instrumentor.enable();
```

### LangChain

> **IMPORTANT:** `LangChainTraceInstrumentor.instrument()` requires `ObservabilityManager` to be
> fully initialized first. Calling it **after** `useMicrosoftOpenTelemetry()` as a separate
> statement will throw `"ObservabilityManager is not configured yet"` if `a365.enabled` is `true`.
>
> **Preferred approach:** Use `instrumentationOptions: { langchain: {} }` inside the
> `useMicrosoftOpenTelemetry()` call. This ensures correct initialization order:
>
> ```typescript
> useMicrosoftOpenTelemetry({
>   a365: { enabled: true, tokenResolver: ... },
>   instrumentationOptions: {
>     langchain: {},
>   },
> });
> ```
>
> **Alternative (conditional):** If you must call `instrument()` separately, guard it:
> ```typescript
> if (process.env.ENABLE_A365_OBSERVABILITY_EXPORTER === 'true') {
>   LangChainTraceInstrumentor.instrument(LangChainCallbacks);
> }
> ```

```typescript
import { LangChainTraceInstrumentor } from '@microsoft/agents-a365-observability-extensions-langchain';
import * as LangChainCallbacks from '@langchain/core/callbacks/manager';

// Assumes useMicrosoftOpenTelemetry(...) already ran in your entry point.
LangChainTraceInstrumentor.instrument(LangChainCallbacks);
```

---

## .env Variables

> **Note:** If you ran `a365 setup`, `ENABLE_A365_OBSERVABILITY_EXPORTER=false` is **already
> present** in your `.env` file. Preserve this value when instrumenting.

```dotenv
# ── A365 Observability ────────────────────────────────────────────────────────
# Set to true to export to Microsoft Admin Center (production only).
# a365 setup automatically adds this with value "false".
ENABLE_A365_OBSERVABILITY_EXPORTER=false

# Shown in Microsoft Admin Center observability dashboard.
SERVICE_NAME=my-agent

# Log level: pipe-separated list of levels to emit.
A365_OBSERVABILITY_LOG_LEVEL=info|warn|error

# Set to true to use a custom token resolver instead of AgenticTokenCacheInstance.
# Default: false (use built-in cache). Set to true for local testing with custom auth.
Use_Custom_Resolver=false

# Sponsor / CallerDetails for MAC portal trace visibility (S2S / autonomous agents).
agent365Observability__sponsorUserId=<<Blueprint ID>>
agent365Observability__sponsorUserName=<<Blueprint Name>>
agent365Observability__sponsorUserEmail=<<Blueprint Sponsor Email>>
# ─────────────────────────────────────────────────────────────────────────────
```

| Variable | Local | Production |
|---|---|---|
| `ENABLE_A365_OBSERVABILITY_EXPORTER` | `false` | `true` |
| `Use_Custom_Resolver` | `true` (optional) | `false` |
| `agent365Observability__sponsorUserId` | `<<Blueprint ID>>` | `<<Blueprint ID>>` |
| `agent365Observability__sponsorUserName` | `<<Blueprint Name>>` | `<<Blueprint Name>>` |
| `agent365Observability__sponsorUserEmail` | `<<Blueprint Sponsor Email>>` | `<<Blueprint Sponsor Email>>` |
| `NODE_ENV` | `development` | `production` |

---

## Validate Locally

Set `ENABLE_A365_OBSERVABILITY_EXPORTER=false` — spans export to the console.

To investigate export failures, enable verbose logging:

```bash
ENABLE_A365_OBSERVABILITY_EXPORTER=true
A365_OBSERVABILITY_LOG_LEVEL=info|warn|error
```

Key console messages:

```text
[INFO]  [Agent365Exporter] Exporting 245 spans
[INFO]  [Agent365Exporter] Partitioned into 3 identity groups (2 spans skipped)
[INFO]  [Agent365Exporter] Token resolved successfully via tokenResolver
[EVENT] export-group succeeded in 98ms {"tenantId":"...","agentId":"...","correlationId":"abc-123"}
[ERROR] [Agent365Exporter] Failed with status 401, correlation ID: abc-123
[WARN]  export-partition-span-missing-identity: 5 spans skipped due to missing tenant or agent ID
```

Custom logger for capturing export events to a file:

```typescript
import { setLogger, ExporterEventNames } from '@microsoft/agents-a365-observability';

setLogger({
  info: (msg, ...args) => myLogger.info(msg, ...args),
  warn: (msg, ...args) => myLogger.warn(msg, ...args),
  error: (msg, ...args) => myLogger.error(msg, ...args),
  event: (eventType: ExporterEventNames, isSuccess: boolean, durationMs: number,
          message?: string, details?: Record<string, string>) => {
    myLogger.info({ eventType, isSuccess, durationMs, message, ...details });
  }
});
```

---

## Key API Surface

| Symbol | Module | Purpose |
|--------|--------|---------|
| `useMicrosoftOpenTelemetry(options)` | `@microsoft/opentelemetry` | Configure the OTel pipeline with the A365 exporter |
| `shutdownMicrosoftOpenTelemetry()` | `@microsoft/opentelemetry` | Graceful shutdown of the OTel provider |
| `tokenResolver` | `./observability/token-cache` | Returns cached token for the A365 exporter |
| `startTokenService(config)` | `./observability/observability-token-service` | Background MSAL FMI token acquisition |
| `BaggageBuilder` | `@microsoft/opentelemetry` | Fluent builder for tenant/agent/correlation baggage |
| `BaggageBuilderUtils.fromTurnContext(builder, ctx)` | `@microsoft/agents-a365-observability-hosting` | Populates baggage from a `TurnContext` automatically |
| `BaggageMiddleware` | `@microsoft/agents-a365-observability-hosting` | Adapter middleware — auto-populates baggage for every request |
| `ObservabilityHostingManager` | `@microsoft/agents-a365-observability-hosting` | Composite hosting configuration |
| `ScopeUtils.populateInvokeAgentScopeFromTurnContext` | `@microsoft/agents-a365-observability-hosting` | Creates `InvokeAgentScope` from `TurnContext` |
| `ScopeUtils.populateExecuteToolScopeFromTurnContext` | `@microsoft/agents-a365-observability-hosting` | Creates `ExecuteToolScope` from `TurnContext` |
| `ScopeUtils.populateInferenceScopeFromTurnContext` | `@microsoft/agents-a365-observability-hosting` | Creates `InferenceScope` from `TurnContext` |
| `AgenticTokenCacheInstance.getObservabilityToken(agentId, tenantId)` | `@microsoft/agents-a365-observability-hosting` | Retrieve cached observability token |
| `AgenticTokenCacheInstance.RefreshObservabilityToken(...)` | `@microsoft/agents-a365-observability-hosting` | Refresh and cache token for the current turn |
| `getObservabilityAuthenticationScope()` | `@microsoft/agents-a365-runtime` | Returns the OAuth2 scope string for the observability API. **Deprecated** in v0.2.0-preview.5 — still functional; modern replacement is `defaultObservabilityConfigurationProvider.getConfiguration().observabilityAuthenticationScopes` |
| `InvokeAgentScope.start(request, scopeDetails, agentDetails, callerDetails)` | `@microsoft/opentelemetry` | Start agent invocation telemetry scope |
| `ExecuteToolScope.start(request, toolDetails, agentDetails, userDetails)` | `@microsoft/opentelemetry` | Start tool execution telemetry scope |
| `InferenceScope.start(request, inferenceDetails, agentDetails, userDetails)` | `@microsoft/opentelemetry` | Start LLM inference telemetry scope |
| `OutputScope.start(request, response, agentDetails, userDetails, spanDetails)` | `@microsoft/opentelemetry` | Start output telemetry scope (async scenarios) |
| `setLogger(logger)` | `@microsoft/agents-a365-observability` | Optional custom exporter logger |
| `ExporterEventNames` | `@microsoft/agents-a365-observability` | Event names emitted by the exporter logger |
| `scope.withActiveSpanAsync(fn)` | — | Execute async work within the active OTel span |
| `scope.recordInputMessages(msgs)` / `scope.recordOutputMessages(msgs)` | — | Record prompts and completions |
| `scope.recordInputTokens(n)` / `scope.recordOutputTokens(n)` | — | Record token counts |
| `scope.recordFinishReasons(reasons)` | — | Record finish reasons (e.g. `['stop']`) |
| `scope.recordError(error)` | — | Record an error on the span |
| `scope.dispose()` | — | End and export the span (call in `finally`) |

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| No console traces | `useMicrosoftOpenTelemetry()` not initialized early enough | Call it in the entry point before importing LLM or agent modules |
| Spans missing baggage | Handler not wrapped in baggage scope | Register `BaggageMiddleware` or wrap handler body in `baggageScope.run()` |
| Token resolver always returns `''` | `RefreshObservabilityToken` not called per turn | Call it at the start of each message handler turn |
| `Cannot find module '@microsoft/agents-a365-observability'` | Package not installed | Run `npm install @microsoft/agents-a365-observability` |
| `Cannot find module '@microsoft/agents-a365-observability-hosting'` | Package not installed | Run `npm install @microsoft/agents-a365-observability-hosting` |
| Traces not in Admin Center | Exporter env var not set | Set `ENABLE_A365_OBSERVABILITY_EXPORTER=true` in production |
| 401 on export | Missing permission | Check if upgrading past `0.2.0-preview.1` (requires new `Agent365.Observability.OtelWrite` permission) |
| Spans dropped silently | Missing tenant/agent ID | Ensure `BaggageBuilder` (or `BaggageMiddleware`) populates tenant/agent ID before creating spans |
| TypeScript error on `agentAuid` in `AgentDetails` | Interface field is `agentAUID` (uppercase UID), not `agentAuid` | Change to `agentAUID: '...'` |
| `extensions-openai` install fails / peer dep error | Missing `@openai/agents` peer dep | Run `npm install @openai/agents@^0.7.0` first; this is the OpenAI Agents SDK, not the `openai` package |
| S2S: AADSTS82001 or AADSTS1002012 | Direct MSAL client credentials not supported | Use the 3-hop FMI chain: Blueprint → FMI path → Agent Identity → Observability API token. |
| S2S: 401 on export | Token scope mismatch | Ensure Hop 3 scope is `api://9b975845-388f-4429-889e-eab1ef63949c/.default`. Also ensure Agent Identity SP has OtelWrite role assigned |
| S2S: 403 on `observabilityService/` endpoint | Missing app role | Assign `Agent365.Observability.OtelWrite` to the **Agent Identity** SP (not just the Blueprint) via Graph API |
| S2S: MSI fails locally | No Managed Identity in dev | Set `AGENT365_USE_MANAGED_IDENTITY=false` and provide `AGENT365_CLIENT_SECRET` |
| S2S: token resolver never called | `RefreshObservabilityToken` called for S2S | Remove `AgenticTokenCacheInstance.RefreshObservabilityToken` — not used in S2S; token comes from `a365.tokenResolver` in `useMicrosoftOpenTelemetry(...)` |
| `fromTurnContext` not found on `BaggageBuilder` | Static method is on `BaggageBuilderUtils`, not `BaggageBuilder` | Use `BaggageBuilderUtils.fromTurnContext(new BaggageBuilder(), context)` |
