# Lobby Overlay

In-game stat cards for Zeepkist online lobbies, toggled by chat command. Shows the same
kind of player info the COTD casting tool puts on stream (weighted ELO, peak, wins,
podiums, cups, best finish), but rendered inside the game.

Pool-based cards (Player Stats, Head-to-Head) plus a live per-round-times card. The inline
leaderboard column is planned next.

## Commands

**Mode bar:** a small always-visible bar with three separate buttons — **Stats**, **Times**,
**Round Wins** (the active one is highlighted). Stats/Times set what a single click shows; Round
Wins shows the round-wins card. It stays on screen for at-a-glance mode and can be dragged
anywhere.

**Control panel (no typing):** press **F4** (or `/overlay panel`) to open a click-to-cast
panel listing the lobby players. **Left-click a player to follow** (camera + Stats card),
**right-click to compare** (sets the H2H partner, keeping the followed player as the primary;
right-click again to un-compare). "Clear" hides everything. While the panel is open the mouse
cursor is freed so you can click (the mode bar and drag grips need the cursor free too); press
F4 again to close it and return camera control.

The menu no longer freezes the camera: while it's open, only the **mouse** photomode look
sensitivity is zeroed, so moving the mouse to click doesn't swing the camera — but you can
**keep flying with a controller** (its own look sensitivity is untouched, and movement is never
affected). Restored when you close the menu.

**Moving things:** while the panel is open, each box (card, panel, mode bar) shows a small
**grip square in its bottom-right corner** — drag from there to reposition. Positions are saved
to `BepInEx/config/lobbyoverlay_layout.json` and restored on next launch.

Chat commands (still available):

```
/overlay panel                 toggle the click control panel (same as F4)
/overlay stats <name>          show one player's stat card (pinned; ignores camera)
/overlay h2h <name1> <name2>   side-by-side comparison
/overlay times <name>          per-round times for this cup (live)
/overlay cam on|off            toggle the photomode follow-camera link
/overlay reset                 clear live cup times (between cups)
/overlay test                  draw a fixed test card (no data needed)
/overlay off                   hide
```

Names are matched against the current lobby roster first (by Steam ID), then against the
data pool by name. Partial names work (exact > prefix > contains). The `times` card matches
against names seen live in the current cup.

## Photomode follow-camera link

The Stats card stays bound to whoever the photomode / spectator camera is following, so the
player on screen and the player in the card are always the same. It works both ways:

- **Click a player** in the F4 panel and the camera swings to them (live, even with the panel
  open) while their Stats card shows.
- **Cycle players** with the game's own next/prev keys and the Stats card auto-updates to match.
- With **nothing selected**, the card just tracks whoever you're currently following. If
  photomode is off, nothing shows and nothing breaks.
- **Photomode starts quiet**: the game auto-follows someone the moment the camera turns on, but
  no card shows for that auto-pick. The card appears once you cycle to a player yourself (or
  click one in the panel).

H2H, Times and Round Wins are explicit modes that pause the binding until you press Clear; a
typed `/overlay stats <name>` is a pinned lookup the camera won't override. Toggle the whole
behaviour with the **Cam sync** button (or `/overlay cam on|off`); the state is persisted.

It only ever reads/writes the caster's own spectator camera (no network, no effect on anyone
else) and never force-enables photomode.

## Compare cam (optional, needs the PhotoDrone mod)

If Metalted's **PhotoDrone** mod is installed (integration is with his blessing), the panel
shows one extra button while a compare is active: **Compare cam: On/Off**. Toggled on, a
picture-in-picture window opens following the compared player, so the main camera can stay on
the followed player while both are on screen. Right-clicking a different player retargets the
window; leaving compare (Clear or left-click follow) closes it.

The window is styled for casting automatically: PhotoDrone's buttons are hidden, the window is
locked and pinned directly below the H2H card at the same size (drag the card and the window
follows), it renders in front of chat, and the camera uses the smooth follow mode. No presets,
modes or scripts to manage. PhotoDrone closes its windows at every round end; while the toggle
is on, the overlay simply reopens it on the next round, still pointed at the compared player.
Without PhotoDrone installed the button never appears and nothing else changes.

## Live per-round times

Built by listening to COTDTracker's in-process BepInEx log events (no file tailing) and
running the same parse logic as `casting-tool/parser.py` (`Doing eliminations with
leaderboard`, `Player X: Time: Y`, `Eliminating ...`). State auto-resets when a new cup's
first round starts after a `Winner` line; `/overlay reset` forces it. Requires the
COTDTracker mod to be running.

## Data (multi-comp)

`overlay_pool.json` is **fetched live at launch** from
`raw.githubusercontent.com/Aizpunr/Zeepkist-Lobby-Overlay/main/overlay_pool.json` (mirrors the
SoF mod), so published stats stay current without a mod update. A local `overlay_pool.json` next
to the DLL is used as an offline/dev fallback until the fetch lands (applied on the main thread
in `Update`, never mutated off the Unity thread). It is keyed by Steam ID and built by
`build_overlay_pool.py` from **each comp's native ranking data** (COTD, Petite/PCDJ, Eggy, Qube,
TyO, Kerki, ZSL), resolved to Steam ID via the COTD `players.json` bridge. Per comp it stores
wins / best / podiums / cups +
per-event finish positions (for h2h). Wins/best/podiums/cups are **derived from finish
positions** (not the comps' aggregate fields, which are inconsistent — e.g. TyO/Qube count tags
/rounds). Cross-comp is aggregated in the mod, not stored.

**ELO + peak + rank are always COTD weighted** (the fixed skill benchmark) — never swapped.
The **rank** is the position among *qualified* players only (mirrors the COTD site: 6+ cups, or
any podium, or 4+ cups with a recent appearance), so one-cup players don't inflate it. The ELO
values on the Stats card are tinted by COTD tier (Gold 1600+, Master 1700+, Pro 1800+,
Legend 2000+); below 1600 stays neutral.

Two cycle buttons drive the panel:

- **Stats** (`/overlay pool <x>`) chooses which comp's numbers the cards show (wins/best/podiums/
  cups) and which comp feeds the H2H mutual record. Order: COTD, Cross-comp, then the rest.
- **Comp** (`/overlay comp cup|topout|pursuit`) chooses which cup *format* orders the player
  list. It only changes the ordering/colors of the player list, not the stats shown.

Both are **set once and persisted** to `BepInEx/config/lobbyoverlay_layout.json`. On the cycle
buttons, **left-click goes forward and right-click goes back** (to undo an overshoot).

### Player-list ordering per Comp

- **Cup** (COTD-style, default): only players still alive. Red = alive with no time yet this
  round, yellow = alive but in the last *elim/round* places (the bubble), white = the rest, in
  leaderboard order. Eliminated players and spectators are dropped. Driven by COTDTracker's log
  events + the live leaderboard.
- **Topout** (agix's TopOutTournament): finalists shown yellow, everyone else white, winners
  sent to the bottom, all ordered by championship points (highest first). Nuisances (eliminated
  but still on track as blockers) sit red at the very bottom while the cup is live — still worth
  a glance — then drop once the cup is decided (a winner exists, or 3 finalists are set). Reads
  the game's native custom-leaderboard data the host pushes (`GetLeaderboardOverride` text +
  replicated `ChampionshipPoints`), so it works for a non-host caster without the results file.
- **Pursuit**: placeholder (uses the Cup/leaderboard ordering) until its own format is built.

Regenerate the pool with:
```
python build_overlay_pool.py
```
It reads each comp's committed output, so run it after those update (or wire into the
cross-comp `zeepkist holistic/refresh.py` tail). Not the cross-comp `allcompdata.json` — that
drops troll cups, so its counts don't match the public sites.

## Build

Game must be closed (BepInEx locks the DLL).

```
build.bat
```

Uses the Framework C# 5 `csc.exe` (no .NET SDK needed). Output `bin\LobbyOverlay.dll`,
auto-copied with `overlay_pool.json` to `BepInEx\plugins\LobbyOverlay\` (the JSON for local/dev
testing; the published release zip is DLL-only and fetches the pool from the repo). After
regenerating the pool, push `overlay_pool.json` to the repo so live users get the new numbers.
See `PUBLISH.md` for the full release steps and `MODIO_PAGE.md` for the listing copy.

## Rendering note

Drawn with Unity IMGUI (`OnGUI`) rather than ZeepSDK's Imui: only the C# 5 compiler is
available here and Imui's API is `Span<char>`-based. IMGUI is C#-5-friendly, always renders,
and needs no SDK GUI hook. ZeepSDK is still used for chat-command registration.

Text and boxes auto-scale to the screen: everything is sized relative to 1080p
(`scale = Screen.height / 1080`), so the overlay keeps the same proportions at 1440p, 4K, etc.
Drag positions stay in real pixels (each player sets their own), so only sizes scale.

## Rules compliance

Client-side rendering only: no server messages, no per-frame network traffic, no gameplay
interference. Lobbies remain joinable without the mod.
