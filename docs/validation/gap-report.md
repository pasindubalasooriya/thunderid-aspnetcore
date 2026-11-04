# ThunderID SDK specification: gap report for ThunderID.AspNetCore

Validated against ThunderID's SDK Development skill and the SDK specification and threat model,
pinned by content:

| Document | Version | Blob |
|---|---|---|
| `.agent/skills/sdk-development/SKILL.md` | not versioned | `e6ff612` |
| `docs-internals/sdk-development/spec.md` | 0.8, Draft | `64c9b69` |
| `docs-internals/sdk-development/threat-model.md` | 0.3 (change log) | `233ecf6` |

The project was originally built against spec v0.5. Line numbers below refer to the pinned
`spec.md`.

## Status after Phase 1 (0.1.0)

Phase 1 of [phases.md](../../phases.md), on 2026-11-04. Sections 1 to 6 below are the validation
as it was done; this section says what has changed since. The matrix rows Phase 1 changed carry
"**Phase 1:**" in their evidence.

| Gap | Now | How |
|---|---|---|
| G1 discovery issuer | Closed | A discovery document whose `issuer` isn't `Authority` exactly is refused, for sign-in, logout tokens and API tokens |
| G2 `sub`-only tolerance | Closed | The store applies the validator's 2-minute clock tolerance |
| G7 `Scopes` default | Documented | Kept as `openid profile email`, with the reason in the README |
| G8 unsupported keys | Closed | The README maps all 31 spec keys |
| G9 startup validation | Closed | `IValidateOptions` with `ValidateOnStart()`; also refuses weakened PKCE, nonce and token validation settings (threat review 01.4) |
| G10 CI | Partly closed | A PR builder builds, tests and packs on every pull request (R1, T8 unit half, D6, AC7.7). The parity check (R2) was added, then removed by the author's decision not to use its labels |
| G11 release and package | Partly closed | Package metadata, `LICENSE` and `net10.0` (K1, K2). The release workflow exists; R3 is Partial until it publishes 0.1.0 |
| G13 spec version and docs site | Partly closed | The README names spec v0.8 (L7, AC1.1). The docs site page (D1, AC6.1) is Phase 5 |
| G14 layers | Closed | The README names the layers covered and the recorded deviation |

**Partial rows:**
- T1 and AC7.1 are Meets, and so are D5, AC6.5 and BCL-8.
- T2 and AC7.2 stay Partial. The delegated ID token checks are now pinned; state comparison and the storage default are still untested.
- S1, S2, S11 and AC3.3 stay Partial, with what ASP.NET Core leaves to the app now documented in the README's "Running in production" section.

**Still open:** G3 (revoking sign-out), G4 (refresh), G5 (error model), G6 (operations), G12
(end-to-end suite), G15 (embedded mode), R2 (parity check), and the docs site page from G13.

| Section | Rows | Meets | Partial | Gap | N/A |
|---|---|---|---|---|---|
| 1. Security floor and back-channel logout | 48 | 28 | 5 | 3 | 12 |
| 2. Client surface, configuration, framework integration, errors | 51 | 22 | 7 | 11 | 11 |
| 3. Layering, packaging, modes, CI and release, testing, docs and samples | 58 | 28 | 6 | 10 | 14 |
| **Total** | **157** | **78** | **18** | **24** | **37** |

Recounted by script from the matrix rows.

## 1. Classification

The spec requires this decision before anything else ("Applying this specification", step 2,
`spec.md:901`; skill §3). Every later row is judged against it.

### What the project is

- `ThunderID.AspNetCore` is a library for ASP.NET Core. It configures ASP.NET Core's own
  OpenID Connect and JWT bearer handlers for ThunderID (`AddThunderID`, `AddThunderIDApi`), adds
  a scope policy (`AddScopePolicy`), and adds a back-channel logout endpoint
  (`MapThunderIDBackchannelLogout`) with its own logout token validator and session store.
- Sign-in, the code exchange, ID token validation and PKCE are done by Microsoft's
  `Microsoft.AspNetCore.Authentication.OpenIdConnect` handler, not by ThunderID code.
- Logout token validation is done by this library (`LogoutTokenValidator`), using
  Microsoft.IdentityModel for the signature.
- There is no ThunderID Agnostic or Platform SDK for .NET for it to build on.

### Reading A: Framework Specific SDK for ASP.NET Core (recommended)

The closest existing shape is `@thunderid/express`, a Framework Specific SDK on
`@thunderid/node` (`spec.md:89-106`). ASP.NET Core is the server framework, and the library
plugs into its composition model (`IServiceCollection` and endpoint routing), which
"Framework integration" asks for (`spec.md:549`).

What fits:
- Back-channel logout is meant for "the Node Platform SDK and the Framework Specific SDKs built
  on it" (`spec.md:469-470`). It is exposed "as a request handler in its framework's idiom ...
  mounted at the path the application registered" (`spec.md:479-481`), which is what
  `app.MapThunderIDBackchannelLogout("/backchannel-logout")` does.
- The server-side sample shape (`spec.md:889-891`) matches `samples/Api`: a public route
  (`/public`), and a protected route that rejects no token and returns claims with one (`/me`).

What doesn't fit, and would be recorded as a deviation:
- **No ThunderID parent.** An SDK "MUST declare exactly one parent layer" (`spec.md:179`) and
  "MUST NOT skip its immediate parent layer" (`spec.md:189`). Here the protocol layer is
  Microsoft's, not ThunderID's. The spec names permitted deviations (`spec.md:146-153`), but not
  this one, so it is a new deviation the maintainers would have to accept.
- **Not in an ecosystem repository.** The topology lists four ThunderID repositories
  (`spec.md:158-166`); .NET has none.

### Reading B: integration package

An integration "adapts ThunderID into a third-party framework that already owns an auth
abstraction" (`spec.md:203`), and ASP.NET Core does own one (authentication schemes and
handlers). But:
- It "MUST delegate every protocol operation to a ThunderID SDK" (`spec.md:207`). There is no
  .NET ThunderID SDK to delegate to.
- It "MUST NOT implement OAuth 2.0 or OIDC logic of its own" (`spec.md:207-208`). The logout
  token validator is OIDC logic, so it would have to move into a .NET SDK that doesn't exist.
- Integrations "live alongside SDKs in the ecosystem repository, under `packages/`"
  (`spec.md:210-211`). That isn't possible without a .NET ecosystem repository.

So under Reading B, the project fails two MUSTs by design, and the fix is to write a .NET
Agnostic or Platform SDK first.

### What each reading switches on

| Spec area | Reading A (Framework Specific) | Reading B (integration) |
|---|---|---|
| Layering rules (`:177-200`) | Applies, with the parent deviation | Replaced by the integration rules (`:201-211`) |
| Configuration keys (`:291-360`) | Applies, with canonical names where the framework allows | Follows ASP.NET Core's conventions instead (`:209`) |
| Client surface (`:361-546`) | Applies, at least sign-in, sign-out, user and back-channel | Replaced by ASP.NET Core's handler interface |
| Error model (`:630-660`) | Applies (`ThunderIDError` codes) | Follows ASP.NET Core's conventions instead (`:209`) |
| Framework integration (`:547-562`) | Applies | Applies, through the target framework |
| UI components (`:563-608`) | Applies only if it "targets a UI platform" (Razor Pages, Blazor); an open question | Not applicable |
| Operational modes, embedded (`:243-290`) | Applies (redirect is in place; embedded is absent) | Not applicable |
| Security floor (`:661-763`) | Applies | Applies |
| Back-channel logout (`:462-546`, R9) | Applies | Applies |
| Testing, samples, docs, release (`:801-893`) | Applies | Applies |

### Recommendation

Judge it as **Reading A, a Framework Specific SDK for ASP.NET Core**, and record one deviation:
the protocol layer is ASP.NET Core's built-in OpenID Connect handler rather than a ThunderID
parent SDK. Reading B fails by construction, and so wouldn't produce a useful list of gaps.

Whether that deviation is acceptable is a maintainer decision. It's the first question to ask
them (see the questions at the end).

**Decision (2026-11-03, by the author):** Reading A. Validated as a Framework Specific SDK for ASP.NET Core, with the "no ThunderID parent; protocol delegated to ASP.NET Core's OpenID Connect handler" deviation recorded.

## 2. Summary

Validated on 2026-11-04 against spec v0.8 and the threat model as of ThunderID `upstream/main`
`fc8b081fd`, with the project at `d524d97`. Both are drafts, so a later spec version may move
rows.

**The library's own code meets the spec's security floor.** Back-channel logout is the part
ThunderID.AspNetCore implements itself, and it meets every rule bar one: the clock tolerance for
`sub`-only logout (G2). Sign-in is delegated to ASP.NET Core and mostly meets the floor through
it. The exceptions are the discovery issuer (G1), revocation (G3) and refresh (G4).

**Most gaps are maturity rather than security.** 19 of the 40 Gap rows are repository, release,
testing and documentation work (section 3.3). 12 are the error model, three operations and two
configuration items (section 3.2).

The figures below include the decisions of 2026-11-04 (section 5). Before them, the matrix was
48 Meets, 29 Partial, 45 Gap and 35 N/A.

### Specification matrix ([spec-matrix.md](spec-matrix.md))

| Section | Rows | Meets | Partial | Gap | N/A |
|---|---|---|---|---|---|
| 1. Security floor and back-channel logout | 48 | 23 | 6 | 7 | 12 |
| 2. Client surface, configuration, framework integration, errors | 51 | 19 | 8 | 13 | 11 |
| 3. Layering, packaging, modes, CI and release, testing, docs and samples | 58 | 16 | 8 | 20 | 14 |
| **Total** | **157** | **58** | **22** | **40** | **37** |

Meets includes "Meets (delegated)", rules ASP.NET Core implements, each shown by a probe, and
"Meets (accepted deviation)", rules met differently as accepted by a decision. All 67 acceptance
criteria in the spec (64 numbered, plus AC2.4b, AC4.1b and AC4.6b) have a row.

### Threat model ([threat-model-review.md](threat-model-review.md))

| Rows | Mitigated | Partial | Gap | N/A | Residual |
|---|---|---|---|---|---|
| 30 | 14 | 4 | 1 | 8 | 3 |

Interactions 01, 02, 04, 05 and 06 apply. 03 and 07 don't yet, because there's no embedded mode.
The one Gap is sign-out without revocation (G3).

No finding is exploitable without a precondition the threat model already treats as out of scope
or residual.

## 3. Gaps, by priority

Each gap groups the Gap rows that share a cause; the row IDs link to their section of the
matrix. The 40 Gap rows are each counted once. Effort is a rough guess for one developer: **S** is
up to a day, **M** a few days, **L** a week or more.

### 3.1 Security (9 Gap rows)

| # | Gap | Rows | Effort | What closing it takes |
|---|---|---|---|---|
| G1 | The discovery document's `issuer` isn't checked against the configured authority, so the ID token and logout token issuer checks compare against whatever discovery says | [S7](spec-matrix.md#security-requirements-specmd661-763), [AC4.8](spec-matrix.md#r4-acceptance-criteria-specmd991-1039) | S to M | Reject a discovery document whose `issuer` differs from `Authority` before any of its endpoints is used, for the OIDC handler and the logout validator alike. Set `ValidIssuer` explicitly. A probe test (P5) already exists to turn into a unit test |
| G2 | A `sub`-only logout doesn't end sessions that began within the clock tolerance after `iat` (a rule added in v0.7) | [BCL-3b](spec-matrix.md#back-channel-logout-handler-requirements-specmd482-516), [AC9.2](spec-matrix.md#r9-acceptance-criteria-specmd1135-1169) | S | Give the store the validator's clock tolerance and compare `authTime < iat + tolerance` (`InMemoryLogoutSessionStore.cs:72`) |
| G3 | Sign-out doesn't revoke at the server before clearing local state, and there's no SDK sign-out operation to report a failed revocation | [AC4.5](spec-matrix.md#r4-acceptance-criteria-specmd991-1039), [`signOut`](spec-matrix.md#canonical-name-mapping), [AC2.4b](spec-matrix.md#r2-and-r3-acceptance-criteria-specmd953-990), and the sign-out half of [S12](spec-matrix.md#security-requirements-specmd661-763) | M | A sign-out operation that calls RFC 7009 revocation, then clears the cookie and redirects to end-session, and tells the caller when revocation failed. Smaller if ThunderID issues no refresh token to this client, which wasn't measured. `@thunderid/node`'s `signOut` doesn't revoke either; this stays a gap because revocation is in the security floor |
| G4 | No token refresh: no refresh ahead of expiry, rotation or deduplication | [AC4.7](spec-matrix.md#r4-acceptance-criteria-specmd991-1039), the refresh half of [S12](spec-matrix.md#security-requirements-specmd661-763) | M | Refresh in the cookie's `OnValidatePrincipal` with one in-flight request per session, and write the new token set in one cookie update. Applies: `@thunderid/node` has `refreshAccessToken` (Q3) |

**Partial rows to close alongside (security):**
- **Tests that pin the delegated checks:** [T2, AC7.2](spec-matrix.md#testing-specmd821-866-r7). Turning off ID token issuer, audience or nonce validation leaves all 87 tests passing. Effort S.
- **Configuration error tests:** [T1, AC7.1](spec-matrix.md#testing-specmd821-866-r7). Effort S.
- **Logging of the authorization code by ASP.NET Core's request log:** [S11](spec-matrix.md#security-requirements-specmd661-763), [AC3.3](spec-matrix.md#r2-and-r3-acceptance-criteria-specmd953-990). Document it, or filter the callback URL. Effort S.
- **Token storage:** [S1, S2](spec-matrix.md#security-requirements-specmd661-763), [AC4.3](spec-matrix.md#r4-acceptance-criteria-specmd991-1039). Document how to protect and share the Data Protection key ring, and how to supply an `ITicketStore`. Effort S.
- **Caller authorize parameters:** [AC4.1b](spec-matrix.md#r4-acceptance-criteria-specmd991-1039). A duplicate `code_challenge`, `client_id` or `redirect_uri` throws rather than being ignored. Effort S.
- **Encrypted logout tokens:** [BCL-8](spec-matrix.md#back-channel-logout-handler-requirements-specmd482-516). Name the client setting (ID token encryption) in the README. Effort S.
- **Framework options:** ASP.NET Core's `UsePkce` and `TokenValidationParameters` stay writable by the app (threat review 01.4; matrix [AC4.11](spec-matrix.md#r4-acceptance-criteria-specmd991-1039), N/A). An options validator could refuse a weakened setting. Effort S.
The PKCE verifier in the encrypted `state` ([S4](spec-matrix.md#security-requirements-specmd661-763)) is no longer on this list: it's ASP.NET Core's design, accepted with decision Q1.

### 3.2 Client surface, configuration and errors (12 Gap rows)

| # | Gap | Rows | Effort | What closing it takes |
|---|---|---|---|---|
| G5 | No ThunderID error type: no `code` or `origin`, no API error status, and no documented codes | [E1, E2, E9](spec-matrix.md#error-model-specmd630-660), [AC3.1, AC3.2, AC3.4](spec-matrix.md#r2-and-r3-acceptance-criteria-specmd953-990) | M | A `ThunderIDException` hierarchy, wrapping the framework's failures at the SDK's boundary, and a README table of the codes, including the 13 back-channel rejection reasons |
| G6 | Operations the server SDKs have that this one lacks: `reInitialize`, `exchangeToken`, `decodeJwtToken` | [mapping table](spec-matrix.md#canonical-name-mapping) (3 rows) | M | `reInitialize` through ASP.NET Core's options reload, `exchangeToken` (RFC 8693), and `decodeJwtToken` documented as unsafe for authorization. `signUp` and the profile operations became N/A with Q3 |
| G7 | Closed by Q3, no Gap rows left | [C2](spec-matrix.md#configuration-specmd291-360) (now Partial), [AC1.3](spec-matrix.md#r1-r6-r7-and-r8-acceptance-criteria) (now Meets) | S | `Authority` is accepted as the framework's name for `baseUrl`. What remains is the `Scopes` default, `openid profile email` against the spec's `openid`: document it, or change it |
| G8 | The spec keys the SDK doesn't support aren't documented as unsupported | [C5](spec-matrix.md#configuration-specmd291-360), [AC1.4](spec-matrix.md#r1-r6-r7-and-r8-acceptance-criteria) | S | A README table of the unsupported keys, and which of them ASP.NET Core's own options cover |
| G9 | Configuration is validated on the first sign-in request, not at startup | [F2](spec-matrix.md#framework-integration-specmd547-562) | S | `IValidateOptions` with `ValidateOnStart()`. Also closes [C6](spec-matrix.md#configuration-specmd291-360) (Partial) |

**Partial rows to close alongside (surface):**
- **`getAccessToken`:** the [mapping row](spec-matrix.md#canonical-name-mapping). The framework verb is accepted (Q3), but it doesn't refresh a token near expiry. Closes with G4.
- **`handleBackchannelLogout` result:** the [mapping row](spec-matrix.md#canonical-name-mapping). It doesn't return `sid`, `sub` and the count of ended sessions to the app.
- **Operation before initialization:** [C7](spec-matrix.md#configuration-specmd291-360), [AC2.3](spec-matrix.md#r2-and-r3-acceptance-criteria-specmd953-990). Mapping the logout endpoint without `AddThunderID` throws .NET's generic error.
- **Names and verbs:** [AC2.1](spec-matrix.md#r2-and-r3-acceptance-criteria-specmd953-990) stays Partial until the mapping table's Gap and Partial rows close.

### 3.3 Maturity: repository, release, testing, docs (19 Gap rows)

| # | Gap | Rows | Effort | What closing it takes |
|---|---|---|---|---|
| G10 | No CI: no pull request pipeline, no parity check, samples not built in CI | [R1, R2](spec-matrix.md#repository-ci-and-release-specmd801-819-r5), [T8](spec-matrix.md#testing-specmd821-866-r7), [D6](spec-matrix.md#documentation-and-samples-specmd868-892-r6), [AC7.7](spec-matrix.md#r1-r6-r7-and-r8-acceptance-criteria) | S | A GitHub Actions workflow running build, tests and sample builds, and the reusable `sdk-parity-check.yml` from the ThunderID repository |
| G11 | No release or package: no tag-triggered publish, no NuGet metadata, no `LICENSE` | [R3](spec-matrix.md#repository-ci-and-release-specmd801-819-r5), [K2](spec-matrix.md#packaging-specmd213-242-817-819) | S | A release workflow, package metadata and a license. Retarget to `net10.0` first: .NET 9 leaves support on 2026-11-10 ([K1](spec-matrix.md#packaging-specmd213-242-817-819)) |
| G12 | No end-to-end suite and no reproducible server; setup is manual console steps | [R4](spec-matrix.md#repository-ci-and-release-specmd801-819-r5), [T6, T7](spec-matrix.md#testing-specmd821-866-r7), [AC7.5, AC7.6, AC7.8](spec-matrix.md#r1-r6-r7-and-r8-acceptance-criteria) | L | Provision ThunderID from the repository (a container and its configuration as files), then drive sign-in, sign-out and sign-up against the samples, nightly |
| G13 | No spec version in the README, and no page on the ThunderID docs site | [L7](spec-matrix.md#layering-specmd177-200), [D1](spec-matrix.md#documentation-and-samples-specmd868-892-r6), [AC1.1, AC6.1](spec-matrix.md#r1-r6-r7-and-r8-acceptance-criteria) | M | One README line (S). A quickstart and reference under `docs/content` in the ThunderID repository, by pull request (M) |
| G14 | The README doesn't say which layers the package covers, or record the accepted deviation | [L2](spec-matrix.md#layering-specmd177-200) | S | A README paragraph. L1 closed with decision Q1 |
| G15 | No embedded mode | [O2](spec-matrix.md#operational-modes-specmd243-290) | L | ThunderID's Flow Execution API from the server. Applies: `@thunderid/express` ships an embedded sign-in flow (`handleFlow`) (Q3) |

**Partial rows to close alongside (maturity):**
- **Reference coverage:** [D2, AC6.2](spec-matrix.md#documentation-and-samples-specmd868-892-r6). Document each options key, `ThunderIDApiOptions` and the logout types.
- **Sample defaults:** [D5, AC6.5](spec-matrix.md#documentation-and-samples-specmd868-892-r6). `appsettings.json` ships a test `ClientId` and `localhost` URLs, and `CallApi.cshtml:8` prints a fixed URL.

### 3.4 Suggested order

The release plan is [phases.md](../../phases.md). In short:
1. **First:** G1, G2, the pinned tests (T1, T2), G8, G9, G10, G11 (with `net10.0`), G14 and the
   README spec version from G13. All are S, and they close most of what a reviewer checks first.
2. **Next:** G12 and G3, then G5, then G4 and G6.
3. **Last:** the docs site page (G13), then G15.

## 4. Not applicable (37 rows)

| Reason | Rows |
|---|---|
| No embedded mode (redirect only) | S6, S10, AC4.6b, AC4.13, AC4.15, AC2.2, O3 |
| No outbound request proxy or isolated storage, so no `allowedExternalUrls` | S3, AC4.9, AC4.10 |
| Not a browser, mobile or bridged SDK | AC4.14, AC9.8, C9, K4, K5, AC8.2, AC8.3 to AC8.6 |
| No bundled UI | C10, T5, AC2.5 |
| A server-side confidential client | C4, `isLoading`, `handleRedirectCallback` (the framework handles the callback itself) |
| Not implemented by ThunderID's server SDKs (decision Q3) | `signUp`, the profile operations (`getUserProfile`, `updateUserProfile`, `getUserSchema`) |
| No such key or concept | AC4.11 (`tokenValidation`), AC4.12 (`instanceId`), E6 (error codes, see G5) |
| Optional and not shipped | Management operations, O4 (`client_credentials`) |
| Follows from Reading A | L3, I1 |
| Nothing released yet | L6 |
| Owned elsewhere, or nothing of the kind to test | R5 (the parity check's own behaviour), T4 and AC7.4 (no reimplemented logic), AC1.5 (no new capability) |

Three of these carry a caveat:
- **AC4.11:** ASP.NET Core's own validation settings are still writable (section 3.1, framework options).
- **AC4.12:** the missing key isn't documented (G8).
- **AC2.5:** whether Razor Pages or Blazor make this a UI platform is open (section 1).

## 5. Questions and decisions

Q1 to Q4 were answered on 2026-11-04. The rows each answer changed are listed in the matrix,
under "Decisions".

| # | Question | Answer | Effect |
|---|---|---|---|
| Q1 | Is "no ThunderID parent; the protocol layer is ASP.NET Core's OpenID Connect handler" an acceptable deviation for a .NET Framework Specific SDK? | Yes, the deviation is acceptable | L1 and S4 are Meets (accepted deviation). L2 stays Gap until the README records it (G14) |
| Q2 | Should a replayed `jti` get `400`? | Yes | No change: [BCL-2](spec-matrix.md#back-channel-logout-handler-requirements-specmd482-516) keeps `Replayed_token_is_400`. `@thunderid/node` also rejects a token already handled |
| Q3 | Which client surface applies to a server-side framework SDK, and are the framework's idioms acceptable? | Match a server-side framework SDK. The framework's idioms are acceptable. The spec wasn't written for server SDKs, so there's room to adapt | The surface is what `@thunderid/node` and `@thunderid/express` ship: `signUp` and the profile operations are N/A, while `reInitialize`, `exchangeToken`, `decodeJwtToken`, refresh and embedded mode apply. Framework verbs and `Authority` are accepted |
| Q4 | Do `AddThunderIDApi` and `AddScopePolicy` need the cross-SDK parity process? | Both are acceptable. Follow cross-SDK parity when possible | "Operations beyond the set" is Meets |

Still open:

| # | Question | Why it matters |
|---|---|---|
| Q1b | Should the threat model gain entries for ASP.NET Core Data Protection and the self-contained cookie session (threat review, section 3)? | The model doesn't describe this SDK's session storage |
| Q5 | May a community package use the ID `ThunderID.AspNetCore` on NuGet? If ThunderID reserves the `ThunderID.*` prefix later, should it be co-owned or transferred? | Needed before the first NuGet release (`phases.md`, Phase 1) |

## 6. Method and limits

- Each rule was judged from code, an existing test, or a throwaway probe run in a scratch copy
  (P1 to P14), never from the README alone. Mutation runs (M1 to M9) checked which security
  behaviour the tests pin. The commands and their output are in `RUNBOOK.md`, "SDK validation".
- No library, sample or test code in this repository was changed. The 87 tests pass.
- The skill's sections 2, 6 and 7 assume the four official SDK repositories, so only sections 1,
  3 to 5 and 8 were applied.
- Coverage is by spec section, with every acceptance criterion checked by script. The 143 MUST
  lines weren't each mapped to their own row.
- Not measured: whether ThunderID issues this client a refresh token (G3, G4).
