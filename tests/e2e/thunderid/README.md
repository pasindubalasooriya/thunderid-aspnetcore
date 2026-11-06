# ThunderID for the end-to-end tests

A throwaway ThunderID at `https://localhost:8091`, built from a pinned ThunderID commit and provisioned entirely from the files in this folder. The tests in `tests/ThunderID.AspNetCore.E2E` sign in to it.

> **Test-only credentials.** The admin password in `compose.yaml`, and the client secret and user password in `bootstrap/10-e2e.yaml`, are fixed values for this container only. Never reuse them anywhere else.

## What's here

| File | What it does |
|---|---|
| `compose.yaml` | Runs ThunderID's `setup.sh` (which imports the bootstrap resources), then `start.sh`, with fresh volumes |
| `deployment.yaml` | The image's own `deployment.yaml` with `public_url` and the passkey origin moved to port 8091, and back-channel delivery to private addresses allowed, since the test app is reached through `host.docker.internal` |
| `bootstrap/10-e2e.yaml` | The e2e sign-in flow (with SSO check and session, so tokens carry `sid`), a registration flow for sign-up, the `e2e-webapp` client (authorization code, refresh and token exchange grants), the `e2e-user` user, and the `https://billing.e2e.example` resource server that token exchange targets |

The app registration expects the test app on `https://localhost:7201`, and delivers back-channel logout to `http://host.docker.internal:5201/backchannel-logout`.

## Run it locally

1. Build the image from the pinned commit (`46a6afc35`) of a ThunderID checkout:

   ```bash
   git -C <thunderid-checkout> archive 46a6afc35 | docker build -t thunderid-e2e:46a6afc35 -
   ```

2. Start ThunderID. `E2E_TLS_DIR` must hold a `server.cert` and `server.key` for `localhost` that this machine trusts, because the test app validates ThunderID's certificate:

   ```bash
   E2E_TLS_DIR=<folder> docker compose up -d --wait
   ```

3. Install the browser once, then run the tests:

   ```bash
   cd ../../ThunderID.AspNetCore.E2E
   dotnet build
   pwsh bin/Debug/net10.0/playwright.ps1 install chromium
   dotnet test
   ```

4. `docker compose down -v` removes the container and its data.

Two optional environment variables for the tests:

- `E2E_BACKCHANNEL_URL`: where the test app listens for back-channel logout. The default, `http://localhost:5201`, works with Docker Desktop. On Linux, `host.docker.internal` is the Docker bridge, so use `http://0.0.0.0:5201`.
- `E2E_TRACE_DIR`: when set, each browser context records a Playwright trace there.

`.github/workflows/nightly-e2e.yml` runs the same steps every night, with a throwaway certificate it adds to the runner's trust store.
