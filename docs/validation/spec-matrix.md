# SDK specification matrix: ThunderID.AspNetCore

Judged as a **Framework Specific SDK for ASP.NET Core** (gap report, section 1), against
`spec.md` v0.8 (blob `64c9b69`). Line numbers refer to that file.

**Status values:**

| Value | Meaning |
|---|---|
| Meets | Evidence shows the rule holds |
| Meets (delegated) | ASP.NET Core does it, and a probe shows it |
| Meets (accepted deviation) | Differs from the spec in a way accepted by a recorded decision (see "Decisions" at the end) |
| Partial | Holds in part; the cell says what's missing |
| Gap | Doesn't hold |
| N/A | Doesn't apply to this SDK; the cell says why |

**Evidence values:**
- A **test** name is a test in `tests/ThunderID.AspNetCore.Tests`.
- **P1 to P13** are the throwaway probes. Output for P1 to P7 is in RUNBOOK, "SDK validation, Step 3"; for P8 to P13, "Step 4".

## 1. Security floor and back-channel logout (Step 3)

### Security requirements (`spec.md:661-763`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| S1 | Tokens in the platform's most secure store (an OS keyring on a server). Never persisted in an environment variable (`:665-672`) | Partial | `SaveTokens = true`. With no `SessionStore`, tokens live inside the auth cookie, encrypted by ASP.NET Data Protection (P3). The key ring is DPAPI-protected on Windows but not encrypted at rest by default on Linux. Never an environment variable: meets. **Phase 1:** README "Running in production" says to protect and share the keys (`README.md:202-206`); the Linux default is unchanged |
| S2 | Accept a caller-supplied storage adapter, and document that the secure default applies only when it's the one in use (`:674-676`) | Partial | ASP.NET Core accepts an `ITicketStore` (`CookieAuthenticationOptions.SessionStore`), so an app can set one with `PostConfigure`. `AddThunderID` doesn't surface it. **Phase 1:** the README's key table names it for `storage` (`README.md:188`), but doesn't say the default's protection applies only while the default is in use |
| S3 | Token attachment only to `allowedExternalUrls`, by parsed origin, HTTPS only, re-checked on redirect (`:680-693`) | N/A | The SDK doesn't proxy outbound requests or hold tokens in isolated storage. `samples/WebApp` attaches the token itself (`CallApi.cshtml.cs:22`) |
| S4 | PKCE: random verifier of 43 to 128 unreserved characters, S256, never `plain`, held only in memory and cleared after the exchange (`:695-698`) | Meets (accepted deviation) | Meets (delegated) for generation: P1 shows S256, a 43-character unreserved verifier, challenge = S256(verifier), and a fresh one per request. **Deviation:** ASP.NET Core doesn't keep the verifier in server memory. It goes inside the Data-Protected `state` value that travels through the browser (P1: state length 390). It's encrypted and authenticated, but that isn't "held only in memory" **Decision Q1:** the deviation is accepted, so the verifier inside the encrypted `state` is ASP.NET Core's accepted design |
| S5 | A random `state` on every request, validated on callback; a mismatch fails (`:700-701`) | Meets (delegated) | P1: present, and different per request. P2: a garbage or empty `state` gives `AuthenticationFailureException` ("Unable to unprotect the message.State", "message.State is null or empty"), and no session cookie is set |
| S6 | Federated redirects in embedded mode (`:703-718`) | N/A | No embedded mode |
| S7 | Discovery over HTTPS, and its `issuer` MUST match the expected issuer exactly before any endpoint is used. Overrides held to the same bar (`:720-727`) | Meets | HTTPS: meets (`RequireHttpsMetadata = True`, P3 and P4). **Phase 1:** `IssuerPinnedConfigurationManager` refuses a discovery document whose `issuer` isn't `Authority`, by ordinal comparison (`IssuerPinnedConfigurationManager.cs:19`), before any of its endpoints or keys is used. It wraps the OIDC handler's configuration manager, which the logout validator shares, and JWT bearer's (`ThunderIDAuthenticationExtensions.cs:84`, `:111`); `ValidIssuer = Authority` (`:60`, `:108`). Tests: the 7 in `DiscoveryIssuerTests`, including `Sign_in_is_refused_when_discovery_reports_another_issuer`, `Logout_token_is_refused_when_discovery_reports_another_issuer`, `Api_token_is_refused_when_discovery_reports_another_issuer` and `Issuer_comparison_is_exact`. Removing the check fails the attack tests (Phase 1, Step 4). Overrides: N/A (no override keys) |
| S8 | ID token: signature, issuer (exact), audience, expiry, nonce. JWKS cached, re-fetched once on failure (`:729-732`) | Meets (delegated) | P3: `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`, `RequireSignedTokens` and `RequireNonce` are all true. Caveat: the issuer compared is discovery's (`ValidIssuer` null, P3), so S7 carries over. Re-fetch: done by `ConfigurationManager`, not probed for the ID token path |
| S9 | Logout token: signature with an ID token algorithm, `typ` = `logout+jwt`, issuer exact, audience, `iat`/`exp` in tolerance, events member, no `nonce`, `sid` or `sub`. Can't be switched off. Bounded JWKS re-fetch. `jti` replay SHOULD be refused. Never logged (`:734-746`) | Meets | Tests: `Id_token_typ_is_rejected`, `Missing_typ_is_rejected`, `Signature_from_another_key_with_the_same_kid_is_rejected`, `Tampered_payload_is_rejected`, `Algorithm_not_used_for_id_tokens_is_rejected`, `Unsigned_alg_none_token_is_rejected`, `Wrong_issuer_is_rejected`, `Issuer_with_trailing_slash_is_rejected`, `Token_for_another_client_is_rejected`, `Expired_token_is_rejected`, `Iat_in_the_future_is_rejected`, `Missing_events_is_rejected`, `Nonce_is_rejected`, `Neither_sid_nor_sub_is_rejected`, `Unknown_kids_cannot_force_a_jwks_fetch_per_request`, `Same_jti_twice_is_a_replay`, `Logs_never_contain_the_token`. No option disables a check (`LogoutTokenValidator.cs:120-146`). Caveat: "exact" means exact against the **discovery** issuer, so S7 carries over (P5: a token from the real authority was rejected once discovery lied) |
| S10 | Embedded credentials, HTTPS without loopback, certificate pinning SHOULD (`:748-753`) | N/A | No embedded mode |
| S11 | Logging: codes and callback URLs with a code never logged; tokens masked; email and phone masked, before any caller logger (`:755-759`) | Partial | The SDK logs only `sid`, `sub` and rejection reasons (`BackchannelLogoutEndpoint.cs:69,78,84`, `BackchannelLogoutCookieValidator.cs:40`), never a token (test `Logs_never_contain_the_token`). ASP.NET Core's own request logging still writes the `/signin-oidc?code=...` URL at Information level. **Phase 1:** documented in README "Running in production" (`README.md:207-209`), but the SDK doesn't prevent it |
| S12 | Sessions: refresh ahead of expiry, rotation, deduplication. Sign-out revokes server-side **before** clearing local state. Each session bound to the ID token's `sid` (`:761-765`) | Partial | Refresh: none. `AddThunderID` has no refresh handling (P3), so a session runs on the access token until it expires (Phase 4). **Phase 2:** sign-out meets, see AC4.5. `sid` binding: meets (`MapInboundClaims = false` keeps `sid`; test `Cookie_for_the_ended_sid_is_refused_and_cleared`) |

### Back-channel logout handler requirements (`spec.md:482-516`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| BCL-1 | `POST`, form-encoded, one `logout_token`, body capped, no cookie or credential required | Meets | Tests `Get_is_405`, `Json_body_is_400`, `Multipart_body_is_400`, `Oversized_body_is_400`, `Oversized_body_without_content_length_is_400`, `Two_logout_tokens_is_400`, `Missing_logout_token_is_400`, `No_cookie_or_credential_is_needed`. P7 |
| BCL-2 | `200` for valid tokens, including unknown sessions. `400` for invalid. `5xx` only when the logout couldn't be recorded. `no-store` on every response | Meets | Tests `Valid_token_returns_200_with_no_store_and_records_the_logout`, `Valid_token_for_a_session_the_app_does_not_hold_returns_200`, `Invalid_token_is_400_with_reason_and_no_logout`, `Store_failure_is_500_with_no_store_so_thunderid_retries`. P7 (`no-store`). **Q2, answered (keep `400`):** `Replayed_token_is_400` means an identical redelivery after a lost `200` gets `400`, while the spec says a retried notification should be harmless **Decision Q2:** keep `400` for a replayed `jti`. `@thunderid/node` also rejects a token "already handled" |
| BCL-3a | A `sid` token ends the sessions bound to that `sid`. The `sid` is recorded at sign-in | Meets | Tests `Sid_logout_ends_that_session`, `Cookie_for_the_ended_sid_is_refused_and_cleared`, `Cookie_for_another_sid_of_the_same_user_survives` |
| BCL-3b | A `sub`-only token ends sessions that began before `iat`, **extended by the clock tolerance** | Meets | **Phase 1:** the store ends a session when `authTime <= IssuedAt + clockSkew` (`InMemoryLogoutSessionStore.cs:78`), with the validator's tolerance, `LogoutTokenValidator.DefaultClockSkew`, 2 minutes (`LogoutTokenValidator.cs:21`, `InMemoryLogoutSessionStore.cs:29`). Tests: `Sub_only_logout_ends_sessions_that_began_within_the_clock_tolerance` (+0, +30, +119, +120 s), `Sub_only_logout_leaves_a_sign_in_after_the_clock_tolerance_alone` (+121 s), `Sub_only_logout_uses_the_configured_clock_tolerance`, `Sub_only_logout_refuses_earlier_sessions_and_keeps_a_later_sign_in`. Reverting to the strict `<` fails them (RUNBOOK, Phase 1, Step 3). `ILogoutSessionStore` documents the rule for custom stores |
| BCL-4 | Local only: no revocation or end-session call, no redirect | Meets | P7: the only outbound calls were discovery and JWKS. No `Location`, no `Set-Cookie` |
| BCL-5 | Cookie sessions: record the logout and refuse matching requests for as long as such a session could be valid | Meets | `BackchannelLogoutCookieValidator.cs:36-43`. Retention is the cookie's `ExpireTimeSpan` (`ThunderIDAuthenticationExtensions.cs:26-28`, 14 days, P3). Test `Ended_session_is_remembered_for_the_retention_period` |
| BCL-6 | The store is replaceable by the app; an in-memory default MAY be used; the multi-instance need MUST be documented | Meets | `TryAddSingleton<ILogoutSessionStore>` (`ThunderIDAuthenticationExtensions.cs:26`). Doc comment on `ILogoutSessionStore` and README "Limits" |
| BCL-7 | Document that access tokens already handed out aren't recalled | Meets | README "Limits": "Access tokens already issued stay valid" |
| BCL-8 | Document that back-channel logout is unsupported for a client configured for ID token encryption | Meets | **Phase 1:** README "Limits": "back-channel logout doesn't work for a client configured for ID token encryption" (`README.md:247-248`) |

### R4 acceptance criteria (`spec.md:991-1039`)

| AC | Status | Evidence |
|---|---|---|
| AC4.1 PKCE S256, 43 to 128 unreserved characters, random state | Meets (delegated) | P1 |
| AC4.1b SDK-owned parameters win over caller `signInOptions` | Partial | There's no `signInOptions` key; the nearest is ASP.NET Core's `AdditionalAuthorizationParameters`. P12: a caller `state` is replaced by the SDK's own (meets). A caller `code_challenge`, `code_challenge_method`, `client_id` or `redirect_uri` makes the sign-in request throw `ArgumentException` ("An item with the same key has already been added"). The caller's value is never sent, so it fails closed, but it isn't "ignored, and the SDK's own value used". A non-owned parameter (`login_hint`) is sent as given |
| AC4.2 State mismatch fails, nothing stored | Meets (delegated) | P2 |
| AC4.3 Default token storage is the platform's secure store | Partial | S1 |
| AC4.4 ID token signature, issuer, audience, expiry and nonce validated | Meets (delegated) | P3, with the S7 issuer caveat |
| AC4.5Sign-out revokes server-side before clearing local state | Meets | **Phase 2:** `HttpContext.SignOutOfThunderIDAsync` POSTs the refresh token to the discovered `revocation_endpoint` with `client_secret_post`, then signs out of the cookie scheme, then of OpenID Connect (`ThunderIDSignOutExtensions.cs`). Unit: `Revokes_the_refresh_token_before_clearing_the_cookie_then_redirects_to_end_session` records the order; swapping it fails 2 tests. End-to-end: `Sign_out_revokes_the_refresh_token_and_ends_the_session_at_thunderid` (end-to-end), where the refresh token saved before sign-out then gets `invalid_grant`. The access token is revoked instead when there's no refresh token (`Revokes_the_access_token_when_the_session_has_no_refresh_token`) |
| AC4.6 Non-HTTPS, non-loopback `baseUrl` fails before any network call | Meets (delegated) | P4: `http://idp.example.com` is rejected when the options are first resolved. It's stricter than the spec: `http://localhost:8090` is also rejected. P8: in a running app the options are first resolved on the first sign-in request, so that's when it fails, with no outbound call before it (see C6) |
| AC4.6b Embedded mode with non-HTTPS | N/A | No embedded mode |
| AC4.8 Discovery issuer mismatch fails, and no endpoint from that document is used | Meets | S7, `DiscoveryIssuerTests` |
| AC4.9 `allowedExternalUrls` origin matching | N/A | S3 |
| AC4.10 Authorization header not carried across a redirect to a disallowed origin | N/A | S3 |
| AC4.11 A `tokenValidation` setting that disables a mandatory check is rejected | N/A | There's no `tokenValidation` key. The logout validator has no switch at all (S9). ASP.NET Core's own `TokenValidationParameters` stay writable by the app (for example `ValidateIssuer = false` through `PostConfigure`), and `AddThunderID` doesn't guard them; that's framework configuration outside the SDK's keys |
| AC4.12 Separate `instanceId` clients don't share state | N/A | There's no `instanceId` concept. P13: a second `AddThunderID` call throws `InvalidOperationException` ("Scheme already exists: Cookies"), so one context per app is enforced. The missing key isn't documented (C5) |
| AC4.13 Embedded `REDIRECTION` callback without code or state | N/A | No embedded mode |
| AC4.14 Mobile `REDIRECTION` state checks | N/A | Not mobile |
| AC4.15 Chained `REDIRECTION` | N/A | No embedded mode |
| AC4.7 Concurrent refreshes deduplicated | **Gap** | S12: there's no refresh to deduplicate |

### R9 acceptance criteria (`spec.md:1135-1169`)

| AC | Status | Evidence |
|---|---|---|
| AC9.1 A `sid` token ends that session; the next request is unauthenticated | Meets | `Cookie_for_the_ended_sid_is_refused_and_cleared`; live in Step 11 |
| AC9.2 A `sub`-only token ends sessions that began before `iat` or within the tolerance after; later ones unaffected | Meets | BCL-3b |
| AC9.3 A valid token naming no session gives `200`, and nothing changes | Meets | `Valid_token_for_a_session_the_app_does_not_hold_returns_200` |
| AC9.4 Bad signature, issuer, audience or lifetime gives `400`, and no session ends | Meets | S9 tests, plus `Invalid_token_is_400_with_reason_and_no_logout`, `Invalid_token_leaves_the_cookie_session_alone` |
| AC9.5 An ID token, wrong `typ`, no event, or a `nonce` gives `400` | Meets | `Id_token_replayed_as_logout_token_is_rejected`, `Id_token_typ_is_rejected`, `Missing_events_is_rejected`, `Events_without_the_logout_member_is_rejected`, `Nonce_is_rejected` |
| AC9.6 No revocation or end-session call, no redirect | Meets | P7 |
| AC9.7 Unknown `kid` stream: JWKS fetches bounded | Meets | `Unknown_kids_cannot_force_a_jwks_fetch_per_request` (20 unknown kids, at most 2 fetches) |
| AC9.8 Browser and mobile SDKs don't expose the handler | N/A | Server-side SDK |
| AC9.9 No logout token in logs | Meets | `Logs_never_contain_the_token`, `Errors_never_contain_the_token` |
| AC9.10 Cookie sessions refused until they could no longer be valid | Meets | BCL-5 |

### Step 3 totals (48 rows)

| Status | Count |
|---|---|
| Meets, including delegated | 22 |
| Partial | 7 |
| Gap | 7 |
| N/A | 12 |

Corrected in Step 4: AC4.1b moved from N/A to Partial (P12).

After the decisions of 2026-11-04 (see "Decisions" at the end): 23 Meets, 6 Partial, 7 Gap, 12 N/A. S4 moved to Meets (accepted deviation).

The 7 Gap rows cover four underlying gaps:
- `sub`-only clock tolerance: BCL-3b, AC9.2.
- Discovery issuer not pinned: S7, AC4.8.
- No refresh handling: AC4.7, part of S12.
- No revoking sign-out: AC4.5, part of S12.

After Phase 1 (0.1.0): 28 Meets, 5 Partial, 3 Gap, 12 N/A. BCL-3b, AC9.2, S7, AC4.8 and BCL-8 moved to Meets. The 3 Gap rows are refresh (AC4.7) and the revoking sign-out (AC4.5), with S12 covering both. S1, S2 and S11 stay Partial; what ASP.NET Core leaves to the app is now documented.

After Phase 2 (0.2.0): 29 Meets, 6 Partial, 1 Gap, 12 N/A. AC4.5 moved to Meets and S12 to Partial. The 1 Gap row is refresh (AC4.7), Phase 4.

## 2. Client surface, configuration, framework integration, errors (Step 4)

The spec's contract is behavioural: names are canonical, and signatures follow the platform
(`spec.md:363-365`). In ASP.NET Core, several canonical operations are framework verbs on
`HttpContext` (`ChallengeAsync`, `SignOutAsync`, `User`) rather than members of a ThunderID type.
Those rows are marked Partial: the behaviour exists, but there's no operation under the canonical
name.

### Canonical name mapping

| Canonical (`spec.md`) | ThunderID.AspNetCore | Status | Evidence |
|---|---|---|---|
| `initialize` (`:371`) | `services.AddThunderID(...)` | Meets | `ThunderIDAuthenticationExtensions.cs:19`. Validation is late, see C6 |
| `reInitialize` (`:372`) | Absent | Gap | Settings are copied once at registration (`ThunderIDAuthenticationExtensions.cs:21-22`). Q3: the server SDKs implement it (`@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a`, inherited from `@thunderid/javascript`), so it applies |
| `getConfiguration` (`:373`) | Framework only: `IOptionsMonitor<OpenIdConnectOptions>` | Meets | P3 reads the effective settings this way; there's no ThunderID-shaped view. **Q3:** framework idioms are accepted |
| `isLoading` (`:374`) | Absent | N/A | A server request has no loading state for the app to observe, and there's no background refresh. Q3: `@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a` don't implement it either (the base class throws "Method not implemented") |
| `signIn` (`:380`) | `HttpContext.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme)` | Meets | Behaviour: P1 (authorization code with PKCE). No `SignIn` operation; the sample calls the framework (`samples/WebApp/Pages/Account/SignIn.cshtml.cs:11`). **Q3:** framework idioms are accepted |
| `signOut` (`:381`) | `HttpContext.SignOutOfThunderIDAsync(redirectUri)` | Meets | **Phase 2:** revokes, then clears local state, then redirects to end-session (AC4.5). No session: succeeds silently (AC2.4). Revocation unreachable: local state cleared and the failure returned in `ThunderIDSignOutResult` (AC2.4b). An `HttpContext` extension in the framework's idiom (Q3) |
| `signUp` (`:382`) | Absent | N/A | **Q3:** not implemented by `@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a` (the base class throws "Method not implemented") |
| `isSignedIn` (`:383`) | `User.Identity.IsAuthenticated` | Meets | Framework property. **Q3:** framework idioms are accepted |
| `handleRedirectCallback` (`:384`) | Implicit at `/signin-oidc` | N/A | "Required wherever the SDK does not handle the callback implicitly". The OIDC handler does (P2) |
| `getAccessToken` (`:390`) | `HttpContext.GetTokenAsync("access_token")` | Partial | Returns the stored token (`CallApi.cshtml.cs:22`). No refresh near expiry (S12) |
| `exchangeToken` (`:391`) | Absent | Gap | Q3: implemented by `@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a`, so it applies |
| `decodeJwtToken` (`:392`) | Absent | Gap | The sample decodes the payload itself (`CallApi.cshtml.cs:54-60`). Q3: implemented by `@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a` (inherited), so it applies |
| `setSession`, `clearSession` (`:393`) | `SignInAsync`, `SignOutAsync` on the cookie scheme | Meets | Framework verbs. **Q3:** framework idioms are accepted |
| `getUser` (`:399`) | `HttpContext.User` | Meets | A `ClaimsPrincipal` with JWT claim names and a `name` fallback (`ThunderIDAuthenticationExtensions.cs:56-60,105-121`). **Q3:** framework idioms are accepted |
| `getUserProfile`, `updateUserProfile`, `getUserSchema` (`:400-402`) | Absent | N/A | One row for the three. **Q3:** not implemented by `@thunderid/node` and `@thunderid/express` at `javascript-sdks` `f25c3526a`; `getUserSchema` doesn't exist there |
| Management operations (`:414-457`) | Absent | N/A | Optional (MAY) |
| Organization operations (`:410-412`) | Absent | Meets | "MUST NOT ship a partial implementation": none shipped |
| Operations beyond the set (`:404-408`) | `AddThunderIDApi`, `AddScopePolicy` | Meets | Allowed, but "MUST follow Cross-SDK parity and delivery", which hasn't happened yet. **Q4:** both are accepted; parity is followed when possible |
| `handleBackchannelLogout` (`:477-481`) | `app.MapThunderIDBackchannelLogout(path)`, or `(path, onLoggedOut)` | Partial | A route handler mounted at the app's path, which the spec allows (`:479-481`): meets. It validates and ends sessions (section 1). **Phase 2:** the `onLoggedOut` overload gives the app the `sid` and `sub` after the logout is recorded (tests `Callback_gets_the_ended_sid_and_sub_after_the_logout_is_recorded`, `Callback_gets_a_null_sid_for_a_sub_only_token`, `Callback_is_not_called_for_a_rejected_replayed_or_unrecorded_token`, `Throwing_callback_is_logged_and_still_returns_200`). Still missing: the number of sessions ended, which can't be known with cookie sessions because they end on their next request (documented in the README), and documented failures (E9) |

### Configuration (`spec.md:291-360`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| C1 | Required keys implemented: `baseUrl`, and `clientId` in redirect mode (`:293-306`) | Meets | `ThunderIDOptions.Authority`, `ThunderIDOptions.ClientId` (`ThunderIDOptions.cs:7-9`) |
| C2 | Canonical key names; MUST NOT rename one or add a second name (`:297-299`) | Partial | `baseUrl` is `Authority` (ASP.NET Core's word). Matches: `ClientId`, `ClientSecret`, `Scopes`. `Resource` and `ThunderIDApiOptions.Audience` have no canonical key, so they aren't renames. **Q3:** `Authority` is acceptable. What remains is the `Scopes` default, `openid profile email` against the spec's `openid` (`spec.md:326`). **Phase 1:** kept, by your decision, and documented with the reason (`README.md:174-176`) |
| C3 | `baseUrl` rejected when not HTTPS; loopback MAY be allowed (`:305`) | Meets (delegated) | AC4.6, P4, P8 |
| C4 | `clientSecret` not accepted by a public client (`:307`) | N/A | Server-side confidential client only |
| C5 | A key that can't apply is documented as unsupported, not silently ignored (`:294-295`, `:353`) | Meets | **Phase 1:** README "Configuration" maps all 31 spec keys, each as supported, the ASP.NET Core option that covers it, not supported, or not applicable (`README.md:178-198`). `preferences` is listed as not applicable |
| C6 | Initialization validates and fails before any network call when a required key is missing or malformed (`:357-358`) | Meets | **Phase 1:** validated at startup, with 0 outbound calls: `Invalid_web_app_configuration_fails_at_startup_without_a_network_call` (empty, `http` and malformed `Authority`; empty `ClientId`), `Invalid_api_configuration_fails_at_startup_without_a_network_call` (`Authority`, `Audience`), `Http_authority_is_refused_even_when_aspnet_core_allows_it`. The valid cases also make 0 calls |
| C7 | An operation before initialization fails with a distinct, documented error (`:358-359`) | Partial | `MapThunderIDBackchannelLogout` without `AddThunderID` resolves `LogoutTokenValidator` with `GetRequiredService` (`BackchannelLogoutEndpoint.cs:52`), so a request throws .NET's "No service for type" `InvalidOperationException`. Not a null dereference, but not a documented ThunderID error. Read from the code, not probed |
| C8 | `backchannelLogout`: an SDK that exposes the handler for the app to mount takes the store on the handler and doesn't read the key (`:345`) | Meets | The app mounts the handler (`MapThunderIDBackchannelLogout`). The store comes from DI (`ILogoutSessionStore`, `ThunderIDAuthenticationExtensions.cs:26-28`), which is how the framework passes it |
| C9 | `session`: browser SDKs only (`:344`) | N/A | Server-side |
| C10 | `components`, `extensions`: SDKs with bundled UI (`:354-355`) | N/A | No bundled UI |

### Framework integration (`spec.md:547-562`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| F1 | Integrates into the framework's composition model; initialized once, at the root (`:549-550`) | Meets | `IServiceCollection` extension, called in `Program.cs` (`samples/WebApp/Program.cs:9`). P13: a second call throws |
| F2 | Configuration validated at the root, before the app renders (`:551`) | Meets | **Phase 1:** `IValidateOptions` with `ValidateOnStart()` for both schemes (`ThunderIDAuthenticationExtensions.cs:86`, `:113`). Test `Invalid_web_app_configuration_fails_at_startup_without_a_network_call`: the host fails at `StartAsync`, before it serves |
| F3 | A single primary entry point exposes the authentication surface; a standard flow needs no combination of them (`:557-561`) | Meets | `AddThunderID` alone is enough for sign-in, user and sign-out; back-channel logout is an optional extra (`MapThunderIDBackchannelLogout`). But no ThunderID service exposes the surface: the app uses framework verbs (mapping table). **Q3:** framework idioms are accepted, so framework verbs are the surface |

### Error model (`spec.md:630-660`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| E1 | One hierarchy: a base error with `message`, `code`, `origin`, and a runtime error (`:632-646`) | **Gap** | No ThunderID error type exists. P10: a failed sign-in callback raises `Microsoft.AspNetCore.Authentication.AuthenticationFailureException`, with no `Code` or `Origin` property and empty `Data`. P8: configuration errors are `ArgumentException` and `InvalidOperationException`. Back-channel rejections are HTTP 400 with `{"error":"invalid_request","error_description":"logout_token is invalid"}` (P10) |
| E2 | An API error adds `statusCode` and `statusText` (`:638`) | **Gap** | None; token endpoint failures surface as the framework's exceptions |
| E3 | Catchable through the platform's standard mechanism (`:647`) | Meets | All are .NET exceptions (P8, P10) |
| E4 | Never swallowed silently (`:648`) | Meets | The library's only `catch` logs at Error and answers 500 (`BackchannelLogoutEndpoint.cs:66-71`). Validator failures are returned, not thrown (`LogoutTokenValidator.cs:70-72`) |
| E5 | No credential, token or personal data in an error message (`:649`) | Meets | Tests `Errors_never_contain_the_token`, `Logs_never_contain_the_token`. P10: the failed-callback exception doesn't contain the code |
| E6 | Codes never change meaning; SHOULD match across SDKs and reuse existing codes (`:650-651`) | N/A | There are no codes (E1) |
| E7 | No automatic retry, except refresh and one JWKS re-fetch (`:652-654`) | Meets | No retry in the library; the one JWKS re-fetch is bounded (`LogoutTokenValidator.cs:61-67`, test `Unknown_kids_cannot_force_a_jwks_fetch_per_request`) |
| E8 | Input validation fails before any network call (`:655`) | Meets | P8 (0 outbound). The logout token's size is checked before parsing (`LogoutTokenValidator.cs:53-56`) |
| E9 | The codes the SDK raises are documented (`:657-659`) | **Gap** | 13 back-channel rejection reasons (9 in `LogoutTokenValidator.cs`, 4 in `BackchannelLogoutEndpoint.cs`), plus the exception mappings in `Describe` (`LogoutTokenValidator.cs:154`); none is in the README |

### R2 and R3 acceptance criteria (`spec.md:953-990`)

| AC | Status | Evidence |
|---|---|---|
| AC2.1 Names and returned concepts match other SDKs | Partial | Mapping table: 2 Meets, 8 Partial, 6 Gap, 3 N/A. After the decisions: 8 Meets, 2 Partial, 4 Gap, 5 N/A |
| AC2.2 Same `signIn()` call site in either mode | N/A | Redirect only; embedded is documented as unsupported (`README.md:169-170`) |
| AC2.3 An operation before `initialize()` fails with a distinct documented error | Partial | C7 |
| AC2.4 `signOut()` with no session succeeds without raising | Meets | **Phase 2:** `Without_a_session_sign_out_succeeds_silently_without_contacting_thunderid`: no exception, no outbound call, a redirect to the given URI, and no failure reported. Before Phase 2, P9 showed the same for the framework's `SignOutAsync` |
| AC2.4b Revocation unreachable: local state cleared, caller told | Meets | **Phase 2:** `Unreachable_revocation_still_clears_the_cookie_and_reports_the_failure`, `Revocation_refused_by_thunderid_is_reported`, `Revocation_that_times_out_is_reported`, `Missing_revocation_endpoint_is_reported`, `Unreachable_discovery_still_clears_the_cookie_and_redirects_locally`. Making the local sign-out depend on revocation fails 6 tests |
| AC2.5 A UI framework SDK needs only its primary entry point | N/A | Judged as a server framework (gap report, section 1). Whether Razor Pages or Blazor makes it a UI platform is an open question there |
| AC3.1 Errors carry message, code, origin | **Gap** | E1 |
| AC3.2 API errors carry status code and text | **Gap** | E2 |
| AC3.3 No credential, token or unmasked personal data in messages **or log output** | Partial | The SDK's own messages and logs: meets (E5). Log output: P11 captured 3 lines containing the authorization code from a failed callback. Two are ASP.NET Core's hosting request lines at **Information** (`Request starting ... /signin-oidc?code=<CODE>&state=...`), one is the OIDC handler at Trace (`MessageReceived: '?code=<CODE>...'`). Same finding as S11. **Phase 1:** documented in README "Running in production" (`README.md:207-209`) |
| AC3.4 Every code documented | **Gap** | E9 |

### Step 4 totals (51 rows)

| Status | Count |
|---|---|
| Meets, including delegated | 12 |
| Partial | 14 |
| Gap | 16 |
| N/A | 9 |

The 16 Gap rows come from six underlying gaps:
- No ThunderID error type with codes and `origin`, and no documented codes: E1, E2, E9, AC3.1, AC3.2, AC3.4.
- Missing operations: `reInitialize`, `signUp`, `exchangeToken`, `decodeJwtToken`, and the three profile operations.
- No revoking sign-out (from section 1): `signOut`, AC2.4b.
- `baseUrl` named `Authority`: C2.
- Unsupported keys not documented: C5.
- Configuration validated on the first sign-in request instead of at startup: F2 (C6 is the same cause, as Partial).

After the decisions of 2026-11-04 (see "Decisions" at the end): 19 Meets, 8 Partial, 13 Gap, 11 N/A. The 13 Gap rows are the error model (6), `reInitialize`, `exchangeToken` and `decodeJwtToken` (3), the revoking sign-out (`signOut`, AC2.4b), C5 and F2. `signUp` and the profile operations became N/A, C2 became Partial, and six mapping rows and F3 became Meets.

After Phase 1 (0.1.0): 22 Meets, 7 Partial, 11 Gap, 11 N/A. C5, C6 and F2 moved to Meets. The 11 Gap rows are the error model (6), `reInitialize`, `exchangeToken` and `decodeJwtToken` (3), and the revoking sign-out (`signOut`, AC2.4b). C2 stays Partial: the `Scopes` default is kept and documented.

After Phase 2 (0.2.0): 24 Meets, 7 Partial, 9 Gap, 11 N/A. `signOut` and AC2.4b moved to Meets. `handleBackchannelLogout` stays Partial: the `sid` and `sub` reach the app, the count doesn't. The 9 Gap rows are the error model (6), and `reInitialize`, `exchangeToken` and `decodeJwtToken` (3).

## 3. Layering, packaging, modes, CI and release, testing, docs and samples (Step 5)

Evidence values added in this step:
- **M1 to M9, M7b** are mutation runs in the scratch clone. Each changes one line of the library, runs the 87 repository tests, and restores the line. Output is in RUNBOOK, "Step 5".
- "Repo listing" means `git ls-files` at `d524d97`: there is no `.github/`, no `LICENSE`, and no tag.

### Layering (`spec.md:177-200`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| L1 | Exactly one parent layer, at or below the SDK's own (`:179-184`) | Meets (accepted deviation) | The recorded deviation (gap report, section 1): there is no ThunderID parent. The protocol layer is `Microsoft.AspNetCore.Authentication.OpenIdConnect`. **Decision Q1:** accepted |
| L2 | Collapsed layers kept separable, and the package documents which layers it covers (`:185-188`) | Meets | **Phase 1:** README "Install", **Layers**: the protocol layer is ASP.NET Core's, the package covers the Framework Specific layer plus its own logout token validation, and the deviation is recorded (`README.md:25-31`) |
| L3 | Doesn't skip its immediate parent (`:189-190`) | N/A | No ThunderID parent (L1) |
| L4 | Doesn't reimplement the parent's logic or expose its internal types (`:191-192`) | Meets | Login is left to the framework handler, not reimplemented (`README.md:6-8`). The logout validator is logic the framework doesn't have. Public types are the library's own plus framework builder types (`AuthenticationBuilder`, `IEndpointConventionBuilder`), which is the framework's extension idiom |
| L5 | Parent declared as a versioned dependency (`:193-196`) | Meets | Taking the framework handler as the parent: `Microsoft.AspNetCore.Authentication.OpenIdConnect` `9.0.20` and `JwtBearer` `9.0.20` (`ThunderID.AspNetCore.csproj:14-15`) |
| L6 | A breaking change bumps the major version here and above (`:197-198`) | N/A | Nothing released: no `<Version>`, no tag (repo listing) |
| L7 | The README records the spec version implemented (`:199`) | Meets | **Phase 1:** README "Install": "implements the ThunderID SDK specification v0.8" (`README.md:21-23`) |

### Integration packages (`spec.md:201-211`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| I1 | Integration package rules | N/A | Judged as Reading A (gap report, section 1) |

### Packaging (`spec.md:213-242`, `:817-819`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| K1 | Minimum platform version declared, and not raised in a patch or minor release (`:215-217`) | Meets | **Phase 1:** `<TargetFramework>net10.0</TargetFramework>` (`ThunderID.AspNetCore.csproj:4`), with `global.json` pinning SDK 10.0.100 or a later feature band; README prerequisite ".NET 10 SDK". .NET 10 is supported to 2028-11-10 |
| K2 | Package metadata for the ecosystem registry | Meets | **Phase 1:** the csproj has `PackageId`, `Version` 0.1.0, `Authors`, `Description`, `PackageTags`, `PackageLicenseExpression` Apache-2.0, `PackageProjectUrl`, `RepositoryUrl`, a package README and a `.snupkg` with Source Link; `LICENSE` is Apache-2.0. The packed nuspec was read back (RUNBOOK, Phase 1, Step 7). On nuget.org from Step 12 |
| K3 | Naming follows the ecosystem (`:817-819`) | Meets | `ThunderID.AspNetCore` follows NuGet's PascalCase dotted convention (`Microsoft.AspNetCore.*`). .NET isn't in the spec's list |
| K4 | Apple and Android: Core Lib separable from Platform (`:228-230`) | N/A | Not Apple or Android |
| K5 | Bridged SDKs: pinned native dependencies, no protocol logic in the bridge, mirrored names, platform capabilities (`:231-242`) | N/A | Not bridged |

### Operational modes (`spec.md:243-290`)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| O1 | Redirect mode: authorization code with PKCE, handling the callback (`:248-252`) | Meets (delegated) | P1, P2 |
| O2 | The SDK supports two modes (`:245`) | **Gap** | Embedded mode is absent, documented as unsupported (`README.md:169-170`). Q3: `@thunderid/express` ships an embedded sign-in flow (`handleFlow`), so embedded mode applies |
| O3 | `mode` key; inference isn't the only way to select it; the mode doesn't change the surface (`:263-274`) | N/A | Redirect only |
| O4 | `client_credentials` (MAY), with its rejection and unsupported-operation rules (`:276-289`) | N/A | Not shipped |

### Repository, CI and release (`spec.md:801-819`, R5)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| R1 | Pull request pipeline that lints, builds and tests (`:808`) | Meets | **Phase 1:** `.github/workflows/pr-builder.yml`, on every pull request and push to `main`: restore, a vulnerable-package check, build with `-warnaserror` (the .NET analyzers are the lint; no format check), test, pack. First run green: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41823076901 |
| R2 | The parity check as a required check (`:809`) | Gap | **Phase 1:** the caller of ThunderID's `sdk-parity-check.yml` was added, then removed by your decision not to create its labels (RUNBOOK, Phase 1, Step 9) |
| R3 | Release pipeline that publishes on a version tag (`:810-811`) | Meets | **Phase 1:** `.github/workflows/release.yml` publishes on a `v*` tag on `main` with NuGet trusted publishing, after checking the tag against the csproj `Version`. Tag `v0.1.0` published `ThunderID.AspNetCore` 0.1.0 and its symbols, and created the GitHub release (https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41828692549); nuget.org lists 0.1.0 |
| R4 | Nightly or scheduled end-to-end run against a real server (`:812`) | Partial | **Phase 2:** `.github/workflows/nightly-e2e.yml` runs the end-to-end suite at 02:30 UTC and on demand, against a ThunderID built from pinned commit `46a6afc35`. It can only run once it's on `main`; Meets after its first green run |
| R5 | AC5.1 to AC5.6 | N/A | They test how the parity check itself behaves, and that check lives in the ThunderID repository. This repository's part is R2 |

### Testing (`spec.md:821-866`, R7)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| T1 | Every public operation has unit coverage for its success path and each error (`:832-833`) | Meets | Back-channel: every rejection reason has a test. `AddScopePolicy`: 10 tests, including its one error. **Phase 1:** `AddThunderID` and `AddThunderIDApi` failure cases: every configuration error and weakened setting in `StartupValidationTests` (21 tests), and the issuer mismatch in `DiscoveryIssuerTests` (7) |
| T2 | Mandatory security behaviour pinned by a test that fails if it's removed (`:833-837`) | Partial | **Caught:** PKCE off (M1), logout `typ` (M5), logout issuer (M6), logout lifetime (M7b), `no-store` (M8), `sid` claim kept (M9). **Phase 1:** ID token issuer validation off (M2), audience off (M3) and nonce not required (M4) are now refused at startup; each mutation fails 30 tests. Also caught: the discovery issuer pin and the `sub`-only tolerance. **Still untested:** state comparison and the storage default, both delegated to ASP.NET Core. Refresh deduplication and revocation ordering don't exist to test **Phase 2:** revocation ordering on sign-out is pinned: revoking after the cookie is cleared fails 2 tests in `SignOutTests`. |
| T3 | No network in unit tests (`:837-838`) | Meets | Server interactions are stubbed: `StaticConfigurationManager`, test server, `HttpMessageHandler` (`LogoutTokenValidatorTests.cs`, `BackchannelLogoutEndpointTests.cs`); no `new HttpClient(` in `tests/`. README: "87 tests, no network access" (`README.md:134`) |
| T4 | Reference parity fixtures for reimplemented deterministic behaviour (`:840-844`) | N/A | The library reimplements none. The sample's initials (`samples/WebApp/UserDisplay.cs`) are sample code |
| T5 | Component tests for UI components (`:829`) | N/A | No UI components |
| T6 | End-to-end suite running the sample against a real server: sign-in, sign-out, sign-up, from a clean state, driven by flow element identifiers (`:846-850`) | Partial | **Phase 2:** `tests/ThunderID.AspNetCore.E2E` (Microsoft.Playwright) covers sign-in, sign-up, sign-out and back-channel logout against a real ThunderID started from clean volumes, selecting ThunderID's page elements by flow identifiers (AC7.6). 5 of 5 passed in 3 runs from clean volumes. **Not the quickstart sample:** it drives `E2EApp`, a test app hosted in the test process, because the suite reads the refresh token and the callback from it, and the sample's HTTPS redirect blocks back-channel delivery over plain HTTP from the container (RUNBOOK, Phase 2, Step 3) |
| T7 | The end-to-end server is provisioned from the repository, reproducibly on a developer machine (`:858-861`) | Meets | **Phase 2:** `tests/e2e/thunderid`: a compose file, a `deployment.yaml` overlay and a bootstrap file with the flows, app and test user, imported by ThunderID's own `setup.sh`. No console steps. Run locally from clean volumes 6 times (Steps 4 and 6) |
| T8 | Unit tests on every pull request; end-to-end at least nightly (`:863-866`) | Partial | Unit tests on every pull request: meets, **Phase 1:** `pr-builder.yml` (R1). End-to-end nightly: **Phase 2:** the workflow exists (R4), first run pending |

### Documentation and samples (`spec.md:868-892`, R6)

| ID | Rule | Status | Evidence |
|---|---|---|---|
| D1 | Docs authored in the product repository's `docs/` and published to thunderid.dev, with a quickstart and a full reference (`:872-875`) | **Gap** | The quickstart is only in this README (`README.md:43-137`). `upstream/main:docs/content` has no .NET page (`git ls-tree`) |
| D2 | A reference covering every public operation, key and type (`:874-875`) | Partial | README "What's in the box" covers `AddThunderID`, `MapThunderIDBackchannelLogout`, `AddThunderIDApi`, `AddScopePolicy` and `ILogoutSessionStore` (`README.md:14-20`). Not covered: each `ThunderIDOptions` key, `ThunderIDApiOptions`, `LogoutTokenValidator`, `LogoutToken`, `LogoutTokenValidationResult`, `InMemoryLogoutSessionStore` |
| D3 | At least one runnable sample in the SDK's own repository (`:883-884`) | Meets | `samples/WebApp`, `samples/Api` |
| D4 | The sample runs against a local server using only its documented settings (`:884-885`) | Meets | Settings table and user-secrets or environment variables (`README.md:94-110`). The recorded demo (clip C5, `docs/blog/video.md`) ran this way |
| D5 | No hardcoded credentials or server URLs; only the public SDK surface (`:885-886`) | Meets | No secret anywhere; code reads configuration. **Phase 1:** `samples/WebApp/appsettings.json` ships an empty `ClientId`, and `CallApi.cshtml` shows the configured `BillingApi:BaseUrl`. The remaining `localhost` values are configuration defaults matching the README quickstart, not code; judged Meets on that reading. Public surface: meets |
| D6 | Samples built in CI (`:886`) | Meets | **Phase 1:** `pr-builder.yml` builds the solution, samples included, with `-warnaserror` |
| D7 | Minimum sample flows: client side (unauthenticated state, sign-in, name, email, picture or initials, sign-out) and server side (public route, protected route rejecting no token, claims with a token) (`:888-892`) | Meets | Client: `Index`, `Profile` with `UserDisplay.DisplayName`, `Email`, `Initials`, and `Account/SignOut`. Server: `/public`, and `/me` with `RequireAuthorization()` (`samples/Api/Program.cs:20-25`) |

### R1, R6, R7 and R8 acceptance criteria

| AC | Status | Evidence |
|---|---|---|
| AC1.1 README names the spec version | Meets | L7 |
| AC1.2 The proposal states its layer and parent | Meets | Gap report, section 1 |
| AC1.3 Canonical configuration key names | Meets | C2. **Q3:** `Authority` accepted as the framework's name |
| AC1.4 Unsupported keys listed | Meets | C5 |
| AC1.5 A new capability updates the spec | N/A | No capability beyond the spec is proposed; back-channel logout is already in it |
| AC6.1 A quickstart on the documentation site | **Gap** | D1 |
| AC6.2 The reference covers every operation, key and type | Partial | D2 |
| AC6.3 A runnable sample in the repository | Meets | D3 |
| AC6.4 The sample completes the flow with only the documented settings | Meets | D4 |
| AC6.5 No hardcoded credentials or URLs; public surface only | Meets | D5 |
| AC7.1 Success path and each error covered | Meets | T1 |
| AC7.2 Removing a mandatory security behaviour fails a test | Partial | T2 |
| AC7.3 Unit tests pass with no network | Meets | T3 |
| AC7.4 Reference fixtures | N/A | T4 |
| AC7.5 End-to-end sign-in, sign-out and sign-up from a clean state | Meets | **Phase 2:** `Sign_in_reaches_the_protected_page_with_the_users_claims`, `Sign_up_creates_a_user_and_lands_signed_in`, `Sign_out_revokes_the_refresh_token_and_ends_the_session_at_thunderid`, from clean volumes. Through `E2EApp`, not the sample (T6) |
| AC7.6 End-to-end selects elements by flow identifiers | Meets | **Phase 2:** ThunderID's hosted page renders inputs with their flow `ref` and actions with their flow element ID. The page objects select `#username`, `#password`, `#email`, `#action_001`, `#action_credentials`, `#action_schema_attrs` and `[data-action-ref=action_signup]` (`ThunderIDPages.cs`), never visible text or structure |
| AC7.7 Unit tests run on every pull request | Meets | T8, R1 |
| AC7.8 End-to-end server provisioned from the repository | Meets | T7 |
| AC8.1 Minimum platform version declared | Meets | K1 |
| AC8.2 Platform product without Core Lib (Apple, Android) | N/A | K4 |
| AC8.3 to AC8.6 Bridged SDK rules | N/A | K5 |

### Step 5 totals (58 rows)

| Status | Count |
|---|---|
| Meets, including delegated | 14 |
| Partial | 8 |
| Gap | 22 |
| N/A | 14 |

The 22 Gap rows come from six underlying gaps:
- No CI, release pipeline or package metadata (including a licence): R1 to R4, T8, D6, AC7.7, K2.
- No end-to-end suite or reproducible server provisioning: T6, T7, AC7.5, AC7.6, AC7.8.
- Docs: the README doesn't name the spec version, and there's no page on the ThunderID docs site: L7, AC1.1, D1, AC6.1.
- The layering deviation (no ThunderID parent, layers covered not documented): L1, L2.
- No embedded mode: O2.
- Carried from section 2: AC1.3 (C2), AC1.4 (C5).

After the decisions of 2026-11-04 (see "Decisions" at the end): 16 Meets, 8 Partial, 20 Gap, 14 N/A. L1 and AC1.3 moved to Meets.

After Phase 1 (0.1.0): 29 Meets, 5 Partial, 10 Gap, 14 N/A. L2, L7, K1, K2, R1, R3, D5, D6, T1, AC1.1, AC1.4, AC6.5, AC7.1 and AC7.7 are Meets; T8 moved to Partial. The 10 Gap rows are the end-to-end suite (R4, T6, T7, AC7.5, AC7.6, AC7.8), the docs site page (D1, AC6.1), embedded mode (O2), and the parity check (R2), whose caller was removed.

After Phase 2 (0.2.0): 33 Meets, 7 Partial, 4 Gap, 14 N/A. T7, AC7.5, AC7.6 and AC7.8 moved to Meets. R4 and T6 moved to Partial: R4 until the nightly run's first pass on `main`, and T6 because the suite drives a test app, not the sample. The 4 Gap rows are the docs site page (D1, AC6.1), embedded mode (O2), and the parity check (R2).

## Whole matrix (157 rows)

After the decisions of 2026-11-04:

| Status | Section 1 | Section 2 | Section 3 | Total | After Phase 1 (0.1.0) | After Phase 2 (0.2.0) |
|---|---|---|---|---|---|---|
| Meets, including delegated and accepted deviations | 23 | 19 | 16 | 58 | 79 (28, 22, 29) | 86 (29, 24, 33) |
| Partial | 6 | 8 | 8 | 22 | 17 (5, 7, 5) | 20 (6, 7, 7) |
| Gap | 7 | 13 | 20 | 40 | 24 (3, 11, 10) | 14 (1, 9, 4) |
| N/A | 12 | 11 | 14 | 37 | 37 (12, 11, 14) | 37 (12, 11, 14) |
| Rows | 48 | 51 | 58 | 157 | 157 | 157 |

Before the decisions: 48 Meets, 29 Partial, 45 Gap, 35 N/A.

Phase 1 rows carry "**Phase 1:**" in their evidence, and Phase 2 rows "**Phase 2:**". The counts were recounted by script from the rows, not edited by hand.

Coverage: all 67 acceptance criteria in the pinned `spec.md` (64 numbered, plus AC2.4b, AC4.1b and AC4.6b) appear in the matrix, checked by script.

## Decisions (2026-11-04)

Answers to the gap report's questions Q1 to Q4, and the rows they changed.

| # | Decision | Rows changed |
|---|---|---|
| Q1 | The deviation is acceptable: no ThunderID parent, with the protocol layer delegated to ASP.NET Core's OpenID Connect handler | L1 and S4 to Meets (accepted deviation). L2 stays Gap: the README still has to say which layers the package covers |
| Q2 | Yes: a replayed `jti` keeps getting `400` | None; BCL-2 was already Meets |
| Q3 | Match a server-side framework SDK, and the framework's idioms are acceptable. The spec's client surface wasn't written for server SDKs, so there's room to adapt | `getConfiguration`, `signIn`, `isSignedIn`, `setSession`/`clearSession`, `getUser`, F3 and AC1.3 to Meets. C2 to Partial (only the `Scopes` default remains). `signUp` and the profile operations to N/A |
| Q4 | `AddThunderIDApi` and `AddScopePolicy` are acceptable, with cross-SDK parity followed when possible | "Operations beyond the set" to Meets |

"A server-side framework SDK" is read as what ThunderID's own server SDKs ship: `@thunderid/node`
and `@thunderid/express` in `thunder-id/javascript-sdks` at `f25c3526a` (2026-11-03).
- **They implement:** `initialize`, `reInitialize`, `getConfiguration`, `signIn`, `signOut`,
  `isSignedIn`, `getAccessToken`, `exchangeToken`, `decodeJwtToken`, `setSession`,
  `clearSession`, `getUser`, `handleBackchannelLogout`, and an embedded sign-in flow (Express's
  `handleFlow`).
- **They don't implement:** `isLoading`, `signUp`, `getUserProfile` and `updateUserProfile` are
  stubs that throw "Method not implemented" in the shared base class, and Node doesn't override
  them. `getUserSchema` doesn't exist.

So `reInitialize`, `exchangeToken`, `decodeJwtToken` (Gap) and embedded mode (O2, Gap) still
apply.

Node's `signOut` returns the end-session URL without revoking, and offers `revokeAccessToken`
separately. The `signOut` row and AC4.5 stay Gap anyway, because revocation before clearing local
state is in the spec's security floor and the threat model (interaction 05).

Not yet answered: Q1's second part, whether the threat model should gain entries for ASP.NET Core
Data Protection and the self-contained cookie session.
