# Runbook

Exact commands and observed results, written as each step runs.

## Step 0: Preflight (2026-10-16, read-only)

| Check | Command | Result |
|---|---|---|
| PowerShell | `pwsh --version` | 7.6.7 (7+ required) ✅ |
| Go | `go version` | 1.26.5. Repo `go.mod` needs **1.27.2**. `GOTOOLCHAIN=auto`, so Go downloads 1.27.2 on first build ⚠️ |
| Node | `node --version` | v24.20.0 |
| pnpm | `pnpm --version` | 11.21.0, matches `packageManager` in `package.json` ✅ |
| .NET | `dotnet --version` | 9.0.318 ✅ |
| Ports 8090, 7101, 7102, 5101 | `netstat -ano` | All free ✅ |
| Worktree target `..\thunderid-main` | `ls` | Does not exist ✅ |
| Disk C: | `df -h /c` | 22 GB free (91% used) ⚠️ |
| ASP.NET dev cert | `dotnet dev-certs https --check --trust` | Cert exists (valid to 2027-08-06) but is **not trusted** ⚠️ |
| Existing worktrees | `git worktree list` | Only the main clone on `add-mobile-number-library-attribute` |

## Step 1: Fresh build from `upstream/main` (2026-10-17)

```bash
# in thunderid/
git fetch upstream                     # upstream/main 2927a1d00 -> 59ffbf47a
git worktree add ../thunderid-main upstream/main --detach
git -C ../thunderid-main merge-base --is-ancestor 95db6efb5 HEAD   # true
# in thunderid-main/
pnpm install --frozen-lockfile && pwsh ./build.ps1 build   # log: ../thunderid-main-build.log
```

- Worktree pinned at **`59ffbf47a`** (2026-10-16, "Merge pull request #5875"). It's newer than the `2927a1d00` noted in the plan, because the fetch moved `upstream/main`.
- Parent clone unchanged: still on `add-mobile-number-library-attribute`, with only `?? frontend/apps/console/src/features/` untracked.
- Dev cert still **not trusted** when checked at the start of this step. You then ran `dotnet dev-certs https --trust`. Re-check: "A trusted certificate was found: 5FAC86E5… CN=localhost" ✅
- Backend binary built at 00:12 (`target/out/.build/thunderid.exe`, about 60 MB). Go auto-downloaded toolchain 1.27.2.
- **Build ended with `EXIT=1`, in the sample-app packaging stage, after the server was packaged.**
  - Error: `tar (child): Cannot connect to C: resolve failed`, then `Rename-Item ... target\dist\package ... does not exist` at `build.ps1:787`.
  - Cause: **my invocation, not ThunderID.** I started `pwsh` from Git Bash, so `tar` resolved to Git's GNU `/usr/bin/tar`, which reads `C:\...` as `host:path`. Windows' own `tar.exe` (bsdtar) handles it. Run from a native PowerShell terminal, this stage would pass.
  - Impact: none for this project. Only `thunderid-vanilla-sample` (a JS sample) failed to unpack.
- Server artifact: `thunderid-main/target/dist/thunderid-1.0.0-win-x64.zip` (38.8 MB, 284 files, built 00:20). It contains `thunderid.exe` (00:12), `start.ps1`, `deployment.yaml`, `apps/console/index.html` and `apps/gate/index.html`. `version.txt` reads **`v1.0.0`**, the hard-coded product version, even though this is `main` at `59ffbf47a`.

## Step 2: Configure and start the server (2026-10-17)

Server dir: `C:\Users\pasin\Documents\Academics\Thunder\thunderid-main\target\dist\thunderid-1.0.0-win-x64\`
Server log: `...\target\dist\server.log`

1. Unzipped the zip into that folder.
2. The shipped `deployment.yaml` has no `oauth:` block, so I appended:
   ```yaml
   oauth:
     logout:
       backchannel:
         # Local development only: lets ThunderID deliver logout tokens to https://localhost.
         reject_private_addresses: false
   ```
3. **First attempt failed.** `ADMIN_PASSWORD=<pw> .\start.ps1 --bootstrap-and-serve` gave `[ERROR] Bootstrap failed`, because `config\certs\crypto.key` doesn't exist ("one or more file path substitutions failed"). On a fresh unzip `config/certs/` is empty, and only `setup.ps1` generates keys. The docs tip says this flag "replaces the separate setup + start steps", but it doesn't on a first run. Capture: `docs/blog/captures/02-bootstrap-and-serve-fails.txt`.
4. Ran `ADMIN_PASSWORD=<pw> .\setup.ps1`, giving `EXIT=0` and "imported=45". It generated `crypto.key`, `server.cert/key`, `signing.cert/key` (RSA) and `ecdsa-signing.cert/key`. It also wrote the Direct API secret to `config\secrets\direct_auth_secret`.
5. Ran `.\start.ps1`. The server answered discovery after 2 s, at PID 26792.
   - ⚠️ `start.ps1` printed `Killing PID 23832` and `Killing PID 10144 that is listening on port 8090`. Port 8090 was free at preflight. These were probably leftovers from the failed `--bootstrap-and-serve` run or setup's temporary server, but the processes are gone, so I can't confirm that.
6. Admin user: `admin`. The password was given in chat and **is not stored in any file**.

**Discovery (verified with `curl -sk https://localhost:8090/.well-known/openid-configuration`):**

| Field | Value |
|---|---|
| issuer | `https://localhost:8090` (no trailing slash) |
| authorization_endpoint | `https://localhost:8090/oauth2/authorize` |
| token_endpoint | `https://localhost:8090/oauth2/token` |
| jwks_uri | `https://localhost:8090/oauth2/jwks` |
| end_session_endpoint | `https://localhost:8090/oauth2/logout` |
| backchannel_logout_supported | `true` ✅ |
| backchannel_logout_session_supported | `true` ✅ |
| id_token_signing_alg_values_supported | `RS256`, `ES256` |
| code_challenge_methods_supported | `S256` |

- Discovery is at the **root** (`/.well-known/openid-configuration`). `/oauth2/.well-known/...` returns 404.
- JWKS has two signing keys: RSA/RS256 and EC/ES256. **No ML-DSA**, so .NET can verify both. This closes the "ID token alg is ML-DSA" risk.
- **Server stopped afterwards.** The background task running `start.ps1` exited with code 4 some time after the report, and discovery returned nothing on 2026-10-28. I missed that notification when it arrived.
- **Restart (2026-10-28):** `.\start.ps1 --bootstrap-and-serve --admin-password <new pw>` gave "Bootstrap completed", PID 14888, discovery ready after 3 s, `backchannel_logout_supported: true`, console 200. Once keys exist, `--bootstrap-and-serve` works, which confirms the step-2 failure only happens on a fresh install. Bootstrap is meant to update the admin user's password. Login to be confirmed by you.
- Note: the server runs as a child of a background tool task. If that task ends, the server stops. For a long-lived server, run `start.ps1` in your own terminal.
- `reject_private_addresses: false` can't be seen in discovery. It gets tested in step 3, when the console accepts or rejects the localhost back-channel URI.
- Login with the new admin password confirmed by you (2026-10-28).

## Step 3: Test user and WebApp registration (2026-10-28)

Done by you in the console. Checked by me by reading the server's SQLite databases read-only (`sqlite3 -readonly`), not through the console.

| What | Read back | Result |
|---|---|---|
| User `alice` | `entitydb.ENTITY` | id `01a182da-b10c-764b-bcc3-4bf3f93282de`, `user/person`, ACTIVE, `email=alice@example.com`, `username=alice` ✅ |
| App `aspnet-webapp` | `entitydb.ENTITY_IDENTIFIER` | entity `01a182dc-6a47-77ba-befd-1a84cd860549`, **client ID `WItzJCtJA0Ij77xkgHIjKg`** ✅ |
| Client secret | `POST /oauth2/token` with `client_credentials` | 200, so the secret you sent is correct ✅ (stored in no file) |
| App type | `INBOUND_CLIENT.PROPERTIES` | `{"template":"custom","type":"m2m"}` ❌ expected a web app |
| OAuth config | `OAUTH_INBOUND_PROFILE.OAUTH_CONFIG` | `grantTypes: ["client_credentials"]`, `redirectUris: []`, `responseTypes: []`, `pkceRequired: false`, **no `backchannelLogoutUri`** ❌ |
| Browser sign-in | `GET /oauth2/authorize?...redirect_uri=https://localhost:7101/signin-oidc` | 302 to `/gate/error?errorCode=invalid_request&errorMessage=Invalid+redirect+URI` ❌ |

- So the app was saved as a machine-to-machine client. None of the web values (auth code, PKCE, redirect URIs, back-channel URI) were stored. It can't be used for step 4 as it is.
- A4 (console has a back-channel field) is **still unresolved**: nothing reached the database, so I can't tell if the field exists or was rejected.
- Screenshot `02-console-home.png` was saved to `thunderid/docs/blog/img/` (the parent repo) and moved by me into `thunderid-aspnetcore/docs/blog/img/`. No `03-*.png` files exist.

**After your fix (app edited in place at 14:21 UTC, same entity and client ID `WItzJCtJA0Ij77xkgHIjKg`):**

| Field | Read back from `OAUTH_CONFIG` |
|---|---|
| redirectUris | `https://localhost:7101/signin-oidc` ✅ |
| postLogoutRedirectUris | `https://localhost:7101/signout-callback-oidc` ✅ |
| backchannelLogoutUri | `https://localhost:7101/backchannel-logout` ✅ (localhost accepted, because `reject_private_addresses: false`) |
| grantTypes | `client_credentials`, `authorization_code` (client_credentials left over from the m2m start; harmless) |
| responseTypes / pkceRequired / publicClient | `code` / `true` / `false` ✅ |
| tokenEndpointAuthMethod | `client_secret_basic` |
| `INBOUND_CLIENT.PROPERTIES.type` | still `m2m` (a console label; the OAuth config is what the server enforces) |

- `GET /oauth2/authorize` with PKCE S256, `scope=openid profile email` gives a 302 to `/gate/signin?applicationId=01a182dc-...`. That's ThunderID's login page, so the redirect URI and scopes are accepted ✅
- **A4 resolved:** the console has a back-channel logout URI field, and it accepted `https://localhost`.
- There's no per-app allowed-scope list in the config. `openid profile email` were accepted without one.
- Screenshots: no `03-*.png` files exist yet.

## Step 4: Stock OIDC sign-in (2026-10-28)

**[ME] done:**
- `dotnet add package Microsoft.AspNetCore.Authentication.OpenIdConnect --version 9.0.20` (matches the installed ASP.NET Core 9.0.20 runtime). `dotnet user-secrets init` set `UserSecretsId` `a8f0f903-...`.
- `launchSettings.json`: a single `https` profile on `https://localhost:7101`.
- `appsettings.json`: `ThunderID:Authority = https://localhost:8090`, `ThunderID:ClientId = WItzJCtJA0Ij77xkgHIjKg`. The secret goes to user-secrets only.
- `Program.cs`: `AddCookie` + `AddOpenIdConnect` (Authority, ClientId, ClientSecret, `ResponseType=code`, `SaveTokens=true`, scope `email` added to the default `openid profile`). Nothing else changed from Microsoft's defaults.
- `Pages/Claims`: `[Authorize]`, lists every cookie claim, `User.Identity.Name`, and the decoded ID token header and payload. A nav link was added.
- Probe `POST /backchannel-logout`: logs content type, length and raw body, returns 200. Temporary.
- `dotnet build`: 0 warnings, 0 errors.

**Smoke test (app run by me without the secret, then stopped):**

| Request | Result |
|---|---|
| `GET /` | 200 |
| `POST /backchannel-logout` with `logout_token=test.probe.value` | 200, logged `Back-channel probe: POST application/x-www-form-urlencoded 29 bytes: logout_token=test.probe.value` ✅ |
| `GET /Claims` | **500**: `IDX20803 Unable to obtain configuration` ... `UntrustedRoot` ❌ |

- Cause: ThunderID's `server.cert` is self-signed (`CN=localhost, O=ThunderID`, SAN localhost/127.0.0.1, thumbprint `9162736405449AC21B4D30076618F0B835A69EDA`, valid to 2027-10-17). Browsers let you click through; .NET's `HttpClient` doesn't. `Invoke-WebRequest` fails the same way.
- Capture: `docs/blog/captures/04-untrusted-root.txt`.
- Next: you trust ThunderID's cert in `CurrentUser\Root`, set the secret, and sign in.

**Your run, after trusting the cert (the `UntrustedRoot` error is gone):**
- `/Claims` gives `OpenIdConnectProtocolException: unauthorized_client, 'Client is not allowed to use the specified authentication method'`, thrown in `OpenIdConnectHandler.PushAuthorizationRequest`. Screenshot saved as `img/04-par-unauthorized-client.png`.
- Cause, reproduced with curl against `https://localhost:8090/oauth2/par`:
  - Secret in the form body (`client_secret_post`, which is what .NET sends): `unauthorized_client`.
  - Secret in a Basic header (`client_secret_basic`): 200 with a `request_uri`.
- Two things combine:
  1. .NET 9's OIDC handler uses PAR automatically when discovery advertises `pushed_authorization_request_endpoint` (`PushedAuthorizationBehavior` defaults to `UseIfAvailable`). ThunderID advertises it. So the very first call is a back-end POST, not a browser redirect.
  2. .NET sends client credentials in the request body. The app was registered with `tokenEndpointAuthMethod: client_secret_basic`, and ThunderID strictly enforces the registered method. Code redemption at `/oauth2/token` would hit the same mismatch.
- Fix: set the app's token endpoint auth method to `client_secret_post` in the console. That's a config change only, so `Program.cs` stays stock.
- Capture: `docs/blog/captures/04-par-auth-method.txt`.
- You changed the client authentication method to `client_secret_post` (screenshot `img/04-auth-method.png`). Read back from the DB: `tokenEndpointAuthMethod: client_secret_post` ✅. The same curl with the secret in the body now gets a `request_uri` from `/oauth2/par` ✅

**"Still the same error" (2026-10-28):** it wasn't the same error. The exception type and wording look alike, but the message changed:
- I captured the PAR request ASP.NET Core sends, by running a second copy of the app on 7111 against a local Python listener on 9999 (`ThunderID__Authority=http://localhost:9999`, `Authentication__Schemes__OpenIdConnect__RequireHttpsMetadata=false`, `ASPNETCORE_ENVIRONMENT=Development`).
  - The first capture had no `client_secret`. That was my mistake: `--no-launch-profile` dropped the Development environment, so user-secrets weren't loaded.
  - The second capture had `client_secret` in the body and no Authorization header, so it's `client_secret_post` ✅. It also has **`response_mode=form_post`**.
- Replaying that exact body to ThunderID gave `invalid_request: Unsupported response_mode parameter`. Your running app showed the same message (`curl https://localhost:7101/Claims`).
- ThunderID accepts only an empty `response_mode` or `query` (`constants.go:77`). It doesn't publish `response_modes_supported` in discovery.
- Fix: `options.ResponseMode = "query";` in `Program.cs`. I built it to a scratch folder and ran it on 7111. The error became `Invalid redirect URI`, expected because 7111 isn't registered, so the response mode is no longer the problem. I stopped the 7111 instance and the listener.
- Capture: `docs/blog/captures/04-response-mode.txt`.

**"The issue persists" (2026-10-28, ~22:00):** your app was restarted at 21:57 with the new build, so it was running the fix. The error had changed again: `invalid_request: nonce exceeds maximum allowed length`.
- **My mistake:** my 7111 check after the response_mode fix stopped at `Invalid redirect URI`, and ThunderID validates the redirect URI before the nonce (`validator.go:91`). So that check couldn't reach the nonce rule. From now on I verify by capturing .NET's real PAR body and replaying it to ThunderID with the 7101 redirect URI, which runs every PAR check.
- ThunderID: `MaxNonceLength = 64` (`constants.go:97`). ASP.NET Core's nonce is 115 characters (`<ticks>.<base64 of two GUIDs>`). OIDC Core sets no length limit.
- Fix in `Program.cs`: `ShortNonceProtocolValidator` overrides `GenerateNonce()` to `<ticks>.<base64url of 32 random bytes>`, 62 characters, so .NET's timestamp check (`RequireTimeStampInNonce`) still works. Set as `options.ProtocolValidator`, carrying over the handler's own defaults `RequireStateValidation = false` and `NonceLifetime = 15 min`.
- Verified: build OK. The captured request has nonce length 62, `client_secret` in the body, and no `response_mode` param (.NET omits it for `query`). The replay to `/oauth2/par` returned a `request_uri` ✅
- Not verifiable by me: the rest (login page, callback, code redemption, ID token validation). Those need your browser sign-in.
- Capture: `docs/blog/captures/04-nonce-length.txt`.

**First successful sign-in (2026-10-28, ~22:10):** login page then `/Claims` rendered. Screenshots `img/04-thunderid-login.png` and `img/04-claims-default.png`.

| Where | What arrived |
|---|---|
| `User.Identity.Name` | `null` |
| Cookie claims | `auth_time`, `jti`, `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier` (= `sub`). .NET dropped `iss`, `aud`, `exp`, `iat`, `nbf`, `nonce` by default, and mapped `sub` to the long XML URI |
| ID token header | `alg: RS256`, `typ: JWT`, `kid: MT-DQ4F...` |
| ID token payload | `aud` (client ID), `auth_time`, `exp` (1 h), `iat`, `iss: https://localhost:8090`, `jti`, `nbf`, `nonce` (our 62-char one), `sub` (alice's entity id) |

- **No `name` or `email`** even though `profile email` were requested. The app's `scopeClaims` has `"profile": []`, `"email": []` (empty lists), so no user attributes are released for those scopes.
- **A2 resolved: negative. No `sid`.** `sid` is copied from the auth assertion (`authz/service.go:1075`), which only carries one when the login flow has a session step (spec AC3.1). `aspnet-webapp` uses `default-flow` (`01900000-...0061`), which has **no `SSOCheckExecutor` or `SessionExecutor`**. Only `console-app-flow` (`...0068`) has them.
- Consequence: no SSO session is created for this app's logins, so there is no session participant record and **nothing for back-channel logout to notify**. Step 5 can't work until the app uses a flow with a session step.
- Capture: `docs/blog/captures/04-no-sid.txt`.

**Your fix, option 1 (dedicated flow):** `aspnet-webapp` `AUTH_FLOW_ID` is now `01a18366-1ee4-7997-9c10-5a5a4a05f98e`, handle `sso-login-flow`, name "sso login flow", active version 2, updated 16:44:38 UTC.
- Nodes: start → `sso_check` (SSOCheckExecutor, `checkpointRef: session`; success → `session`, failure → `prompt_credentials`) → `prompt_credentials` → `credentials_auth` → `session` (SessionExecutor) → `authorization_check` → `auth_assert` → end.
- `sso_check` and `session` routing is identical to `console-app-flow` ✅
- `scopeClaims` are unchanged (still empty), so expect no `name`/`email` yet.
- Pending: a fresh sign-in, to confirm `sid` in the ID token.

**Allowed user types + scope mappings (your fix):** the scope mapper only listed system attributes because `aspnet-webapp` had no allowed user types. You added `Person` on the Access tab, then mapped `given_name` (profile) and `email` (email) and allowed them in the ID token.

**Sign-in in an InPrivate window (16:57 UTC), screenshot `img/04-claims-with-sid.png`:**
- ID token now has **`sid: 01a18372-2364-7a2a-afe3-c0626b927850`** ✅, plus `email` and `given_name`. **A2 resolved** once the flow has a session step.
- DB read-back (`runtime_persistent.db`): `SSO_SESSION` row with that id, subject alice, flow `sso-login-flow` v2; `SSO_SESSION_PARTICIPANT` row linking it to `aspnet-webapp` ✅. That participant row is what back-channel logout notifies.
- Cookie claims: `sid` and `jti` keep short names, but `email` → `.../claims/emailaddress`, `given_name` → `.../claims/givenname`, `sub` → `.../claims/nameidentifier`. `User.Identity.Name` is still `null` (no `name` claim, and .NET looks for the long `.../claims/name` type anyway).
- Capture: `docs/blog/captures/04-session-in-db.txt`.
- **Step 4 complete.**

## Step 5: Probe back-channel delivery (2026-10-28)

**[ME]:** the probe also appends `<UTC time> <content type> <body>` to `samples/WebApp/backchannel-probe.log` (gitignored, since it holds raw tokens), because I can't read your terminal. Baseline before the logout: `server.log` 12 lines; SSO session `01a18372-2364-...` ACTIVE.

**[YOU]:** restarted the app; in the InPrivate window `/Claims` still showed `sid 01a18372-2364-...`; opened `https://localhost:8090/oauth2/logout?client_id=WItzJCtJA0Ij77xkgHIjKg&post_logout_redirect_uri=https%3A%2F%2Flocalhost%3A7101%2Fsignout-callback-oidc`. There was no confirmation page: it redirected straight to `/signout-callback-oidc`, a blank page (screenshot `img/05-end-session.png`). `/Claims` afterwards: **still signed in, same sid** (`img/05-still-signed-in.png`).

**Evidence:**
- SSO_SESSION row `01a18372-2364-...`: **deleted** ✅
- Probe file: one POST at `17:06:24.43 UTC`, `application/x-www-form-urlencoded`, `logout_token=` 881 chars ✅. Token `iat` = 17:06:24 UTC, so it was delivered within the same second.
- **A1 resolved: yes.** ThunderID's HTTP client trusted the ASP.NET dev cert on `https://localhost:7101`. No http fallback needed.
- `server.log`: no new lines. Success is logged at Debug only (`dispatcher.go:518`); failures at Warn (`:526`). So "no line" means no failure.

**Token vs the spec contract (`docs-internals/back-channel-logout/spec.md:241-264`):**

| Field | Spec | Received |
|---|---|---|
| `typ` | `logout+jwt` | `logout+jwt` ✅ |
| `alg` | same as ID token | RS256, same `kid` as the ID token ✅ |
| `iss` | issuer | `https://localhost:8090` ✅ |
| `aud` | client id | `WItzJCtJA0Ij77xkgHIjKg` ✅ |
| `sub` | same as ID token | `01a182da-...` matches ✅ |
| `sid` | terminated session | `01a18372-2364-...` matches the ID token ✅ |
| `events` | `{"http://schemas.openid.net/event/backchannel-logout":{}}` | exact ✅ |
| `jti` | present | `01a1837a-2b97-...` ✅ |
| `iat` / `exp` | exp = iat + 120 s | 120 s ✅ |
| `nonce` | MUST be absent | absent ✅ |
| extra | — | `nbf` (= iat), harmless |
| signature | — | valid against `/oauth2/jwks` (checked with Python `cryptography`) ✅ |

- Captures: `05-probe-post.txt`, `05-logout-token-decoded.txt`, `05-thunderid-delivery-log.txt`.
- **Step 5 complete.**

## Step 6: Profile page and sign-out (2026-10-28)

**[ME] done:**
- `UserDisplay.cs`: reads `given_name`/`family_name`/`email` under the **mapped** types (`ClaimTypes.GivenName`, `Surname`, `Email`), since `MapInboundClaims` is still default until step 8. Display name: given + family, else `ClaimTypes.Name`, else email, else sub. Initials: given + family initials, else the first two letters of the display name.
- `Index`: signed-out view ("Sign in with ThunderID") or signed-in view ("Hello, <name>", profile link).
- `Profile` (`[Authorize]`): an initials avatar, name, email, given/family name, and a sign-out button.
- `Account/SignIn` (GET): `Challenge` to OIDC, returns to `/Profile`. `Account/SignOut` (POST, antiforgery-protected Razor handler): `SignOut(cookie, oidc)`. That clears the local cookie, then redirects to ThunderID's end-session endpoint with `id_token_hint`; it comes back through `/signout-callback-oidc` to `/`.
- Layout nav: the user's name + a sign-out button, or a sign-in button.
- Smoke test on 7111 (scratch build): home anonymous view ✅; POST `/Account/SignOut` without antiforgery → 400 ✅; `/Profile` and `/Account/SignIn` reach ThunderID PAR and fail on `Invalid redirect URI` (expected, since 7111 isn't registered). Stopped.
- Baseline before your test: `backchannel-probe.log` has 1 line (the step 5 token).

**Restart (2026-10-30):** found everything down. The ThunderID background task had exited (code 4) again, and the WebApp wasn't running.
- Both are now started with `Start-Process ... -WindowStyle Hidden`, so they're **independent processes**, not children of a tool task:
  - ThunderID: `pwsh -File start.ps1` in the dist folder, server PID 9060. Logs: `target/dist/server.log`, `server.err.log`.
  - WebApp: `dotnet run --project samples/WebApp --launch-profile https` from `thunderid-aspnetcore`. Logs: `webapp.log`, `webapp.err.log` (gitignored).
- Checks: discovery `https://localhost:8090`, backchannel True; console 200; WebApp home 200 with the signed-out view; `/Account/SignIn` → PAR succeeds → 302 to `/oauth2/authorize?client_id=...`.
- To stop: `Stop-Process -Id (Get-NetTCPConnection -LocalPort 8090,7101 -State Listen).OwningProcess`.

**[YOU] test (2026-10-30, normal browser window):** home signed out (`img/06-anonymous.png`) → Sign in with ThunderID → alice → profile with "AL" avatar, given name alice, family name "Not provided", email (`img/06-profile.png`) → Sign out → home signed out again, no prompt (`img/06-signed-out.png`).

**Evidence (ThunderID access log, local time +05:30):**

| Time | Call | Result |
|---|---|---|
| 11:45:22 | `GET /oauth2/authorize` | 302 to login |
| 11:45:32 | `POST /oauth2/token` | 200 (code redeemed with `client_secret_post`) |
| 11:49:22 | `GET /oauth2/logout` (from the app's sign-out) | 302 into ThunderID's sign-out flow |
| 11:49:23.457 | `POST /oauth2/logout/callback` | 200, session ended |
| 11:49:23.520 | back-channel POST received by the probe (`backchannel-probe.log` line 2) | ~60 ms after the callback |

- Logout token #2: `typ logout+jwt`, `sid 01a18b73-02a4-79ea-9d95-e14e3cc19750` (this sign-in's session), `sub` alice, no `nonce`. So **the initiating app is notified too** (spec AC1.1).
- `SSO_SESSION` for alice: none left ✅
- `id_token_hint`: not visible. ThunderID's access log drops the query string, and the WebApp doesn't log the outgoing redirect. Not verified.
- Side note: one orphan `SSO_SESSION_PARTICIPANT` row (`01a18371-9465-...`, from 2026-10-28 16:57:01) has no matching `SSO_SESSION`. It's from a sign-in attempt before the flow change; it doesn't affect anything.
- **Step 6 complete.**

## Step 7: API resource server and stock JwtBearer (2026-10-30)

**What ThunderID's source says, read before building:**
- Access token `aud`: if the request names a resource server (RFC 8707 `resource`) or asks for any non-OIDC scope, the token is bound to **one** resource server and `aud` = its identifier. With no `resource` param, ThunderID looks up a *default* resource server, and if there is none: `invalid_target` (`resourceindicators`, `ResolveTargetResourceServer`). OIDC-only requests get `aud` = the app's default audience / client ID (`granthandlers/authorization_code.go:150-160`).
- Non-OIDC scopes are downscoped to permissions defined on that resource server (`DownscopeToResourceServer`).
- "OIDC scope" means any key in the app's `scopeClaims`, so `billing:read` must **not** be added in the "Scopes & User Attribute Mappings" card.
- The flow's `authorization_check` step (`flow/executor/authz_executor.go`) only grants a permission scope if the user's roles hold that permission on the bound resource server.
- Permission string = `<resource handle><delimiter><action handle>`, default delimiter `:` (`resource/service.go:985`). So `billing:read` = resource `billing` + action `read`.

**[ME] done:**
- `samples/Api`: + `Microsoft.AspNetCore.Authentication.JwtBearer` 9.0.20; stock `AddJwtBearer` (Authority `https://localhost:8090`, Audience `https://localhost:7102` from appsettings); naive policy `RequireClaim("scope", "billing:read")`; endpoints `/public` (anonymous), `/me` (any valid token, echoes claims), `/billing` (naive policy). Port pinned to `https://localhost:7102`. `Api.http` updated.
- `samples/WebApp`: `Scope.Add("billing:read")`, `options.Resource = BillingApi:BaseUrl` (`https://localhost:7102`), `AddHttpClient()`, `Pages/CallApi` (sends the saved access token to `/billing`; shows status, `WWW-Authenticate`, body, and the decoded access token), nav link. **Not restarted yet:** with `resource` set and no resource server registered, PAR would fail with `invalid_target`.
- Both build: 0 errors. Api started detached (`api.log`, gitignored).

| curl | Result |
|---|---|
| `GET /public` | 200 `{"message":"Anyone can read this."}` ✅ |
| `GET /billing` (no token) | 401, `WWW-Authenticate: Bearer` ✅ |
| `GET /billing` (garbage token) | 401, `Bearer error="invalid_token"` ✅ |

**[YOU] console setup, read back from `configdb.db`:**
- `RESOURCE_SERVER` `billing-api`: id `01a18b8b-1a19-...`, identifier `https://localhost:7102`, type API, `delimiter ":"`, authorization engine `rbac`, OU = alice's OU (`...0001`). The console labels it **"Default resource server"** (`img/07-resource-server.png`), so a request with permission scopes but no `resource` would also bind to it.
- First pass: resource `Billing`/`billing` (permission `billing`) but **no action**, and the role granted `billing` (the whole resource). Fixed: action `Read`/`read` → permission **`billing:read`**; role `billing-reader` now grants only `billing:read` (`img/07-role-permissions.png`, which also shows the console's "Selected scopes: billing:read"). Alice assigned (`img/07-role.png`).
- `img/07-resources-actions.png` shows only the resource; a retake with the action is still to do.

**WebApp restarted (detached) with step 7 code:** home shows "Call API"; `/Account/SignIn` → PAR with `resource=https://localhost:7102` + `scope=openid profile email billing:read` accepted → 302 to `/oauth2/authorize` ✅

**[YOU] sign-in + Call API (screenshot `img/07-call-api-403.png`):** HTTP **403**. Access token payload in `captures/07-access-token-claims.txt`:
- **A3 resolved:** `aud` = `"https://localhost:7102"`, a single string (the resource server identifier), because the request carried `resource`.
- `scope` = `"openid profile email billing:read"`, **one space-delimited string**. `billing:read` was granted (role → permission → scope).
- Other claims: `aci`, `client_id`, `grant_type: authorization_code`, `iss`, `jti`, `iat`/`nbf`/`exp` (1 h), `sub`, `tfid` (token family). **No `sid`** (as the spec says) and no `act`.
- 403, not 401: JwtBearer accepted the token (signature, issuer, audience, lifetime) and the naive policy rejected it.
- To show the API's view of the claims, the Call API page now also calls `/me`. WebApp restarted (detached); your cookie and saved tokens survive the restart.

**API's view (`/me` with alice's token, screenshot `img/07-api-sees-scope.png`):** HTTP 200, and the claims include **one** `scope` claim with value `"openid profile email billing:read"`. JwtBearer doesn't split it. `RequireClaim("scope", "billing:read")` compares whole values, so it never matches → 403. Also `sub` → `.../claims/nameidentifier` (same default mapping as in the WebApp); `aud`, `iss`, `client_id`, `tfid`, `aci` keep their names.
- Capture: `captures/07-curl-matrix.txt` (curl rows + Call API page rows, labelled by source).
- Planned fix (step 8): a scope policy that splits the space-delimited claim.
- **Step 7 complete.**

## Step 8: Library helpers (2026-10-30)

**[ME] in `src/ThunderID.AspNetCore`** (references `Microsoft.AspNetCore.App` + OpenIdConnect/JwtBearer 9.0.20):
- `AddThunderID(o => ...)`: cookie + OIDC with `ResponseType=code`, PKCE, `SaveTokens`, **`ResponseMode=query`**, **`ShortNonceProtocolValidator`** (62 chars, keeps `RequireStateValidation=false` and `NonceLifetime=15 min`), **`MapInboundClaims=false`**, `NameClaimType="name"`, `RoleClaimType="roles"`, scopes `openid profile email` + extras, optional `Resource`. `OnTokenValidated` adds a `name` claim from `given_name` + `family_name` when ThunderID sends none.
- `AddThunderIDApi(o => ...)`: JwtBearer with Authority, Audience, `MapInboundClaims=false`, `NameClaimType="sub"`.
- `AuthorizationBuilder.AddScopePolicy(name, params scopes)`: authenticated user + every scope present after splitting all `scope` claims on spaces (ordinal, case-sensitive).
- `ActorClaims` (`act`) not built: dropped with the act endpoint (out of scope).
- `sid`, `sub`, `auth_time`: already kept by the OIDC handler's default claim actions once mapping is off, so no extra code.

**Samples switched:** WebApp `Program.cs` uses `AddThunderID` (OIDC setup 33 lines → 8, and it now also fixes claim names and `User.Identity.Name`). Api uses `AddThunderIDApi` + `AddScopePolicy("billing:read", "billing:read")`. Direct OpenIdConnect/JwtBearer package refs removed from the samples (they come through the library). `UserDisplay` reads `given_name`/`family_name`/`email`/`sub` and `User.Identity.Name`. Before versions saved as `captures/08-*-program-before.cs.txt`.

**Tests:** `dotnet test`: **22 passed, 0 failed** (`captures/08-tests.txt`). Scope policy: found inside a space-delimited claim; all-of-many; one missing; no claim; `billing` vs `billing:read` both ways; case-sensitive; extra spaces; unauthenticated user; policy with no scopes throws. Options: query mode/code/PKCE/SaveTokens; `MapInboundClaims=false` + name claim; scopes list; resource only when set; nonce ≤ 64 chars with a valid timestamp (100 samples); validator defaults; API audience + mapping; name fallback (3 cases), existing name kept, no parts → nothing.

**Build:** library + tests 0 warnings. WebApp had one real warning, `CS0108` (`CallApiModel.StatusCode` hid `PageModel.StatusCode(int)`); renamed to `BillingStatusCode`, now 0 warnings. (A `--no-incremental` solution rebuild I ran while the samples were running produced 29 MSB3026 file-lock retry warnings; that was my mistake and not a code issue.)

**Restarted (detached):** Api `/public` 200, `/billing` no token 401; WebApp home 200, sign-in → PAR → 302 authorize ✅

**[YOU] fresh sign-in through the library (screenshots `img/08-claims-clean.png`, `img/08-call-api-200.png`):**
- `/Claims`: `User.Identity.Name` = **alice** (name fallback from `given_name`). Cookie claims: `auth_time`, `email`, `given_name`, `jti`, `sid`, `sub`, `name`. No XML URIs ✅ `sid` = `01a18ba4-1861-...` (new session).
- `/CallApi`: `/billing` **200** with both invoices ✅ `/me` 200, same token shape as step 7 (`aud https://localhost:7102`).
- Step 6 and 7 behaviour re-checked through the helpers: sign-in, profile name, API call all work; the API result changed from 403 to 200 only because of `AddScopePolicy`.
- **Step 8 complete.**

## Step 9: LogoutTokenValidator and ILogoutSessionStore (2026-10-30)

Contract re-read first: `docs-internals/sdk-development/spec.md:456-508` (receiver), `:678-688` (logout token validation), `:1078-1105` (AC9.1-AC9.10). Beyond the 8 checks it adds: bounded JWKS re-fetch on an unknown kid (AC9.7), `jti` replay SHOULD be refused, the token MUST NOT be logged (AC9.9), and no check can be turned off.

**[ME] in `src/ThunderID.AspNetCore/BackchannelLogout/`:**
- `LogoutToken` record (`Issuer`, `Subject`, `SessionId`, `JwtId`, `IssuedAt`, `ExpiresAt`) and `LogoutTokenValidationResult` (error text names the failed check, never echoes the token).
- `LogoutTokenValidator(IConfigurationManager<OpenIdConnectConfiguration>, clientId, clockSkew = 2 min, TimeProvider)`:
  - input: missing → reject; > 8 KB → reject.
  - `JsonWebTokenHandler` with: `ValidTypes = logout+jwt | application/logout+jwt`; `ValidIssuer` = discovery issuer (exact); `ValidAudience` = client id (aud may be an array); `IssuerSigningKeys` = JWKS; `ValidAlgorithms` = discovery `id_token_signing_alg_values_supported`; `RequireSignedTokens`; `RequireExpirationTime`; lifetime via the injected clock ± skew; **`TryAllIssuerSigningKeys = false`**.
  - unknown kid → `RequestRefresh()` once and retry. `ConfigurationManager` rate-limits refreshes (`RefreshInterval`, default 5 min).
  - then: `iat` present and not in the future; `events` has an object member `http://schemas.openid.net/event/backchannel-logout`; **no `nonce`**; non-empty `sid` or `sub`; `jti` present.
- `ILogoutSessionStore` (replaceable): `TryRecordLogoutAsync(token)` → false if the `jti` was seen (replay); `IsSessionEndedAsync(sid, sub, authTime)`.
- `InMemoryLogoutSessionStore(retention, TimeProvider)`: `sid` token → that session ends; `sub`-only token → subject's sessions with `authTime < iat` end (latest wins; unknown `authTime` treated as older); `jti` kept until token `exp` + 5 min; ended sessions kept for `retention` (must cover the cookie lifetime); lazy pruning; one lock.

**Found while testing (fixed):** with .NET's default `TryAllIssuerSigningKeys = true`, a token whose `kid` names no key was **accepted** if any JWKS key verified it, so the unknown-kid refresh path never ran. Not a forgery (the signature still had to verify), but loose. Now the kid must match exactly.

**Tests:** `dotnet test` → **69 passed, 0 failed**, 0 warnings (`captures/09-tests.txt`): 34 validator, 13 store, 22 from step 8. No network: `StaticConfigurationManager`, plus a real `ConfigurationManager` over a counting `IDocumentRetriever` for the JWKS bound. The valid token copies step 5's real claims (`iat 1793207184`, same `sid`/`sub`/`jti`/`aud`), signed with a test RSA key, with the clock pinned to that `iat`.
- Validator rejections: `typ JWT`; no `typ`; other key same kid; tampered payload; unknown kid; `alg none`; `RS512` (not an ID token alg); wrong `iss`; `iss` + trailing slash; other `aud`; expired past skew (accepted 1 s inside skew); no `exp`; `iat` in the future; no `iat`; no `events`; `events` without the logout member; logout member not an object; `nonce`; a real ID-token shape (typ JWT + nonce + no events); no `sid`/`sub`; empty `sid`/`sub`; no `jti`; null/empty; not a JWT; > 8 KB; error text contains no token. Accepted: ThunderID's shape; aud array; sub-only; sid-only; `application/logout+jwt`.
- Bounded refresh: 20 tokens with 20 different unknown kids → JWKS fetched 1-2 times in total.
- Store: sid ends that session only; other sessions of the same sub untouched; sub-only ends earlier sessions; later sign-in untouched (incl. authTime == iat); no authTime → ended; other subjects untouched; older sub-only logout can't shorten a newer one; unknown session not ended; same jti → replay and nothing recorded; different jtis both recorded; jti forgotten after expiry; retention boundary; retention must be > 0.

## Step 10: Endpoint and cookie enforcement (2026-10-30)

**[ME] in the library:**
- `MapThunderIDBackchannelLogout(pattern = "/backchannel-logout")`: POST only; `.AllowAnonymous()`, `.DisableAntiforgery()`; content type must be exactly `application/x-www-form-urlencoded`; body capped at 16 KB (by Content-Length and while reading, so a chunked body can't get past it); exactly one `logout_token`; validator → store. 200 on success (also for a session the app doesn't hold); 400 + `{"error":"invalid_request","error_description":<check>}` for a bad request, a bad token, or a replayed `jti`; 500 if the store throws (so ThunderID retries). `Cache-Control: no-store` on every response; no redirect. Logs name the failed check or the `sid`/`sub`, never the token.
- `AddThunderID()` now also registers: `ILogoutSessionStore` → `InMemoryLogoutSessionStore` with retention = the cookie's `ExpireTimeSpan` (via `TryAdd`, so an app can replace it); `LogoutTokenValidator` built from the **OIDC handler's own `ConfigurationManager` and ClientId** (one discovery/JWKS cache). The cookie's `OnValidatePrincipal` → `BackchannelLogoutCookieValidator`: reads `sid`, `sub`, `auth_time` (falls back to the cookie's `IssuedUtc`), asks the store, and on "ended" calls `RejectPrincipal()` and signs the cookie out.
- WebApp: the probe is replaced by `app.MapThunderIDBackchannelLogout("/backchannel-logout")` (no `backchannel-probe` references left).

**Bug caught by the analyzer (ASP0016):** my first `MapPost(pattern, HandleAsync)` bound to the `RequestDelegate` overload, which **discards the returned `IResult`**, so every 400/500 would have gone out as an empty 200. Fixed by passing it as `Func<HttpContext, Task<IResult>>`.

**Tests:** **87 passed, 0 failed**, 0 warnings (18 new endpoint/cookie tests in `captures/10-endpoint-tests.txt`). They run on `Microsoft.AspNetCore.TestHost` 9.0.20 (new test-only package) with the real `AddThunderID()` wiring; only the validator is swapped for one with the test key and pinned clock.
- Endpoint: valid → 200 + no-store + no Location + recorded; unknown session → 200; no cookie needed; GET → 405; JSON → 400 + nothing recorded; multipart → 400; oversized (with and without Content-Length) → 400; no `logout_token` → 400; two → 400; invalid (nonce) → 400 + reason + nothing recorded; replay → 400; throwing store → 500 + no-store; logs contain no part of any token.
- Cookie: ended `sid` → next request anonymous + cookie expired; other `sid` of the same user survives; `sub`-only logout ends the session that began 10 min earlier and keeps one that began 5 s after `iat`; an invalid token leaves the session alone.
- **Mutation check:** putting the ASP0016 bug back makes **9 tests fail**; restored → 87 pass.

**Live (WebApp restarted, detached), `captures/10-live-curl.txt`:** JSON → 400 content type; form without `logout_token` → 400; garbage → 400 "not a JWT"; ThunderID's real step 5 token → 400 "token is outside its lifetime" (it's 2 days old); every response `Cache-Control: no-store`. GET → **404** (PUT/DELETE → 405): in the sample another GET-capable route candidate (Razor Pages/static assets) makes the method matcher answer 404 instead of 405. Non-POST never reaches the handler either way. `webapp.log` contains no `logout_token=ey`.

## Step 11: End-to-end proof and recording (2026-10-30)

Deferred at your request ("il record later"). The [ME] part (matching the WebApp log with ThunderID's session table, with timestamps) runs on the same attempt as your recording, so both are done together. Pending: `img/11-before.png`, `img/11-after.png`, `img/11-demo.gif`, `captures/11-both-logs.txt`, blog section "The moment it works" (a placeholder comment marks its spot in the draft).

## Step 12: README and diagram (2026-10-30)

- `README.md`: what's in the box (API table + snippet), quickstart (prerequisites, `deployment.yaml`, `setup.ps1` then `start.ps1`, trusting ThunderID's cert, console registration table, config keys, run, tests), findings table from FRICTION-LOG, limits, layout.
- **Deviation from the plan:** the plan named env vars `THUNDERID_AUTHORITY` etc. The code reads standard config keys (`ThunderID:Authority`), so the README documents user-secrets and the `ThunderID__Authority` env var form instead of inventing a second naming scheme.
- `docs/backchannel-logout.drawio`: sequence diagram (Browser A, WebApp, ThunderID, "somewhere else"), hand-written XML; parses as XML. **draw.io desktop isn't installed**, so I couldn't export the PNG: `docs/backchannel-logout.drawio.png` is a [YOU] export. README and the draft already link it.
- **Quickstart check:** no commits exist yet, so instead of a fresh clone I copied exactly the files git would commit (`git ls-files --cached --others --exclude-standard`, 165 files) to a clean scratch folder. `dotnet build`: 0 warnings, 0 errors. `dotnet test`: 87 passed. Config keys read by the samples (`ThunderID:Authority`, `ThunderID:ClientId`, `ThunderID:ClientSecret`, `ThunderID:Audience`, `BillingApi:BaseUrl`) match the README table exactly.
- Not checked: the quickstart's ThunderID console steps on a fresh server (they're taken from steps 3, 4 and 7 as done here).
- Blog: diagram inserted into "The message nobody was listening for"; new section "What this doesn't do".

**Run (2026-10-30, after "il record later" was reversed the same afternoon):**
- First attempt (~15:35) looked like a failure: you saw the profile after F5. Logs showed the logout recorded and a cookie refused, but no new sign-in at ThunderID. Cause: window A had been closed before window B logged out; window B's landing request carried the shared cookie and was the one refused. I reproduced the `[Authorize]` case in-process (scratch copy, not the repo): ended `sid` → 302 to the authorize endpoint and every cookie chunk deleted, both for a small and a 5-chunk cookie. Old log kept as `webapp-run1-1535.log` (gitignored by `*.log`).
- WebApp restarted with request logging and timestamps (`Logging__Console__FormatterOptions__TimestampFormat`, `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information`, env vars only, no code change).
- Second attempt, window A kept open: sign-in 15:43:24 (sid `01a18c4c-c519-79f8-8c02-11bd447824cd`); ThunderID ends the session at 15:43:42.152; POST `/backchannel-logout` arrives 15:43:42.164 (**+12 ms**), recorded 15:43:42.213 (**+61 ms**), 200 in 51 ms; F5 at 15:43:48.056 → refused → 302 → ThunderID `/oauth2/par` + `/oauth2/authorize` → sign-in page ✅
- Side observation: window B's `GET /signout-callback-oidc` (15:43:42.178) was not refused, because the OIDC handler answers that path itself before the cookie is checked.
- Screenshots: `11-after.png` from this run. `11-before.png` is the profile screenshot from the first attempt (same page, same user). `11-demo.gif` not recorded yet.
- Capture: `captures/11-both-logs.txt`. Blog section "The moment it works" replaces the placeholder.

## Step 13: Blog assembly (2026-10-30)

- Read the whole draft; kept the chronological order (it already reads as one story).
- Added: a short "if you're in a hurry" summary with the repo link after the questions; "What I'd tell you before you start" (7-point checklist); "What's next" (Flow Execution API from Blazor) with the repo link.
- Trimmed: the Git Bash `tar` paragraph (build-environment detail, kept in RUNBOOK); a stale forward reference ("I'll find out in the next step"); internal step numbers ("step 4/5") in the validator section.
- Corrected: the ML-DSA paragraph claimed ThunderID can sign with ML-DSA and that RS256 is the default; neither was verified. Now: the build offered RS256 and ES256 only (discovery, Step 2).
- Added `07-resource-server.png` to the API section.
- Image check: all 25 links resolve. Unused: `07-role.png`, `07-resources-actions.png` (the latter doesn't show the `read` action). `11-demo.gif` not recorded.
- Claim check against records: 25 SDKs (plan facts), release vs merge dates (`captures/01`), build ~19 min (build log 00:01:50 to 00:20:28), 115/62-char nonce and 64 limit (Step 4), 12 ms / 61 ms (Step 11), ~60 ms initiator logout (Step 6), 1 h access token (`captures/07`: exp - iat = 3600), 22 / 69 / 87 tests, 100-nonce test, 20 unknown kids -> at most 2 JWKS fetches (test source).
- Style check: no em dashes, no bare product short form, no `--` in prose (one in a CLI flag inside a code block).

## Blog re-verification and video guide (2026-10-30)

- Re-checked every claim in `docs/blog/draft.md` against the captures, RUNBOOK, FRICTION-LOG, the library source and ThunderID `main` (`59ffbf47a`).
- Newly verified: the console's back-channel field hint (`frontend/packages/i18n/src/locales/en-US.ts:3154`); "none of these checks can be configured off" (`docs-internals/sdk-development/spec.md:686`); 16 KB cap (`BackchannelLogoutEndpoint.cs:14`); store retention = cookie `ExpireTimeSpan` (`ThunderIDAuthenticationExtensions.cs:28`); the `OnValidatePrincipal` snippet matches `BackchannelLogoutCookieValidator.cs:36-43`; test token uses the real `iat` 1793207184 (`LogoutTokens.cs:18`).
- Corrected:
  - "25 SDKs" → **26**: `docs/content/sdks-and-tools` on `main` has 26 `entry.yaml` files, none for .NET (Flutter AppAuth was added 2026-10-01).
  - "or an admin ending her session" removed: no admin session-revocation action was found or tested (`TerminateBySubject` is only called from a flow executor).
  - ML-DSA (blog and README): "Microsoft.IdentityModel can't verify ML-DSA" was wrong. `Microsoft.IdentityModel.Tokens` 8.19.2 includes `ML-DSA-44/65/87` (via `Microsoft.Bcl.Cryptography`). Now stated as untested.
- Added `docs/blog/video.md`: recording guide (Snipping Tool or OBS, since Game Bar records one window only), clip list, demo steps, GIF steps, restart commands, and the voiceover script with a source for each fact.
