# mod.io page content for Tournament Casting UI

Paste-ready copy for the mod.io listing. Name, summary, tags, dependencies, then the full
description (markdown). Logo is the one thing you still need to supply (mod.io requires an image).

---

## Name
Tournament Casting UI

## Summary (one line, ~250 char max on mod.io)
In-game casting overlay for Zeepkist online cups: live stat cards, head-to-head, per-round times and a click-to-cast control panel, with cup-aware player ordering for COTD and Topout.

## Suggested tags
Online, Multiplayer, Tool, UI, Casting

## Dependencies (link these on the mod.io page)
- ZeepSDK (required)
- COTDTracker (recommended: needed for live elimination-cup data and per-round times)
- PhotoDrone by Metalted (optional: enables the picture-in-picture "VS cam" on the compared player)
- PursuitZK (optional: needed for the Pursuit / Tag You're Out player-list mode)

---

## Description (markdown)

Tournament Casting UI brings the COTD casting tool into the game. It draws the same kind of player
information you see on stream (weighted ELO, peak, wins, podiums, cups, best finish, head to
head) directly over an online lobby, plus live per-round times and a click-to-cast control
panel. Built for casters who want broadcast-quality stat cards without alt-tabbing.

It renders client-side only. No server messages, no per-frame network traffic, no gameplay
interference. Lobbies stay joinable by everyone, with or without the mod.

### Requirements

- **ZeepSDK** (required).
- **COTDTracker** (recommended): supplies live elimination data and per-round times for Cup mode.
- **PhotoDrone** by Metalted (optional): if installed, the panel gains a "VS cam" toggle that
  opens a small follow-camera window on the compared player. Integration used with the author's
  blessing.
- **PursuitZK** (optional): supplies the live pursuer/target/lives data for the Pursuit (Tag You're
  Out) player-list mode. Without it, Pursuit mode falls back to plain leaderboard order.

### Quick start

Press **F4** in an online lobby to open the control panel. Left-click a player to follow them
(the camera and the stat card both lock onto them). Right-click a player to compare (head to
head). **F5** clears everything (or click Clear). Closing the panel with F4 leaves the current
card up; F5 wipes it.

### The control panel (F4)

- **Player list**: everyone worth casting, ordered and colored by the current Comp mode (see
  below). Left-click = follow, right-click = compare.
- **Stats**: which competition's numbers the cards show (COTD, Cross-comp, Petite, Eggy, Qube,
  TyO, Kerki, ZSL). It also feeds the head-to-head record. Left-click cycles forward, right-click
  back.
- **Comp**: which cup format orders the player list (Cup, Topout, Pursuit). This changes only the
  ordering and colors of the list, not the stats shown.
- **Cam sync**: bind the stat card to whoever the photomode camera follows. With it on, cycling
  players with the game's own keys updates the card automatically.
- **VS cam** (only with PhotoDrone, only while comparing): opens a follow-camera window on the
  compared player, pinned under the head-to-head card.

### Mode bar

A small always-visible bar with three buttons: **Stats**, **Times**, **Round Wins**. Stats and
Times set what a single click shows; Round Wins shows the rounds-won card for the current cup.

### Moving things

While the panel is open, each box (card, panel, mode bar) shows a small grip in its bottom-right
corner. Drag from there to reposition it. Positions are saved and restored on the next launch.
Everything scales to your resolution, so it keeps its proportions at 1080p, 1440p, 4K and beyond.

### Camera follow link

The stat card stays bound to whoever the photomode or spectator camera is following, so the
player on screen and the player in the card are always the same. Click a player in the panel and
the camera swings to them (and, by default, switches to the smooth dynamic-follow camera; set
Camera -> Follow camera mode to change or disable this). Cycle with the game's next/prev keys and
the card follows along. With nothing selected the card just tracks whoever you are watching.
Photomode starts quiet (no card pops for the auto-picked first target until you choose someone).
**Leaving photomode or the lobby always clears the overlay and closes the panel** (so a racer can
never keep another player's cam or stats on screen, and the mouse cursor is always handed back).

### Comp modes (player-list ordering)

- **Cup** (COTD elimination format): only players still in the cup. Red means alive with no time
  yet this round, yellow means alive but in the last few places (the danger zone), white means
  safe, in leaderboard order. Eliminated players and spectators are hidden.
- **Topout** (agix's TopOutTournament): ordered for click-to-follow casting. Nuisances
  (eliminated players still racing as blockers) are pinned red on top while the race is on, then
  drop once the finals take shape (a winner exists, or 2 finalists are locked). Finalists show in
  yellow, everyone else in white by championship points, and winners in green at the bottom (kept
  so you can still watch their runs). This reads the game's native leaderboard data, so it works
  even when you are not the host.
- **Pursuit** (PursuitZK / Tag You're Out): alive players only (eliminated hidden), in leaderboard
  order. Each player has a pursuer and a target and loses a life when their pursuer beats their
  time, so the colors flag danger: orange on the last life, yellow when their pursuer is currently
  beating them this round, white otherwise. Reads PursuitZK's replicated state, so it works for a
  non-host caster.

### Settings

Settings live in the BepInEx config (`BepInEx/config/com.aizpun.tournamentcastingui.cfg`) and also
show in ZeepSDK's in-game settings menu (ESC -> Settings), which draws each entry by type and gives
the hotkeys a click-to-rebind:

- **Hotkeys**: `Toggle panel` (F4) and `Clear overlay` (F5) are rebindable.
- **Follow camera mode** (default DynamicFollow): the photomode camera applied when you left-click
  a player to follow. None leaves it alone; State0-State7 force a specific raw photomode camera.
- **Disable mouse look in photomode** (off; `/overlay mouselock on|off`): while in photomode, holds
  the mouse look at zero so you can move the mouse to click the panel without swinging the camera.
  You still look and fly with a controller. Leave it off if you steer the camera with the mouse.
- **Stay in photomode** (off; `/overlay staycam on|off`): for a dedicated caster, auto re-enters
  photomode at the start of each round. It always respects the server's photomode rules and will
  not enter when a lobby disables or gates photomode, so it never affects a racer.

### Chat commands

```
/overlay panel                 toggle the control panel (same as F4)
/overlay stats <name>          show one player's stat card
/overlay h2h <name1> <name2>   side-by-side comparison
/overlay times <name>          per-round times for this cup
/overlay pool <comp>           set which stats pool the cards show
/overlay comp cup|topout|pursuit   set which cup format orders the list
/overlay cam on|off            toggle the photomode follow-camera link
/overlay mouselock on|off      disable mouse look in photomode
/overlay staycam on|off        auto re-enter photomode each round
/overlay camstate              print the live photomode camera state (diagnostic)
/overlay reset                 clear live cup times (between cups)
/overlay resetpos              move all overlay windows back on-screen
/overlay test                  draw a fixed test card
/overlay off                   hide
```

F5 clears everything on screen (same as the panel's Clear button).

Names match the current lobby roster first (by Steam ID), then the stats pool by name. Partial
names work.

### Data

Stats are fetched fresh from a public repository each time the game launches, so the numbers stay
current as competitions run. ELO, peak and rank are always COTD weighted (the fixed skill
benchmark), and ELO values are tinted by COTD tier. Cup winners show in their custom COTD name
color.

### Compliance

Client-side rendering only. No server messages, no per-frame network traffic, no gameplay
interference. The mod only ever reads and steers your own spectator camera, never anyone else's.
Leaving photomode always clears the overlay. The optional "Stay in photomode" setting (off by
default) can auto-enter your own photomode at round start, but only when the server already allows
it - it respects the lobby's photomode rules exactly as the game does and never forces it on a
racer. Lobbies remain fully joinable without the mod.
