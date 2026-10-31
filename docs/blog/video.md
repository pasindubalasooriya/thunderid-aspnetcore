# Demo video: recording guide and voiceover script

Target length: about 2 minutes 45 seconds. Record the screen with no sound first, then record the
voiceover on top of it. That way a stumble in either one doesn't cost you the other.

## Part 1: Recording guide

### Tools

| Job | Tool | Why |
|---|---|---|
| Screen recording | **Snipping Tool** (Win+Shift+R, then drag a region), or OBS with Display Capture | Both record more than one window. Win+Alt+R (Game Bar) records **only one app window**, so it would miss the second browser window and the terminal |
| GIF | ScreenToGif, Recorder | Records a region straight to GIF |
| Editing and voiceover | Clipchamp (built into Windows 11) | Trim clips, add a voice track, export 1080p |

### Before you record

1. **Check that everything is still running.** All three should be listening:
   ```powershell
   netstat -ano | findstr "LISTENING" | findstr ":8090 :7101 :7102"
   ```
   If any are missing, see [Restarting](#restarting) at the end.
2. **Screen**: 1920x1080, Focus assist (Do Not Disturb) on, taskbar notifications hidden.
3. **Browser**: Edge, **one normal profile** for both windows. Both windows must share the app's
   cookie, so don't use InPrivate for one of them. Zoom 125%. Hide the favourites bar
   (Ctrl+Shift+B). Close every other tab.
4. **Start signed out.** Open `https://localhost:7101`. If it shows Alice, click Sign out.
5. **Layout**:
   - Window A: left two thirds of the screen, on `https://localhost:7101`.
   - Window B: right third, empty tab. Copy this into its address bar but **don't press Enter yet**:
     ```text
     https://localhost:8090/oauth2/logout?client_id=WItzJCtJA0Ij77xkgHIjKg&post_logout_redirect_uri=https://localhost:7101/signout-callback-oidc
     ```
   - Terminal: under window B, running this filtered log tail. It shows only the logout lines,
     so no authorization codes or state values appear on screen:
     ```powershell
     cd C:\Users\pasin\Documents\Academics\Thunder\thunderid\thunderid-aspnetcore
     Clear-Host
     Get-Content .\webapp.log -Wait -Tail 0 | Select-String "backchannel|Back-channel|Refused|/Profile"
     ```
6. **Code clips** (scenes 2 and 4) are recorded separately in VS Code: open
   `samples/WebApp/Program.cs`, font size up (Ctrl+=) two or three times, close the sidebar
   (Ctrl+B).

### Never on screen

- The client secret: don't open `dotnet user-secrets list`, the WebApp `secrets.json`, or the
  app's credentials in the ThunderID console.
- The admin password or Alice's password in plain text (the sign-in box shows dots, which is fine).
- Raw tokens. The Claims and Call API pages show only decoded JSON, which is fine.
- The unfiltered `webapp.log`: its sign-in lines contain the authorization code.

### What to record (clips)

Record each as its own clip. Short clips are easier to line up with the voice later.

| Clip | Scene | What to capture | Length |
|---|---|---|---|
| C1 | 1 | ThunderID docs, "SDKs and tools" page, scroll slowly through the list | 15 s |
| C2 | 2 | The README "What I found" table, or the before/after `Program.cs` from the blog | 25 s |
| C3 | 3 | `docs/backchannel-logout.png` full screen | 30 s |
| C4 | 4 | VS Code: `AddThunderID(...)` and `app.MapThunderIDBackchannelLogout(...)` in `samples/WebApp/Program.cs` | 20 s |
| C5 | 5 | **The live demo, one continuous take** (steps below) | 50 to 60 s |
| C6 | 6 | `docs/blog/captures/11-both-logs.txt` in VS Code, or the GitHub repo page once it exists | 20 s |

### The live demo (clip C5)

Go slowly. Pause about two seconds after each step so the voiceover has room.

1. Start recording the whole screen (all three windows visible).
2. **Window A**: click **Sign in**, sign in as `alice`. You land on **Profile**. Hover over the
   name and avatar for a moment.
3. **Window A**: open **Claims**, and scroll until `sid` is visible. Pause. Then go back to
   **Profile**.
4. **Window B**: press Enter on the logout URL. ThunderID ends the session and window B lands on a
   blank `/signout-callback-oidc` page. That's expected.
5. **Terminal**: two lines appear, the POST to `/backchannel-logout` and
   "Back-channel logout recorded". Pause on them.
6. **Window A**: press **F5**. It goes to ThunderID's sign-in page. The terminal shows "Refused a
   cookie session ended by back-channel logout".
7. Hold for three seconds, then stop recording.

**The one mistake to avoid**: don't close window A, and don't touch it between steps 4 and 6.
Both windows share the cookie, so the first request from either window after the logout is the
one that gets refused. If you close A first, the logout happens in window B and A looks like it
didn't work (that's what happened on my first try).

**Retake**: just sign in again in window A and repeat from step 2. Each logout ends the session
for good, so every take starts clean.

### The GIF (optional)

1. Get into the state just before step 4 of the demo (Alice on Profile in window A).
2. Open ScreenToGif, choose **Recorder**, and put the frame over window A plus the terminal.
   Set 15 fps.
3. Record steps 4 to 6: the logout in window B (Alt+Tab, or keep B inside the frame), then F5 in
   window A.
4. In the editor, trim it to about 10 seconds, then **File, Save as, GIF**. Aim for under 5 MB.
5. Save as `docs\blog\img\11-demo.gif`, and tell me so I can add it to "The moment it works".

### Editing (Clipchamp)

1. Import C1 to C6 and your voiceover file. Put the clips in order on the timeline.
2. Put the voice on its own track. Cut or slow down clips to fit the voice, not the other way round.
3. For C5, split the clip at each pause and line each piece up with its lines in the script.
4. Add a title card at the start ("ASP.NET Core + ThunderID: back-channel logout") and the repo
   URL at the end.
5. Export 1080p.

### Restarting

ThunderID (only if 8090 isn't listening):

```powershell
cd C:\Users\pasin\Documents\Academics\Thunder\thunderid-main\target\dist\thunderid-1.0.0-win-x64
.\start.ps1
```

API and WebApp, each in its own terminal, from `thunderid-aspnetcore`. The WebApp needs the two
logging variables, or the log has no timestamps and no request lines:

```powershell
dotnet run --project samples/Api
```

```powershell
$env:Logging__Console__FormatterOptions__TimestampFormat = "HH:mm:ss.fff "
${env:Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics} = "Information"
dotnet run --project samples/WebApp *> webapp.log
```

## Part 2: Voiceover script

Read it at a normal pace, a little slower than conversation. Square brackets are what's on
screen; don't read them. Roughly 140 words a minute fits the timings.

### Scene 1: The question (0:00 to 0:20) [C1, docs SDK list]

> ThunderID is an open-source IAM stack, and it has twenty-six SDKs and guides. React, Vue,
> Next.js, Flutter, Spring, Go. None of them is for .NET.
>
> But ASP.NET Core already speaks OpenID Connect. So I wanted to find out: does it just work?

### Scene 2: Sign-in, after a few fixes (0:20 to 0:50) [C2, findings table or before/after code]

> Mostly, yes. Sign-in worked after a few small fixes.
>
> .NET 9 uses pushed authorization requests on its own, and it sends the client secret in the
> request body. So the app has to be registered as client secret post.
>
> ASP.NET Core asks for form post as the response mode. ThunderID only accepts query.
>
> And ASP.NET Core's nonce is a hundred and fifteen characters long. ThunderID allows sixty-four.
>
> Two lines of code and one setting in the console. Not bad.

### Scene 3: The gap (0:50 to 1:20) [C3, sequence diagram]

> The interesting part is logout.
>
> When a session ends in ThunderID, ThunderID sends a signed logout token straight to each app's
> server. That's called back-channel logout.
>
> I tested it. ThunderID sent the token within the same second. My app answered two hundred, OK,
> and kept Alice signed in. ASP.NET Core has nothing that listens for this message.
>
> So I built the listener.

### Scene 4: The library (1:20 to 1:40) [C4, Program.cs]

> It's a small library. AddThunderID sets the options ThunderID needs. One more line maps the
> back-channel logout endpoint.
>
> That endpoint checks the logout token eight different ways, and records the session as ended.
> The server can't delete a cookie in someone's browser, so it refuses that cookie the next time
> it comes in.

### Scene 5: The demo (1:40 to 2:30) [C5, live demo]

> [Profile] Here's Alice, signed in.
>
> [Claims, sid visible] This is her session ID from the ID token. A logout token names exactly
> this session.
>
> [Window B, Enter] Now I end her session somewhere else. This second window opens ThunderID's
> logout URL. My app's sign-out button isn't involved.
>
> [Terminal lines] And there it is. The logout token arrives, and the app records it.
>
> [Window A, F5] Back in the first window, I refresh. [sign-in page] And she's out. Straight to
> ThunderID's sign-in page.

### Scene 6: Numbers, limits, link (2:30 to 2:50) [C6, both logs or repo page]

> In my test, the logout token reached the app twelve milliseconds after ThunderID ended the
> session.
>
> It does have limits. The logout takes effect on the next request. The default store is in
> memory, so you'd swap in a shared one if you run more than one instance. And access tokens that
> were already issued stay valid until they expire.
>
> The code, the tests and a quickstart are on GitHub. The link is below.

### Facts behind each line

| Line | Source |
|---|---|
| 26 SDKs and guides, none for .NET | `docs/content/sdks-and-tools` on ThunderID `main` (26 entries) |
| PAR, secret in the body, client_secret_post | `captures/04-par-auth-method.txt` |
| form_post vs query | `captures/04-response-mode.txt` |
| 115 vs 64 character nonce | `captures/04-nonce-length.txt` |
| Token sent within the same second, app stayed signed in | `captures/05-probe-post.txt`, `img/05-still-signed-in.png` |
| Eight checks | Blog, "Eight ways to reject a logout token"; `captures/09-tests.txt` |
| 12 ms | `captures/11-both-logs.txt` |
| Limits | README "Limits" |
