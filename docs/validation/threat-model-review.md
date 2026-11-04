# Threat model review: ThunderID.AspNetCore

Reviewed against `docs-internals/sdk-development/threat-model.md` (blob `233ecf6`, change log
0.3), as a **Framework Specific SDK for ASP.NET Core** (gap report, section 1). Line numbers
refer to that file. It answers the four questions in the SDK Development skill, section 5.

**Status values:**

| Value | Meaning |
|---|---|
| Mitigated | Evidence shows the mitigation holds |
| Mitigated (delegated) | ASP.NET Core provides it, and a probe shows it |
| Mitigated (accepted deviation) | Mitigated differently from the model, as accepted by a recorded decision (`spec-matrix.md`, "Decisions") |
| Partial | Holds in part; the cell says what's missing |
| Gap | The mitigation the model relies on is absent |
| N/A | The threat can't arise in this SDK; the cell says why |

**Evidence values:**
- **S, BCL, C, E, T, AC** IDs are rows in [spec-matrix.md](spec-matrix.md).
- **P1 to P14** are the throwaway probes. P14 is new in this step; its output is in RUNBOOK,
  "SDK validation, Step 6".
- **M1 to M9** are the mutation runs in RUNBOOK, "Step 5".
- A test name is a test in `tests/ThunderID.AspNetCore.Tests`.

## 1. The four questions (skill, section 5)

| # | Question | Answer for ThunderID.AspNetCore |
|---|---|---|
| 1 | Which modelled interactions does it touch? | **01** redirect and callback, **02** code exchange and storage, **04** token attachment (the non-isolated path only), **05** sign-out, **06** back-channel logout. Not **03** or **07**: there's no embedded mode (O2). Both come into scope when embedded mode is added (`phases.md`, Phase 6) |
| 2 | Does it weaken a mitigation the model relies on, or add a key that could? | No ThunderID configuration key weakens anything: `ThunderIDOptions` has five keys, none of them security switches. But three mitigations are weaker than the model assumes: no revocation on sign-out (05.1), the discovery issuer isn't pinned (02.2), and ASP.NET Core's own options, such as `UsePkce`, stay writable by the app (01.4). A fourth difference is accepted: the PKCE verifier travels through the browser, encrypted, rather than staying in memory (01.3, decision Q1) |
| 3 | Does it add an interaction, actor or external dependency? | Yes, three dependencies the model doesn't list: ASP.NET Core's OpenID Connect handler (the protocol layer), ASP.NET Core Data Protection (its key ring protects `state`, the verifier and the tokens in the cookie), and NuGet (the model's registry list stops at pub.dev, `:126`). It also adds a component: a self-contained cookie session with no server-side record (section 3) |
| 4 | Does it change a recorded residual risk? | Two. Residual 3 is worse here: sign-out never revokes, not only when revocation is unreachable. Residual 7 has a second part: several instances need a shared Data Protection key ring as well as a shared store. See section 5 |

No finding in this review is exploitable without a precondition the model already treats as out
of scope or residual, such as control of the ThunderID server's HTTPS endpoint or a stolen cookie.

## 2. Interactions

### 01: Redirect authorization and callback (`:140-189`)

Applies. Done by ASP.NET Core's OpenID Connect handler, configured by `AddThunderID`.

| ID | Threat | Status | Evidence |
|---|---|---|---|
| 01.1 | Injected callback with the attacker's code (`:186`) | Mitigated (delegated) | S5, AC4.2: P2 shows a garbage or empty `state` fails with `AuthenticationFailureException`, and no session cookie is set |
| 01.2 | Intercepted code redeemed without the verifier (`:187`) | Mitigated (delegated) | S4, AC4.1: P1 shows S256, a 43-character unreserved verifier, and a fresh one per request. `UsePkce = true` (`ThunderIDAuthenticationExtensions.cs:48`). Pinned by a test: turning it off (M1) fails 1 test |
| 01.3 | Verifier persisted where co-resident code can read it (`:188`) | Mitigated (accepted deviation) | **Decision Q1 (2026-11-04):** the delegation to ASP.NET Core's handler is accepted, and with it this design. The verifier isn't written to disk or web storage, but it isn't "held in memory only" either. ASP.NET Core puts it inside the `state` value, encrypted and authenticated with the app's Data Protection key ring, and `state` travels through the browser (P1: `state` is 390 characters). Page scripts see only ciphertext; reading it needs the key ring. Recorded as a deviation (S4) |
| 01.4 | A key that disables PKCE or state (`:189`) | Partial | The SDK's keys can't: `ThunderIDOptions` has `Authority`, `ClientId`, `ClientSecret`, `Scopes` and `Resource` (`ThunderIDOptions.cs`). State has no off switch in ASP.NET Core. But the app can still set `OpenIdConnectOptions.UsePkce = false` through `PostConfigure`, and `AddThunderID` doesn't guard against it. The same holds for `TokenValidationParameters` (AC4.11) |

Security considerations (`:173-180`): transport meets, and is stricter than the model. A
non-HTTPS authority is rejected, loopback included (AC4.6, P4). The redirect URI is enforced by
ThunderID, as the model says.

### 02: Code exchange, token storage, and refresh (`:191-240`)

Applies. Exchange and ID token validation are delegated. Storage is ASP.NET Core's cookie.

| ID | Threat | Status | Evidence |
|---|---|---|---|
| 02.1 | Tokens in web storage read by any script (`:235`) | Partial | No web storage. Tokens are saved (`SaveTokens = true`, `ThunderIDAuthenticationExtensions.cs:49`) inside the authentication cookie, encrypted by Data Protection. P14: every cookie chunk is `httponly; samesite=lax`, so no page script can read it. Partial because of the server side (S1): the key ring is DPAPI-protected on Windows, but not encrypted at rest by default on Linux. The model's conditional applies too: an app can supply an `ITicketStore`, which `AddThunderID` doesn't surface and nothing documents (S2) |
| 02.2 | Forged or substituted ID token accepted (`:236`) | Partial | Signature, issuer, audience, expiry and nonce are all on (S8, AC4.4, P3). Two caveats. The issuer is compared against discovery's, and discovery's own `issuer` isn't checked against the authority (S7, AC4.8, P5). Reaching that needs control of the authority's HTTPS endpoint. And no test pins issuer, audience or nonce validation: turning each off (M2, M3, M4) leaves all 87 tests passing (T2) |
| 02.3 | Authorization decided from a locally decoded JWT (`:237`) | Mitigated | The SDK has no `decodeJwtToken`. The sample decodes the access token's payload for display only (`CallApi.cshtml.cs:31`). Access is decided by the API, which validates the token itself (`AddThunderIDApi`) and checks scopes (`samples/Api/Program.cs:12,25,34`) |
| 02.4 | Interrupted refresh leaves a mixed token set (`:238`) | N/A | There's no refresh (S12). The token set is written once, in the sign-in response |
| 02.5 | Concurrent refreshes stampede the token endpoint (`:239`) | N/A | There's no refresh. Missing refresh is itself a spec gap (AC4.7, S12) |
| 02.6 | Tokens or claims in logs (`:240`) | Partial | The SDK never logs a token (`Logs_never_contain_the_token`). But ASP.NET Core's request logging writes `/signin-oidc?code=...` at Information level, and the OIDC handler writes it again at Trace (S11, AC3.3, P11). The SDK neither prevents nor documents this |

### 03: Credential submission in embedded mode (`:242-289`)

N/A. There's no embedded mode (O2). The user enters credentials on ThunderID's own page.

### 04: Attaching access tokens to outbound requests (`:291-356`)

Applies, on the path where the app holds the token (`:319-336`). The SDK doesn't proxy
requests. The sample asks for the token (`CallApi.cshtml.cs:22`) and attaches it itself.

| ID | Threat | Status | Evidence |
|---|---|---|---|
| 04.1 | The SDK attaches a token for a disallowed host (`:353`) | N/A | The SDK never attaches a token (S3) |
| 04.1b | Authorization header follows a redirect (`:354`) | N/A | As 04.1 (AC4.10) |
| 04.2 | Prefix-matched allowlist (`:355`) | N/A | No allowlist (AC4.9) |
| 04.3 | Token in a query string (`:356`) | Mitigated | The sample sends it in the `Authorization` header (`CallApi.cshtml.cs:36,45`). On the API side, `AddThunderIDApi` sets no `OnMessageReceived`, so JWT bearer reads the header only |

### 05: Sign-out and session termination (`:358-402`)

Applies. Sign-out is the framework's `SignOutAsync` on the cookie and OIDC schemes. The SDK has
no sign-out operation of its own.

| ID | Threat | Status | Evidence |
|---|---|---|---|
| 05.1 | Local state cleared, refresh token still valid (`:400`) | **Gap** | No revocation call (AC4.5, S12). P9 and P14: 0 outbound calls on sign-out. Sign-out does redirect to ThunderID's end-session endpoint with `id_token_hint` (P14), which ends the ThunderID session. Whether ThunderID issues this client a refresh token wasn't measured: the default scopes are `openid profile email`, without `offline_access` |
| 05.2 | Partial clearing leaves tokens or claims behind (`:401`) | Mitigated (delegated) | P14: sign-in with an 8,000-character token set produced 4 cookies (the main one and 3 chunks). Sign-out expired all 4. Tokens and claims live only in those cookies, so nothing is left |
| 05.3 | Revocation fails offline: blocks, or clears without revoking (`:402`) | N/A | There's no revocation to fail. Local state never depends on the network: the cookie is expired in the same response that redirects to ThunderID (P14) |

### 06: Receiving a back-channel logout token (`:404-461`)

Applies. This is the part the library implements itself (`LogoutTokenValidator`,
`MapThunderIDBackchannelLogout`, `InMemoryLogoutSessionStore`).

| ID | Threat | Status | Evidence |
|---|---|---|---|
| 06.1 | Self-made token signs users out (`:451`) | Mitigated | S9, AC9.4: `Signature_from_another_key_with_the_same_kid_is_rejected`, `Tampered_payload_is_rejected`, `Unsigned_alg_none_token_is_rejected`, `Wrong_issuer_is_rejected`. Pinned: M6 (issuer) fails 3 tests. The S7 caveat applies: the issuer and keys come from discovery |
| 06.2 | Another client's token replayed here (`:452`) | Mitigated | `Token_for_another_client_is_rejected` (AC9.4) |
| 06.3 | ID token posted as a logout token (`:453`) | Mitigated | AC9.5: `Id_token_replayed_as_logout_token_is_rejected`, `Id_token_typ_is_rejected`, `Missing_events_is_rejected`, `Nonce_is_rejected`. No option disables a check (`LogoutTokenValidator.cs:120-146`). Pinned: M5 (`typ`) fails 4 tests |
| 06.4 | Captured token replayed after re-sign-in (`:454`) | Mitigated | Lifetime: `Expired_token_is_rejected`, `Iat_in_the_future_is_rejected`; M7b fails 2 tests. `jti` tracked: `Same_jti_twice_is_a_replay`, remembered until the token's `exp` plus 5 minutes (`InMemoryLogoutSessionStore.cs:54`). A `sub`-only token ends only sessions that began before `iat` (`InMemoryLogoutSessionStore.cs:72`), so a replay can't end a later session. BCL-3b is a gap in the other direction: sessions that began within the clock tolerance after `iat` aren't ended (P6). Decision Q2: a replay keeps getting `400` |
| 06.5 | Unknown `kid` flood forces JWKS fetches (`:455`) | Mitigated | AC9.7: `Unknown_kids_cannot_force_a_jwks_fetch_per_request` (20 unknown kids, at most 2 fetches) |
| 06.6 | Large or slow bodies (`:456`) | Mitigated | BCL-1: `Oversized_body_is_400`, `Oversized_body_without_content_length_is_400`, `Json_body_is_400`, `Get_is_405`. Token capped at 8 KiB (`LogoutTokenValidator.cs:19`). Request rate limiting is left to the app, as the model says, and isn't mentioned in the README |
| 06.7 | Ended-session list grows without bound (`:457`) | Mitigated | AC9.10: entries are kept for the cookie's lifetime (`ThunderIDAuthenticationExtensions.cs:26-28`) and dropped by `Prune` (`InMemoryLogoutSessionStore.cs:79`), which runs on every recorded logout (`InMemoryLogoutSessionStore.cs:32`). Only validated tokens reach the store |
| 06.8 | Logout token in logs (`:458`) | Mitigated | AC9.9: `Logs_never_contain_the_token`, `Errors_never_contain_the_token` |
| 06.9 | Several instances; the session survives on the others (`:459`) | Residual, documented | BCL-6: replaceable store, limit in README "Limits". See section 5, residual 7 |
| 06.10 | Access token already handed out keeps working (`:460`) | Residual, documented | BCL-7 |
| 06.11 | Encrypted logout token can't be read (`:461`) | Residual, partly documented | BCL-8: README says encrypted (JWE) logout tokens aren't supported, but doesn't name the client setting, ID token encryption |

### 07: Federated redirect inside an embedded flow (`:463-520`)

N/A. There's no embedded mode (O2). Federation happens on ThunderID's hosted page.

## 3. What the model doesn't cover: the self-contained cookie session

The model assumes the SDK keeps session state in a store it can clear (`:401`). ASP.NET Core's
default cookie is different: the whole session, tokens included, is inside the cookie, and the
server keeps no record of it. Expiring it in the browser (P14) doesn't invalidate a copy taken
earlier. That copy stays valid until the cookie expires: 14 days, sliding (P3).

| Case | Status | Evidence |
|---|---|---|
| Back-channel logout registered | Mitigated | Signing out from the app makes ThunderID send this app a logout token for its own session (RUNBOOK, original Step 6: "the initiating app is notified too"). The `sid` is then recorded as ended, and any cookie carrying it is refused: `Cookie_for_the_ended_sid_is_refused_and_cleared`, and live in Step 11 (logout delivered 12 ms after the session ended; the next request was refused) |
| Back-channel logout not registered | Partial | A copied cookie stays valid for its full lifetime. This is standard ASP.NET Core behaviour, and it needs a stolen cookie first. The README doesn't say that back-channel logout also closes this case |

If the maintainers accept the Reading A deviation, this needs an entry in the model: a cookie
session component, and the Data Protection key ring as an external dependency.

## 4. Security review checklist (`:522-580`), answered for this project

### Security considerations

| # | Consideration | State | Comments |
|---|---|---|---|
| 1 | Inputs and outputs validated | Partial | Callback `state` and the ID token are validated (delegated, P2, P3). Configuration is validated, but on the first sign-in rather than at startup (C6, F2). Discovery's `issuer` isn't (S7) |
| 2 | Rate limits | Partial | No automatic retry (E7). The JWKS re-fetch is bounded (06.5). Request limiting on the logout endpoint is left to the app and isn't documented |
| 3 | Least privilege | Partial | A confidential server-side client with a secret, which is appropriate here. The default scopes are `openid profile email`, wider than the spec's `openid` (C2) |
| 4 | Authentication and authorization at both layers | Yes | The web app authenticates by OIDC. The API validates the token itself (`AddThunderIDApi`) and checks scopes (`AddScopePolicy`, 10 tests). Decoded claims are used for display only (02.3) |
| 5 | Isolation between components | Partial | Tokens are in an `HttpOnly` cookie, out of page scripts' reach (P14). Server side, see 02.1 |
| 6 | Default credentials | N/A | None shipped. `appsettings.json` carries the author's test `ClientId`, which isn't a credential (D5) |
| 7 | Best-practice guidelines | Partial | RFC 7636, OIDC Core and OIDC Back-Channel Logout 1.0 are followed. RFC 7009 revocation isn't used (05.1). The discovery issuer check from RFC 9700's mix-up defence is missing (S7) |
| 8 | Secrets kept out of the tree and its history | Yes | Scan of all history (1 commit) on 2026-11-04: the only secret-like assignments are the test literals `"secret"` (`AddThunderIDTests.cs:19`) and `"unused"` (`BackchannelLogoutEndpointTests.cs:37`), and there are 0 JWT-shaped strings. The samples read the secret from user-secrets or environment variables (`README.md:94-110`) |
| 9 | Security-focused code review | Partial | This validation (Steps 3 to 6) is a review against the specification and threat model. No independent reviewer yet |
| 10 | SAST | Partial | The .NET SDK analyzers run on every build with 0 warnings, and caught a real bug during development (ASP0016, RUNBOOK Step 10). No security scanner such as CodeQL, and no CI to run one (R1) |
| 11 | SCA | Partial | `dotnet list package --vulnerable --include-transitive` on 2026-11-04: no vulnerable packages in any of the 4 projects. A one-off run, not in CI |
| 12 | DAST or API scanning | N/A | A client library, as in the model. There's no end-to-end suite either (T6) |
| 13 | Audit logs | N/A | Server-side |
| 14 | Configuration change audit | N/A | |
| 15 | Encryption in transit and at rest | Partial | In transit: HTTPS metadata required, non-HTTPS authority rejected (P3, P4). At rest: see 02.1 |
| 16 | Secrets in a secret store | Partial | The samples use user-secrets in development. There's no production guidance, such as a vault, for `ClientSecret` |
| 17 | Personal data kept out of logs | Partial | The SDK logs `sid` and `sub`, which are pseudonymous identifiers (`BackchannelLogoutEndpoint.cs:78`, `BackchannelLogoutCookieValidator.cs:40`). The framework's request log can contain the authorization code (02.6) |
| 18 | Secure-usage instructions | Partial | README "Limits" covers several instances and access tokens already issued. Missing: Data Protection key ring persistence and protection, request log levels, `ITicketStore`, and section 3 |

### Business impact and resilience

| # | Consideration | State | Comments |
|---|---|---|---|
| 1 | Business impact analysis | N/A | As in the model |

Resilience the model expects (`:553-555`): the logout path re-fetches JWKS once on an unknown key
(`LogoutTokenValidator.cs:61-67`). The ID token path relies on ASP.NET Core's
`ConfigurationManager` (S8, not probed). There's no refresh ahead of expiry (S12).

### Dependency and component health

| # | Consideration | State | Comments |
|---|---|---|---|
| 1 | Dependencies monitored and current | Partial | Clean scan today (checklist item 11). No automated monitoring such as Dependabot (no `.github/`) |
| 2 | End-of-life components | **Yes, from 2026-11-10** | `net9.0` and the `9.0.20` ASP.NET Core packages reach end of support on 2026-11-10 (K1). .NET 10 is supported to 2028-11-10 |
| 3 | Hardening guidance for operators | No | No deployment guidance: key ring persistence across instances, HTTPS termination, log levels |

### Privacy considerations

| # | Consideration | State | Comments |
|---|---|---|---|
| 1 | Purpose and legal basis | N/A | The application's |
| 2 | Data minimization | Partial | Default scopes request `profile` and `email` as well as `openid` (C2) |
| 3 | Personal data stored securely | Partial | See 02.1 |
| 4 | Privacy notices | N/A | The application's |
| 5 | Need-to-know access | Yes | Scoped tokens, with the scope checked at the API (`AddScopePolicy`) |
| 6 | Retention | Yes | Cookies are cleared on sign-out (P14). Logout records are kept only for the cookie's lifetime, and `jti` values only until the token's `exp` plus 5 minutes (06.4, 06.7) |
| 7 | Disposal on request | N/A | Server-side |
| 8 | Processing records | N/A | The application's |

## 5. Residual risks (`:581-611`) and this project

| # | Residual risk | Applies | Effect here |
|---|---|---|---|
| 1 | `allowedExternalUrls` prefix footgun | No | No allowlist (04) |
| 2 | Credential guessing at the flow endpoint | No | Redirect mode only; ThunderID's hosted page takes the credentials |
| 3 | Sign-out with revocation unreachable | Yes, and worse | Sign-out never revokes (05.1), so a refresh token, if one is issued, stays valid until it expires after every sign-out, not only an offline one |
| 4 | Caller-supplied storage adapter | Yes | Through `ITicketStore`. Not surfaced or documented (S2) |
| 5 | Platform compromise | Yes | On a server: host compromise, which includes the Data Protection key ring |
| 6 | No shared error code catalogue | Yes | There are no ThunderID error codes at all (E1, E9) |
| 7 | One instance gets the notification | Yes, documented, with a second part | The store is replaceable and the limit is in the README (BCL-6). Several instances also need a shared Data Protection key ring, or cookies issued by one instance can't be read by another. That isn't documented |
| 8 | Access tokens in circulation | Yes, documented | BCL-7. The window is the access token lifetime, 1 hour in the captured token (RUNBOOK, original Step 13 claim check) |
| 9 | Encrypted logout tokens | Yes, partly documented | BCL-8 |

## 6. Totals

Across the 30 threat rows in section 2: 28 table rows, plus 03 and 07 counted as one N/A row
each.

| Status | Count |
|---|---|
| Mitigated, including delegated and accepted deviations | 14 |
| Partial | 4 |
| Gap | 1 |
| N/A | 8 |
| Residual, documented in part or in full | 3 |
