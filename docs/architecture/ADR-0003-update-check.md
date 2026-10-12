# ADR-0003: The opt-in update check

- **Status:** Accepted
- **Date:** 2026-10-10, amended 2026-10-11 for the daily check

## Context

Mossmint makes no network calls in normal use. People who install it can only update by downloading each new `Mossmint-Setup.exe` by hand. Velopack already installs Mossmint and can update it in place, but only if the app asks it to.

## Decision

- **Manual, plus an optional daily check.** Settings → Updates → **Check now** checks once. *Check once a day while Mossmint is open* is off unless the user turns it on. When it's on, Mossmint looks at start-up and then hourly, but asks GitHub at most once a day after a check succeeds; a failed check (offline, rate-limited) is tried again an hour later.
- **A quiet strip, never a notification** (the test user's pick). A newer version shows a strip at the top of the unlocked app, *Mossmint 0.7.0 is ready*, with **Download** and **Not now**. *Not now* hides it until a newer version. It never takes keyboard focus when it appears, and Mossmint never raises a Windows notification.
- **The preferences live outside profiles.** Whether the daily check is on, when it last succeeded, and the version put off with *Not now* are kept in `updates.json` in the data root. They hold no financial data and need no unlock.
- **GitHub Releases only,** through Velopack's `GithubSource` for `hazeliscoding/mossmint`, with no token. Mossmint never uses Velopack's hosted service. A build with a pre-release version (`0.4.0-beta.1`) also looks at pre-releases, which carry the `win-beta` channel.
- **Three separate steps: check, download, restart.** The restart waits for any running backup or import to finish, and it locks the profile before Mossmint exits. An update that's downloaded but not applied installs the next time Mossmint starts, which is Velopack's default.
- **A failure is reported, never fatal.** If the PC is offline, GitHub is rate-limiting, or a download breaks, the Updates card says so and everything else keeps working.
- **The network code is Velopack's.** No Mossmint assembly references `System.Net.*`, so the architecture test still holds. The offline smoke test visits Settings with the real Velopack service and sees no network activity, because nothing happens until the button is pressed or the daily check is turned on.

## What GitHub sees

Checked on 2026-10-10 by logging every request Velopack 1.2.161 makes:

| Step | Request |
|---|---|
| Check now | `GET https://api.github.com/repos/hazeliscoding/mossmint/releases?per_page=10&page=1` |
| Check now | `GET https://github.com/hazeliscoding/mossmint/releases/download/v<latest>/releases.win.json` |
| Download | The update package (`.nupkg`, full or delta) from the same release |

- The daily check makes the same two requests as **Check now**, at most once a day once one succeeds. Its hourly retries after a failure stay far below GitHub's limit.
- Every request carries the User-Agent `Velopack/1.2.161` and comes from the user's IP address.
- No installed version, install ID, operating system, profile or financial data is sent. The check is the same for every user.
- GitHub can tell that someone at that IP address checked for Mossmint updates. A delta download also shows roughly which version they had.
- Unauthenticated requests are limited to 60 an hour per IP address.

Velopack's requests can change between versions, so log them again whenever Velopack is updated.

## Trust

An update is only as trustworthy as the GitHub release it comes from. Requests use TLS, and Velopack checks each package's SHA-256 against `releases.win.json` from the same release. That's the same trust as downloading `Mossmint-Setup.exe` by hand. Draft releases aren't visible without a token, so an update reaches users only after the owner publishes it. Code signing (M6) will add a check that doesn't depend on GitHub alone.

## Consequences

- 0.3.0 has no update check. Anyone on 0.3.0 has to install the first release that has one by hand, once.
- Mossmint can't push an update. People who never press **Check now** or turn on the daily check stay on their version until they update by hand.
- With the daily check on, GitHub can tell that someone at that IP address runs Mossmint most days.
