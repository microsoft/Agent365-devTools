# Python — A365 Observability Reference

Authoritative package versions and code patterns for instrumenting A365 observability
into a Python agent. All samples mirror the official Microsoft Learn docs (updated 2026-04-30).

---

## pip Packages

| Package | Purpose |
|---------|---------|
| `microsoft-opentelemetry` | Unified distro entry point: `use_microsoft_opentelemetry()`, all scope types from `microsoft.opentelemetry.a365.core`, hosting helpers from `microsoft.opentelemetry.a365.hosting`, and OBO/S2S exporter wiring |
| `msal` | MSAL Python `ConfidentialClientApplication` for Hop 3 token acquisition (Hop 1+2 uses direct HTTP POST — see known issue below) |
| `azure-identity` | `ManagedIdentityCredential` for MSI-based token acquisition (async variant) |
| `httpx` | Direct HTTP POST for FMI Hop 1+2 token acquisition (MSAL `fmi_path` workaround) |

Install commands:
```bash
pip3 install microsoft-opentelemetry 2>/dev/null || pip install microsoft-opentelemetry
pip3 install msal azure-identity httpx 2>/dev/null || pip install msal azure-identity httpx
```

---

## Entry Point — Observability Init

### Unified Distro

```python
# A365 Observability — best-effort instrumentation (verify against official sample)
from microsoft.opentelemetry import use_microsoft_opentelemetry
from token_cache import get_cached_agentic_token

use_microsoft_opentelemetry(
    enable_a365=True,
    enable_azure_monitor=False,
    a365_token_resolver=lambda agent_id, tenant_id: get_cached_agentic_token(
        tenant_id, agent_id
    ),
)
```

This matches the current official sample: initialize the unified distro once at startup,
then refresh the per-turn OBO token in your message handler.

### S2S configuration (`authMode: S2S`)

S2S observability is supported for Python. The token service uses a **3-hop FMI (Federated Managed Identity) token chain**:

```
Blueprint (client_credentials / MSI)
  → Hop 1+2: FMI token (api://AzureADTokenExchange/.default with fmi_path=agentId)
    → Agent Identity token
      → Hop 3: Observability API token (scope=api://9b975845-388f-4429-889e-eab1ef63949c/.default)
```

No OBO user token is required.

> **Auth strategy** is controlled by `AGENT365_USE_MANAGED_IDENTITY`:
>   - `true` (production) — MSI → Blueprint FIC → Agent Identity → API
>   - `false` (local dev) — Client Secret → Blueprint FIC → Agent Identity → API

> **⚠️ Known Issue (msal v1.34.0):** Python MSAL does NOT properly support `fmi_path` as a parameter to `acquire_token_for_client()`. Passing it causes `TypeError: Session.request() got an unexpected keyword argument 'fmi_path'`. Use **direct HTTP POST** to the token endpoint with `fmi_path` as a form parameter for Hop 1+2 (same workaround as Node.js). MSAL is fine for Hop 3 (no `fmi_path` needed).

> **Note:** As of CLI 1.1, `a365 setup all` automatically grants `Agent365.Observability.OtelWrite` to the Agent Identity SP (both delegated and application). No manual role assignment is needed for newly provisioned agents.

#### Step 1 — Create `observability/token_cache.py`

Simple in-memory token cache shared by the token service and the OTel exporter:

```python
# observability/token_cache.py
# A365 Observability — best-effort instrumentation (verify against official sample)

"""Simple in-memory token cache for observability tokens."""

import threading
from datetime import datetime, timedelta, timezone

_lock = threading.Lock()
_cache: dict[str, tuple[str, datetime]] = {}

# Tokens are considered valid if they expire more than 5 minutes from now.
_EXPIRY_BUFFER = timedelta(minutes=5)


def cache_token(agent_id: str, tenant_id: str, token: str, expires_in: timedelta = timedelta(hours=1)) -> None:
    """Cache an observability token for a specific agent/tenant pair."""
    key = f"{agent_id}:{tenant_id}"
    expires_at = datetime.now(timezone.utc) + expires_in
    with _lock:
        _cache[key] = (token, expires_at)


def get_cached_token(agent_id: str, tenant_id: str) -> str | None:
    """Retrieve a cached token if it exists and hasn't expired."""
    key = f"{agent_id}:{tenant_id}"
    with _lock:
        entry = _cache.get(key)
        if entry is None:
            return None
        token, expires_at = entry
        if datetime.now(timezone.utc) + _EXPIRY_BUFFER >= expires_at:
            del _cache[key]
            return None
        return token
```

#### Step 2 — Create `observability/observability_token_service.py`

Background token acquisition via 3-hop FMI chain (direct HTTP POST for Hop 1+2, MSAL for Hop 3):

```python
# observability/observability_token_service.py
# A365 Observability — best-effort instrumentation (verify against official sample)
# A365 auth mode: S2S — 3-hop FMI token chain (direct HTTP POST + MSAL)
#   Hop 1+2: Blueprint (MSI or client secret) → T1 via token endpoint POST + fmi_path → Agent Identity
#   Hop 3:   Agent Identity uses T1 as assertion → Observability API token

import asyncio
import logging
from datetime import timedelta

import httpx
import msal

from observability import token_cache

logger = logging.getLogger(__name__)

FMI_SCOPE = "api://AzureADTokenExchange/.default"
OBSERVABILITY_SCOPES = ["api://9b975845-388f-4429-889e-eab1ef63949c/.default"]
REFRESH_INTERVAL_SECONDS = 50 * 60  # 50 minutes


async def acquire_initial_token(
    tenant_id: str,
    agent_id: str,
    blueprint_client_id: str,
    blueprint_client_secret: str,
    use_managed_identity: bool,
) -> None:
    """Acquire the first observability token before background services start."""
    await _acquire_and_register_token(
        tenant_id, agent_id, blueprint_client_id, blueprint_client_secret, use_managed_identity
    )


async def run_token_service(
    tenant_id: str,
    agent_id: str,
    blueprint_client_id: str,
    blueprint_client_secret: str,
    use_managed_identity: bool,
) -> None:
    """Run the background token acquisition loop."""
    logger.info("ObservabilityTokenService started (use_managed_identity=%s).", use_managed_identity)

    while True:
        try:
            await _acquire_and_register_token(
                tenant_id, agent_id, blueprint_client_id, blueprint_client_secret, use_managed_identity
            )
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.warning(
                "Failed to acquire observability token; will retry in %d seconds.",
                REFRESH_INTERVAL_SECONDS,
                exc_info=True,
            )

        await asyncio.sleep(REFRESH_INTERVAL_SECONDS)


async def _acquire_and_register_token(
    tenant_id: str,
    agent_id: str,
    blueprint_client_id: str,
    blueprint_client_secret: str,
    use_managed_identity: bool,
) -> None:
    authority = f"https://login.microsoftonline.com/{tenant_id}"
    token_url = f"{authority}/oauth2/v2.0/token"

    # Hop 1+2: Blueprint → T1 via FMI path
    if use_managed_identity:
        t1_token = await _acquire_t1_via_msi(token_url, blueprint_client_id, agent_id)
    else:
        t1_token = await _acquire_t1_via_client_secret(
            token_url, blueprint_client_id, blueprint_client_secret, agent_id
        )

    # Hop 3: Agent Identity uses T1 → Observability API token
    identity_app = msal.ConfidentialClientApplication(
        client_id=agent_id,
        client_credential={"client_assertion": t1_token},
        authority=authority,
    )
    obs_result = identity_app.acquire_token_for_client(scopes=OBSERVABILITY_SCOPES)

    if "access_token" not in obs_result:
        raise RuntimeError(f"Failed to acquire observability token: {obs_result.get('error_description', obs_result)}")

    token_cache.cache_token(agent_id, tenant_id, obs_result["access_token"], expires_in=timedelta(minutes=55))
    logger.info("Observability token registered for agent %s.", agent_id)


async def _acquire_t1_via_msi(token_url: str, blueprint_client_id: str, agent_id: str) -> str:
    """Acquire T1 token using Managed Identity (production) — direct HTTP POST."""
    from azure.identity.aio import ManagedIdentityCredential

    async with ManagedIdentityCredential() as credential:
        msi_token = await credential.get_token("api://AzureADTokenExchange")

    async with httpx.AsyncClient() as client:
        resp = await client.post(
            token_url,
            data={
                "grant_type": "client_credentials",
                "client_id": blueprint_client_id,
                "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                "client_assertion": msi_token.token,
                "scope": FMI_SCOPE,
                "fmi_path": agent_id,
            },
        )
        result = resp.json()

    if "access_token" not in result:
        raise RuntimeError(f"FMI T1 via MSI failed: {result.get('error_description', result)}")
    return result["access_token"]


async def _acquire_t1_via_client_secret(
    token_url: str, blueprint_client_id: str, blueprint_client_secret: str, agent_id: str
) -> str:
    """Acquire T1 token using client secret (local dev) — direct HTTP POST with fmi_path."""
    async with httpx.AsyncClient() as client:
        resp = await client.post(
            token_url,
            data={
                "grant_type": "client_credentials",
                "client_id": blueprint_client_id,
                "client_secret": blueprint_client_secret,
                "scope": FMI_SCOPE,
                "fmi_path": agent_id,
            },
        )
        result = resp.json()

    if "access_token" not in result:
        raise RuntimeError(f"FMI T1 via client secret failed: {result.get('error_description', result)}")
    return result["access_token"]
```

#### Step 3 — Wire in entry point (`main.py` or `app.py`)

```python
# authMode: S2S — 3-hop FMI token chain via direct HTTP POST + MSAL, no user OBO.
import asyncio
import logging
import os

from dotenv import load_dotenv
from aiohttp import web

from microsoft.opentelemetry import use_microsoft_opentelemetry
from microsoft.opentelemetry.a365.core import AgentDetails

from observability import token_cache
from observability.observability_token_service import acquire_initial_token, run_token_service

load_dotenv()

# ── Configuration ────────────────────────────────────────────────────────────
TENANT_ID = os.environ.get("AGENT365_TENANT_ID", "")
AGENT_ID = os.environ.get("AGENT365_AGENT_ID", "")
BLUEPRINT_ID = os.environ.get("AGENT365_BLUEPRINT_ID", "")
CLIENT_ID = os.environ.get("AGENT365_CLIENT_ID", "")
CLIENT_SECRET = os.environ.get("AGENT365_CLIENT_SECRET", "")
AGENT_NAME = os.environ.get("AGENT365_AGENT_NAME", "my-agent")
AGENT_DESCRIPTION = os.environ.get("AGENT365_AGENT_DESCRIPTION", "")
USE_MANAGED_IDENTITY = os.environ.get("AGENT365_USE_MANAGED_IDENTITY", "true").lower() == "true"

def _has_a365_credentials() -> bool:
    required_values = [TENANT_ID, AGENT_ID, CLIENT_ID]
    if not all(v and not v.startswith("<<") for v in required_values):
        return False
    if USE_MANAGED_IDENTITY:
        return True
    return bool(CLIENT_SECRET) and not CLIENT_SECRET.startswith("<<")

A365_ENABLED = _has_a365_credentials()

# ── Agent Details ────────────────────────────────────────────────────────────
agent_details = AgentDetails(
    agent_id=AGENT_ID or "local-dev",
    agent_name=AGENT_NAME,
    agent_description=AGENT_DESCRIPTION,
    agent_blueprint_id=BLUEPRINT_ID,
    tenant_id=TENANT_ID or "local-dev",
)

# ── Microsoft OpenTelemetry Distro ───────────────────────────────────────────
use_microsoft_opentelemetry(
    enable_a365=True,
    enable_azure_monitor=False,
    enable_console=True,  # disable in production
    a365_use_s2s_endpoint=True,  # CRITICAL for S2S — posts to /observabilityService/
    a365_enable_observability_exporter=True,
    a365_token_resolver=lambda aid, tid: token_cache.get_cached_token(aid, tid) or "",
)

# ── Background Tasks ─────────────────────────────────────────────────────────
async def start_background_tasks(app: web.Application) -> None:
    if A365_ENABLED:
        try:
            await acquire_initial_token(
                tenant_id=TENANT_ID,
                agent_id=AGENT_ID,
                blueprint_client_id=CLIENT_ID,
                blueprint_client_secret=CLIENT_SECRET,
                use_managed_identity=USE_MANAGED_IDENTITY,
            )
        except Exception:
            logging.warning("Initial token acquisition failed; continuing with background refresh.", exc_info=True)

        app["token_task"] = asyncio.create_task(
            run_token_service(
                tenant_id=TENANT_ID,
                agent_id=AGENT_ID,
                blueprint_client_id=CLIENT_ID,
                blueprint_client_secret=CLIENT_SECRET,
                use_managed_identity=USE_MANAGED_IDENTITY,
            )
        )
    else:
        logging.warning(
            "Agent365 credentials not configured — skipping token service. "
            "Run 'a365 setup all' to enable A365 observability export."
        )

    # ... rest of background task startup ...
```

> **⚠️ `a365_use_s2s_endpoint=True` is required for S2S agents.** Without it, the exporter posts to `/observability/` (OBO endpoint) instead of `/observabilityService/` (S2S endpoint), causing 401 errors. The Python SDK uniquely supports this as a native kwarg — no custom `spanProcessors` workaround needed (unlike Node.js).

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
AGENT365_USE_MANAGED_IDENTITY=true

# Sponsor identity for CallerDetails (MAC portal visibility)
AGENT365_SPONSOR_USER_ID=<blueprint-sponsor-user-object-id>
AGENT365_SPONSOR_USER_EMAIL=<sponsor@contoso.com>
AGENT365_SPONSOR_USER_NAME=<Sponsor Display Name>
```

Message handler baggage setup is **identical** to `user-delegated` / `agentic-identity` — only the token resolver and credential source differ. Do **not** use the OBO per-turn token-registration flow for S2S agents.

### Hosting path — OBO token cache (AI Teammate agents)

#### Unified Distro

```python
# A365 Observability — best-effort instrumentation (verify against official sample)
from microsoft.opentelemetry import use_microsoft_opentelemetry
from token_cache import cache_agentic_token, get_cached_agentic_token

use_microsoft_opentelemetry(
    enable_a365=True,
    a365_token_resolver=lambda agent_id, tenant_id: get_cached_agentic_token(
        tenant_id, agent_id
    ),
)
```

```python
# token_cache.py
# A365 Observability — best-effort instrumentation (verify against official sample)

"""Token caching utilities for Agent 365 Observability exporter authentication."""

import logging

logger = logging.getLogger(__name__)

_agentic_token_cache = {}


def cache_agentic_token(tenant_id: str, agent_id: str, token: str) -> None:
    """Cache the agentic token for use by Agent 365 Observability exporter."""
    key = f"{tenant_id}:{agent_id}"
    _agentic_token_cache[key] = token
    logger.debug(f"Cached agentic token for {key}")


def get_cached_agentic_token(tenant_id: str, agent_id: str) -> str | None:
    """Retrieve cached agentic token for Agent 365 Observability exporter."""
    key = f"{tenant_id}:{agent_id}"
    return _agentic_token_cache.get(key)
```

#### Alternative: AgenticTokenCache helper

```python
from microsoft.opentelemetry import use_microsoft_opentelemetry
from microsoft.opentelemetry.a365.hosting.token_cache_helpers import AgenticTokenCache

token_cache = AgenticTokenCache()

use_microsoft_opentelemetry(
    enable_a365=True,
    a365_token_resolver=token_cache.get_observability_token,
)
```

---

## Adapter — Hosting Baggage

Register hosting baggage helpers to auto-populate baggage from every incoming `TurnContext`.
This removes the need to call `BaggageBuilder` manually in each activity handler.

### Unified Distro

```python
from microsoft.opentelemetry.a365.hosting import (
    ObservabilityHostingManager,
    ObservabilityHostingOptions,
)

ObservabilityHostingManager.configure(
    adapter.middleware_set,
    ObservabilityHostingOptions(enable_baggage=True),
)
```

Use these import paths when you need manual baggage wiring too:

```python
from microsoft.opentelemetry.a365.core import BaggageBuilder, InvokeAgentScope
from microsoft.opentelemetry.a365.hosting.scope_helpers.populate_baggage import populate
```

---

## Message Handler — Token Refresh + BaggageBuilder

### Unified Distro

```python
# A365 Observability — best-effort instrumentation (verify against official sample)
from microsoft.opentelemetry.a365.core import BaggageBuilder
from microsoft.opentelemetry.a365.hosting.scope_helpers.populate_baggage import populate
from microsoft.opentelemetry.a365.runtime import get_observability_authentication_scope
from token_cache import cache_agentic_token

async def _setup_observability_token(self, context: TurnContext, tenant_id: str, agent_id: str):
    try:
        exaau_token = await self.agent_app.auth.exchange_token(
            context,
            scopes=get_observability_authentication_scope(),
            auth_handler_id=self.auth_handler_name,
        )
        cache_agentic_token(tenant_id, agent_id, exaau_token.token)
    except Exception as e:
        logger.warning(f"Failed to cache observability token: {e}")


@AGENT_APP.activity("message", auth_handlers=["AGENTIC"])
async def on_message(context: TurnContext, state: TurnState):
    tenant_id = context.activity.recipient.tenant_id
    agent_id = context.activity.recipient.agentic_app_id

    await self._setup_observability_token(context, tenant_id, agent_id)

    builder = BaggageBuilder()
    populate(builder, context)

    with builder.build():
        # ... your agent message handling logic ...
        pass
```

Manual `BaggageBuilder` (without the `populate()` helper):

```python
from microsoft.opentelemetry.a365.core import BaggageBuilder

with (
    BaggageBuilder()
    .tenant_id("tenant-123")
    .agent_id("agent-456")
    .conversation_id("conv-789")
    .build()
):
    # Any spans started in this context will receive these as attributes
    pass
```

---

## Manual Instrumentation Scopes

> **Store publishing requirement:** `InvokeAgentScope`, `InferenceScope`, and `ExecuteToolScope`
> are **required** for store validation. Missing any one causes store validation failure.

> **Import source:** Use the unified distro import path: `from microsoft.opentelemetry.a365.core import ...`.

```python
from microsoft.opentelemetry.a365.core import (
    AgentDetails,
    BaggageBuilder,
    InferenceCallDetails,
    InferenceOperationType,
    InferenceScope,
    InvokeAgentScope,
    InvokeAgentScopeDetails,
    ExecuteToolScope,
    ToolCallDetails,
    Request,
    ServiceEndpoint,
)
```

### InvokeAgentScope

```python
from microsoft.opentelemetry.a365.core import (
    InvokeAgentScope,
    InvokeAgentScopeDetails,
    AgentDetails,
    CallerDetails,
    UserDetails,
    Channel,
    Request,
    ServiceEndpoint,
)

# Reuse the same agent_details and request instances across all scopes in a request.
agent_details = AgentDetails(
    agent_id="agent-456",
    agent_name="My Agent",
    agent_description="An AI agent powered by Azure OpenAI",
    agentic_user_id="auid-123",
    agentic_user_email="agent@contoso.com",
    agent_blueprint_id="blueprint-789",
    tenant_id="tenant-123",
)

scope_details = InvokeAgentScopeDetails(
    endpoint=ServiceEndpoint(hostname="myagent.contoso.com", port=443),
)

request = Request(
    content="User asks a question",
    session_id="session-42",
    conversation_id="conv-xyz",
    channel=Channel(name="msteams"),
)

caller_details = CallerDetails(
    user_details=UserDetails(
        user_id="user-123",
        user_email="jane.doe@contoso.com",
        user_name="Jane Doe",
    ),
)

with InvokeAgentScope.start(request, scope_details, agent_details, caller_details) as scope:
    # Record input messages
    scope.record_input_messages(["User asks a question"])
    # Perform agent invocation logic
    response = call_agent(...)
    # Record output messages
    scope.record_output_messages([response])
```

### Shared Observability Context Module (`observability/obs_context.py`)

For autonomous/S2S agents, create a shared module to avoid circular imports between agent, monitor, and main:

```python
# observability/obs_context.py
import os
from microsoft.opentelemetry.a365.core import AgentDetails, CallerDetails, UserDetails

# ── Configuration from .env ──────────────────────────────────────────────────
A365_ENABLED = os.environ.get("ENABLE_A365_OBSERVABILITY", "").lower() == "true"
TENANT_ID = os.environ.get("AGENT365_TENANT_ID", "")
AGENT_ID = os.environ.get("AGENT365_AGENT_ID", "")
BLUEPRINT_ID = os.environ.get("AGENT365_BLUEPRINT_ID", "")
CLIENT_ID = os.environ.get("AGENT365_CLIENT_ID", "")
CLIENT_SECRET = os.environ.get("AGENT365_CLIENT_SECRET", "")
USE_MANAGED_IDENTITY = os.environ.get("AGENT365_USE_MANAGED_IDENTITY", "false").lower() == "true"
USE_S2S_ENDPOINT = os.environ.get("AGENT365_USE_S2S_ENDPOINT", "false").lower() == "true"

# ── Shared instances (imported by agent & monitor modules) ───────────────────
agent_details = AgentDetails(
    agent_id=AGENT_ID,
    agent_name=os.environ.get("AGENT365_AGENT_NAME", ""),
    agent_description=os.environ.get("AGENT365_AGENT_DESCRIPTION", ""),
    agent_blueprint_id=BLUEPRINT_ID,
    tenant_id=TENANT_ID,
)

# CallerDetails — for autonomous agents, use Blueprint sponsor identity
caller_details = CallerDetails(
    user_details=UserDetails(
        user_id=os.environ.get("AGENT365_SPONSOR_USER_ID", BLUEPRINT_ID),
        user_email=os.environ.get("AGENT365_SPONSOR_USER_EMAIL", ""),
        user_name=os.environ.get("AGENT365_SPONSOR_USER_NAME", ""),
    ),
)
```

> **Why CallerDetails?** Without `CallerDetails`, traces will NOT appear in the Microsoft Admin Center (MAC) portal. For autonomous agents with no real user, use the Blueprint sponsor's identity. The `user.id`, `user.email`, and `user.name` span attributes are set from CallerDetails.

> **Import pattern:** Import `agent_details` and `caller_details` from `obs_context` in your agent and monitor modules — do NOT create them inline to avoid circular imports with `main.py`.

### ExecuteToolScope

```python
from microsoft.opentelemetry.a365.core import (
    ExecuteToolScope,
    ToolCallDetails,
    Request,
    ServiceEndpoint,
)

# Use the same agent_details and request instances from InvokeAgentScope above.

tool_details = ToolCallDetails(
    tool_name="summarize",
    tool_type="function",
    tool_call_id="tc-001",
    arguments="{'text': '...'}",
    description="Summarize provided text",
    endpoint=ServiceEndpoint(hostname="tools.contoso.com", port=8080),
)

with ExecuteToolScope.start(request, tool_details, agent_details) as scope:
    result = run_tool(tool_details)
    scope.record_response(result)
```

### InferenceScope

> **⚠️ Python SDK uses camelCase parameter names** (matching the underlying .NET/Java convention):
> `operationName`, `model`, `providerName`, `inputTokens`, `outputTokens`, `finishReasons`, `thoughtProcess`, `endpoint`.
> Do NOT use snake_case (`operation_name`, `provider_name`) — this causes `TypeError` at runtime.

```python
from microsoft.opentelemetry.a365.core import (
    InferenceScope,
    InferenceCallDetails,
    InferenceOperationType,
)

# Use the same agent_details and request instances from InvokeAgentScope above.

inference_details = InferenceCallDetails(
    operationName=InferenceOperationType.CHAT,
    model="gpt-4o-mini",
    providerName="azure-openai",
    inputTokens=123,
    outputTokens=456,
    finishReasons=["stop"],
)

with InferenceScope.start(request, inference_details, agent_details) as scope:
    completion = call_llm(...)
    scope.record_output_messages([completion.text])
    scope.record_input_tokens(completion.usage.input_tokens)
    scope.record_output_tokens(completion.usage.output_tokens)
```

### OutputScope (async scenarios)

```python
from microsoft.opentelemetry.a365.core import (
    OutputScope,
    Response,
    SpanDetails,
)

# Use the same agent_details and request instances from InvokeAgentScope above.

# Get the parent context from the originating scope
parent_context = invoke_scope.get_context()

response = Response(messages=["Here is your organized inbox with 15 urgent emails."])

with OutputScope.start(
    request,
    response,
    agent_details,
    span_details=SpanDetails(parent_context=parent_context),
):
    # Output messages are recorded automatically from the response
    pass
```

---

## Auto-Instrumentation Extensions

The unified distro handles supported framework instrumentation automatically after startup.
No framework-specific bootstrap call or manual extension instrumentor is needed.

### Semantic Kernel

```python
from microsoft.opentelemetry import use_microsoft_opentelemetry

use_microsoft_opentelemetry(enable_a365=True)
# Semantic Kernel is auto-instrumented when installed.
```

### OpenAI Agents SDK

```python
from microsoft.opentelemetry import use_microsoft_opentelemetry

use_microsoft_opentelemetry(enable_a365=True)
# OpenAI Agents SDK instrumentation is handled by the distro.
```

### Agent Framework

```python
from microsoft.opentelemetry import use_microsoft_opentelemetry

use_microsoft_opentelemetry(enable_a365=True)
# Agent Framework instrumentation is handled by the distro.
```

### LangChain

```python
from microsoft.opentelemetry import use_microsoft_opentelemetry

use_microsoft_opentelemetry(enable_a365=True)
# LangChain instrumentation is handled by the distro.
```

---

## .env Variables

> **⚠️ Python requires TWO env vars** (unlike Node.js and .NET which use a single flag):
> - `ENABLE_A365_OBSERVABILITY_EXPORTER` — controls exporter creation
> - `ENABLE_A365_OBSERVABILITY` — controls A365 span creation
>
> Without **both** set to `true`, `use_microsoft_opentelemetry()` can initialize successfully but `InvokeAgentScope.start()` still creates a **no-op scope** (no actual OTel spans are produced or exported). This is the #1 cause of "spans seem to run but nothing is exported."

> **Note:** If you ran `a365 setup`, `ENABLE_A365_OBSERVABILITY_EXPORTER=false` is **already
> present** in your `.env` file. Preserve this value when instrumenting.

```dotenv
# ── A365 Observability ────────────────────────────────────────────────────────
# BOTH are required for Python (set to true for production):
ENABLE_A365_OBSERVABILITY_EXPORTER=false
ENABLE_A365_OBSERVABILITY=true
# ─────────────────────────────────────────────────────────────────────────────
```

---

## Validate Locally

Set `ENABLE_A365_OBSERVABILITY_EXPORTER=false` — spans export to the console.

To investigate export failures, enable verbose logging in your application startup:

```python
import logging

logging.basicConfig(level=logging.DEBUG)
logging.getLogger("microsoft.opentelemetry").setLevel(logging.DEBUG)
logging.getLogger("microsoft.opentelemetry.a365").setLevel(logging.DEBUG)
```

Key log messages:

```text
DEBUG  Token resolved for agent {agentId} tenant {tenantId}
DEBUG  Exporting {n} spans to {url}
DEBUG  HTTP 200 - correlation ID: abc-123
ERROR  Token resolution failed: {error}
ERROR  HTTP 401 exporting spans - correlation ID: abc-123
INFO   No spans with tenant/agent identity found; nothing exported.
```

Import check to verify packages are installed:

```bash
python -c "from microsoft.opentelemetry import use_microsoft_opentelemetry; from microsoft.opentelemetry.a365.hosting import ObservabilityHostingManager; print('A365 observability imports OK')"
```

---

## `use_microsoft_opentelemetry()` kwargs

| Kwarg | Description |
|-------|-------------|
| `enable_a365` | Enables A365 observability instrumentation and exporter wiring |
| `a365_token_resolver` | Sync callable `(agent_id, tenant_id) -> str \| None` for OBO or S2S export authentication |
| `a365_cluster_category` | Optional cluster label such as `prod` |
| `a365_use_s2s_endpoint` | Uses the service-to-service export endpoint |
| `a365_suppress_invoke_agent_input` | Suppresses input messages on `InvokeAgent` spans |
| `a365_enable_observability_exporter` | Enables the A365 exporter in code instead of env-only configuration |
| `a365_observability_scope_override` | Overrides the default observability OAuth scope |
| `resource` | Standard OpenTelemetry `Resource` for `service.name` / `service.namespace` |

---

## Key API Surface

| Symbol | Module | Purpose |
|--------|--------|---------|
| `use_microsoft_opentelemetry()` | `microsoft.opentelemetry` | Configure the unified OTel pipeline with the A365 exporter |
| `AgentDetails` | `microsoft.opentelemetry.a365.core` | Agent identity for manual scopes |
| `BaggageBuilder` | `microsoft.opentelemetry.a365.core` | Propagates tenant/agent/conversation context across spans |
| `populate(builder, turn_context)` | `microsoft.opentelemetry.a365.hosting.scope_helpers.populate_baggage` | Auto-populates `BaggageBuilder` from `TurnContext` |
| `ObservabilityHostingManager` | `microsoft.opentelemetry.a365.hosting` | Composite hosting configuration for adapter middleware |
| `AgenticTokenCache` | `microsoft.opentelemetry.a365.hosting.token_cache_helpers` | Official hosting token-cache helper for AI Teammate agents |
| `cache_agentic_token()` / `get_cached_agentic_token()` | `token_cache` | Custom in-memory token cache module for per-turn OBO refresh |
| `acquire_initial_token()` / `run_token_service()` | `observability.observability_token_service` | Background FMI token acquisition for S2S (direct HTTP POST for Hop 1+2, MSAL for Hop 3) |
| `get_observability_authentication_scope()` | `microsoft.opentelemetry.a365.runtime` | Returns the OAuth2 scope string |
| `InvokeAgentScope.start(request, scope_details, agent_details, caller_details)` | `microsoft.opentelemetry.a365.core` | Start agent invocation telemetry scope (context manager) |
| `ExecuteToolScope.start(request, tool_details, agent_details)` | `microsoft.opentelemetry.a365.core` | Start tool execution telemetry scope (context manager) |
| `InferenceScope.start(request, inference_details, agent_details)` | `microsoft.opentelemetry.a365.core` | Start LLM inference telemetry scope (context manager) |
| `OutputScope.start(request, response, agent_details, span_details)` | `microsoft.opentelemetry.a365.core` | Start output telemetry scope (async scenarios) |
| `scope.record_input_messages(msgs)` / `scope.record_output_messages(msgs)` | — | Record prompts and completions |
| `scope.record_input_tokens(n)` / `scope.record_output_tokens(n)` | — | Record token counts |
| `scope.record_response(result)` | — | Record tool execution result |
| `scope.get_context()` | — | Get OTel context for use as parent in `OutputScope` |

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| No console traces | `use_microsoft_opentelemetry()` not called | Call the observability initializer before any spans are created |
| Spans missing baggage | Handler not wrapped in baggage scope | Use `ObservabilityHostingManager.configure(...)` or `with builder.build():` |
| Token resolver returns `None` | Per-turn OBO token cache was never refreshed | Call `exchange_token()` and `cache_agentic_token()` at the start of each message handler turn |
| `ModuleNotFoundError` | Package not installed | Run `pip install microsoft-opentelemetry` and install `msal azure-identity httpx` when needed |
| Traces not in Admin Center | Exporter env var not set | Set `ENABLE_A365_OBSERVABILITY_EXPORTER=true` in production |
| 401 on export | Missing permission | Check if upgrading past `0.3.0` (requires new `Agent365.Observability.OtelWrite` permission) |
| Spans dropped silently | Missing tenant/agent ID | Ensure `BaggageBuilder` or `populate()` adds tenant/agent identity before creating spans |
| S2S: OBO token-refresh code still runs in the handler | S2S does not use per-turn OBO token exchange | Remove the OBO handler refresh path; token comes from the background token service via `a365_token_resolver` |
| S2S 401: wrong Hop 3 scope | FMI Hop 3 used `https://api.powerplatform.com/.default` from older samples | Change Hop 3 scope to `api://9b975845-388f-4429-889e-eab1ef63949c/.default` |
| S2S 401 even with correct scope | `OtelWrite` role not on Agent Identity SP | For agents provisioned before CLI 1.1, manually assign `Agent365.Observability.OtelWrite` to the Agent Identity SP via Entra portal (App registrations > Blueprint app > API permissions) |
| S2S: Spans appear to run but nothing is exported | `ENABLE_A365_OBSERVABILITY=true` not set | Python SDK has **two** env vars: `ENABLE_A365_OBSERVABILITY_EXPORTER` (exporter creation) AND `ENABLE_A365_OBSERVABILITY` (span creation). Both must be `true`. Without the second, `InvokeAgentScope.start()` creates a no-op scope. |
| S2S: `_is_telemetry_enabled()` returns `False` | `ENABLE_A365_OBSERVABILITY` env var missing | Set `ENABLE_A365_OBSERVABILITY=true` in `.env` — this is separate from `ENABLE_A365_OBSERVABILITY_EXPORTER` |
| S2S: MSI fails locally | No Managed Identity in dev | Set `AGENT365_USE_MANAGED_IDENTITY=false` and provide `AGENT365_CLIENT_SECRET` |
| S2S: FMI Hop 1+2 returns 400 | `fmi_path` missing or wrong `client_id` | Ensure `fmi_path=<agentId>` (Agent Identity app ID, not Blueprint ID) and `client_id=<blueprintClientId>` |
| S2S: `TypeError: Session.request() got an unexpected keyword argument 'fmi_path'` | MSAL Python v1.34.0 bug | Use direct HTTP POST to `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token` with `fmi_path` as form data instead of MSAL `acquire_token_for_client(fmi_path=...)`. MSAL is still used for Hop 3 (no `fmi_path` needed). |
| S2S: `InferenceCallDetails.__init__() got an unexpected keyword argument 'operation_name'` | Python SDK uses camelCase kwargs | Use `operationName=`, `providerName=`, `inputTokens=`, `outputTokens=`, `finishReasons=` (camelCase, NOT snake_case) |
| S2S: HTTP 400 TenantIdInvalid from exporter | Token not yet acquired when exporter first fires | Ensure `acquire_initial_token()` runs in lifespan BEFORE monitor starts. The `a365_token_resolver` returns `""` when no cached token exists, causing 400. |
| S2S: HTTP 403 `insufficient_scope: Required app role: Agent365.Observability.OtelWrite` | OtelWrite role not assigned to Agent Identity SP | Run PowerShell: `Connect-MgGraph; $sp = Get-MgServicePrincipal -Filter "appId eq '<agentId>'"` then `New-MgServicePrincipalAppRoleAssignment` with OtelWrite role from observability API SP (`9b975845-388f-4429-889e-eab1ef63949c`) |
| S2S: FMI Hop 3 returns `AADSTS700024` | Agent Identity has no FMI credential | Verify `a365 setup all` completed successfully — it creates the federated credential on the Agent Identity |
| S2S: HTTP 200 but `rejectedSpans > 0` | Missing baggage context (tenant_id/agent_id) | Ensure `BaggageBuilder().tenant_id(...).agent_id(...).build()` wraps all scope code — without it, spans lack identity and are rejected |
