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

## SDK validation (from 2026-11-03)

Validating this project against ThunderID's SDK Development skill, `docs-internals/sdk-development/spec.md` and `threat-model.md`, after a maintainer invited it to become a community SDK. Plan: `~/.claude/plans/how-do-we-make-adaptive-liskov.md`.

### Step 0: Preflight (2026-11-03)

- `upstream/main` is now `818626a78` (the plan pinned `fc8b081fd`). The new commits don't touch the inputs: the blobs are identical at both commits.

  | File | Blob |
  |---|---|
  | `.agent/skills/sdk-development/SKILL.md` | `e6ff61266aba206b8a991113c8000328c3dbb3e4` |
  | `docs-internals/sdk-development/spec.md` (v0.8, Draft) | `64c9b697a3a7501ef7ad866e4354e3e6368250c5` |
  | `docs-internals/sdk-development/threat-model.md` | `233ecf650f86320998cb3d0540478290da44b458` |

  The scratchpad snapshots hash to the same blobs. The validation pins the **content** by these blob hashes, not by commit.
- `~/.claude/skills/sdk-development` doesn't exist yet.
- `thunderid-aspnetcore`: clean, `main` at `d524d97`, in sync with `origin/main`. `dotnet build`: 0 warnings, 0 errors. `dotnet test`: 87 passed, 0 failed.
- The ThunderID clone is still on `add-mobile-number-library-attribute`, with only `?? frontend/apps/console/src/features/`. Untouched.
- Tooling note: with `MSYS_NO_PATHCONV=1` set (needed for `git show <rev>:<path>`), Git Bash also stops converting `/c/...` file paths for `git.exe`, so `git hash-object /c/...` fails. Use relative or Windows paths.

### Step 1: Install the skill globally (2026-11-03)

- Installed with:
  ```bash
  export MSYS_NO_PATHCONV=1
  mkdir -p "C:/Users/pasin/.claude/skills/sdk-development"
  git -C C:/Users/pasin/Documents/Academics/Thunder/thunderid show upstream/main:.agent/skills/sdk-development/SKILL.md > "C:/Users/pasin/.claude/skills/sdk-development/SKILL.md"
  ```
  The source was `upstream/main` `818626a78`. The content is unchanged: nothing was added inside the file.
- Verified: `git hash-object SKILL.md` = `e6ff61266aba206b8a991113c8000328c3dbb3e4`, identical to the upstream blob. 289 lines, 14,658 bytes. The frontmatter has `name: sdk-development` and `allowed-tools: Read Write Edit Bash WebFetch`.
- **To update later:** run `git -C <thunderid> fetch upstream`, then the same `git show` command. Compare the hash with `git rev-parse upstream/main:.agent/skills/sdk-development/SKILL.md`.
- Pending: your confirmation that it appears in a new session.

### Step 2: Classification (2026-11-03)

- Wrote section 1 of `docs/validation/gap-report.md`, comparing Reading A (Framework Specific SDK for ASP.NET Core) with Reading B (integration package), with spec lines.
- Evidence for A: back-channel handling is defined for "Framework Specific SDKs built on [Node]" (`spec.md:469-470`) and exposed "as a request handler in its framework's idiom" (`:479-481`). The server-side sample shape (`:889-891`) matches `samples/Api`.
- Evidence against B: an integration "MUST delegate every protocol operation to a ThunderID SDK" and "MUST NOT implement OAuth 2.0 or OIDC logic" (`:207-208`). There's no .NET ThunderID SDK, and `LogoutTokenValidator` is OIDC logic.
- Deviation under A: there's no ThunderID parent layer (`:179`, `:189`), and it isn't among the permitted deviations (`:146-153`).
- Decision (you, 2026-11-03): **Reading A**.

### Step 3: Security floor and back-channel logout (2026-11-03)

- Scope: `spec.md:661-765` (Security requirements), `:482-516` (back-channel handler requirements), R4 (AC4.x) and R9 (AC9.x). Written to `docs/validation/spec-matrix.md` section 1.
- Probes: a throwaway clone of `d524d97` at `<scratchpad>/probe`, with `tests/ThunderID.AspNetCore.Tests/ProbeTests.cs`. They aren't in the repo. Run with `dotnet test tests/ThunderID.AspNetCore.Tests --filter "FullyQualifiedName~ProbeTests" --logger "console;verbosity=detailed"`. All 9 probe cases ran; the first P1 run failed on a bug in my probe code (it assumed `response_mode` would be present), which I fixed and reran.

  | Probe | Output (trimmed) |
  |---|---|
  | P1 PKCE/state | `code_challenge_method=S256 code_challenge.length=43 challenge==S256(verifier): True`; `verifier.length=43 unreserved-only=True`; `state.length=390 nonce.length=62 response_mode=(absent)`; the two challenges have different state and verifier |
  | P2 state mismatch | `state='garbage'`: `AuthenticationFailureException: Unable to unprotect the message.State.`; `state=''`: `message.State is null or empty`. No session cookie |
  | P3 settings | `UsePkce=True RequireHttpsMetadata=True ResponseMode=query SaveTokens=True PAR=UseIfAvailable`; TVP `ValidateIssuer/Audience/Lifetime=True RequireSignedTokens=True ValidIssuer=(null, uses discovery issuer)`; `RequireNonce=True`; cookie `HttpOnly=True SecurePolicy=SameAsRequest SameSite=Lax ExpireTimeSpan=14d Sliding=True SessionStore=(null)` |
  | P4 HTTPS | `http://idp.example.com` and `http://localhost:8090`: `InvalidOperationException: The MetadataAddress or Authority must use HTTPS...` at configuration time. `https://idp.example.com`: accepted |
  | P5 issuer | `Authority=https://localhost:8090, discovery issuer=https://evil.example: configuration ACCEPTED`. Logout token `iss=https://evil.example`: valid. `iss=https://localhost:8090`: `issuer does not match` |
  | P6 sub-only | logout `iat`=T: sessions from T-60s and T-1s ended (ok); T+0s, T+30s, T+119s **not ended** (spec: ended); T+121s and T+600s not ended (ok) |
  | P7 handler | sid and sub-only tokens: `200`, no `Location`, `Cache-Control: no-store`, no `Set-Cookie`. Outbound: only `GET /.well-known/openid-configuration` and `GET /oauth2/jwks` |
- Every test name cited in the matrix was checked to exist in `tests/` (none missing).
- Totals, counted from the table: 48 rows, 22 Meets, 6 Partial, 7 Gap, 13 N/A. I first wrote 51 rows by hand, which was wrong, and corrected it from the count.
- Gaps: `sub`-only clock tolerance (BCL-3b, AC9.2); discovery issuer not pinned to `Authority` (S7, AC4.8); no token refresh (AC4.7, S12); sign-out doesn't revoke before clearing local state (AC4.5, S12).
- Exploitability check (skill §5): no finding is exploitable unmitigated. S7 needs an attacker able to serve the authority's discovery document over HTTPS, so the gap is hardening, not an open vulnerability. Safe to keep in the repo.

### Step 4: Client surface, configuration, framework integration, errors (2026-11-04)

- Scope: `spec.md:291-360` (Configuration), `:361-481` (Client surface, back-channel operation contract), `:547-562` (Framework integration), `:630-660` (Error model), R2 and R3, and the skill's §4 "Naming and surface", "Behaviour" and "Errors" lists. Written to `docs/validation/spec-matrix.md` section 2.
- Probes P8 to P13 were added to the same scratch clone (not the repo), and run with `--filter "FullyQualifiedName~ProbeTests.P8|...P13"`. Two runs failed on my probe code: a missing `using Microsoft.Extensions.Logging;`, and a Python rewrite of P12 that turned `'\n'` into a real line break. Both were fixed and rerun. The first P12 version set all six parameters at once, and threw on `code_challenge`; that was a real result, so I split it into one case per parameter.

  | Probe | Output (trimmed) |
  |---|---|
  | P8 config timing | Empty `ClientId`: `ArgumentException: The value cannot be an empty string. (Parameter 'ClientId')`. Empty `Authority`: `InvalidOperationException: Provide Authority, MetadataAddress, Configuration, or ConfigurationManager...`. `not a url`: `InvalidOperationException: ...must use HTTPS...`. All three **at the first challenge request**, none at `AddThunderID`, `Build` or `StartAsync`. Outbound calls before the failure: 0 |
  | P9 sign-out, no session | `status 302, Location https://localhost:8090/oauth2/logout, params=post_logout_redirect_uri,state,x-client-SKU,x-client-ver, id_token_hint present=False`. No exception. Outbound calls: 0 |
  | P10 error shape | Failed callback raised `Microsoft.AspNetCore.Authentication.AuthenticationFailureException`; `has Code property: False, has Origin property: False`, `Data` empty; message doesn't contain the code. Back-channel: `400 {"error":"invalid_request","error_description":"logout_token is invalid"}` |
  | P11 logs | 41 lines at Trace; 3 contain the code: `Information Microsoft.AspNetCore.Hosting.Diagnostics: Request starting HTTP/1.1 GET http://localhost/signin-oidc?code=<CODE>&state=garbage`, the matching `Request finished` line, and `Trace ...OpenIdConnectHandler: MessageReceived: '?code=<CODE>&state=garbage'` |
  | P12 SDK-owned parameters | Caller `state`: sent once, caller's value not used (SDK's wins). Caller `code_challenge`, `code_challenge_method`, `client_id`, `redirect_uri`: sign-in request threw `ArgumentException: An item with the same key has already been added. Key: <name>`. Control `login_hint`: sent once, caller's value |
  | P13 second context | Second `AddThunderID`: `InvalidOperationException: Scheme already exists: Cookies` |
- Corrections to section 1, made in place: AC4.1b went from N/A to Partial (P12). AC4.6's wording now says the HTTPS rule fires when options are first resolved, which in an app is the first sign-in (P8). AC4.11 and AC4.12 keep N/A, with evidence added. Section 1 recounted by script: 48 rows, 22 Meets, 7 Partial, 7 Gap, 12 N/A.
- Section 2 counted by script: 51 rows, 12 Meets (2 delegated), 14 Partial, 16 Gap, 9 N/A. My first hand count of the mapping table said 7 Gap; the table has 6, corrected before writing.
- Every `file:line` cited in section 2 was checked against the source listing.
- Exploitability check (skill §5): nothing new is exploitable. P12 fails closed (the caller's value is never sent). P11 is the S11 finding already recorded.
- Questions added for the gap report: Q3, which client surface operations apply to a server-side SDK (`isLoading`, and framework verbs instead of named operations). Q4, whether `AddThunderIDApi` and `AddScopePolicy` need the cross-SDK parity process.

### Step 5: Layering, packaging, modes, CI and release, testing, docs and samples (2026-11-04)

- Scope: `spec.md:177-242` (layering, integration packages, packaging), `:243-289` (operational modes), `:801-819` (repository, CI, release), `:821-866` (testing), `:868-892` (docs and samples), R1, R5 to R8. Written to `docs/validation/spec-matrix.md` section 3.
- Repo facts (`git ls-files`, `git tag` at `d524d97`): no `.github/`, no `LICENSE`, 0 tags. The csproj has only `TargetFramework net9.0` and the two `9.0.20` package references, with no NuGet metadata. `upstream/main:docs/content` has no .NET page.
- Read along the way: `ShortNonceProtocolValidator.cs:20` sets `RequireStateValidation = false`. That's ASP.NET Core's own default for its validator (the handler checks `state` itself through Data Protection), and P2 already showed a bad `state` is refused. Not a finding.
- .NET support date: .NET 8 and .NET 9 reach end of support on **2026-11-10**, and .NET 10 is supported to 2028-11-10 (https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/). Microsoft's lifecycle page still shows the older May 2026 date; the blog announcing the 24-month STS window supersedes it.
- Mutation runs in the scratch clone (one line changed, the 87 repo tests run with `--filter "FullyQualifiedName!~ProbeTests"`, the line restored with `git checkout`):

  | Mutation | File | Result |
  |---|---|---|
  | M1 `UsePkce = false` | `ThunderIDAuthenticationExtensions.cs` | Failed: 1 |
  | M2 ID token `ValidateIssuer = false` | same | **Passed: 87** (not caught) |
  | M3 ID token `ValidateAudience = false` | same | **Passed: 87** (not caught) |
  | M4 `RequireNonce = false` | `ShortNonceProtocolValidator.cs` | **Passed: 87** (not caught) |
  | M5 logout `ValidTypes = null` | `LogoutTokenValidator.cs` | Failed: 4 |
  | M6 logout `ValidateIssuer = false` | same | Failed: 3 |
  | M7 logout `ValidateLifetime = false` | same | Passed: 87, but **equivalent mutant**: the custom `LifetimeValidator` delegate runs whatever `ValidateLifetime` says, so expiry was still checked. My mistake in choosing it |
  | M7b `LifetimeValidator` returns `true` | same | Failed: 2 (`Expired_token_is_rejected`, `Missing_exp_is_rejected`) |
  | M8 no `Cache-Control: no-store` | `BackchannelLogoutEndpoint.cs` | Failed: 4 |
  | M9 `MapInboundClaims = true` (first occurrence) | `ThunderIDAuthenticationExtensions.cs` | Failed: 1 |

  After the runs, `git status` in the clone showed only the untracked `ProbeTests.cs`.
- Line citations: I first wrote 37 spec line numbers in section 3 from memory of the section layout, and most were off by 2 to 8 lines. I checked them against `grep -n` on the pinned `spec.md` and corrected all of them by script before appending (0 misses). README `:133` was corrected to `:134`.
- Section 3 counted by script and checked by hand: 58 rows, 14 Meets (1 delegated), 8 Partial, 22 Gap, 14 N/A; no empty Status cells.
- Whole matrix recounted by script: 157 rows, 48 Meets, 29 Partial, 45 Gap, 35 N/A. All 67 acceptance criteria are present (64 numbered plus three `b` variants; the plan said 64).
- Not done: a line-by-line mapping of all 143 MUST lines. Coverage is by section, with every AC checked by script; a MUST line inside a covered section may share a row with others.
- Exploitability check (skill §5): nothing in this step is exploitable. M2 to M4 are missing tests of delegated checks, not missing checks (P3 shows them on).

### Step 6: Threat model review (2026-11-04)

- Scope: `threat-model.md` (blob `233ecf6`) interactions 01 to 07 (`:140-520`), the Security Review Checklist (`:522-580`) and the residual risks (`:581-611`), plus the four questions in skill section 5. Written to `docs/validation/threat-model-review.md`. Reuses the Step 3 to 5 matrix rows as evidence.
- Applies: 01, 02, 04 (the path where the app holds the token), 05, 06. N/A: 03 and 07 (no embedded mode).
- **Correction to rows already written (Step 3 and Step 4):** I printed the code behind every `file:line` citation in `spec-matrix.md` (script `cites.py` in the scratchpad). Five were wrong and are now fixed:

  | Row | Was | Now |
  |---|---|---|
  | S11 | `BackchannelLogoutCookieValidator.cs:303` (the file has 47 lines) | `:40` |
  | BCL-3b | `InMemoryLogoutSessionStore.cs:239` (97 lines) | `:72` |
  | BCL-5 | `BackchannelLogoutCookieValidator.cs:299-306` | `:36-43` |
  | E8 | `LogoutTokenValidator.cs:50-55` (the empty-token check) | `:53-56` (the size check) |
  | C5, AC2.2, O2 | `README.md:170-171` | `:169-170` |

  Rerun after the fix: every citation points at the code it describes, and none is past the end of its file. In Step 5 I had checked only the spec line numbers, not the code ones.
- Probe P14 (scratch clone, `ProbeTests.cs`): sign-in with an 8,000-character token set, then sign-out with those cookies.

  ```
  P14 sign-in set 4 cookies: .AspNetCore.Cookies(28 chars), .AspNetCore.CookiesC1(4017 chars), .AspNetCore.CookiesC2(4017 chars), .AspNetCore.CookiesC3(3174 chars)
     attributes of .AspNetCore.Cookies: path=/;samesite=lax;httponly
     (the same for C1, C2 and C3)
  P14 sign-out: status 302, Location https://localhost:8090/oauth2/logout, id_token_hint present=True
     Set-Cookie: .AspNetCore.Cookies value-empty=True expires-1970=True
     (the same for C1, C2 and C3)
  P14 every issued cookie cleared: True; outbound calls (a revocation would appear here): 0
  ```

  The first run didn't compile: I'd left out `using Microsoft.AspNetCore.Authentication;`. Added, then it ran.
- Other checks, read-only:
  - `dotnet list package --vulnerable --include-transitive`: "has no vulnerable packages given the current sources" for all 4 projects.
  - Secrets in history: `git rev-list --all` gives 1 commit. Added lines matching a secret or password assignment with a value: only `options.ClientSecret = "secret";` (`AddThunderIDTests.cs:19`) and `"unused"` (`BackchannelLogoutEndpointTests.cs:37`), both test fixtures. JWT-shaped strings in added lines: 0.
  - `AddThunderIDApi` sets no `OnMessageReceived`, so the API reads bearer tokens from the `Authorization` header only.
  - Store bounds: `Prune` (`InMemoryLogoutSessionStore.cs:79`) runs on every recorded logout (`:32`). A `jti` is kept until the token's `exp` plus 5 minutes (`:54`).
- Not in the model: ASP.NET Core's cookie is self-contained, so a copy taken before sign-out stays valid until it expires (14 days, sliding). It's closed when back-channel logout is registered, because ThunderID notifies the app that initiated sign-out too (build "Step 6: Profile page and sign-out", logout token #2) and the `sid` is then refused (build "Step 11"). Recorded in section 3 of the review.
- Not measured: whether ThunderID issues this client a refresh token (the default scopes have no `offline_access`). No record has one.
- Threat rows recounted by script: 30 (28 table rows, plus 03 and 07 as one row each). 13 Mitigated (including delegated), 5 Partial, 1 Gap (05.1, no revocation), 8 N/A, 3 Residual. My hand count first said 33 rows, 6 Partial and 10 N/A; corrected before recording.
- Exploitability check (skill section 5): nothing exploitable without a precondition the model already treats as out of scope or residual. S7 needs control of the authority's HTTPS endpoint. The cookie copy needs a stolen cookie, is standard ASP.NET Core behaviour, and is closed by back-channel logout.

### Step 7: Gap report (2026-11-04)

- `docs/validation/gap-report.md` sections 2 to 6 added: summary and totals, 15 gaps by priority (G1 to G4 security, G5 to G9 surface, G10 to G15 maturity) with effort and the Partial rows to close alongside, a suggested order, the 35 N/A rows by reason, questions Q1 to Q4, and method and limits.
- Correction to section 1 (Step 2): the threat model was listed as "not versioned"; its change log says 0.3.
- Matrix recounted by script from the file: 157 rows, 48 Meets, 29 Partial, 45 Gap, 35 N/A, by section 48/51/58. The report's tables use these numbers.
- Report checked by script against the matrix: all 45 Gap rows appear in the gap tables (9 security, 16 surface, 20 maturity, each once), all 35 N/A rows appear in section 4, every `spec-matrix.md#...` link resolves to a heading, no em dashes, no bare product short form.
- Final state: `dotnet test` gives "Passed! Failed: 0, Passed: 87". `git status --short`: `M FRICTION-LOG.md`, `M RUNBOOK.md`, `?? docs/validation/`. `git diff --stat`: 156 insertions, 0 deletions. No library, sample or test code changed.
- The draft reply to the maintainer is in the scratchpad, not in the repository. Its link works only once `docs/validation/` is pushed.

### Decisions on Q1 to Q4, and the release phases (2026-11-04)

- `phases.md` added: six phases, each ending in a NuGet release, with a wait of at least two weeks between them.
- Answers (gap report, section 5): Q1, the deviation is acceptable. Q2, yes, a replayed `jti` keeps `400`. Q3, match a server-side framework SDK; the framework's idioms are acceptable; the spec wasn't written for server SDKs, so there's room to adapt. Q4, `AddThunderIDApi` and `AddScopePolicy` are acceptable, with cross-SDK parity followed when possible.
- Q3 read concretely from ThunderID's server SDKs, `thunder-id/javascript-sdks` at `f25c3526a` (2026-11-03), via `gh api`: `packages/node/src/ThunderIDNodeClient.ts`, `packages/express/src/ThunderIDExpressClient.ts`, `packages/express/src/index.ts`, `packages/javascript/src/ThunderIDJavaScriptClient.ts`.
  - Implemented: `initialize`, `reInitialize` and `getConfiguration` (inherited), `signIn`, `signOut`, `isSignedIn`, `getAccessToken`, `refreshAccessToken`, `revokeAccessToken`, `exchangeToken`, `decodeJwtToken` (inherited), `setSession`, `clearSession`, `getUser`, `handleBackchannelLogout`, and an embedded flow (Express `handleFlow`).
  - Stubs that throw "Method not implemented", not overridden by Node: `isLoading`, `signUp`, `getUserProfile`, `updateUserProfile`. `getUserSchema` doesn't exist.
  - Node's `signOut` returns the end-session URL and doesn't revoke. Kept G3 as a gap anyway: revocation before clearing local state is in the spec's security floor.
- Rows changed by script (22 rows; `apply_decisions.py` in the scratchpad), each with the decision quoted in its evidence cell. Matrix recounted: 157 rows, 58 Meets, 22 Partial, 40 Gap, 37 N/A (was 48, 29, 45, 35). By section: 23/6/7/12, 19/8/13/11, 16/8/20/14. Threat review: 14 Mitigated, 4 Partial (01.3 accepted).
- Gap report re-checked by script: all 40 Gap rows in the gap tables, all 37 N/A rows in section 4, every matrix link resolves. G7 closed (only the `Scopes` default remains, as Partial C2). G6 shrank to `reInitialize`, `exchangeToken`, `decodeJwtToken`. G14 is L2 only. G15 applies.
- Still open: Q1b (threat model entries for Data Protection and the cookie session) and Q5 (the NuGet package ID).

## Phase 1: First NuGet release (0.1.0)

Plan: 14 checkpoints (Steps 0 to 13), one stop after each. Branch `phase-1`, one commit per step, one squash-merged PR. Answers before starting (who answered is not recorded): Q5 yes (the NuGet ID `ThunderID.AspNetCore` may be used), Q1b no (no threat model entries for the cookie session). Author decisions: keep the `openid profile email` default and document it; Authors `Pasindu Balasooriya`; Apache-2.0.

### Step 0: Preflight (2026-11-04)

- Repo: `main`, clean apart from the untracked root `*.log` files, 1 commit ahead of `origin/main` (`2a97f0c`).
- `dotnet test` on net9.0: "Passed! Failed: 0, Passed: 87".
- `gh api .../actions/permissions`: `{"enabled":true,"allowed_actions":"all","sha_pinning_required":false}`.
- ThunderID `upstream/main` moved to `46a6afc35`, but `spec.md`, `threat-model.md` and `SKILL.md` are unchanged (blobs `64c9b69`, `233ecf6`, `e6ff612`), so no row needs re-judging.
- Plan corrected: Step 9 had pointed to "Step 13" for the parity check follow-up; it's Step 11.

### Step 1: .NET 10 SDK and nuget.org account (2026-11-04)

- [YOU] installed the .NET 10 SDK and created the nuget.org account. `dotnet --list-sdks`: `9.0.318`, `10.0.403`.
- nuget.org username `pasindudilshanbalasooriya`; `https://www.nuget.org/profiles/pasindudilshanbalasooriya` returns 200. The **Trusted Publishing** menu item is present, so assumption A1 holds and no API key is needed.

### Step 2: Push, branch, retarget to .NET 10 (2026-11-04)

- `git push origin main`: `d524d97..2a97f0c  main -> main`. Then `git switch -c phase-1`.
- All 4 projects `net9.0` to `net10.0`. `Microsoft.AspNetCore.Authentication.JwtBearer`, `.OpenIdConnect`, `.TestHost` and `.OpenApi` (the Api sample also had it) `9.0.20` to `10.0.14`. New `global.json`: SDK `10.0.100`, `rollForward: latestFeature`.
- `dotnet build` (SDK 10.0.403): 0 warnings, 0 errors. `dotnet test`: "Passed! Failed: 0, Passed: 87" on net10.0. No code change needed (assumption A2 holds).
- Transitive IdentityModel: 8.19.2. `dotnet list package --vulnerable --include-transitive`: none in all 4 projects.
- `git diff --stat`: the 4 csproj files, 8 insertions and 8 deletions, plus `global.json`.

### Step 3: Clock tolerance for `sub`-only logout, G2 (2026-11-04)

- `LogoutTokenValidator.DefaultClockSkew` (2 minutes), a new public field, is now the single default for the validator and for `InMemoryLogoutSessionStore`, which gains an optional `clockSkew` parameter. A `sub`-only logout now ends a session when `authTime <= iat + clockSkew` (was `authTime < iat`). The boundary is inclusive: the spec says "within the clock tolerance after it", and ending the session is the safe side. The plan had written `<`.
- `ILogoutSessionStore.IsSessionEndedAsync` doc comment states the tolerance rule, for custom stores.
- **Surprise: two existing tests encoded the old rule, not one as planned.** `InMemoryLogoutSessionStoreTests.Sub_only_logout_leaves_a_later_sign_in_alone` (kept sessions at `iat` and `iat`+5 s) became `Sub_only_logout_ends_sessions_that_began_within_the_clock_tolerance` (Theory: +0, +30, +119, +120 s are ended) and `Sub_only_logout_leaves_a_sign_in_after_the_clock_tolerance_alone` (+121 s kept). `BackchannelLogoutEndpointTests.Sub_only_logout_refuses_earlier_sessions_and_keeps_a_later_sign_in` moved its "later" sign-in from `iat`+5 s to `iat`+121 s, and gained a session at `iat`+60 s that must be refused. New: `Sub_only_logout_uses_the_configured_clock_tolerance` (30 s: +30 ended, +31 kept).
- `dotnet build`: 0 warnings, 0 errors. `dotnet test`: "Passed! Failed: 0, Passed: 92" (87, minus 1 replaced, plus 4 theory cases and 2 facts).
- Mutation, in a scratch copy made with `git ls-files`: back to `authTime.Value < logout.IssuedAt` gives "Failed! Failed: 6, Passed: 86": the 4 theory cases, the configured-tolerance test, and the endpoint test.

### Step 4: Pin the discovery issuer, G1 (2026-11-04, in progress, uncommitted)

- Correction to the Step 3 report: its commit is `b5b41e9`. The chat report said `ef1a8b9`, a hash I hadn't seen (the command output that would have shown it was cut off). It wasn't written to any file.
- **First design failed to compile:** in IdentityModel 8.19.2, `ConfigurationManager<T>.ConfigurationValidator` isn't settable after construction (CS1061). Replaced with `IssuerPinnedConfigurationManager`, a decorator around whatever `IConfigurationManager` the OIDC and JWT bearer options end up with. It's installed by post-configures registered after `AddOpenIdConnect` and `AddJwtBearer`, and throws `DiscoveryIssuerMismatchException` when `issuer` isn't exactly `Authority`. `RequestRefresh` passes through, so the bounded JWKS re-fetch is unchanged. `ValidIssuer = Authority` set on both handlers.
- `LogoutTokenValidator` turns the mismatch into a normal rejection: 400, "discovery document was rejected: its issuer is not the configured authority". Network failures still escape (500), so ThunderID retries those.
- JWT bearer rethrows configuration failures rather than answering 401, so an API with a lying discovery document fails every request with the mismatch exception. That's fail-closed, the same as discovery being unreachable. The test asserts the exception and its message.
- New `DiscoveryIssuerTests` (7): sign-in refused / redirects; logout token refused (400) / accepted (200); API token refused / accepted (200); exact comparison (`Authority + "/"` refused). Full suite: 99 passed.
- **Surprise: the suite is intermittently failing, and that's pre-existing.**
  - On this branch: 1 failing run in 25, then again at run 3. A different test each time, always an exact-reason assertion: `Token_for_another_client_is_rejected`, `Neither_sid_nor_sub_is_rejected`, `Invalid_token_is_400_with_reason_and_no_logout`, `Missing_jti_is_rejected`.
  - Experiment A, scratch worktree of `5fd775f` (before Steps 3 and 4): 2 failing runs in 40. Pre-existing on .NET 10; not yet checked on net9.0.
  - Experiment B, signing serialized with a lock: still 1 in 40, so the hypothesis "concurrent `SignData` on the shared test `RSA`" is not enough.
  - Instrumented reason (scratch only): a correctly signed token was rejected with `IDX10511: Signature validation failed. Keys tried: RsaSecurityKey, KeyId: MT-DQ4FKOMHPyzw3pdRs_q8kxGkd1lezyLktNeNTv7w`. So under parallel tests, signature verification of a valid token sometimes fails.
  - Experiment C (export the public key once) did not run: my patch script's sanity check expected 4 uses where 3 is correct, so it aborted before writing. The 80 runs that followed tested unpatched code (2 failures, including `DiscoveryIssuerTests.Api_token_is_refused_when_discovery_reports_another_issuer`) and prove nothing about C.
  - My own instrumentation mistakes on the way: one version appended to every rejection reason and broke every exact assertion, and one heredoc failed on quoting. Neither touched the repo.
- Scratch worktree `baseA` removed; `git worktree prune`; `git worktree list` shows only the repo.
- Not done yet: the cause of the flake, the Step 4 mutation check, the commit.
- Committed as `cba5527` "Pin the discovery issuer to the configured authority" (empty body, no trailer), before the flake investigation, at your request.

### Step 4a: Intermittent test failures (2026-11-04)

- Fresh scratch copy of `cba5527`, with logging added that records each rejection reason and exception without changing what `Describe` returns (`instrument2.py`; every edit asserts its target occurs exactly once, and the patched lines were read back before running).
- Experiment C, the test helper exports each public key once and serializes signing: **0 failing runs in 80**.
- Experiment D, export once only, no lock: **0 failing runs in 80** (the 80 runs went past the 10-minute command limit and finished in the background).
- With Experiment B (lock only: 1 in 40), this places the cause in the test helper: `LogoutTokens.Configuration()` and `Jwks()` called `ExportParameters` on the shared private `RSA` on every call while parallel test classes signed with it, and the exported public key intermittently failed to verify a correctly signed token (`IDX10511`).
- Not a library defect: the library never holds a private key or calls `ExportParameters`; it builds keys from ThunderID's JWKS.
- Fix, in `tests/ThunderID.AspNetCore.Tests/LogoutTokens.cs` only: the public parameters of `SigningKey` and `OtherKey` are exported once into static fields.
- Repo with the fix: **0 failing runs in 40**, 0 build warnings, "Passed! Failed: 0, Passed: 99".
- Step 4 mutation, in a fresh scratch copy of the fixed tree: the issuer comparison replaced with `if (false)` gives "Failed! Failed: 4, Passed: 95". The 4 attack tests fail (`Sign_in_is_refused_...`, `Logout_token_is_refused_...`, `Api_token_is_refused_...`, `Issuer_comparison_is_exact`); the 3 control tests still pass.
- Not checked: whether the flake also existed on net9.0, before this phase. It's moot now that the cause is found and fixed.
- Committed as `99ca9a0` "Export test signing keys once to fix intermittent failures".

### Step 5: Startup validation and the framework-setting guard (2026-11-04)

- New `SettingsValidators.cs`: `OpenIdConnectSettingsValidator` and `JwtBearerSettingsValidator` (`IValidateOptions`, ThunderID's scheme only), registered with `ValidateOnStart()` for both schemes. Checks:
  - `Authority` is an absolute https URL;
  - `ClientId` (web app) and `Audience` (API) are present;
  - `UsePkce` and `ResponseType = code` (web app);
  - `ProtocolValidator.RequireNonce` (web app);
  - `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` and `RequireSignedTokens` (both).
  - Messages start with "ThunderID: " and name the setting, never its value.
- Measured messages (throwaway scratch test):
  - empty `Authority`: ASP.NET Core's `InvalidOperationException` "Provide Authority, MetadataAddress, Configuration, or ConfigurationManager";
  - `http://` or `not a url`: ASP.NET Core's "must use HTTPS unless disabled ... RequireHttpsMetadata=false";
  - empty `ClientId`: `ArgumentException` "(Parameter 'ClientId')";
  - `UsePkce = false` through `PostConfigure`: `OptionsValidationException` "ThunderID: UsePkce must stay true; PKCE can't be turned off."
  - So the basic errors come from ASP.NET Core's own validation. `ValidateOnStart` moves them from the first sign-in to `StartAsync`. The new validators catch the weakened settings, and an `http` authority when the app sets `RequireHttpsMetadata = false` (test `Http_authority_is_refused_even_when_aspnet_core_allows_it`).
- New `StartupValidationTests` (21):
  - valid web app and API start with 0 outbound calls;
  - 4 web app and 3 API configuration errors fail at `StartAsync` with 0 outbound calls;
  - 7 web app and 4 API weakened settings, each through `PostConfigure`, fail with "ThunderID: <setting>".
- `dotnet build`: 0 warnings. `dotnet test`: "Passed! Failed: 0, Passed: 120".
- Mutations from the validation, each in a fresh scratch copy (each patch asserts its target is unique): M2 (ID token `ValidateIssuer = false`), M3 (`ValidateAudience = false`) and M4 (`RequireNonce = false`) each give "Failed! Failed: 30, Passed: 90". At the end of Step 5 of the validation, all three passed all 87 tests.
- Committed as `ff08768` "Validate configuration at startup and refuse weakened security settings".

### Step 6: Samples and README (2026-11-04)

- `samples/WebApp/appsettings.json`: `ThunderID:ClientId` is now `""` (it was the author's test client ID). `CallApi` shows and calls `BillingUrl`, built from `BillingApi:BaseUrl`, instead of a fixed `https://localhost:7102`.
- README:
  - new "Install" (`dotnet add package`, .NET 10, `0.x` note, spec v0.8, layers and the recorded deviation);
  - "Configuration" (the startup checks, the exact issuer match, default scopes and why, and every spec configuration key mapped to what the package does);
  - "Running in production" (Data Protection keys, callback request logging, back-channel logout ending copied cookies, a shared store);
  - prerequisite now .NET 10;
  - ThunderID note now names v1.1.0 (`gh release list`: v1.1.0, 2026-11-02, is the latest, and `git merge-base --is-ancestor` shows neither back-channel commit, `8fdea9d44` or `4c52caab4`, in v1.1.0 or v1.0.2);
  - the `ClientId` default row; 120 tests;
  - JWE limit names "a client configured for ID token encryption".
- The spec key list came from the pinned `spec.md` table (31 keys), not from memory.
- **Correction:** my first README draft said the deviation was "accepted by the ThunderID maintainers". Who answered Q1 isn't recorded, so that was unverified. The README now calls it a recorded deviation and links to the gap report. The same assumption was in the plan's context ("The maintainer answered") and in this section's header line ("Maintainer answers"); both are corrected.
- Checks:
  - `ThunderID__ClientId=""` (overriding user-secrets) gives "Hosting failed to start ... ArgumentException: The value cannot be an empty string. (Parameter 'ClientId')".
  - **Surprise:** with your user-secrets alone, the same failure. Your secrets file (`%APPDATA%\Microsoft\UserSecrets\a8f0f903-...\secrets.json`, key names read only) holds `ThunderID:ClientSecret` but not `ThunderID:ClientId`, which used to come from `appsettings.json`. `[YOU]`, before running the sample: `dotnet user-secrets set "ThunderID:ClientId" "<client id>" --project samples/WebApp`.
  - With `ThunderID__ClientId` set for one run: "Now listening on: https://127.0.0.1:7199", "Application started".
  - README: 0 em dashes, 0 `--` in prose, no bare product short form; every relative link and anchor resolves (`docs/backchannel-logout.png`, `docs/validation/gap-report.md`, `#configuration`, `#limits`, `FRICTION-LOG.md`, `RUNBOOK.md`).
  - `dotnet build`: 0 warnings. `dotnet test`: 120 passed.
- `tests/ThunderID.AspNetCore.Tests/LogoutTokens.cs` still uses the same string as `ClientId`, as the audience of test tokens shaped like ThunderID's. It isn't a credential; left as is.
- Committed as `adb213f` "Document configuration and production notes, and drop the sample client ID".

### Step 7: Packaging (2026-11-04)

- `LICENSE`: the Apache License 2.0 text, copied from ThunderID's own `LICENSE` (`git show upstream/main:LICENSE`, 201 lines, SHA-1 `7df05959...`, the same for both files).
- Library csproj: `PackageId`, `Version` 0.1.0, `Authors` Pasindu Balasooriya, `Description`, `PackageTags`, `PackageLicenseExpression` Apache-2.0, `PackageProjectUrl`, `RepositoryUrl`, `RepositoryType`, `PublishRepositoryUrl`, `PackageReadmeFile`, `IncludeSymbols` with `snupkg`, and `ContinuousIntegrationBuild` when `GITHUB_ACTIONS` is `true`. New package README `src/ThunderID.AspNetCore/README.md`, with absolute links only.
- Before: `dotnet pack` produced `ThunderID.AspNetCore.1.0.0.nupkg`, the default version.
- **Deviation from the plan:** I added `IsPackable=false` to both samples, then reverted it. `dotnet msbuild -getProperty:IsPackable` already gives `false` for them (the web SDK), and the warning "This project cannot be packaged because packaging has been disabled" comes from packing the whole solution, for every non-packable project that isn't a test project. The fix is to pack the library project only: `dotnet pack src/ThunderID.AspNetCore`, which CI will use.
- `dotnet pack src/ThunderID.AspNetCore -c Release`: `ThunderID.AspNetCore.0.1.0.nupkg` (23,823 bytes) and `.snupkg` (18,328 bytes), no warnings.
  - nupkg files: `ThunderID.AspNetCore.nuspec`, `README.md`, `lib/net10.0/ThunderID.AspNetCore.dll`, plus the package metadata.
  - nuspec: id, version 0.1.0, authors, license expression Apache-2.0, readme, projectUrl, description, tags, repository (git, url, branch, commit); dependencies JwtBearer and OpenIdConnect `10.0.14`; frameworkReference `Microsoft.AspNetCore.App`.
  - snupkg: `lib/net10.0/ThunderID.AspNetCore.pdb`. Its Source Link map points at `https://raw.githubusercontent.com/pasindubalasooriya/thunderid-aspnetcore/<commit>/*`.
  - The released package must be built by CI from a pushed commit, or the Source Link URLs won't resolve.
- Install check, scratch `installcheck/app`: `dotnet new webapp`; a `nuget.config` with the local pack folder and nuget.org; `dotnet add package ThunderID.AspNetCore --version 0.1.0`; `AddThunderID` and `MapThunderIDBackchannelLogout` added to `Program.cs`. `dotnet build`: 0 warnings, 0 errors. `obj/project.assets.json`: `ThunderID.AspNetCore/0.1.0`.
- **Cache cleanup:** the install put the local 0.1.0 into `%USERPROFILE%\.nuget\packages\thunderid.aspnetcore\0.1.0`. A later install "from nuget.org" (Step 13) would silently reuse it, so I deleted that folder (it was created by this check). `ls` afterwards: no such directory.
- `dotnet test`: 120 passed.

### Step 8: CI workflows (2026-11-04)

- `.github/workflows/pr-builder.yml`: on pull requests, pushes to `main`, and `workflow_dispatch`. Top-level `permissions: contents: read`; checkout with `persist-credentials: false`. Steps: `setup-dotnet` from `global.json`, `dotnet restore`, the vulnerable-package check, `dotnet build --no-restore --configuration Release -warnaserror` (the solution, so the samples too), `dotnet test --no-build`, then `dotnet pack src/ThunderID.AspNetCore --no-build` (the library only; see Step 7).
- Actions pinned by commit SHA. The tags were resolved with `gh api repos/<action>/git/ref/tags/<tag>`; both are lightweight tags pointing at commits.
  - `actions/checkout` v7.0.1 (released 2026-07-20) is `3d3c42e5aac5ba805825da76410c181273ba90b1`.
  - `actions/setup-dotnet` v6.0.0 (released 2026-07-16) is `a98b56852c35b8e3190ac28c8c2271da59106c68`. I didn't take v6.1.0, released 2026-11-03, one day before this step.
  - The input names (`global-json-file`, `persist-credentials`) were read from each `action.yml` at the pinned SHA; both run on `node24`.
- **Surprise:** `dotnet package list --vulnerable --include-transitive` exits 0 even when it finds a vulnerability. The workflow reads its `--format json` output in PowerShell and fails on any vulnerability, or on any `problems` entry, such as an audit source that can't be reached. So the check fails closed.
  - Tested on this solution: "No vulnerable packages.", exit 0.
  - Tested on a scratch project with `System.Text.Json` 8.0.4: `::error::System.Text.Json 8.0.4 (High): https://github.com/advisories/GHSA-8g4q-xg66-9fp4`, exit 1.
  - Tested on a hand-written `problems` sample: exit 1. That sample matches the shape I expect from the CLI; I haven't seen the CLI emit it.
- `.github/workflows/parity-check.yml`: `diff` against `thunder-id/javascript-sdks`'s `parity-check.yml` shows the files are identical. The reusable workflow takes optional `siblings` and `automation_authors` inputs; the defaults are right here, since the siblings are the four ThunderID SDK repos.
- `.github/dependabot.yml`: `nuget` and `github-actions`, weekly.
- Checks:
  - actionlint isn't installed, so I used the plan's fallback: all three files parse with `yaml.safe_load`, and the triggers are as intended.
  - The workflow's commands, run locally in order: restore is up to date; the build gives 0 warnings and 0 errors; the tests give 120 passed; the pack gives the `0.1.0` nupkg and snupkg in `artifacts/`, which is git-ignored and deleted afterwards.
  - No em dashes and no bare product short form in `.github/`.
- Not pushed. The first real run is Step 9.

### Step 9: Push the branch and open the PR (2026-11-04)

- `git push -u origin phase-1`: 8 commits, `5fd775f` to `e84d740`.
- `gh pr create --base main --head phase-1 --title "Prepare the 0.1.0 NuGet release"`: https://github.com/pasindubalasooriya/thunderid-aspnetcore/pull/1
- **Deviation from the plan, your decision:** the labels `parity/prs-raised` and `parity/prs-not-needed` were not created.
  - The shared parity check reads only those two labels, and fails a pull request until one of them is on it.
  - It adds `parity/prs-not-needed` itself only to bot pull requests, such as Dependabot's.
  - So on a person's pull request, without the labels, the check stays red. It doesn't block a merge unless branch protection requires it, and none is set.
- First PR builder run: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41823076901, "🔨 Build, Test & Pack", pass in 41 s. From the job log:
  - "No vulnerable packages."
  - "0 Warning(s)", "0 Error(s)"
  - "Passed! - Failed: 0, Passed: 120, Skipped: 0, Total: 120"
  - "Successfully created package .../artifacts/ThunderID.AspNetCore.0.1.0.nupkg", and the same for `.snupkg`.
  - No warning or error annotations.
- The parity check didn't run, as expected: `pull_request_target` runs the copy on `main`, and there isn't one yet.
- **Then, your decision:** remove `.github/workflows/parity-check.yml`. Without the labels it could never pass on a person's pull request. The spec matrix's R2 row (the parity check as a required check) stays a Gap; Step 11 records it, and drops the follow-up PR that was to watch the parity check.

### Step 10: Release workflow (2026-11-04)

- `.github/workflows/release.yml`: runs when a `v*` tag is pushed, or by hand (`workflow_dispatch`).
  - It publishes only on a tag push (`PUBLISH: ${{ github.event_name == 'push' }}`), so a manual run is always a dry run.
  - **Deviation from the plan:** the plan had a `dry-run` input, but a manual run can be started on a tag ref, and an input set to false would then publish. A manual run that never publishes is safer, and needs no input.
- Permissions: the workflow has `contents: read`. The job has `contents: write`, for `gh release create`, and `id-token: write`, for NuGet trusted publishing.
- The steps, in order:
  1. Checkout with `fetch-depth: 0`, so `origin/main` is there to check against, and with `persist-credentials: false`.
  2. Set up .NET from `global.json`.
  3. Check the version, before anything is published. On a tag: the tag must equal `v` + the csproj `Version`, and the tagged commit must be on `main`. Always: `docs/release-notes/v<version>.md` must exist.
  4. Build with `-warnaserror`, test, then pack the library.
  5. Tag push only: log in with `NuGet/login` v1.2.0 (`8d196754b4036150537f80ac539e15c2f1028841`, a lightweight tag on a commit), with `user: ${{ vars.NUGET_USER }}`. Its inputs and its `NUGET_API_KEY` output were read from `action.yml` at that SHA.
  6. `dotnet nuget push` the `.nupkg`, with `--skip-duplicate`. The `.snupkg` beside it is pushed too; that's NuGet's documented behaviour, not yet observed here.
  7. `gh release create` with the artifacts, `--notes-file docs/release-notes/<tag>.md` and `--verify-tag`.
- `docs/release-notes/v0.1.0.md`: the release notes. Every claim in them is in the README, or is covered by the startup and issuer tests.
- The version check, extracted from the YAML and run with GitHub's variables set by hand:

  | Case | Result |
  |---|---|
  | Tag `v0.1.0` on this branch's HEAD | "isn't on main", exit 1 |
  | Tag `v0.2.0` | "doesn't match the project version 0.1.0", exit 1 |
  | Tag `v0.1.0` on `origin/main` | exit 0, `VERSION=0.1.0` |
  | A manual run | exit 0 |
  | Release notes missing | exit 1 |

- `release.yml` parses with `yaml.safe_load`; the three publishing steps all have `if: env.PUBLISH == 'true'`. No em dashes, and no bare product short form.
- **Surprise:** the first `git add` refused `docs/releases/`; `.gitignore:26` (`[Rr]eleases/`, from the Visual Studio template, meant for build output) matches it. Renamed it to `docs/release-notes/` rather than change the ignore rules, and re-ran the missing-notes test against the new path.
- Committed as `e865b06` "Remove the parity check workflow" and `d3644b6` "Add the release workflow and the 0.1.0 release notes", then pushed to `phase-1`.
- `gh variable set NUGET_USER --body pasindudilshanbalasooriya`; `gh variable list` shows it. The PR description no longer mentions the parity check, and now describes the release workflow.
- PR builder on `d3644b6`: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41823851351, pass. "No vulnerable packages.", "0 Warning(s)", "Passed: 120".
- `release.yml` can't run until it's on `main`: GitHub only offers manual runs for workflows on the default branch. The dry run is in Step 11, after the merge.
- `[YOU]`: the nuget.org trusted publishing policy, pending.
- `[YOU]`: you reported the trusted publishing policy created ("done"). Its first form showed "At least one valid glob pattern or package must be specified"; the package field takes `ThunderID.AspNetCore`.

### Step 11: Update the records (2026-11-04)

- First, a check of the recount script (`recount.py`, scratchpad): it reproduced the matrix's recorded totals before any edit, 58 Meets, 22 Partial, 40 Gap, 37 N/A. It counts each table row by the status in its second or third cell.
- `docs/validation/spec-matrix.md`: 30 rows rewritten by script, each keeping its rule text and getting "**Phase 1:**" evidence (test names, code lines, README lines, the CI run). AC7.2 was also rewritten, with the same status and evidence as before. The rows, by new status:
  - **Meets:** BCL-3b, AC9.2, S7, AC4.8, BCL-8, F2, C6, C5, L7, AC1.1, L2, AC1.4, D5, AC6.5, R1, D6, AC7.7, K2, T1, AC7.1. K1 stays Meets, now citing `net10.0`.
  - **Partial:** R3 and T8 moved up from Gap. S1, S2, S11, AC3.3, C2, T2 and AC7.2 keep their status, with the documentation or new tests noted.
  - **Gap:** R2 stays a Gap.
- Judgement calls, stated in the rows:
  - D5 is Meets because the remaining `localhost` values are configuration defaults, not code.
  - R1 counts the .NET analyzers in `-warnaserror` as the lint; there's no format check.
  - R3 stays Partial until the release workflow has published.
  - T8 is Partial because there's no end-to-end suite yet.
- The recount after the edits, by section:

  | Section | Meets | Partial | Gap | N/A |
  |---|---|---|---|---|
  | 1 | 28 | 5 | 3 | 12 |
  | 2 | 22 | 7 | 11 | 11 |
  | 3 | 28 | 6 | 10 | 14 |
  | Total | 78 | 18 | 24 | 37 |

  157 rows. The script listed the Gap rows left: S12, AC4.5, AC4.7; the error model, the 3 operations, `signOut` and AC2.4b; O2, R2, R4, T6, T7, D1, AC6.1, AC7.5, AC7.6 and AC7.8. Each section's totals got an "After Phase 1" paragraph, and the whole-matrix table got an "After Phase 1" column.
- `docs/validation/gap-report.md`: a new section, "Status after Phase 1 (0.1.0)", before section 1. It lists each gap as closed, partly closed or documented, gives the counts above, and names what's still open. Sections 1 to 6 are unchanged, as the record of the validation.
- `phases.md`: a Phase 1 status block. It says the work is in PR #1, notes Q5 and Q1b answered and the new counts, and records the two changes from the plan (the parity caller removed, and no `IsPackable` edits).
- Style check of the added lines: no em dashes, no ` -- `, no bare product short form.
- Committed as `2b161cf` and pushed. PR builder: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41826717948, pass. `gh pr view 1`: `MERGEABLE`, `CLEAN`, 11 commits.
- `[YOU]`: you approved the squash merge.
- `gh pr merge 1 --squash --subject "Prepare the 0.1.0 NuGet release" --body "" --delete-branch` produced `0e02c3a` on `main`.
  - Its tree, `4bb90c05...`, is the same as `2b161cf`'s, so `main` holds exactly what was reviewed.
  - The commit body is empty.
  - `git ls-remote --heads origin phase-1` is empty, so the branch is deleted. I pruned the stale local tracking ref.
- The PR builder, on the push to `main`: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41827535484, pass.
- Release dry run, `gh workflow run release.yml --ref main`: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41827646036. It succeeded on `0e02c3a`.
  - Checkout, Set up .NET, Check the Version, Build, Test and Pack: success.
  - Log in to NuGet, Publish to NuGet and Create the GitHub Release: **skipped**.
  - From the log: "Version: 0.1.0", "0 Warning(s)", "Passed: 120", and both 0.1.0 packages created.
- Nothing was published: the nuget.org flat container for `thunderid.aspnetcore` gives 404, `gh release list` is empty, and there are no tags.
- Seen in passing, out of scope: `gh api .../dependabot/alerts` says "Dependabot alerts are disabled for this repository". The version updates from `dependabot.yml` don't depend on alerts.

### Step 12: Publish 0.1.0 (2026-11-04)

- `[YOU]`: "publish".
- `git tag v0.1.0 0e02c3a` (the merge commit, which is `origin/main`), then `git push origin v0.1.0`.
- Release run: https://github.com/pasindubalasooriya/thunderid-aspnetcore/actions/runs/41828692549. It succeeded on `0e02c3a`, with every step a success, the three publishing steps included. From the log:
  - "Successfully exchanged OIDC token for NuGet API key." The key shows as `NUGET_API_KEY: ***`.
  - "Pushing ThunderID.AspNetCore.0.1.0.nupkg to 'https://www.nuget.org/api/v2/package'... Created ... Your package was pushed."
  - "Pushing ThunderID.AspNetCore.0.1.0.snupkg to 'https://www.nuget.org/api/v2/symbolpackage'... Created ... Your package was pushed." So the symbols package went with the `.nupkg`, as documented; now observed.
  - `gh release create` produced https://github.com/pasindubalasooriya/thunderid-aspnetcore/releases/tag/v0.1.0. It's not a draft or a prerelease, and has assets `ThunderID.AspNetCore.0.1.0.nupkg` (23,764 bytes) and `.snupkg` (18,155 bytes).
- **Surprise:** right after the push, you saw the package under nuget.org's "Unlisted Packages", and the package page said "This package has not been indexed yet". That's the normal state between validation and indexing; nothing was changed.
- Indexing, as polled:
  - About 8 minutes after the push, the flat container returned `{"versions": ["0.1.0"]}`.
  - About 1 minute after that, the registration returned 200: version 0.1.0, `listed: True`, `published: 2026-11-04T12:22:45.417+00:00`. Search for `packageid:ThunderID.AspNetCore` gave 1 hit.
- Records, uncommitted, for the follow-up PR:
  - R3 moved to Meets with this run as evidence. Section 3 is now 29 Meets and 5 Partial; the whole matrix is 79 Meets, 17 Partial, 24 Gap, 37 N/A, recounted by script.
  - The gap report's G11 is closed. The `phases.md` status says 0.1.0 is published.
  - The Step 11 lines above give the counts as they were before this step.

### Step 13: Install from nuget.org and sign in (2026-11-04)

- ThunderID, started on your request: `.\start.ps1` in `thunderid-main\target\dist\thunderid-1.0.0-win-x64\`, as a background task of mine. "ThunderID Server started" in 540 ms. Discovery returns 200, issuer `https://localhost:8090`, `backchannel_logout_supported: True`; the console returns 200.
- Fresh app, in scratch `fromnuget/`: `dotnet new webapp -f net10.0`.
  - `dotnet nuget list source`, run in that folder, shows only nuget.org and the Visual Studio offline folder, which holds no ThunderID package. Before the install, `%USERPROFILE%\.nuget\packages\thunderid.aspnetcore` didn't exist.
  - `dotnet add package ThunderID.AspNetCore --version 0.1.0`: "GET https://api.nuget.org/v3-flatcontainer/thunderid.aspnetcore/0.1.0/thunderid.aspnetcore.0.1.0.nupkg", then "Installed ThunderID.AspNetCore 0.1.0 from https://api.nuget.org/v3/index.json". `.nupkg.metadata` records `"source": "https://api.nuget.org/v3/index.json"`, and `project.assets.json` resolves `ThunderID.AspNetCore/0.1.0`.
  - Wired up with the README snippet: `AddThunderID`, `UseAuthentication`, `MapThunderIDBackchannelLogout`, and `/Privacy` requiring sign-in. The app uses the sample's `UserSecretsId`, and its HTTPS profile is `https://localhost:7101`. `dotnet build`: 0 warnings, 0 errors.
- **Friction:** the package README's snippet has no `using` lines, and `ImplicitUsings` doesn't add `ThunderID.AspNetCore` or `ThunderID.AspNetCore.BackchannelLogout`. I added them, as the sample does. It's a docs fix for a later patch.
- `ThunderID:ClientId` set by me to the ID you gave (not a secret). `dotnet user-secrets list`, key names only: `ThunderID:ClientSecret`, `ThunderID:ClientId`.
- The run: "Now listening on: https://localhost:7101"; the home page returns 200.
- **Failure:** `/Privacy` returns 500. The OIDC handler's PAR request (`POST /oauth2/par`) got a 401 from ThunderID: `invalid_client`, "Invalid client credentials", thrown in `OpenIdConnectHandler.PushAuthorizationRequest`.
  - The same client passed PAR earlier in this project with `client_secret_post` (Step 4 and Step 7 of the original build, above).
  - So the client ID and secret pair is the likely cause: the stored secret doesn't match the client.
  - Not checked by me: I don't read secret values.
- My mistake: the PowerShell line meant to raise the authentication log level (`$env:Logging__LogLevel__Microsoft.AspNetCore.Authentication`) failed, because of the dots in the name. It doesn't affect the result; the default logging showed the error.
- `[YOU]`: in the ThunderID console, you set a matching secret for the client, and stored it with `dotnet user-secrets set "ThunderID:ClientSecret" ... --project samples/WebApp`.
- Restarted the app. `/Privacy` returned 302 to `https://localhost:8090/oauth2/authorize?client_id=WItzJCtJA0Ij77xkgHIjKg&request_uri=...`, so PAR was accepted.
- `[YOU]`: you signed in in the browser and saw "Signed in with ThunderID.AspNetCore 0.1.0 from nuget.org".
- App log: "AuthenticationScheme: OpenIdConnect was challenged", then "AuthenticationScheme: Cookies signed in". No `fail:` lines.
- **Phase 1 exit criteria met:**
  - 0.1.0 is on nuget.org, published by `release.yml`;
  - installed from nuget.org into a fresh `dotnet new webapp`, it signs in against a local ThunderID;
  - the matrix is updated.

### Step 14: Follow-up PR (2026-11-04)

- Branch `phase-1-records`, from `main` (`0e02c3a`). It carries the Steps 11 to 13 records, written after PR #1's merge.
  - The R3, G11 and `phases.md` updates from Step 12.
  - The README fix: `using ThunderID.AspNetCore;` and `using ThunderID.AspNetCore.BackchannelLogout;` added to the code snippet in `README.md` and `src/ThunderID.AspNetCore/README.md`.
- Check of the fix: the full `README.md` snippet, with its usings and wrapped in a minimal `Program.cs`, was compiled in scratch `snippetcheck/` against the nuget.org 0.1.0 package. `dotnet build`: 0 warnings, 0 errors.
- No tag and no new version. nuget.org shows the old package README until the next release.

## Phase 2: End-to-end suite and sign-out (0.2.0)

Plan approved 2026-11-04: `C:\Users\pasin\.claude\plans\how-do-we-make-adaptive-liskov.md`, Steps 0 to 12. Your decisions:
- the e2e ThunderID is built from a pinned `main` commit;
- the browser tool is Microsoft.Playwright.

You asked to run Steps 0 to 7 without stopping, and to start before the two-week wait (0.1.0 shipped today).

### Step 0: Preflight (2026-11-04)

- Repo: `main` equals `origin/main`, with 0 commits ahead or behind. PR #6 is open and mergeable, **not merged**.
- **Deviation:** `phase-2` is branched from `phase-1-records` (PR #6's branch), so the records stay continuous. After #6 is squash-merged, it gets `git rebase --onto main phase-1-records phase-2`.
- Open PRs #2 to #5 (Dependabot, test packages): out of scope, left alone.
- `dotnet test`: 120 passed.
- **Surprise:** `git fetch upstream` hung for over 5 minutes, and `timeout 90` didn't stop it (Git Bash's `timeout` doesn't stop git's child processes on Windows). `gh api` calls to GitHub were reset ("An existing connection was forcibly closed by the remote host") 3 times out of 6. The network to GitHub is unreliable today.
  - GitHub's API reports upstream `main` as `2b09d0851` (17:14 UTC). The local `upstream/main`, fetched earlier today, is `46a6afc35`.
- **ThunderID pin: `46a6afc350d85201d75027f18257492875abbf0b`** (2026-11-04, "Merge pull request #6343"), chosen because it's already local and checkable offline.
  - At the pin: `spec.md` `64c9b69`, `threat-model.md` `233ecf6`, `SKILL.md` `e6ff612`, the same as Phase 1, so no rows need re-judging.
  - `8fdea9d44` and `4c52caab4` (back-channel logout) are ancestors of the pin, and the `Dockerfile` is present.
- Docker: `docker info` gives 29.6.1, linux, 6 CPUs, 11 GB. You started Docker Desktop.

### Step 1: The pinned ThunderID image (2026-11-04, in progress)

- Source: `git -c core.autocrlf=false archive --format=tar 46a6afc35`, 167 MB, in the scratchpad. It's piped to `docker build -t thunderid-e2e:46a6afc35 -` as the build context. Nothing is extracted on Windows, which avoids long-path and CRLF problems, and ThunderID's working folder isn't touched.
- **Failure 1:** the build stopped after 25 s, because Docker couldn't reach Docker Hub: `alpine:3.23: failed to resolve source metadata ... TLS handshake timeout`. It's the same unreliable network as in Step 0.
- Retry: `alpine:3.23` pulled on the first attempt; `golang:1.27-alpine3.23` failed 3 times. You said the network would be fixed in 10 minutes, so a background job waits 10 minutes, retries the pull 5 times, then builds.
- Retry 1 (after the wait): `golang:1.27-alpine3.23` pulled on the first attempt. The build failed after 282 s in the runtime stage's `apk add` (exit 10): "fetching .../APKINDEX.tar.gz: I/O error", then "DNS: transient error".
- A connectivity check right after: the Alpine CDN, `proxy.golang.org` and `registry.npmjs.org` all gave 200.
- Retry 2: failed after 464 s in `./build.sh build linux amd64`. The Go compile ran. pnpm resolved 2233 packages, with npm requests taking about 77 s each (`[WARN] Request took 77204ms`), and then `TimeoutError: The operation was aborted due to timeout`.
- **Stopped:** three network failures in a row. The image isn't built.
- Read ahead offline from the pin, for Step 3:
  - `config/resources/*.yaml` loads `application`, `flow` and `user` at startup when `declarative_resources.enabled`;
  - `default-flow` (AUTHENTICATION) has no SSO check or session step, so the e2e flow is modelled on `console-app-flow` (`SSOCheckExecutor`, then `SessionExecutor`);
  - `default-flow` (REGISTRATION) exists, for sign-up;
  - the flow's element IDs are `input_001`, `input_002`, `action_001` and `action_signup`.
- Container networking, for Step 3:
  - back-channel delivery from the container goes to `host.docker.internal` over HTTP on 5101;
  - the native install's TLS cert (CN `localhost`, SAN `localhost` and `127.0.0.1`, valid to 2027-10-17, already trusted here) can be mounted for local runs.

**Attempt 4 (2026-11-05, after the network was reported fixed):**
- Single requests were fast first: npm 1.9 s, Docker Hub 1.1 s, the Alpine CDN 0.9 s, the Go proxy 0.4 s.
- The build failed after 354 s in `build.sh`, as in attempt 3. pnpm had downloaded 2,206 of 2,233 packages, at 6 to 40 KiB/s each under parallel load, with `ETIMEDOUT` retries; then `[23] The operation was aborted due to timeout`.
- ThunderID's Dockerfile has no cache mount for the pnpm store, so each attempt downloads every package again.

**Order changed (your decision, 2026-11-05):** Steps 5 and 7 run first, because they need no network or Docker. Then the image is built from a patched copy of the Dockerfile (option 1), and Steps 1 to 4 and 6 follow.

### Step 5: Sign-out that revokes (2026-11-05)

- `src/ThunderID.AspNetCore/ThunderIDSignOutExtensions.cs` (the plan named a `SignOut/` folder; one file at the root keeps it in the `ThunderID.AspNetCore` namespace, like `AddThunderID`):
  - `HttpContext.SignOutOfThunderIDAsync(redirectUri)` returns `ThunderIDSignOutResult` (`RevocationFailed`, `RevocationError`).
  - The order:
    1. Read the cookie session.
    2. Revoke its `refresh_token` (or its `access_token` when there's none; Step 2 settles which ThunderID issues). The request goes to `revocation_endpoint` from the issuer-pinned configuration manager, with `client_secret_post`, capped at 10 seconds.
    3. Clear the cookie.
    4. Redirect to end-session.
  - A failed revocation (unreachable, timeout, non-2xx, no `revocation_endpoint`, issuer mismatch) is logged without the token and returned in the result. It never stops the local sign-out.
  - With no session: nothing is revoked, ThunderID isn't contacted, and the response redirects to `redirectUri`. Before this, ASP.NET Core redirected to end-session without `id_token_hint`.
- **Surprise, found by a test:** when discovery fails, the OpenID Connect handler's end-session redirect throws after the cookie is cleared. In a real app, the exception handler then clears the response headers, so the cookie deletion would be lost. Now it's caught and logged, and the response redirects locally.
- Sample: `Pages/Account/SignOut.cshtml.cs` uses it. `_Layout.cshtml` shows a `TempData` notice when revocation failed.
- Tests: `SignOutTests`, 10 tests. 130 passed (120 before). The timeout test takes 10 seconds.
- Mutations, in a scratch copy:
  - clearing the cookie before revoking failed 2 tests;
  - returning before the local sign-out when revocation fails failed 6;
  - the original passed 130.
