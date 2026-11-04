# Release phases: ThunderID.AspNetCore

How ThunderID.AspNetCore gets from today's state to a ThunderID SDK that meets the
specification, one shipped version at a time. The gaps come from the
[gap report](docs/validation/gap-report.md); IDs such as G1 and S7 refer to it and to the
[spec matrix](docs/validation/spec-matrix.md).

## How the phases work

- **Every phase ends with a release on NuGet** (`ThunderID.AspNetCore`), tagged in this
  repository, with release notes listing the matrix rows it closes.
- **After each release there's a wait** of at least two weeks before the next phase starts.
  During it:
  - only patch releases ship (`0.1.1`, `0.1.2`), for bugs found in the release;
  - feedback is collected from issues, and from the ThunderID maintainers;
  - any maintainer answer the next phase depends on is awaited.
- **Each phase starts by re-checking the specification.** It's a Draft and changes often (v0.5 to
  v0.8 in ten days). Fetch it, compare the blob hashes with the ones the matrix is pinned to, and
  re-judge any changed rows before planning the phase's work.
- **Each phase ends by updating the matrix.** Change the rows the phase closed, with evidence,
  and recount the totals.
- **Versioning:** `0.x` until Phase 5. Under semantic versioning a `0.x` minor release may break
  the API, so breaking changes are grouped into Phase 3 to break users once. From `1.0.0`, the
  spec's rule applies: a breaking change needs a major version (`spec.md:197-198`).

## Overview

| Phase | Version | Theme | Gaps closed | Breaking | Still needs |
|---|---|---|---|---|---|
| 1 | 0.1.0 | First NuGet release, and quick security fixes | G1, G2, G8, G9, G10, G11, G14, the README part of G13, the `Scopes` default left from G7 | No | Yes to the NuGet package ID (Q5) |
| 2 | 0.2.0 | End-to-end suite, and sign-out with revocation | G3, G12 | No | Nothing |
| 3 | 0.3.0 | The error model | G5 | **Yes** | Nothing |
| 4 | 0.4.0 | Token operations | G4, G6 | No | Nothing |
| 5 | 1.0.0 | Documentation site, full reference, stability | The docs site part of G13 | No | Docs review by the maintainers |
| 6 | 1.1.0 | Embedded mode | G15 | No | Q1b, for the threat model entries |

Questions Q1 to Q4 were answered on 2026-11-04 (gap report, section 5). The surface to build is
what ThunderID's own server SDKs ship, `@thunderid/node` and `@thunderid/express`, and the
framework's idioms are accepted in place of the spec's names. Phase 1 can start now; only its
publish waits for Q5.

## Phase 1: First NuGet release (0.1.0)

**Goal:** anyone can install the package from NuGet, it builds and tests on every pull request,
and the small security gaps are closed.

**Before writing code:**
- Ask the maintainers the two open questions (gap report, section 5):
  - **Q5.** May a community package use the ID `ThunderID.AspNetCore` on NuGet? If ThunderID
    reserves the `ThunderID.*` prefix later, should the package be co-owned or transferred?
    The ID is free today: `api.nuget.org` returns 404 for it, and a NuGet search for
    "thunderid" finds no packages (checked 2026-11-04).
  - **Q1b.** Should the threat model gain entries for ASP.NET Core Data Protection and the
    cookie session? Needed by Phase 6.
- Don't publish until Q5 is answered. Everything else in this phase can proceed.

**Platform and packaging (G11, K1, K2):**
- Retarget from `net9.0` to `net10.0`, and move the ASP.NET Core package references to `10.0.x`.
  .NET 9 leaves support on 2026-11-10, and .NET 10 is supported to 2028-11-10.
- Add a `LICENSE` file. Apache 2.0 matches ThunderID's own license.
- Add NuGet metadata to the csproj:
  - `PackageId`, `Version`, `Authors`, `Description`, `PackageTags`;
  - `PackageLicenseExpression` (`Apache-2.0`), `RepositoryUrl`;
  - `PackageReadmeFile`, so the README shows on nuget.org;
  - Source Link and a symbols package (`.snupkg`).
- Mark the samples and tests `IsPackable=false`, so only the library is packed.

**Pipelines (G10, G11: R1, R2, R3, T8, D6, AC7.7):**
- A pull request workflow that builds the solution, runs the tests, builds both samples, and runs
  `dotnet list package --vulnerable --include-transitive`.
- Dependabot for NuGet packages and GitHub Actions.
- The parity check (R2): find out whether ThunderID's reusable `sdk-parity-check.yml` can be
  called from a repository outside the `thunder-id` organization. If it can't, ask the
  maintainers what a community SDK should run instead.
- A release workflow, triggered by a `v*` tag:
  - pack, then push to nuget.org with NuGet trusted publishing (GitHub's OIDC token exchanged by
    `NuGet/login` for a short-lived key, so no API key is stored);
  - create a GitHub release with notes.

**Security fixes:**
- **G1:** reject a discovery document whose `issuer` doesn't equal `Authority`, before any of its
  endpoints is used. Apply it to both the OIDC handler and the logout validator, and set
  `ValidIssuer` explicitly. Closes S7 and AC4.8.
- **G2:** give the session store the validator's clock tolerance, so a `sub`-only logout also
  ends sessions that began within it. Closes BCL-3b and AC9.2.
- Refuse a weakened framework setting at startup, such as `UsePkce = false` or ID token issuer
  validation turned off (threat review 01.4).

**Tests (T1, T2):**
- Tests that fail if ID token issuer, audience or nonce validation is turned off. Mutations M2,
  M3 and M4 must now fail a test.
- Tests of every configuration error.

**Configuration (G9):**
- Validate the options at startup (`IValidateOptions` with `ValidateOnStart()`), so a
  misconfigured app fails to start instead of failing on its first sign-in. Closes F2, and moves
  C6 to Meets.

**README:**
- The spec version implemented (L7, AC1.1).
- Which layers the package covers, and the accepted deviation: no ThunderID parent, with the
  protocol layer delegated to ASP.NET Core's OpenID Connect handler (G14: L2).
- The `Scopes` default, `openid profile email` against the spec's `openid`: document why, or
  change it (C2).
- The spec keys it doesn't support (G8: C5, AC1.4).
- Production notes:
  - protecting the Data Protection key ring, and sharing it across instances;
  - supplying an `ITicketStore`;
  - request logging writes the authorization code at Information level;
  - back-channel logout also ends copied cookies;
  - logout tokens aren't supported for clients configured for ID token encryption (S1, S2, S11,
    AC3.3, BCL-8).
- No real `ClientId` or URLs in the shipped `appsettings.json` (D5, AC6.5).

**Exit criteria:**
- `0.1.0` is on nuget.org, published by the release workflow.
- It works when installed from nuget.org: a fresh `dotnet new webapp` project with
  `dotnet add package ThunderID.AspNetCore` signs in against a local ThunderID.
- The matrix is updated.

**Then wait:** gather feedback and ship patches if needed.

## Phase 2: End-to-end suite and sign-out (0.2.0)

**Goal:** the behaviour that only a real server can show is tested automatically, and sign-out
ends the session at ThunderID as well as locally.

**End-to-end (G12: T6, T7, R4, AC7.5, AC7.6, AC7.8):**
- Provision ThunderID from files in this repository: a container plus the application and user
  setup as configuration, with no console steps.
- Drive the samples with a browser test: sign-in, sign-out, sign-up and back-channel logout,
  starting from a clean state each time. Select page elements by flow element identifiers.
- Run it nightly in CI, and on demand.
- This comes first so the larger changes in Phases 3 and 4 have a safety net.

**Sign-out (G3: AC4.5, S12, `signOut`, AC2.4b):**
- First, measure whether ThunderID issues this client a refresh token. That decides how much
  revocation is needed.
- A sign-out operation that revokes at ThunderID (RFC 7009), then clears the cookie, then
  redirects to end-session. If revocation fails, local state is still cleared and the caller is
  told. In the framework's idiom, for example a `SignOutAsync` extension, as Q3 allows.
- `@thunderid/node` doesn't revoke in `signOut`. It has a separate `revokeAccessToken`. Revoking
  inside sign-out is still the target, because it's in the spec's security floor.

**Back-channel logout:**
- Keep `400` for a replayed `jti` (Q2).
- Return the ended session's `sid` and `sub` to the app (the `handleBackchannelLogout` row), as
  `@thunderid/node`'s `handleBackchannelLogout` does.

**Exit criteria:**
- The nightly end-to-end run passes on `main`.
- `0.2.0` is on NuGet, and the matrix is updated.

**Then wait.**

## Phase 3: The error model (0.3.0, breaking)

**Goal:** the SDK's errors follow the specification's error model. All breaking changes land in
this one release.

**Errors (G5: E1, E2, E9, AC3.1, AC3.2, AC3.4, C7, AC2.3):**
- A `ThunderIDException` hierarchy with `Code` and `Origin`, and an API error with the status
  code and text. Framework failures are wrapped at the SDK's boundary.
- A distinct, documented error for an operation used before `AddThunderID` (C7).
- A table of every code in the reference, including the 13 back-channel rejection reasons.

**Caller authorize parameters (AC4.1b):**
- A parameter the SDK owns, such as `code_challenge` or `client_id`, is ignored when the caller
  supplies it, rather than throwing.

**Parity (Q4):**
- `AddThunderIDApi` and `AddScopePolicy` stay. Follow cross-SDK parity for them where possible,
  for example by checking how the other server SDKs protect an API route (Express's `protect`).

`Authority` keeps its name and the framework's verbs stay the surface (Q3), so there's no
renaming in this phase.

**Exit criteria:**
- `0.3.0` is on NuGet, with a migration section in the release notes covering every breaking
  change.
- The matrix is updated.

**Then wait.** This one may need longer, so users can migrate.

## Phase 4: Token operations (0.4.0)

**Goal:** the operations ThunderID's server SDKs have and this one lacks. Additive only.

**Refresh (G4: AC4.7, S12):**
- Refresh ahead of expiry, with one in-flight refresh per session, refresh tokens rotated, and
  the new token set written in a single cookie update. `@thunderid/node` has
  `refreshAccessToken`, so this applies.
- `getAccessToken` returns a refreshed token when the current one is near expiry.
- Refresh needs a refresh token. Phase 2 measures whether ThunderID issues one, and with which
  scope (`offline_access`).

**Operations (G6):**
- `exchangeToken` (RFC 8693).
- `decodeJwtToken`, documented as unsafe for authorization decisions.
- `reInitialize`, through ASP.NET Core's options reload, rejecting changes to the immutable keys.
- Each comes with unit tests and an end-to-end case.

`signUp` and the profile operations aren't built: ThunderID's server SDKs don't implement them
(Q3).

**Exit criteria:**
- `0.4.0` is on NuGet, and the matrix is updated.

**Then wait.**

## Phase 5: Documentation and 1.0 (1.0.0)

**Goal:** a stable release that the maintainers can review for an official listing.

**Documentation (G13: D1, D2, AC6.1, AC6.2):**
- A pull request to the ThunderID repository, adding a quickstart and full reference under
  `docs/content`. This waits until now so it documents the stable API.
- The reference covers every operation, options key and public type.

**Validation:**
- Re-run the full validation against the current spec and threat model: the matrix, the threat
  review and the gap report.
- Every remaining Gap row is either closed, or agreed with the maintainers as not applicable.

**Exit criteria:**
- `1.0.0` is on NuGet.
- The docs pull request is merged.
- The maintainers have the updated gap report, with a request for an official listing.

From here, the spec's versioning rule applies (`spec.md:197-198`).

## Phase 6: Embedded mode (1.1.0)

**Goal:** sign-in through ThunderID's Flow Execution API, rendered by the app, with no redirect
to ThunderID's page (G15: O2).

It applies: `@thunderid/express` ships an embedded sign-in flow (`handleFlow`), and Q3 says to
match the server SDKs. Use Express's flow as the reference for behaviour.

**Threat model:** it brings interactions 03 and 07 into scope, so the threat review is redone
for them before any code. Q1b's answer decides whether the model also gains entries for this
SDK's cookie session. Credentials are submitted and discarded, never logged, and there's no
loopback HTTP.

**Sample:** a sample app that signs in through embedded mode.

**Exit criteria:**
- `1.1.0` is on NuGet, a minor version because the change is additive.
- The threat review and the matrix are updated.

## Out of scope for these phases

- Writing a separate ThunderID .NET Platform SDK for this library to sit on. The deviation is
  accepted (Q1).
- `signUp`, `getUserProfile`, `updateUserProfile` and `getUserSchema`, which ThunderID's server
  SDKs don't implement (Q3).
- Multi-targeting .NET 8. It leaves support on the same day as .NET 9.
- UI components (Razor or Blazor). The spec asks for them only on UI platforms, and whether this
  is one is still an open question (AC2.5).
