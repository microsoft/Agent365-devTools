# MCP-Platform changes: `logEvaluate` telemetry endpoint

CLI-side counterpart of this doc: `src/Microsoft.Agents.A365.DevTools.Cli/Services/Agent365ToolingService.cs` (`LogEvaluateUsageAsync`).

This endpoint is a near-clone of the existing `LogRegisterExternalMcpServer` action in `AgentController.cs` (~L1241), with one **deliberate difference**: the body is empty. The evaluated MCP server URL is customer-private content and the CLI does not transmit it as part of this telemetry call.

## Purpose

Capture a per-user marker every time the `a365 develop-mcp evaluate` pipeline runs — distinct from `logRegister` which covers the register flow. Fires at the start of `EvaluationPipelineService.RunAsync`, so any future surface that drives evaluations is also attributed.

## Privacy boundary (why the body is empty)

| Data | Where it lives | Why the CLI doesn't send it |
|---|---|---|
| `userId`, `tenantId` | Bearer token | Already in the JWT — server extracts via `requestContextProvider`. Sending it in the body would duplicate (and weaken) the source of truth. |
| `serverUrl` (evaluated MCP endpoint) | Customer content | Identifies which third-party service the customer connects to. Not telemetry data the CLI gets to ship. |
| `evalEngine` (auto/copilot/claude-code/none) | Customer config | Customer's tooling preference. Not necessary for the marker. |

If the server-side handler can pull useful context from `ServiceContext.Activity` ambient state (because some upstream activity in the same request chain logged it), great — but that's a server-side decision, not a CLI obligation.

## CLI → server contract

| | |
|---|---|
| Route   | `POST /agents/externalMcpServers/logEvaluate` |
| Auth    | Same scheme + scope as `logRegister` (`McpScopes.AgentToolsPublishMCPServerAll`) |
| Content | `application/json` |
| Body    | **Empty** |
| Identity| Bearer token — server extracts via `requestContextProvider.GetOid()` / `GetTenantId()` |
| Response| `200 OK` on success; CLI does not branch on body, swallows non-200 as `LogDebug` |

## CLI-side caller (already shipped)

```
Q:\source\Agent365-devTools\src\Microsoft.Agents.A365.DevTools.Cli\
├── Services\
│   ├── IAgent365ToolingService.cs            // Task LogEvaluateUsageAsync(ct)
│   ├── Agent365ToolingService.cs             // BuildLogEvaluateUrl + LogEvaluateUsageAsync
│   └── Evaluate\
│       └── EvaluationPipelineService.cs      // calls LogEvaluateUsageAsync(ct)
                                              //  at top of RunAsync — NOTHING from the
                                              //  evaluate args (serverUrl, evalEngine) is
                                              //  forwarded into telemetry from the CLI side
```

Transport details the server can assume:
- HTTP client is `Internal.HttpClientFactory.CreateAuthenticatedClient(token)` — same factory `logRegister` uses.
- No `x-ms-correlation-id` header on telemetry calls (matches `logRegister`).
- CLI swallows all non-200 responses with `LogDebug` — telemetry never blocks the user-facing command.

## Server-side change required

### Controller action

Add to `AgentController.cs` right next to `LogRegisterExternalMcpServer` (~L1241). The attribute set, identity extraction, and correlation context setup are **identical to logRegister**. Two deliberate omissions vs. logRegister:

1. **No `[FromBody]` parameter** — body is empty.
2. **No body-derived custom properties** (no `ServerName`/`AuthType`/`ToolCount`-equivalent fields). If `ServiceContext.Activity` already carries useful context from earlier activities in the request chain, you can copy those forward, but do not require them.

```csharp
/// <returns>200 OK if telemetry was logged successfully.</returns>
[HttpPost]
[Route("/agents/externalMcpServers/logEvaluate")]
[MonitorWith(typeof(LogEvaluateExternalMcpServerActivity))]
[AuditClassification(
    OperationCategory.CustomerFacing,
    OperationType.Read,
    AccessLevel.User,
    GenevaAuditPlaneType.DataPlane,
    [DataClassification.CustomerContent],
    isGatewayHostedEndpoint: true,
    isCustomerFacing: true,
    isDeprecated: false,
    "Agent controller log evaluate external MCP server usage API")]
[GatewayAllowPassthrough]
[McpRequiredScope(Constants.McpScopes.AgentToolsPublishMCPServerAll)]
[ProducesResponseType(200)]
[ProducesResponseType(500)]
public IActionResult LogEvaluateExternalMcpServer()
{
    try
    {
        var userId = string.Empty;
        var tenantId = string.Empty;
        try
        {
            userId = this.requestContextProvider.GetOid();
            tenantId = this.requestContextProvider.GetTenantId();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to extract userId or tenantId from request context");
        }

        var currentClientCorrelationContext = ServiceContext.CaptureRoot()?.ClientCorrelation;
        using (ServiceContext.ClientCorrelation.SetClientCorrelationContext(
            clientPrincipalId: userId,
            clientTenantId: tenantId,
            clientSessionId: currentClientCorrelationContext?.ClientSessionId ?? string.Empty,
            clientRequestId: currentClientCorrelationContext?.ClientRequestId ?? string.Empty,
            clientAppId: currentClientCorrelationContext?.ClientAppIdId ?? string.Empty,
            organizationId: currentClientCorrelationContext?.OrganizationId ?? string.Empty))
        {
            ServiceContext.Activity.Current?.AddCustomProperty("StatusCode", "200");
        }

        return this.Ok(new { status = "logged" });
    }
    catch (Exception ex)
    {
        ServiceContext.Activity.Current?.AddCustomProperty("StatusCode", "500");
        ServiceContext.Activity.Current?.AddCustomProperty("Error", ex.GetType().Name);
        return this.StatusCode(500, new { error = "Failed to log evaluate telemetry" });
    }
}
```

### Activity class

Add `LogEvaluateExternalMcpServerActivity` alongside `LogRegisterExternalMcpServerActivity` (same file/folder, same base class — whatever the existing convention is). No new request DTO is needed.

### Diff from `LogRegisterExternalMcpServer` (every change explained)

| Line region | Change | Reason |
|---|---|---|
| `[Route(...)]`           | `logRegister` → `logEvaluate` | New endpoint |
| `[MonitorWith(...)]`     | `LogRegisterExternalMcpServerActivity` → `LogEvaluateExternalMcpServerActivity` | New activity class for separate dashboards |
| `AuditClassification` description | `"... log register ..."` → `"... log evaluate ..."` | Cosmetic, matches the activity |
| `[ProducesResponseType(400)]` | **Removed** | No body validation, so no `400 BadRequest` path |
| `[FromBody]` parameter   | **Removed** | Empty body — no DTO |
| Body validation block    | **Removed** | No fields to validate |
| `AddCustomProperty("ServerName"...)`, `"AuthType"`, `"ToolCount"` | **Removed** | None of these are valid for this endpoint — `serverUrl`/`evalEngine` are customer content and the CLI doesn't ship them |
| 500 error message        | `"... registration telemetry"` → `"... evaluate telemetry"` | Cosmetic |

Everything else — `requestContextProvider` usage, `ServiceContext.ClientCorrelation.SetClientCorrelationContext` block, the rest of the attribute set, the scope, the audit classification flags, the outer try/catch shape — is **identical**.

## Reviewer notes

- **No persistence** — telemetry-only, like `logRegister`.
- **Identity via `requestContextProvider`** — same path as logRegister so dashboards join cleanly.
- **No body, no validation** — if the CLI starts sending a body in the future, that is a privacy-review-worthy change and should be discussed before merging.
- **Authorization scope** — `AgentToolsPublishMCPServerAll` (same as logRegister). If evaluate needs a distinct scope, that's a CLI-side change too — coordinate first.

## Out of scope for this PR

- No completion/outcome marker. If you want one later, add a second endpoint (`/logEvaluateComplete`) — do not bolt outcome onto this start-of-workflow marker.
- No retry/backoff in the CLI. Telemetry failure is silently dropped by design.

## How to verify the integration end-to-end

1. Build the CLI from `users/ashragrawal/evaluate` branch with the telemetry changes staged.
2. Run `a365 develop-mcp evaluate --server-url <test-mcp-server> --output-dir /tmp/eval-out`.
3. On the MCP-Platform side, query the Geneva activity sink for `LogEvaluateExternalMcpServerActivity` events. Each event carries `userId` (Entra OID) and `tenantId` from the bearer token. The evaluated server URL is **not** in the event by design.
4. Confirm CLI exit code is unaffected even when the server endpoint returns 500 — telemetry must not block the user's evaluation run.
