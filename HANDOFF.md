# Handoff — Bina AI Copilot: bina-ai login + AI credits

Status as of this session. Branch: `feat/ai-credits` (plugin), `feat/auth-integrated` (landing page).

## ✅ Working now
- Browser OAuth login (authorization-code + PKCE, loopback redirect) against **bina-ai**; register + sign-in end to end.
- After login: project picker is skipped → defaults to a **Demo** project, session saved, **Copilot pane auto-opens**.
- **AI credits**: `GET /credits/balance` is wired into the plugin — a one-time login chat message **and** a live header badge that refreshes (ticks down) after each prompt; clears on sign-out.
- **"Please sign in"** gate when an unauthenticated user sends a Copilot prompt (instead of a backend 401).

## 🔴 TAC / SMS OTP — main backend task
OTP is currently a **dev mock**: `POST /auth/otp/send` returns `{"sent":true,"dev_code":"######"}` and **no SMS is sent**.

- **Frontend is already production-ready** — `signup.astro` shows the dev code *if present*, otherwise "Code sent to your phone." **No frontend change needed.**
- **Backend (bina-ai) to-do for real SMS:**
  1. Integrate an SMS provider (Twilio / AWS SNS / a Malaysian SMS gateway) and actually send the OTP in `/auth/otp/send`.
  2. **Stop returning `dev_code`** in production (gate it behind a DEV/STAGING flag only).
  3. Confirm OTP **storage, expiry, and rate-limiting**. `/auth/register` already verifies the `otp`.

## 🟠 Other backend items to confirm
- **CORS**: the login/signup pages call bina-ai cross-origin. The **`OPTIONS` preflight for `/auth/*` must be answered** — it was being stalled on a corporate network during testing. Ensure prod CORS allows the real web origin and handles preflight.
- **Token refresh**: `BinaOAuthClient.RefreshAsync` posts to `/auth/refresh` — verify refresh-on-expiry is actually wired so sessions don't silently die and force re-login.

## 🟡 Plugin robustness (C# side)
- **Login can freeze Revit indefinitely**: `BinaOAuthClient.InteractiveLoginAsync` waits on `listener.GetContextAsync()` with **no timeout**, on the Revit UI thread (blocked via `.GetAwaiter().GetResult()` in `BrowserLoginCommand`). If the browser handoff never completes, Revit hangs forever — **add a timeout / cancellation**.
- **`SecureTokenStore` is effectively broken**: it saves with `CRED_PERSIST_LOCAL_MACHINE` (needs admin) so the write **silently fails** (Windows Credential Manager was empty after login), and **nothing ever calls `SecureTokenStore.Load()`**. Today the access token only persists in `config.json` (plaintext, mirrored on login). Decide: fix the secure store + load it on startup, or accept config-only and drop the secure store.
- **Project picker** is hardcoded to `ProjectId=1 / "Demo"` in `BrowserLoginCommand` — revisit when bina-ai has a real project model (projects are a legacy bina-be concept).

## ⚙️ Local-only — do NOT treat as product work
- The **dev proxy** in `plugins-landing-page` (commit `953ecc2`) is **opt-in** (`PUBLIC_DEV_API_PROXY=true`) and **inert by default** (proxy endpoints 404, pages use the normal `api` base). It exists only to bypass a corporate proxy/AV that broke CORS preflight + tunnel TLS on the Windows test machine. The real fix is backend CORS; a normal network won't need it.

## Endpoints in play (bina-ai)
| Endpoint | Used by | Notes |
|----------|---------|-------|
| `POST /auth/otp/send` | signup page | returns `dev_code` in dev; **needs real SMS** |
| `POST /auth/register` | signup page | verifies `otp`, returns one-time `code` |
| `POST /auth/login` | login page | returns one-time `code` |
| `POST /auth/token` | plugin (`BinaOAuthClient`) | code + `code_verifier` → access/refresh tokens |
| `POST /auth/refresh` | plugin | refresh token rotation (verify wired) |
| `GET /auth/me` | plugin | display name |
| `GET /credits/balance` | plugin | `{ period, used, monthly_limit, unlimited, remaining, resets_at }` |
