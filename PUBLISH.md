# Publishing Tournament Casting UI (first release: 1.0.0-beta.1)

The mod fetches its stats pool live from a GitHub repo (like the SoF mod), so **the GitHub repo
must be live before the mod.io entry is useful** (a DLL-only install with no repo = empty stats).
Do step 1 first, then step 2.

The BepInPlugin GUID `com.aizpun.tournamentcastingui` LOCKS on first mod.io publish (renamed from
`com.aizpun.lobbyoverlay` before this first release). The in-attribute version is `1.0.0` (BepInEx
needs a System.Version-safe string); the mod.io release is labelled `1.0.0-beta.1`.

## 1. GitHub repo - DONE (2026-06-14)

Repo: **`Aizpunr/zeepkist-casting-UI`** (public, branch `main`). The mod fetches:
`https://raw.githubusercontent.com/Aizpunr/zeepkist-casting-UI/main/overlay_pool.json` (verified live).
Pushed: `overlay_pool.json`, `src/Plugin.cs`, `build.bat`, `README.md`, `.gitignore`. The git repo
lives in this folder (`.git`), remote `origin` set.

**Refreshing stats later:** re-run `python build_overlay_pool.py`, then
`git add overlay_pool.json && git commit -m "pool refresh" && git push`. Every user gets the new
numbers on their next launch. No mod re-upload needed.

## 2. mod.io entry

1. New mod under the Zeepkist game on mod.io.
2. Name / summary / tags / description: copy from `MODIO_PAGE.md`.
3. Logo: supply an image (mod.io requires one).
4. Dependencies: link **ZeepSDK** (required), **COTDTracker** (recommended), **PhotoDrone** (optional), **PursuitZK** (optional, for Pursuit/TyO mode).
5. Upload the release file: `TournamentCastingUI-<version>.zip` (DLL only).
6. Set visible / submit for review.

**Current state (live):** mod.io entry created (mod id 6149473, hidden + pending review). Active
release is **1.0.0-beta.3** (Tier 1 perf fix, camera-follow default + dropdown, rebindable hotkeys,
Pursuit/TyO list mode, leave-lobby + cursor cleanup). Bump the in-source version string and re-run
the upload to publish a new release; no re-link of dependencies needed.

## 3. Review

Ping **#ask-a-mod** on the Zeepkist Discord for the mod to be approved (cannot be done from here).
