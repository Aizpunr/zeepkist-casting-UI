using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZeepSDK.Chat;
using ZeepSDK.ChatCommands;
using ZeepSDK.Controls;
using ZeepSDK.Level;
using ZeepSDK.Multiplayer;
using ZeepSDK.PhotoMode;
using ZeepSDK.Racing;
using ZeepSDK.Storage;
using ZeepkistClient;

namespace LobbyOverlay
{
    // Milestone 1: pool-based overlay cards (Stats + H2H), drawn with Unity IMGUI.
    // Rendering is raw OnGUI() because only the C# 5 csc.exe is available on this
    // machine and ZeepSDK's Imui API is Span<char>-based (uncompilable here).
    // Pure local rendering: no server comms, no Update() traffic -> within mod rules.
    // GUID changed from com.aizpun.lobbyoverlay before first publish (it locks on publish). The
    // BepInEx config file is GUID-derived -> BepInEx/config/com.aizpun.tournamentcastingui.cfg.
    [BepInPlugin("com.aizpun.tournamentcastingui", "Tournament Casting UI", "1.0.0")]
    [BepInDependency("ZeepSDK")]
    public class Plugin : BaseUnityPlugin, ILogListener
    {
        public static Plugin Instance;

        private enum Mode { None, Test, Stats, H2H, Times, RoundWins }
        private int elimCount;   // players eliminated per round (auto from COTDTracker, or manual)
        private Mode mode = Mode.None;
        private Stat target1;
        private Stat target2;
        private string liveTarget;   // resolved live name for the Times card

        private Dictionary<string, Stat> pool = new Dictionary<string, Stat>();
        private string poolVersion = "?";
        // Live stats: fetched from the public repo each launch (always fresh), with a local
        // overlay_pool.json next to the DLL as offline/dev fallback. Mirrors the SoF mod.
        private const string POOL_URL =
            "https://raw.githubusercontent.com/Aizpunr/zeepkist-casting-UI/main/overlay_pool.json";
        private volatile string pendingPoolJson; // fetched JSON awaiting main-thread apply

        // ---- Showdown: teams + season map pool (hand-authored, separate from overlay_pool.json so a
        // pool rebuild can never clobber it). Same load pattern: local file first, repo fetch on top.
        private const string SD_URL =
            "https://raw.githubusercontent.com/Aizpunr/zeepkist-casting-UI/main/showdown_pool.json";
        private volatile string pendingSdJson;

        // ---- GTR world record for the current map (graphql.zeepki.st, fetched on a bg thread) ----
        private string wrUid;                    // level UID the cached/in-flight WR is for (main-thread cache key)
        private string wrHolder = "";            // WR holder name ("" = none/failed)
        private string wrTime = "";              // WR time, "M:SS.mmm" ("" = none/failed)
        private bool wrFetching;                 // a fetch is in flight (main-thread flag; blocks re-fire)
        private volatile bool wrPending;         // bg thread finished; main thread should commit the result
        private volatile string wrPendingUid;
        private volatile string wrPendingHolder;
        private volatile string wrPendingTime;

        // ---- Live cup state (ported from casting-tool/parser.py CupState) ----
        private int liveRound;
        private bool pendingRound;
        private bool cupOver;
        private readonly Dictionary<string, string> roundTimes = new Dictionary<string, string>();
        private readonly HashSet<string> eliminatedLive = new HashSet<string>(); // out of the cup
        private readonly Dictionary<string, List<RoundTime>> playerRoundTimes =
            new Dictionary<string, List<RoundTime>>();

        // ---- Multi-map cup support (Petite = 2 maps, Kerki = 3, alternating between rounds) ----
        // Each stored RoundTime is tagged with the level UID it was raced on, so the "Best" line and
        // the Times view can scope to the map currently up instead of blending both maps together.
        private string curRoundMapUid;                                    // map of the round accumulating now (null until first time seen)
        private readonly Dictionary<string, string> mapNames = new Dictionary<string, string>(); // uid -> human-readable map name
        private readonly List<string> mapOrder = new List<string>();      // uids in first-seen order (grouping order)

        // ---- Layout (drag-to-move while panel is open; persists to disk) ----
        private Rect cardRect = new Rect(24f, 130f, 320f, 290f);
        private Rect panelRect = new Rect(-1f, 130f, 280f, 440f);
        private Rect barRect = new Rect(-1f, 0f, 0f, 0f); // mode bar (Stats/Times/Round Wins); x<0 = default pos
        private Rect cardDrawRect;                          // actual drawn rect of the current card (for its drag grip)
        private int draggingId = -1; // 0 = card, 1 = panel, 2 = bar
        private Vector2 dragOffset;

        // ---- Click-to-cast control panel ----
        private bool showPanel;
        private bool stayPanelWanted;        // was the panel up when photomode auto-exited? (reopen it on stay-in-photomode re-entry)
        public IModStorage Storage { get; private set; } // ZeepSDK mod-scoped JSON store (Plugin.Instance.Storage)
        private bool timesIntent;             // single-select shows Times instead of Stats
        private readonly List<Sel> selected = new List<Sel>();
        private Vector2 panelScroll;
        // Panel rows are rebuilt on the 5 Hz Update poll, never in OnGUI. Unity fires OnGUI several
        // times per frame (Layout + Repaint + one per input event; moving the mouse produces one every
        // frame), and BuildPanelRows does a reflection call per player plus a fresh list+row allocation
        // each pass - which is what made the overlay eat frames on strong machines.
        private List<PRow> panelRowsCache;
        private string barBestLine;           // mode-bar cup-best line, refreshed on the same poll
        private string barWrLine;             // mode-bar world-record line
        private CursorLockMode prevLock;
        private bool prevCursorVisible;
        private bool cursorSaved;
        private float savedMouseSens = -1f;  // photomode MOUSE look sensitivity, zeroed while frozen
        private float savedCtrlSens = -1f;   // photomode CONTROLLER look sensitivity, zeroed only on mouse-move frames
        private bool mouseFreezeWarned;      // logged the "can't reach settings" warning once

        // ---- Config (BepInEx; persists to BepInEx/config/com.aizpun.lobbyoverlay.cfg) ----
        // Both default OFF. Mouse-look freeze is opt-in (it can fight the free-cam); stay-in-photomode
        // only makes sense for a dedicated caster, never on for a racer.
        private ConfigEntry<bool> cfgEnabled;          // master on/off; OFF by default and re-armed OFF each launch (never remembered)
        private bool enabledApplied;                   // last-reconciled enabled state (edge-detect for the on/off teardown)
        private ConfigEntry<bool> cfgStayInPhotomode;  // auto re-enter photomode each round (server-gated)
        private ConfigEntry<bool> cfgFreezeMouse;      // freeze MOUSE look while the panel is open (default ON)
        private ConfigEntry<KeyCode> cfgKeyPanel;      // toggle the control panel (default F4)
        private ConfigEntry<KeyCode> cfgKeyClear;      // clear everything (default F5)
        private ConfigEntry<FollowCam> cfgFollowCamState; // photomode camera mode forced on left-click follow (None = leave alone)

        // ---- Stay-in-photomode auto-enter ----
        private EnableFlyingCamera2 efcRef;  // cached photomode toggle component
        private bool stayEnterPending;       // a round just started: try to (re)enter photomode

        // ---- Photomode follow-camera link (bind the Stats card to who the camera follows) ----
        // FlyingCameraScript is only an active object while the fly/spectator camera is on, so its
        // presence is our "are we in the camera" signal (no reliance on isPhotoMode semantics).
        private bool camLink = true;         // master toggle (persisted)
        private bool statsPinned;            // /overlay stats <name> pinned an arbitrary player
        private string shownFollowSid;       // last steam id applied from the camera (change-detect)
        private FlyingCameraScript fcRef;    // cached photomode camera
        private MethodInfo updateListMI;     // FlyingCameraScript.UpdateZeepkistList(bool) (private)
        private float camPollAccum;          // throttles the camera poll

        // Clear/off pause the camera auto-track until the camera moves to a DIFFERENT player
        // (or something is clicked); otherwise cam sync instantly repaints the card just cleared.
        private string clearHoldSid;         // sid held at Clear time ("" = was following nobody)
        private bool inPhotoMode;            // event-driven (PhotoModeApi enter/exit); gates the cam/drone poll
        private bool holdArmPending;         // photomode just opened: hold its auto-picked target
        // Camera-mode memory: carry the caster's photomode camera (e.g. the trailcam/dynamic follow)
        // across rounds. Captured ONCE when leaving photomode (your last camera choice), then re-applied
        // once on the next entry - no per-frame recording, so it costs nothing while you're casting.
        private int lastCamState = -1;       // camera mode captured on the last photomode exit (-1 = none yet)
        private bool lastCamAlt;             // its alternateCameraState flag
        private bool camRestorePending;      // re-apply the remembered mode on this photomode entry (one-shot)

        // ---- PhotoDrone bridge (optional Metalted mod; reflection so it stays a soft dep) ----
        // One picture-in-picture window following the H2H compare partner. PhotoDrone destroys
        // all drones on round end / photomode exit / disconnect, so droneOn is caster INTENT:
        // the 5 Hz poll lazily re-creates the drone whenever it should exist but doesn't.
        private const string DroneId = "lobbyoverlay_h2h";
        private bool droneOn;                // toggle button state (session-only)
        private string droneSid;             // steam id the drone currently targets (change-detect)
        private bool droneChecked;           // reflection lookup done (once)
        private bool droneAvailable;         // PhotoDrone installed + API matched
        private MethodInfo pdCreateMI;       // DroneCommand.CreateDrone(string, DronePreset, bool)
        private MethodInfo pdGetMI;          // DroneCommand.GetDrone(string)
        private MethodInfo pdDestroyMI;      // DroneCommand.DestroyDrone(PhotoDrone)
        private FieldInfo pdPlayersListFI;   // DroneCommand.players (List<PlayerData>; the
                                             // GetPlayers() METHOD returns name strings - trap!)
        private MethodInfo pdSetTargetMI;    // PhotoDrone.SetTarget(PlayerData)
        private FieldInfo pdPlayerField;     // PlayerData.zeepkistNetworkPlayer
        private FieldInfo pdFollowModeFI;    // PhotoDrone.followMode (optional; forces Smooth)
        private object pdSmoothVal;          // FollowMode.Smooth boxed enum value
        // Window styling (all optional - missing members just keep PhotoDrone's defaults):
        private FieldInfo pdDroneUIField;    // PhotoDrone.droneUI (DroneWindowUI)
        private MethodInfo pdSetVisibilityMI;// DroneWindowUI.SetVisibility(bool) - hides the buttons
        private MethodInfo pdSetLockedMI;    // DroneWindowUI.SetLocked(bool) - no accidental drags
        private FieldInfo pdCanvasField;     // DroneCommand.canvas (static; ships sortingOrder -1)
        private ConstructorInfo pdPresetCtor;// DronePreset(PhotoDrone, bool usePixels)
        private MethodInfo pdPresetSetX, pdPresetSetY, pdPresetSetW, pdPresetSetH;
        private MethodInfo pdApplyPresetMI;  // PhotoDrone.ApplyPreset(DronePreset) - moves/sizes window
        private Rect droneAppliedRect;       // last window rect we applied (change-detect)
        // VS cam window rect. x<0 = "never placed": it follows under the H2H card (the old pinned
        // behaviour, a sane default). The first drag or resize freezes it into a free-floating window
        // with its own position AND aspect (persisted) - it no longer teleports when the card moves.
        private Rect camRect = new Rect(-1f, 0f, 0f, 0f);
        private object droneRef;             // drone instance we styled last (new ref = restyle)
        private readonly HashSet<string> droneLogged = new HashSet<string>(); // once-only log lines

        private class Sel
        {
            public string Sid;
            public string Name;
            public Sel(string sid, string name) { Sid = sid; Name = name; }
        }

        // ---- Live current-map leaderboard (for h2h "fastest lap") ----
        private FieldInfo lbUpdatedField;
        private FieldInfo lbBackingField;
        private bool lbSubscribed;
        private readonly Dictionary<ulong, LbEntry> board = new Dictionary<ulong, LbEntry>();

        private class LbEntry
        {
            public int Position;
            public string Time;
        }

        private class RoundTime
        {
            public int Round;
            public string Time;
            public string Uid;   // level UID this time was raced on (null = untagged/legacy)
            public RoundTime(int round, string time) : this(round, time, null) { }
            public RoundTime(int round, string time, string uid) { Round = round; Time = time; Uid = uid; }
        }

        // Lazily-built IMGUI styles (GUI.skin is only valid inside OnGUI).
        private bool stylesReady;
        private GUIStyle boxStyle;
        private GUIStyle headerStyle;
        private GUIStyle labelStyle;
        private GUIStyle bestStyle;   // white "Best time:" line under the mode-bar buttons
        private GUIStyle sdTagStyle;  // Showdown team tag (big, tinted by team colour)
        private GUIStyle sdScoreStyle;// Showdown "1 - 0" between the tags
        private GUIStyle sdSubStyle;  // Showdown round/map line under the tags
        private GUIStyle sdNameStyle; // Showdown racer-name column
        private GUIStyle valueStyle;
        private GUIStyle nameLeftStyle;
        private GUIStyle nameRightStyle;
        private GUIStyle centerStyle;
        private GUIStyle valLeftStyle;
        private GUIStyle valRightStyle;
        // Number hierarchy: headline / default (valueStyle) / demoted. See EnsureStyles.
        private GUIStyle valueBigStyle;
        private GUIStyle valueSmallStyle;
        private GUIStyle labelSmallStyle;
        private GUIStyle valBigLeftStyle;
        private GUIStyle valBigRightStyle;
        private GUIStyle valSmallLeftStyle;
        private GUIStyle valSmallRightStyle;
        private GUIStyle buttonStyle;
        private GUIStyle buttonSelStyle;
        private Color goodColor = new Color(0.55f, 1f, 0.6f);
        private Color dimColor = new Color(0.62f, 0.66f, 0.74f);
        private Color elimColor = new Color(1f, 0.42f, 0.42f);   // red: alive, no time yet
        private Color bubbleColor = new Color(1f, 0.84f, 0.36f); // yellow: at risk (last N timed) / TyO in-danger
        private Color safeColor = new Color(0.90f, 0.92f, 0.96f);// white: safe
        private Color outColor = new Color(0.42f, 0.45f, 0.50f); // grey: eliminated from the cup
        private Color lastLifeColor = new Color(1f, 0.55f, 0.15f);// orange: TyO last life (L:1)
        private Texture2D bgTex;
        // COTD site palette: amber accent for titles/lines, near-white default player names,
        // and per-player custom colours (cup winners) from the pool's "col" field.
        private Texture2D whiteTex;          // 1x1 white, tinted via GUI.color for lines/frames
        private static readonly Color accentCol = new Color(0.961f, 0.620f, 0.043f); // #f59e0b --accent
        private static readonly Color pnameCol = new Color(0.953f, 0.957f, 0.965f);  // #f3f4f6 --pname
        private GUIStyle vsTitleStyle;       // "VS CAM" title bar (left part)
        private GUIStyle vsTitleRightStyle;  // player name (right part, rich text)
        private GUIStyle pnameStyle;         // white player-name header, tinted per player
        private float uiScale = 1f;      // HUD scale = Screen.height / 1080 (so it looks the same at any res)
        private float builtScale = -1f;  // scale the styles were last built at (rebuild when it changes)

        private class CompStat
        {
            public int Wins;
            public int Best;
            public int Podiums;
            public int Cups;
            public Dictionary<string, int> Hist; // event id -> finish position
        }

        private class Stat
        {
            public string SteamId;
            public string Name;
            public string ColHex; // COTD custom name colour (cup winners only; null = default)
            public float Elo;   // COTD weighted (fixed benchmark)
            public float Peak;
            public int Rank;
            public Dictionary<string, CompStat> Comps = new Dictionary<string, CompStat>();
        }

        // ---- Showdown / generic two-team match ----------------------------------------------------
        // Deliberately format-agnostic: two teams of N, round scored on the team AVERAGE time, first to
        // `sdTarget` points. Showdown just configures it (2v2, first to 2), so ranked can reuse it.
        private class SdPlayer
        {
            public string Name;
            public string SteamId;      // primary/display id
            public string[] AltIds;     // alternate accounts (shared/brother/smurf); any may be in-lobby
            public string[] Aliases;
            public float Qual = -1f;    // qualifier time (seconds); -1 = unknown. Feeds the qualifier tiebreak.
        }

        private class SdTeam
        {
            public string Tag;      // short broadcast tag; team names can run 200+ chars
            public string Name;
            public Color Col;       // team colour from the JSON
            public bool HasCol;
            public string LogoFile; // optional override; defaults to "S7_<TAG>.png" in the logos folder
            public List<SdPlayer> Players = new List<SdPlayer>();
        }

        private class SdMap
        {
            public int N;           // season map number (#1..#7)
            public string Name;
            public string Authors;
            public string Hash;     // GTR hash, uppercase, may carry a "-N" version suffix
        }

        private readonly List<SdTeam> sdTeams = new List<SdTeam>();
        private readonly List<SdMap> sdMaps = new List<SdMap>();
        private readonly Dictionary<string, SdTeam> sdBySid = new Dictionary<string, SdTeam>();
        private readonly Dictionary<string, Texture2D> sdLogoCache = new Dictionary<string, Texture2D>();
        private string sdSeason = "?";

        // Live match state
        private SdTeam sdA, sdB;             // resolved matchup (null until two teams are present)
        private int sdPtsA, sdPtsB;
        private int sdTarget = 2;            // first to this many round wins
        private string sdPickerTag;          // which team picked the map currently up (null = unknown)
        private bool sdPickerRandom;         // map was randomised by the draft (no picker)
        private string sdLastMapHash;        // change-detect so the picker label clears on a new map
        private bool sdQuadOn;               // "show all": one PhotoDrone per racer, 2x2
        private readonly List<QuadSlot> quadSlots = new List<QuadSlot>();
        private bool quadMade;               // we have created >=1 quad drone (so teardown must keep running)
        private bool vsDroneMade;            // ...same for the single VS cam
        private SdTeam sdOvA, sdOvB;         // teams mirrored from the Showdown mod's leaderboard colours
        private string sdOvSig;              // colour+roster signature the mirror was built from
        private bool sdOvNewMatch = true;    // first mirror of a session counts as a new match

        // ---- Mod-to-mod handshake: authoritative match state broadcast by the Showdown mod ----------
        // The Showdown mod sends "@SDSTATE@<base64 json>@SDSTATE@" over chat/servermessage on each state
        // change; we parse it and render it verbatim, which beats colour-scraping and the pool file. See
        // SdIngestState. Everything here is set on the main thread from the event handler.
        private sealed class SdRemoteState
        {
            public SdTeam A, B;               // built straight from the JSON (tag/name/colour/roster/quals)
            public int ScoreA, ScoreB;
            public int BestOf = 3;
            public List<string> Winners = new List<string>(); // "A"/"B" per decided round, in order
            public string PickerTag;          // resolved tag of the map picker, or null
            public bool PickerRandom;
            public string MapHash, Phase;
            public string Sig;                // change-detect so we don't reset the card every message
        }
        private SdRemoteState sdRemote;
        private float sdRemoteAt = -999f;     // Time.time of the last valid parse
        private const float SD_REMOTE_TTL = 30f; // treat remote state as stale after this many seconds
        private Rect sdRect = new Rect(-1f, 0f, 0f, 0f); // Showdown header (score banner + Bo3 pips); x<0 = default pos
        private Rect sdCardRect = new Rect(-1f, 0f, 0f, 0f); // team-times card; independent of the header so the caster can place them apart
        private bool sdMatchupForced;        // caster pinned the matchup; stop auto-detecting
        private string sdRosterSig;          // lobby steam-id signature the matchup was resolved from
        private readonly HashSet<string> sdScored = new HashSet<string>(); // map hashes already scored
        private readonly List<string> sdWinSeq = new List<string>();       // winning team tag per scored round, in order (Bo3 pips)
        private readonly Dictionary<string, SdTeam> sdMoved =             // manual per-player overrides
            new Dictionary<string, SdTeam>();

        // Computed on the Update poll, rendered verbatim by OnGUI (never computed in the render path).
        private float sdAvgA = -1f, sdAvgB = -1f;         // live team average OF FINISHERS (-1 = none yet)
        private float sdProjA = -1f, sdProjB = -1f;       // same, from GTR PBs on the current map
        private int sdFinA, sdFinB;                        // finisher counts (the first tiebreak)
        private int sdLead;                                // -1 A leads, +1 B leads, 0 undecided
        private string sdLeadMethod;                       // which rule decided it, for the caster
        private float sdLeadGap = -1f;
        private float sdLeadChangedAt = -999f;             // Time.time the lead last flipped; drives the transient arrows
        private const float SD_ARROW_HOLD = 10f;           // show the up/down arrows for this long after a lead change
        private SdMap sdCurMap;                            // season map currently loaded (null = off-pool)
        // Overall finish position (1-based) per racer sid, ranked by live time across both teams. This is
        // what lets the broadcast leaderboard list racers by real finish order (teams interleave, e.g. A
        // at #1 and #3), matching Yolo's in-game board. 0/absent = no time yet.
        private readonly Dictionary<string, int> sdFinishPos = new Dictionary<string, int>();
        // "NEW BEST" flash: a team average only ever improves as racers set better times; flash briefly
        // when it drops. Reset when the map changes.
        private float sdBestAvgA = -1f, sdBestAvgB = -1f;  // best (lowest) avg seen this map
        private float sdNewBestFlashA, sdNewBestFlashB;    // Time.time until which to show "NEW BEST"
        private string sdBestMapHash;
        // Deciding-metric display, computed per Update from the current wincon stage (see SdComputeMetrics).
        private float sdQualA = -1f, sdQualB = -1f;        // team qualifier averages (seconds)
        private string sdMetricA = "--", sdMetricB = "--"; // stage-appropriate big value per team
        private string sdNoteA = "", sdNoteB = "";         // caption under the metric (winner side)
        private float sdDiffA = -1f, sdDiffB = -1f;        // "+gap" shown on the LOSING team only (-1 = none)
        private bool sdMetricWord;                          // metric is a word (PICKED / +5:00), not a time
        private const float SD_EPS = 0.001f;               // tie tolerance in seconds (Yolo's 1 ms)
        private int sdDbgMove;                              // debug: 0 normal, 1 A up/B down, 2 B up/A down

        // ---- Per-racer GTR personal bests on the current map (one query for the whole lobby) ----
        private readonly Dictionary<string, float> sdPb = new Dictionary<string, float>(); // sid -> seconds
        private string sdPbKey;                   // "<hash>|<sid,sid,...>" the cache/in-flight fetch is for
        private bool sdPbFetching;
        private volatile bool sdPbPending;        // bg thread finished; main thread should commit
        private volatile string sdPbPendingKey;
        private volatile Dictionary<string, float> sdPbPendingMap;

        // [Stats] button: which comp pool feeds wins/best/podiums/cups AND the H2H mutual record.
        private string selectedComp = "cotd";
        private readonly List<string> availableComps = new List<string>();
        private static readonly string[] COMP_ORDER =
            { "cotd", "crosscomp", "pcdj", "eggy", "qube", "tyo", "kerki", "zsl" };

        // Default photomode camera mode applied on a left-click follow. The integers are the game's
        // own FlyingCameraScript.currentCameraState values; 6 is the dynamic/smooth follow (verified
        // in-game via /overlay camstate). None = leave the caster's current camera mode alone.
        private enum FollowCam { None = -1, State0 = 0, State1 = 1, State2 = 2, State3 = 3, State4 = 4, State5 = 5, DynamicFollow = 6, State7 = 7 }

        // [Comp] button: which cup FORMAT drives the player-list ordering logic (not the stats).
        private enum CastMode { Cup, Topout, Pursuit, Showdown }
        private CastMode castMode = CastMode.Cup;
        private static readonly CastMode[] CAST_ORDER = { CastMode.Cup, CastMode.Topout, CastMode.Pursuit, CastMode.Showdown };
        private static string CastLabel(CastMode m)
        {
            if (m == CastMode.Topout) return "Topout";
            if (m == CastMode.Pursuit) return "Pursuit";
            if (m == CastMode.Showdown) return "Showdown";
            return "Cup";
        }
        private static string CompLabel(string c)
        {
            switch (c)
            {
                case "cotd": return "COTD";
                case "crosscomp": return "Cross-comp";
                case "pcdj": return "Petite";
                case "eggy": return "Eggy";
                case "qube": return "Qube";
                case "tyo": return "TyO";
                case "kerki": return "Kerki";
                case "zsl": return "ZSL";
            }
            return c;
        }

        private void Awake()
        {
            Instance = this;
            Storage = StorageApi.CreateModStorage(this); // mod-scoped JSON store (must exist before LoadLayout)
            cfgEnabled = Config.Bind("General", "Enabled", false,
                "Master switch for the whole overlay. OFF by default and re-armed to OFF every launch, " +
                "so the mod stays completely dormant (no cards, no camera tracking) until you turn it " +
                "on. F4 is the master on/off toggle: press it in a lobby to enable + open the panel, " +
                "press it again to turn the whole mod off. (You can also tick this or run any /overlay " +
                "command to enable.) Cycling players in photomode never enables it - only F4 (or those). " +
                "Deliberately not remembered between sessions, so it only ever runs when you want it to.");
            cfgEnabled.Value = false; // never remember: always start disabled regardless of the saved value
            cfgStayInPhotomode = Config.Bind("General", "Stay in photomode", false,
                "Auto re-enters photomode at the start of each round (for casters/spectators). Respects " +
                "the server's photomode rules and never forces it when a comp disables or gates " +
                "photomode. Off by default.");
            cfgFreezeMouse = Config.Bind("General", "Freeze mouse-look while panel open", true,
                "While the control panel is open, freezes the photomode MOUSE camera look so you can " +
                "move the mouse to click the overlay buttons without spinning the free-cam. The " +
                "controller keeps flying (it has its own look sensitivity), so following the pack with a " +
                "pad is unaffected. On by default - the casting default. Turn it OFF only if you fly the " +
                "free-cam with the mouse itself and accept that clicking will move the camera.");
            cfgKeyPanel = Config.Bind("Hotkeys", "Toggle panel", KeyCode.F4,
                "Key to open/close the click-to-cast control panel.");
            cfgKeyClear = Config.Bind("Hotkeys", "Clear overlay", KeyCode.F5,
                "Key to clear everything on screen (same as the panel's Clear button).");
            cfgFollowCamState = Config.Bind("Camera", "Follow camera mode", FollowCam.DynamicFollow,
                "Camera mode to switch to when you LEFT-CLICK a player to follow. DynamicFollow (the " +
                "smooth chase cam) is the default. None = leave the game's current camera mode alone. " +
                "State0-State7 are the raw photomode camera states if you prefer a different one. The " +
                "in-game camera keybinds still work to change it on the fly; this only sets the default " +
                "applied on a click.");
            LoadPool();
            LoadSdPool();
            BuildAvailableComps();
            LoadLayout();
            ChatCommandApi.RegisterLocalChatCommand("/", "overlay",
                "Overlays. Usage: /overlay stats <name> | h2h <a> <b> | times <name> | reset | test | off",
                (LocalChatCommandCallbackDelegate)OnCommand);
            // Listen to COTDTracker's in-process log events for live per-round times.
            try { BepInEx.Logging.Logger.Listeners.Add(this); }
            catch (Exception ex) { Logger.LogError("Could not add log listener: " + ex); }
            // Subscribe to the live leaderboard for current-map "fastest lap".
            try { DiscoverLeaderboard(); SubscribeLeaderboard(); }
            catch (Exception ex) { Logger.LogError("Leaderboard hook failed: " + ex); }
            // Note: we deliberately do NOT freeze game input while the panel is open. Instead we
            // zero the photomode MOUSE look sensitivity (FreezeMouseLook) so the cursor is free to
            // click without swinging the camera, while the controller keeps flying.
            // Private UpdateZeepkistList(bool) lets us refresh the follow list before steering it.
            try
            {
                updateListMI = typeof(FlyingCameraScript).GetMethod("UpdateZeepkistList",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch (Exception ex) { Logger.LogError("UpdateZeepkistList lookup failed: " + ex); }
            // Casting integrity: leaving photomode force-clears the overlay (un-disableable) so a racer
            // can't keep another player's cam/stats up. Round start re-arms the stay-in-photomode entry.
            try
            {
                PhotoModeApi.PhotoModeEntered += OnPhotoModeEntered;
                PhotoModeApi.PhotoModeExited += OnPhotoModeExited;
                RacingApi.RoundStarted += OnRoundStarted;
                RacingApi.RoundEnded += OnRoundEnded;
                MultiplayerApi.DisconnectedFromGame += OnLeftLobby;
                // Mod-to-mod handshake: the Showdown mod's state arrives as a hidden @SDSTATE@ payload on
                // either channel (server message when it's on the leaderboard, plain chat when it isn't).
                ChatApi.ServerMessageReceived += OnSdServerMessage;
                ChatApi.ChatMessageReceived += OnSdChatMessage;
                // Self-correct if we somehow load while already in photomode (events only fire on
                // the transition). Best-effort: efc may be null this early, which is fine.
                EnableFlyingCamera2 efc0 = FindEFC();
                if (efc0 != null && efc0.isPhotoMode) inPhotoMode = true;
            }
            catch (Exception ex) { Logger.LogError("Photomode/racing hooks failed: " + ex); }
            Logger.LogInfo(string.Format("Tournament Casting UI 1.0.0-beta.8 loaded. Local pool v{0}, {1} players (refreshing from repo).",
                poolVersion, pool.Count));
            Logger.LogInfo(string.Format("[config] Stay in photomode = {0}", cfgStayInPhotomode.Value));
        }

        // Load the local file immediately (offline/dev), then refresh from the repo (async).
        private void LoadPool()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "overlay_pool.json");
                if (File.Exists(path))
                {
                    string ver;
                    Dictionary<string, Stat> disk = ParsePool(File.ReadAllText(path), out ver);
                    if (disk.Count > 0) { pool = disk; poolVersion = ver; }
                }
            }
            catch (Exception ex) { Logger.LogError("LoadPool (local) failed: " + ex); }
            FetchPool();
        }

        // Async fetch of the latest pool from the repo. On success the JSON is stashed for the
        // main thread to apply (in Update) - never mutate the pool off the Unity thread.
        private void FetchPool()
        {
            try
            {
                WebClient client = new WebClient();
                client.DownloadStringCompleted += delegate (object sender, DownloadStringCompletedEventArgs e)
                {
                    if (e.Error != null) { Logger.LogWarning("Pool fetch failed (using local): " + e.Error.Message); return; }
                    pendingPoolJson = e.Result;
                };
                client.DownloadStringAsync(new Uri(POOL_URL));
            }
            catch (Exception ex) { Logger.LogWarning("Pool fetch could not start (using local): " + ex.Message); }
        }

        // Parse a pool JSON string into a fresh dictionary (touches no live state).
        private Dictionary<string, Stat> ParsePool(string json, out string version)
        {
            version = "?";
            Dictionary<string, Stat> result = new Dictionary<string, Stat>();
            JObject root = JObject.Parse(json);
            version = (string)root["version"] ?? "?";
            JObject players = (JObject)root["players_by_steam_id"];
            if (players == null) return result;
            foreach (KeyValuePair<string, JToken> kv in players)
            {
                JObject o = (JObject)kv.Value;
                Stat s = new Stat();
                s.SteamId = kv.Key;
                s.Name = (string)o["name"];
                s.ColHex = (string)o["col"]; // null for non-winners
                s.Elo = JNum(o, "elo");
                s.Peak = JNum(o, "peak");
                s.Rank = (int)JNum(o, "rank");
                JObject comps = o["comps"] as JObject;
                if (comps != null)
                {
                    foreach (KeyValuePair<string, JToken> ck in comps)
                    {
                        JObject co = (JObject)ck.Value;
                        CompStat cs = new CompStat();
                        cs.Wins = (int)JNum(co, "wins");
                        cs.Best = (int)JNum(co, "best");
                        cs.Podiums = (int)JNum(co, "podiums");
                        cs.Cups = (int)JNum(co, "cups");
                        cs.Hist = new Dictionary<string, int>();
                        JObject h = co["hist"] as JObject;
                        if (h != null)
                            foreach (KeyValuePair<string, JToken> hk in h)
                                cs.Hist[hk.Key] = (int)hk.Value;
                        s.Comps[ck.Key] = cs;
                    }
                }
                result[kv.Key] = s;
            }
            return result;
        }

        // Apply a freshly-fetched pool on the main thread: swap the dict atomically, rebuild the
        // comp list, keep the selected pool valid.
        private void ApplyFetchedPool(string json)
        {
            try
            {
                string ver;
                Dictionary<string, Stat> np = ParsePool(json, out ver);
                if (np.Count == 0) return; // ignore an empty/garbage fetch, keep what we have
                pool = np;
                poolVersion = ver;
                BuildAvailableComps();
                if (!availableComps.Contains(selectedComp)) selectedComp = "cotd";
                Logger.LogInfo(string.Format("Pool refreshed from repo: v{0}, {1} players.", poolVersion, pool.Count));
            }
            catch (Exception ex) { Logger.LogError("ApplyFetchedPool failed: " + ex); }
        }

        private static float JNum(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null) return 0f;
            try { return (float)t; } catch { return 0f; }
        }

        // ---- Showdown pool: same two-stage load as the stats pool (local file, then repo on top) ----

        private void LoadSdPool()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "showdown_pool.json");
                if (File.Exists(path)) ApplySdPool(File.ReadAllText(path), "local");
            }
            catch (Exception ex) { Logger.LogError("LoadSdPool (local) failed: " + ex); }
            try
            {
                WebClient client = new WebClient();
                client.DownloadStringCompleted += delegate (object sender, DownloadStringCompletedEventArgs e)
                {
                    if (e.Error != null) { Logger.LogWarning("[sd] pool fetch failed (using local): " + e.Error.Message); return; }
                    pendingSdJson = e.Result;
                };
                client.DownloadStringAsync(new Uri(SD_URL));
            }
            catch (Exception ex) { Logger.LogWarning("[sd] pool fetch could not start (using local): " + ex.Message); }
        }

        // Replace the teams/maps tables from a JSON document. Rejects an empty/garbage payload so a bad
        // fetch can never wipe a good local file mid-event.
        private void ApplySdPool(string json, string source)
        {
            try
            {
                JObject root = JObject.Parse(json);
                List<SdMap> maps = new List<SdMap>();
                JArray ma = root["maps"] as JArray;
                if (ma != null)
                {
                    foreach (JToken t in ma)
                    {
                        JObject o = t as JObject;
                        if (o == null) continue;
                        SdMap m = new SdMap();
                        m.N = (int)JNum(o, "n");
                        m.Name = (string)o["name"];
                        m.Authors = (string)o["authors"];
                        string h = (string)o["hash"];
                        m.Hash = string.IsNullOrEmpty(h) ? null : h.ToUpperInvariant();
                        if (m.Hash != null) maps.Add(m);
                    }
                }
                List<SdTeam> teams = new List<SdTeam>();
                JArray ta = root["teams"] as JArray;
                if (ta != null)
                {
                    foreach (JToken t in ta)
                    {
                        JObject o = t as JObject;
                        if (o == null) continue;
                        SdTeam tm = new SdTeam();
                        tm.Tag = (string)o["tag"];
                        tm.Name = (string)o["name"];
                        Color c;
                        tm.HasCol = TryParseHexColor((string)o["color"], out c);
                        tm.Col = tm.HasCol ? c : pnameCol;
                        tm.LogoFile = (string)o["logo"]; // null -> derived from tag at load time
                        JArray pa = o["players"] as JArray;
                        if (pa != null)
                        {
                            foreach (JToken pt in pa)
                            {
                                JObject po = pt as JObject;
                                if (po == null) continue;
                                SdPlayer p = new SdPlayer();
                                p.Name = (string)po["name"];
                                p.SteamId = (string)po["steam_id"];
                                List<string> al = new List<string>();
                                JArray aa = po["aliases"] as JArray;
                                if (aa != null) foreach (JToken at in aa) { string s = (string)at; if (!string.IsNullOrEmpty(s)) al.Add(s); }
                                p.Aliases = al.ToArray();
                                List<string> alt = new List<string>();
                                JArray ai = po["alt_ids"] as JArray;
                                if (ai != null) foreach (JToken it in ai) { string s = (string)it; if (!string.IsNullOrEmpty(s)) alt.Add(s); }
                                p.AltIds = alt.ToArray();
                                JToken q = po["qual"];
                                p.Qual = q != null ? (float)q : -1f;
                                tm.Players.Add(p);
                            }
                        }
                        if (!string.IsNullOrEmpty(tm.Tag)) teams.Add(tm);
                    }
                }
                if (teams.Count == 0 && maps.Count == 0) { Logger.LogWarning("[sd] " + source + " pool empty - ignored"); return; }

                sdTeams.Clear(); sdTeams.AddRange(teams);
                sdMaps.Clear(); sdMaps.AddRange(maps);
                sdSeason = root["season"] != null ? root["season"].ToString() : "?";
                RebuildSdIndex();
                sdRosterSig = null; // force a matchup re-detect against the new tables
                Logger.LogInfo(string.Format("[sd] pool loaded ({0}): season {1}, {2} teams, {3} maps",
                    source, sdSeason, sdTeams.Count, sdMaps.Count));
            }
            catch (Exception ex) { Logger.LogError("[sd] ApplySdPool (" + source + ") failed: " + ex); }
        }

        // Steam id -> team lookup. Manual /overlay sd move overrides are re-applied to the ROSTERS here,
        // not just to the index: the repo fetch replaces sdTeams wholesale, and a caster's live
        // substitution must survive that (otherwise a fetch landing after a swap silently undoes it).
        private void RebuildSdIndex()
        {
            foreach (KeyValuePair<string, SdTeam> kv in sdMoved)
            {
                SdTeam dest = SdTeamByTag(kv.Value.Tag); // re-resolve: kv.Value may be a stale instance
                if (dest == null) continue;
                string name = null;
                foreach (SdTeam t in sdTeams)
                    for (int i = t.Players.Count - 1; i >= 0; i--)
                        if (t.Players[i].SteamId == kv.Key) { name = t.Players[i].Name; t.Players.RemoveAt(i); }
                SdPlayer np = new SdPlayer();
                np.SteamId = kv.Key;
                np.Name = name != null ? name : (LobbyNameForSid(kv.Key) ?? kv.Key);
                np.Aliases = new string[0];
                dest.Players.Add(np);
            }

            sdBySid.Clear();
            foreach (SdTeam t in sdTeams)
                foreach (SdPlayer p in t.Players)
                {
                    if (!string.IsNullOrEmpty(p.SteamId)) sdBySid[p.SteamId] = t;
                    if (p.AltIds != null)
                        foreach (string a in p.AltIds) if (!string.IsNullOrEmpty(a)) sdBySid[a] = t;
                }
            // Aliases stay a name-only fallback: the index is keyed by steam id, which is what the
            // lobby roster gives us directly, so no alias entries are needed here.
        }

        // "#rrggbb" / "rrggbb" -> Color. Team colours come from the organisers' signup posts.
        private static bool TryParseHexColor(string hex, out Color col)
        {
            col = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            string h = hex.Trim();
            if (h.StartsWith("#")) h = h.Substring(1);
            if (h.Length != 6) return false;
            int r, g, b;
            if (!int.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
            if (!int.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
            if (!int.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            col = new Color(r / 255f, g / 255f, b / 255f);
            return true;
        }

        // ---- Mod-to-mod handshake receiver ---------------------------------------------------------
        // The Showdown mod (host) broadcasts "@SDSTATE@<base64 json>@SDSTATE@" whenever the match state
        // changes. It reaches us as a server message (when riding the leaderboard) or a plain chat line.
        // Those events may fire off the main thread, so the handlers only capture the raw payload; it's
        // decoded and applied from Update (see SdApplyStatePayload), mirroring the pool-fetch pattern.
        private volatile string pendingSdState;
        private static readonly Regex SdStateRe = new Regex("@SDSTATE@(.*?)@SDSTATE@", RegexOptions.Singleline);

        private void OnSdServerMessage(string message) { SdCaptureState(message); }
        private void OnSdChatMessage(ulong playerId, string username, string message) { SdCaptureState(message); }

        private void SdCaptureState(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            Match m = SdStateRe.Match(raw);
            if (m.Success) pendingSdState = m.Groups[1].Value.Trim(); // base64; decoded on the main thread
        }

        // Belt-and-suspenders receiver. As of Showdown4 08a0ac6 the host emits the state via
        // ZeepkistNetwork.SendCustomChatMessage (not plain chat), and ZeepSDK's ChatMessageReceived is
        // wired only to the normal chat packet - it may not re-raise custom chat packets. So we also read
        // the state straight off the game's ChatMessages list (Yolo's documented fallback). Typed access;
        // we already reference Zeepkist.dll. Runs on the main thread from Update, so it feeds pendingSdState
        // the same way the events do and is consumed by the same block below.
        private int sdChatSeen;
        private void SdPollChatMessages()
        {
            List<ZeepkistClient.ZeepkistChatMessage> msgs = ZeepkistClient.ZeepkistNetwork.ChatMessages;
            if (msgs == null) { sdChatSeen = 0; return; }
            int n = msgs.Count;
            if (n < sdChatSeen) sdChatSeen = 0;   // list was cleared (e.g. new lobby) - rescan from the top
            for (int i = sdChatSeen; i < n; i++)
            {
                ZeepkistClient.ZeepkistChatMessage cm = msgs[i];
                if (cm != null && cm.Message != null) SdCaptureState(cm.Message); // newest @SDSTATE@ wins
            }
            sdChatSeen = n;
        }

        // Decode + parse + commit a captured payload. Silent on anything malformed - a stray chat line
        // must never disturb the overlay.
        private void SdApplyStatePayload(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return;
            string json;
            try { json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch { return; }
            SdRemoteState st = SdParseState(json);
            if (st == null) return;
            sdRemote = st;
            sdRemoteAt = Time.time;
            sdRosterSig = null;               // make the detector reconsider immediately
            SdDetectMatchup(true);
            Logger.LogInfo(string.Format("[sd] remote state: {0} {1}-{2} {3} (phase {4})",
                st.A != null ? st.A.Tag : "?", st.ScoreA, st.ScoreB, st.B != null ? st.B.Tag : "?", st.Phase));
        }

        private SdRemoteState SdParseState(string json)
        {
            JObject root;
            try { root = JObject.Parse(json); } catch { return null; }
            if ((string)root["type"] != "showdown_state") return null;
            SdRemoteState st = new SdRemoteState();
            st.Phase = (string)root["phase"];
            JObject match = root["match"] as JObject;
            if (match != null)
            {
                st.BestOf = match["bestOf"] != null ? (int)match["bestOf"] : 3;
                st.ScoreA = match["scoreA"] != null ? (int)match["scoreA"] : 0;
                st.ScoreB = match["scoreB"] != null ? (int)match["scoreB"] : 0;
                JArray rw = match["roundWinners"] as JArray;
                if (rw != null) foreach (JToken w in rw) { string s = (string)w; if (s == "A" || s == "B") st.Winners.Add(s); }
            }
            JObject teams = root["teams"] as JObject;
            if (teams == null) return null;
            st.A = SdTeamFromJson(teams["A"] as JObject);
            st.B = SdTeamFromJson(teams["B"] as JObject);
            if (st.A == null || st.B == null) return null;
            JObject map = root["map"] as JObject;
            if (map != null)
            {
                string h = (string)map["hash"];
                st.MapHash = string.IsNullOrEmpty(h) ? null : h.ToUpperInvariant();
                string pick = (string)map["picker"];
                if (pick == "random") st.PickerRandom = true;
                else if (pick == "A") st.PickerTag = st.A.Tag;
                else if (pick == "B") st.PickerTag = st.B.Tag;
            }
            // Signature for change-detect: only rebuild/reset the card when something meaningful changed.
            st.Sig = st.A.Tag + "|" + st.B.Tag + "|" + st.ScoreA + "-" + st.ScoreB + "|" +
                     string.Join("", st.Winners.ToArray()) + "|" + (st.MapHash ?? "") + "|" +
                     (st.PickerRandom ? "R" : (st.PickerTag ?? ""));
            return st;
        }

        private SdTeam SdTeamFromJson(JObject o)
        {
            if (o == null) return null;
            SdTeam t = new SdTeam();
            t.Tag = (string)o["tag"];
            t.Name = (string)o["name"];
            Color c; t.HasCol = TryParseHexColor((string)o["color"], out c); t.Col = t.HasCol ? c : pnameCol;
            JArray pa = o["players"] as JArray;
            if (pa != null)
                foreach (JToken pt in pa)
                {
                    JObject po = pt as JObject;
                    if (po == null) continue;
                    SdPlayer p = new SdPlayer();
                    p.Name = (string)po["name"];
                    p.SteamId = (string)po["steamId"];
                    p.Aliases = new string[0];
                    p.AltIds = new string[0];
                    JToken q = po["qual"];
                    p.Qual = q != null ? (float)q : -1f;
                    t.Players.Add(p);
                }
            return string.IsNullOrEmpty(t.Tag) ? null : t;
        }

        private bool SdRemoteFresh() { return sdRemote != null && Time.time - sdRemoteAt <= SD_REMOTE_TTL; }

        // A realistic sample payload for /overlay sd sim: STBN vs AgOH, 1-0, round 2, AgOH picked map #3.
        // Exercises the whole receive path (base64 + JSON + apply) without the Showdown mod.
        private string SdSimPayload()
        {
            string json =
                "{\"type\":\"showdown_state\",\"v\":1,\"phase\":\"racing\"," +
                "\"match\":{\"bestOf\":3,\"scoreA\":1,\"scoreB\":0,\"round\":2,\"roundWinners\":[\"A\",null,null]}," +
                "\"teams\":{" +
                "\"A\":{\"tag\":\"STBN\",\"name\":\"Sterben\",\"color\":\"#568B30\",\"players\":[" +
                "{\"steamId\":\"76561198149636594\",\"name\":\"Quickracer10\",\"qual\":62.708}," +
                "{\"steamId\":\"76561199082360966\",\"name\":\"B_ES\",\"qual\":62.813}]}," +
                "\"B\":{\"tag\":\"AgOH\",\"name\":\"Silver Hydroxide\",\"color\":\"#ffefad\",\"players\":[" +
                "{\"steamId\":\"76561199027567424\",\"name\":\"Hydro\",\"qual\":62.319}," +
                "{\"steamId\":\"76561198974231691\",\"name\":\"agix\",\"qual\":63.004}]}}," +
                "\"map\":{\"hash\":\"0363216F4A2396F6CC753BBAA212F4A73A82E63D-3\",\"name\":\"Love City\",\"number\":3,\"picker\":\"B\"}}";
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            return "@SDSTATE@" + b64 + "@SDSTATE@";
        }

        // Push the remote state onto the live card. Only rebuilds (and resets points/picks) when the
        // state signature actually changed, so a re-sent identical message never disturbs the card.
        private void SdApplyRemote()
        {
            SdRemoteState st = sdRemote;
            if (st == null) return;
            bool changed = st.Sig != sdRemoteSigApplied;
            sdRemoteSigApplied = st.Sig;
            sdA = st.A; sdB = st.B;                       // keep his A=left / B=right
            sdPtsA = st.ScoreA; sdPtsB = st.ScoreB;
            sdTarget = Mathf.Max(1, st.BestOf / 2 + 1);   // best-of-3 -> first to 2
            sdWinSeq.Clear();
            foreach (string w in st.Winners) sdWinSeq.Add(w == "A" ? st.A.Tag : st.B.Tag);
            sdPickerTag = st.PickerTag; sdPickerRandom = st.PickerRandom;
            if (changed) { sdScored.Clear(); sdPbKey = null; } // new roster/score -> refetch PBs
        }
        private string sdRemoteSigApplied;

        // ---- Showdown match engine -----------------------------------------------------------------

        // Work out which two teams are in the lobby. This is what makes the mode hands-off: the operator
        // joins and the card is already right. Only recomputed when the set of steam ids changes, and
        // never once the caster has pinned a matchup with /overlay sd <a> <b>.
        private void SdDetectMatchup(bool force)
        {
            if (sdMatchupForced && !force) return;
            // The Showdown mod itself is the best source when it's live. Preferred order:
            //   1. the @SDSTATE@ handshake (full authoritative state), 2. its leaderboard colours,
            //   3. our own pool file. A caster-pinned matchup still wins over all of them.
            // Once the Showdown mod has spoken in this lobby it stays authoritative for the whole
            // match: broadcasts only come at state changes, so mid-round the remote is always "stale"
            // (rounds outlive SD_REMOTE_TTL by minutes). Falling through to the colour-scrape here was
            // a live-event bug: mid-round only FINISHERS carry a colour override, so the scrape rebuilt
            // the teams from whoever had finished (a 1-player roster -> that racer's 4x feed died until
            // the round-end broadcast restored the truth). Stale now only means "stop taking the score
            // from it" (the local auto-scorer covers that); rosters keep the last broadcast state.
            if (!sdMatchupForced && sdRemote != null)
            {
                if (SdRemoteFresh()) SdApplyRemote();
                return;
            }
            if (!sdMatchupForced && SdDetectFromOverrides()) return;
            List<ZeepkistNetworkPlayer> list;
            try { list = ZeepkistNetwork.PlayerList; } catch { return; }
            if (list == null) return;

            // Cheap change-detect: the sorted-ish concatenation of ids present.
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ZeepkistNetworkPlayer p in list) { sb.Append(p.SteamID); sb.Append(','); }
            string sig = sb.ToString();
            if (!force && sig == sdRosterSig) return;
            sdRosterSig = sig;
            if (sdMatchupForced) return;

            Dictionary<SdTeam, int> counts = new Dictionary<SdTeam, int>();
            foreach (ZeepkistNetworkPlayer p in list)
            {
                SdTeam t;
                if (sdBySid.TryGetValue(p.SteamID.ToString(CultureInfo.InvariantCulture), out t))
                { int n; counts.TryGetValue(t, out n); counts[t] = n + 1; }
            }
            SdTeam bestA = null, bestB = null;
            int cA = 0, cB = 0;
            foreach (KeyValuePair<SdTeam, int> kv in counts)
            {
                if (kv.Value > cA) { bestB = bestA; cB = cA; bestA = kv.Key; cA = kv.Value; }
                else if (kv.Value > cB) { bestB = kv.Key; cB = kv.Value; }
            }
            if (bestA == null || bestB == null) return; // no 2-team matchup yet; keep the current one

            // Already showing this pairing (in either order)? Nothing to do - and crucially, don't
            // reset the score just because the detector re-ran. Compare by TAG, not object identity:
            // the same two teams can arrive as pool objects OR as live override objects depending on
            // whether the Showdown mod is broadcasting colours this instant, and a reference mismatch
            // between those would wipe the score every time the source flipped (once per map).
            if (SdSamePair(bestA, bestB, sdA, sdB)) return;

            // Replacing an established matchup needs confidence, or one racer rage-quitting mid-match
            // would "detect" a different pair and wipe the score. A fresh detection is permissive; an
            // overwrite requires both candidate teams to be fully present.
            bool established = sdA != null && sdB != null;
            if (established && (cA < 2 || cB < 2)) return;

            // Stable left/right by tag so the card doesn't swap sides between rounds.
            if (string.Compare(bestA.Tag, bestB.Tag, StringComparison.OrdinalIgnoreCase) <= 0) { sdA = bestA; sdB = bestB; }
            else { sdA = bestB; sdB = bestA; }

            // A different pairing means a different match: points and picks from the last one are
            // meaningless.
            sdPtsA = 0; sdPtsB = 0; sdScored.Clear(); sdWinSeq.Clear();
            sdPickerTag = null; sdPickerRandom = false;
            sdPbKey = null; // new roster -> refetch PBs
            Logger.LogInfo(string.Format("[sd] matchup: {0} vs {1}", sdA.Tag, sdB.Tag));
        }

        private bool SdMatchLive() { return castMode == CastMode.Showdown && sdA != null && sdB != null; }

        // Which path produced the current matchup - worth surfacing, because "mirroring the Showdown
        // mod" and "guessing from our own roster file" are very different levels of trustworthy.
        private string SdTeamSource()
        {
            if (sdMatchupForced) return "pinned by caster";
            if (SdRemoteFresh() && sdRemote != null && sdA == sdRemote.A) return "Showdown mod (handshake)";
            if (sdA != null && sdA == sdOvA) return "Showdown mod (colours)";
            return "showdown_pool.json";
        }

        // Reassign one player to a team, live. Shared by the panel buttons and /overlay sd move.
        private void SdMoveSid(string sid, SdTeam dest)
        {
            if (string.IsNullOrEmpty(sid) || dest == null) return;
            string name = LobbyNameForSid(sid);
            foreach (SdTeam ot in sdTeams)
                for (int i = ot.Players.Count - 1; i >= 0; i--)
                    if (ot.Players[i].SteamId == sid)
                    {
                        if (string.IsNullOrEmpty(name)) name = ot.Players[i].Name;
                        ot.Players.RemoveAt(i);
                    }
            SdPlayer np = new SdPlayer();
            np.SteamId = sid; np.Name = string.IsNullOrEmpty(name) ? sid : name; np.Aliases = new string[0];
            dest.Players.Add(np);
            sdMoved[sid] = dest;
            RebuildSdIndex();
            sdPbKey = null;      // roster changed -> refetch PBs
            ChatApi.AddLocalMessage("Showdown: " + np.Name + " -> " + dest.Tag);
        }

        // ---- Team detection from the Showdown mod's own leaderboard overrides ------------------------
        // Showdown4's State_Racing pushes each racer's TEAM COLOUR into the replicated custom-leaderboard
        // name override:  <nobr><#RRGGBB>Name</color></nobr>.  Reading that makes the Showdown mod
        // authoritative for team membership, which beats any roster file we maintain: it survives late
        // substitutes, an unregistered 8th team, and any drift between their JSON and ours.
        //
        // The colour must come from the field containing the player's NAME. Showdown4 also writes
        // "<#00ff00>" into the TIME override for every finisher, so a blind scan would put the whole
        // lobby on one green team.
        private string SdOverrideTeamColor(ulong sid, string lobbyName)
        {
            if (toOverrideStrFIs == null || toGetOverrideMI == null || string.IsNullOrEmpty(lobbyName)) return null;
            try
            {
                object ov = toGetOverrideMI.Invoke(null, new object[] { sid });
                if (ov == null) return null;
                for (int i = 0; i < toOverrideStrFIs.Length; i++)
                {
                    string s = toOverrideStrFIs[i].GetValue(ov) as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf(lobbyName, StringComparison.OrdinalIgnoreCase) < 0) continue; // not the name field
                    Match m = Regex.Match(s, "<(?:color=)?#([0-9A-Fa-f]{6})>");
                    if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
                }
            }
            catch { }
            return null;
        }

        private static string ColorToHex(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}",
                Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f));
        }

        // Build the matchup from the live overrides. Returns false when the Showdown mod isn't running
        // (or hasn't tagged anyone yet), so the steam-id path stays as the fallback.
        private bool SdDetectFromOverrides()
        {
            EnsureTopoutApi();
            List<ZeepkistNetworkPlayer> list;
            try { list = ZeepkistNetwork.PlayerList; } catch { return false; }
            if (list == null) return false;

            Dictionary<string, List<ZeepkistNetworkPlayer>> byCol = new Dictionary<string, List<ZeepkistNetworkPlayer>>();
            foreach (ZeepkistNetworkPlayer p in list)
            {
                string hex = SdOverrideTeamColor(p.SteamID, SafeName(p));
                if (hex == null) continue;
                List<ZeepkistNetworkPlayer> l;
                if (!byCol.TryGetValue(hex, out l)) { l = new List<ZeepkistNetworkPlayer>(); byCol[hex] = l; }
                l.Add(p);
            }
            if (byCol.Count < 2) return false; // one colour (or none) is not a matchup

            string hexA = null, hexB = null;
            int nA = 0, nB = 0;
            foreach (KeyValuePair<string, List<ZeepkistNetworkPlayer>> kv in byCol)
            {
                if (kv.Value.Count > nA) { hexB = hexA; nB = nA; hexA = kv.Key; nA = kv.Value.Count; }
                else if (kv.Value.Count > nB) { hexB = kv.Key; nB = kv.Value.Count; }
            }
            if (hexA == null || hexB == null) return false;
            // Stable sides: order by colour so the card doesn't flip when counts change.
            if (string.CompareOrdinal(hexA, hexB) > 0) { string t = hexA; hexA = hexB; hexB = t; }

            string sig = hexA + ":" + SdSig(byCol[hexA]) + "|" + hexB + ":" + SdSig(byCol[hexB]);
            if (sig == sdOvSig && sdA == sdOvA && sdB == sdOvB) return true; // unchanged
            sdOvSig = sig;

            if (sdOvA == null) { sdOvA = new SdTeam(); sdOvB = new SdTeam(); }
            SdFillOverrideTeam(sdOvA, hexA, byCol[hexA]);
            SdFillOverrideTeam(sdOvB, hexB, byCol[hexB]);

            // Only treat this as a fresh match (and reset the score) when the two teams actually
            // changed by tag, or the Showdown mod signalled a new match. Swapping from pool objects to
            // these live override objects for the SAME teams must not reset anything.
            bool changed = !SdSamePair(sdOvA, sdOvB, sdA, sdB) || sdOvNewMatch;
            sdOvNewMatch = false;
            sdA = sdOvA; sdB = sdOvB;
            if (changed)
            {
                sdPtsA = 0; sdPtsB = 0; sdScored.Clear(); sdWinSeq.Clear();
                sdPickerTag = null; sdPickerRandom = false;
            }
            sdPbKey = null; // roster changed -> refetch PBs
            Logger.LogInfo(string.Format("[sd] teams from Showdown overrides: {0} ({1}) v {2} ({3})",
                sdOvA.Tag, sdOvA.Players.Count, sdOvB.Tag, sdOvB.Players.Count));
            return true;
        }

        // Two matchups are "the same" when they name the same two teams (in either order), compared by
        // tag rather than object reference - the same team can be represented by a pool object or a live
        // override object, and reference equality would spuriously read those as a new match.
        private static bool SdTagEq(SdTeam x, SdTeam y)
        {
            if (x == null || y == null) return false;
            return string.Equals(x.Tag, y.Tag, StringComparison.OrdinalIgnoreCase);
        }
        private static bool SdSamePair(SdTeam a1, SdTeam b1, SdTeam a2, SdTeam b2)
        {
            if (a1 == null || b1 == null || a2 == null || b2 == null) return false;
            return (SdTagEq(a1, a2) && SdTagEq(b1, b2)) || (SdTagEq(a1, b2) && SdTagEq(b1, a2));
        }

        private static string SdSig(List<ZeepkistNetworkPlayer> l)
        {
            List<string> ids = new List<string>();
            foreach (ZeepkistNetworkPlayer p in l) ids.Add(p.SteamID.ToString(CultureInfo.InvariantCulture));
            ids.Sort(StringComparer.Ordinal);
            return string.Join(",", ids.ToArray());
        }

        // Name/tag come from our pool when the colour matches a known team; otherwise the team still
        // works, just with a generated tag - which is exactly what lets an unregistered team cast fine.
        private void SdFillOverrideTeam(SdTeam t, string hex, List<ZeepkistNetworkPlayer> members)
        {
            // Resolve to a pool team by STEAM ID first - the colour Yolo's mod broadcasts round-trips
            // through a Unity float Color and drifts a bit, so colour matching is unreliable. Steam ids
            // are exact. Colour match is only a fallback for a team that isn't in the pool at all.
            SdTeam known = null;
            foreach (ZeepkistNetworkPlayer p in members)
            {
                SdTeam kt;
                if (sdBySid.TryGetValue(p.SteamID.ToString(CultureInfo.InvariantCulture), out kt)) { known = kt; break; }
            }
            if (known == null)
                foreach (SdTeam k in sdTeams)
                    if (k.HasCol && ColorToHex(k.Col) == hex) { known = k; break; }

            Color c;
            bool parsedHex = TryParseHexColor("#" + hex, out c);
            // Prefer the pool's own colour when we know the team - it's the clean, intended value.
            if (known != null && known.HasCol) { t.HasCol = true; t.Col = known.Col; }
            else { t.HasCol = parsedHex; t.Col = parsedHex ? c : pnameCol; }
            t.Tag = known != null ? known.Tag : ("#" + hex.Substring(0, 3));
            t.Name = known != null ? known.Name : ("Team " + hex);
            t.Players.Clear();
            foreach (ZeepkistNetworkPlayer p in members)
            {
                SdPlayer sp = new SdPlayer();
                sp.SteamId = p.SteamID.ToString(CultureInfo.InvariantCulture);
                sp.Name = SafeName(p) ?? sp.SteamId;
                sp.Aliases = new string[0];
                t.Players.Add(sp);
            }
        }

        private SdTeam SdTeamByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (SdTeam t in sdTeams)
                if (string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        // Team logo, lazy-loaded once into a Texture2D from the logos/ folder next to the DLL and cached
        // by tag. The cache stores null too, so a team without a logo file isn't re-probed every frame -
        // the broadcast card just renders the coloured tag block for those.
        private Texture2D SdLogo(SdTeam t)
        {
            if (t == null || string.IsNullOrEmpty(t.Tag)) return null;
            Texture2D tex;
            if (sdLogoCache.TryGetValue(t.Tag, out tex)) return tex;
            tex = null;
            try
            {
                string file = !string.IsNullOrEmpty(t.LogoFile) ? t.LogoFile : ("S7_" + t.Tag + ".png");
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(Path.Combine(dir, "logos"), file);
                if (File.Exists(path))
                {
                    Texture2D t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(t2, File.ReadAllBytes(path)))
                    {
                        t2.filterMode = FilterMode.Bilinear;
                        t2.wrapMode = TextureWrapMode.Clamp;
                        tex = t2;
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("[sd] logo load failed for " + t.Tag + ": " + ex.Message); }
            sdLogoCache[t.Tag] = tex; // cache null too, to avoid re-probing a missing file every frame
            return tex;
        }

        // Draw a logo into a rect, letter-boxed to preserve aspect (logos are not all square). Returns
        // false when there's no logo, so the caller can fall back to the coloured tag block.
        private bool SdDrawLogo(SdTeam t, Rect box)
        {
            Texture2D tex = SdLogo(t);
            if (tex == null || tex.width <= 0 || tex.height <= 0) return false;
            float ar = (float)tex.width / tex.height;
            float w = box.width, h = box.height;
            if (w / h > ar) w = h * ar; else h = w / ar;
            Rect r = new Rect(box.x + (box.width - w) * 0.5f, box.y + (box.height - h) * 0.5f, w, h);
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            return true;
        }

        // Season map currently loaded, matched on the GTR hash (with the same "-N" version tolerance the
        // WR fetch uses). null when the lobby is on something outside the season pool.
        private SdMap SdMapForCurrent()
        {
            string h = CurrentLevelHash();
            if (string.IsNullOrEmpty(h)) return null;
            h = h.ToUpperInvariant();
            foreach (SdMap m in sdMaps) if (m.Hash == h) return m;
            string bh = StripHashVersion(h);
            foreach (SdMap m in sdMaps) if (StripHashVersion(m.Hash) == bh) return m;
            return null;
        }

        private static string StripHashVersion(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return hash;
            int dash = hash.LastIndexOf('-');
            if (dash <= 0 || dash >= hash.Length - 1) return hash;
            for (int i = dash + 1; i < hash.Length; i++) if (!char.IsDigit(hash[i])) return hash;
            return hash.Substring(0, dash);
        }

        // Which of a player's accounts is actually in the lobby right now. Some players share/swap
        // accounts (e.g. Pants + his brother Butter), so the pool lists a primary + alt_ids and we
        // resolve to whichever one is present. Falls back to the primary when none is in the lobby.
        private string SdActiveSid(SdPlayer p)
        {
            if (p == null) return null;
            if (SdInLobby(p.SteamId)) return p.SteamId;
            if (p.AltIds != null)
                foreach (string a in p.AltIds) if (SdInLobby(a)) return a;
            return p.SteamId;
        }

        private bool SdInLobby(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return false;
            try
            {
                List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
                if (list == null) return false;
                foreach (ZeepkistNetworkPlayer p in list)
                    if (p.SteamID.ToString(CultureInfo.InvariantCulture) == sid) return true;
            }
            catch { }
            return false;
        }

        // This round's live time for a Showdown player, or -1. Reads the replicated in-lobby leaderboard
        // (so a non-host caster works) rather than anything COTDTracker-shaped. Uses whichever of the
        // player's accounts is actually present.
        private float SdLiveTime(SdPlayer p)
        {
            string sidStr = SdActiveSid(p);
            if (string.IsNullOrEmpty(sidStr)) return -1f;
            ulong sid;
            if (!ulong.TryParse(sidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out sid)) return -1f;
            return GetRoundTime(sid);
        }

        // How many of a team's racers have a time this round. This is the FIRST thing that decides a
        // round in Showdown4 (Round.CompareFinishers) - more finishers wins outright, whatever the
        // times. It has to exist, because the cumulative time only sums finishers, so without it not
        // finishing would be an advantage.
        private int SdFinishers(SdTeam t)
        {
            if (t == null) return 0;
            int n = 0;
            for (int i = 0; i < t.Players.Count; i++) if (SdLiveTime(t.Players[i]) >= 0f) n++;
            return n;
        }

        // Sum of the finishers' times - the second tiebreak (Round.CompareCumulativeTeamTimes).
        private float SdCumulative(SdTeam t)
        {
            if (t == null) return -1f;
            float sum = 0f; int n = 0;
            for (int i = 0; i < t.Players.Count; i++)
            {
                float v = SdLiveTime(t.Players[i]);
                if (v >= 0f) { sum += v; n++; }
            }
            return n > 0 ? sum : -1f;
        }

        // Displayed team time: the average OF FINISHERS, matching Showdown4's GetAvgTimeOfTeam (it
        // divides by finishers, not by team size). -1 only when nobody on the team has a time.
        private float SdTeamAvg(SdTeam t, bool useProjected)
        {
            if (t == null || t.Players.Count == 0) return -1f;
            float sum = 0f; int n = 0;
            for (int i = 0; i < t.Players.Count; i++)
            {
                float v = -1f;
                if (useProjected)
                {
                    // The PB projection is a pre-race estimate, so it only means anything with every
                    // racer's PB known - a partial one would flatter whoever is missing.
                    if (!sdPb.TryGetValue(SdActiveSid(t.Players[i]) ?? "", out v)) return -1f;
                }
                else v = SdLiveTime(t.Players[i]);
                if (v < 0f) { if (useProjected) return -1f; continue; }
                sum += v; n++;
            }
            return n > 0 ? sum / n : -1f;
        }

        // Team qualifier average (seconds) from the pool's per-player qualifier times. Uses whichever of
        // the two rostered players have a qual on file; -1 when neither does.
        private float SdTeamQual(SdTeam t)
        {
            if (t == null) return -1f;
            float sum = 0f; int n = 0;
            for (int i = 0; i < t.Players.Count; i++)
                if (t.Players[i].Qual >= 0f) { sum += t.Players[i].Qual; n++; }
            return n > 0 ? sum / n : -1f;
        }

        // Yolo's Showdown cascade (from his interactive spec), in order:
        //   1 finishers -> 2 avg team time -> 3 avg qualifier time -> 4 didn't-pick-the-map -> 5 overtime.
        // We stop before "random": a coin flip must not appear on a broadcast as a result, so an
        // unresolved tie stays undecided (method "overtime") and the caster awards it. Deliberately
        // diverges from Showdown4's *code* cascade (which uses individual placements) - this matches the
        // rules doc Yolo specced. Returns -1 A leads, +1 B leads, 0 undecided.
        private int SdCompare(out string method, out float gap)
        {
            method = null; gap = -1f;
            if (sdA == null || sdB == null) return 0;

            // 1) Finishers decide until BOTH teams are complete (all rostered racers have a time).
            int fa = SdFinishers(sdA), fb = SdFinishers(sdB);
            bool completeA = fa >= sdA.Players.Count && sdA.Players.Count > 0;
            bool completeB = fb >= sdB.Players.Count && sdB.Players.Count > 0;
            if (!completeA || !completeB)
            {
                method = "finishers";
                if (fa == fb) return 0;         // tied and not complete -> undecided
                return fa > fb ? -1 : 1;
            }

            // 2) Both complete: lower average team time wins.
            method = "teamAvg";
            if (sdAvgA >= 0f && sdAvgB >= 0f && Mathf.Abs(sdAvgA - sdAvgB) > SD_EPS)
            { gap = Mathf.Abs(sdAvgA - sdAvgB); return sdAvgA < sdAvgB ? -1 : 1; }

            // 3) Team averages tied: lower qualifier average wins.
            if (sdQualA >= 0f && sdQualB >= 0f && Mathf.Abs(sdQualA - sdQualB) > SD_EPS)
            { method = "qualifier"; gap = Mathf.Abs(sdQualA - sdQualB); return sdQualA < sdQualB ? -1 : 1; }

            // 4) Qualifier tied (or unknown): the team that did NOT pick the current map wins.
            if (!sdPickerRandom && !string.IsNullOrEmpty(sdPickerTag))
            {
                if (string.Equals(sdPickerTag, sdA.Tag, StringComparison.OrdinalIgnoreCase)) { method = "mapPick"; return 1; }
                if (string.Equals(sdPickerTag, sdB.Tag, StringComparison.OrdinalIgnoreCase)) { method = "mapPick"; return -1; }
            }

            // 5) Nobody/both picked (or random map): overtime. Undecided - the caster awards it.
            method = "overtime";
            return 0;
        }

        // The deciding metric shown in the leaderboard's big right column, per the current stage - this is
        // WHY the leader leads, not just a fixed "avg time". Also the winner-side note and the loser-side
        // "+gap" diff. Mirrors the metric/note/diff logic in Yolo's interactive spec.
        private void SdComputeMetrics()
        {
            sdMetricA = "--"; sdMetricB = "--"; sdNoteA = ""; sdNoteB = "";
            sdDiffA = -1f; sdDiffB = -1f; sdMetricWord = false;
            if (sdA == null || sdB == null) return;
            string stage = sdLeadMethod ?? "finishers";
            switch (stage)
            {
                case "finishers":
                    sdMetricA = sdFinA + " / " + Math.Max(1, sdA.Players.Count);
                    sdMetricB = sdFinB + " / " + Math.Max(1, sdB.Players.Count);
                    if (sdLead < 0) sdNoteA = "MORE FINISHERS"; else if (sdLead > 0) sdNoteB = "MORE FINISHERS";
                    break;
                case "teamAvg":
                    sdMetricA = SdTime(sdAvgA); sdMetricB = SdTime(sdAvgB);
                    if (sdLead < 0) { sdNoteA = "FASTER TEAM AVERAGE"; sdDiffB = sdLeadGap; }
                    else if (sdLead > 0) { sdNoteB = "FASTER TEAM AVERAGE"; sdDiffA = sdLeadGap; }
                    break;
                case "qualifier":
                    sdMetricA = SdTime(sdQualA); sdMetricB = SdTime(sdQualB);
                    if (sdLead < 0) { sdNoteA = "FASTER QUALIFIER AVERAGE"; sdDiffB = sdLeadGap; }
                    else if (sdLead > 0) { sdNoteB = "FASTER QUALIFIER AVERAGE"; sdDiffA = sdLeadGap; }
                    break;
                case "mapPick":
                    sdMetricWord = true;
                    sdMetricA = sdLead < 0 ? "DIDN'T PICK" : "PICKED";
                    sdMetricB = sdLead > 0 ? "DIDN'T PICK" : "PICKED";
                    if (sdLead < 0) sdNoteA = "WON MAP-PICK TIEBREAK"; else if (sdLead > 0) sdNoteB = "WON MAP-PICK TIEBREAK";
                    break;
                case "overtime":
                    sdMetricWord = true;
                    sdMetricA = "+5:00"; sdMetricB = "+5:00";
                    sdNoteA = "ROUND EXTENDED"; sdNoteB = "ROUND EXTENDED";
                    break;
            }
        }

        private List<float> SdSortedTimes(SdTeam t)
        {
            List<float> r = new List<float>();
            if (t == null) return r;
            for (int i = 0; i < t.Players.Count; i++)
            {
                float v = SdLiveTime(t.Players[i]);
                if (v >= 0f) r.Add(v);
            }
            r.Sort();
            return r;
        }

        // Recompute everything the card renders. Runs on the 5 Hz Update poll so OnGUI only formats.
        private void SdRefresh()
        {
            SdDetectMatchup(false);
            sdCurMap = SdMapForCurrent();

            // The picker is per-map, so a new map must clear it. Without this the header would carry
            // "(AgOH pick)" onto the next round's map and quietly lie on stream.
            string mh = CurrentLevelHash();
            if (mh != sdLastMapHash)
            {
                sdLastMapHash = mh;
                sdPickerTag = null; sdPickerRandom = false;
            }

            sdAvgA = SdTeamAvg(sdA, false);
            sdAvgB = SdTeamAvg(sdB, false);
            sdProjA = SdTeamAvg(sdA, true);
            sdProjB = SdTeamAvg(sdB, true);
            sdFinA = SdFinishers(sdA);
            sdFinB = SdFinishers(sdB);
            sdQualA = SdTeamQual(sdA);
            sdQualB = SdTeamQual(sdB);
            int sdLeadPrev = sdLead;
            sdLead = SdCompare(out sdLeadMethod, out sdLeadGap);
            if (sdLead != sdLeadPrev) sdLeadChangedAt = Time.time; // stamp the flip so the arrows show, then fade
            SdComputeMetrics();
            SdRecomputeFinishPos();

            // NEW BEST flash. Reset the bests on a map change; flash only after a team's first average
            // exists (so the initial appearance doesn't flash), and only on a genuine improvement.
            string bmh = CurrentLevelHash();
            if (bmh != sdBestMapHash) { sdBestMapHash = bmh; sdBestAvgA = -1f; sdBestAvgB = -1f; }
            if (sdAvgA >= 0f && (sdBestAvgA < 0f || sdAvgA < sdBestAvgA - 0.0005f))
            { if (sdBestAvgA >= 0f) sdNewBestFlashA = Time.time + 3f; sdBestAvgA = sdAvgA; }
            if (sdAvgB >= 0f && (sdBestAvgB < 0f || sdAvgB < sdBestAvgB - 0.0005f))
            { if (sdBestAvgB >= 0f) sdNewBestFlashB = Time.time + 3f; sdBestAvgB = sdAvgB; }
        }

        // Rank every racer in the matchup by live time, so the broadcast leaderboard can list them by real
        // finish order (the two teams interleave). Racers with no time are left unranked (position 0).
        private void SdRecomputeFinishPos()
        {
            sdFinishPos.Clear();
            if (sdA == null || sdB == null) return;
            List<KeyValuePair<string, float>> timed = new List<KeyValuePair<string, float>>();
            for (int side = 0; side < 2; side++)
            {
                SdTeam t = side == 0 ? sdA : sdB;
                foreach (SdPlayer p in t.Players)
                {
                    string sid = SdActiveSid(p);
                    if (string.IsNullOrEmpty(sid)) continue;
                    float v = SdLiveTime(p);
                    if (v >= 0f) timed.Add(new KeyValuePair<string, float>(sid, v));
                }
            }
            timed.Sort(delegate (KeyValuePair<string, float> x, KeyValuePair<string, float> y)
            { return x.Value.CompareTo(y.Value); });
            for (int i = 0; i < timed.Count; i++) sdFinishPos[timed[i].Key] = i + 1;
        }

        private int SdFinishPos(SdPlayer p)
        {
            if (p == null) return 0;
            string sid = SdActiveSid(p);
            int pos;
            if (!string.IsNullOrEmpty(sid) && sdFinishPos.TryGetValue(sid, out pos)) return pos;
            return 0;
        }

        private static string SdMethodLabel(string m)
        {
            switch (m)
            {
                case "finishers": return "finishers";
                case "teamAvg": return "team average";
                case "qualifier": return "qualifier average";
                case "mapPick": return "map pick";
                case "overtime": return "overtime";
                default: return m ?? "";
            }
        }

        // Award the round to whoever has the better average. Idempotent per map hash so a re-fired
        // RoundEnded (or a rejoin) can't double-score, and silent unless both teams are complete.
        private void SdTryScoreRound()
        {
            if (castMode != CastMode.Showdown || sdA == null || sdB == null) return;
            if (SdRemoteFresh()) return; // the Showdown mod owns the score when it's broadcasting state
            if (sdLead == 0) return; // nobody finished, or a genuine dead heat -> caster awards it
            string key = CurrentLevelHash();
            if (string.IsNullOrEmpty(key)) return;
            if (!sdScored.Add(key)) return;
            if (sdLead < 0) sdPtsA++; else sdPtsB++;
            sdWinSeq.Add(sdLead < 0 ? sdA.Tag : sdB.Tag); // ordered history for the Bo3 pips
            Logger.LogInfo(string.Format(
                "[sd] round on {0} to {1} (by {2}): {3} {4:0.000} [{5} fin] - {6:0.000} [{7} fin] {8}  =>  {9} {10} - {11} {12}",
                sdCurMap != null ? sdCurMap.Name : key, sdLead < 0 ? sdA.Tag : sdB.Tag, sdLeadMethod,
                sdA.Tag, sdAvgA, sdFinA, sdAvgB, sdFinB, sdB.Tag,
                sdA.Tag, sdPtsA, sdPtsB, sdB.Tag));
        }

        // ---- GTR personal bests for the four racers on the current map ------------------------------
        // One query covers the whole matchup. Verified query shape (personalBestGlobals is one row per
        // user+level, so no dedupe needed):
        //   {levels(filter:{hash:{equalTo:"HASH"}}){nodes{
        //      personalBestGlobals(filter:{user:{steamId:{in:["..",".."]}}}){
        //        nodes{user{steamId steamName} record{time}}}}}}

        private void MaybeFetchSdPbs()
        {
            if (sdPbFetching) return;
            if (sdA == null || sdB == null) return;
            string hash = CurrentLevelHash();
            if (string.IsNullOrEmpty(hash)) return;

            List<string> sids = new List<string>();
            for (int side = 0; side < 2; side++)
            {
                SdTeam t = side == 0 ? sdA : sdB;
                foreach (SdPlayer p in t.Players)
                {
                    // Query the account that's actually here (covers shared/alt accounts like Pants).
                    string a = SdActiveSid(p);
                    if (!string.IsNullOrEmpty(a) && !sids.Contains(a)) sids.Add(a);
                }
            }
            if (sids.Count == 0) return;
            sids.Sort(StringComparer.Ordinal);
            string key = hash.ToUpperInvariant() + "|" + string.Join(",", sids.ToArray());
            if (key == sdPbKey) return; // already have it (or already tried) for this map + roster

            sdPbFetching = true;
            sdPbKey = key;              // claim now so the poll doesn't re-fire; result commits via pending
            sdPb.Clear();               // drop the previous map's PBs immediately
            string hashUp = hash.ToUpperInvariant();
            string[] sidArr = sids.ToArray();
            Logger.LogInfo("[sd] PB fetch start for hash " + hashUp + " (" + sidArr.Length + " racers)");
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                Dictionary<string, float> res = null;
                try { res = FetchSdPbsBlocking(hashUp, sidArr); }
                catch { res = null; }
                sdPbPendingMap = res; sdPbPendingKey = key; sdPbPending = true;
            });
            th.IsBackground = true;
            try { th.Start(); }
            catch { sdPbFetching = false; }
        }

        private Dictionary<string, float> FetchSdPbsBlocking(string hashUpper, string[] sids)
        {
            Dictionary<string, float> r = TrySdPbs(hashUpper, sids);
            if (r != null && r.Count > 0) return r;
            // Adjusted/versioned levels carry a "-N" suffix; GTR may key the base hash.
            string bh = StripHashVersion(hashUpper);
            if (bh != hashUpper)
            {
                Dictionary<string, float> r2 = TrySdPbs(bh, sids);
                if (r2 != null && r2.Count > 0) return r2;
            }
            Logger.LogInfo("[sd] no PBs found for hash " + hashUpper);
            return r;
        }

        private Dictionary<string, float> TrySdPbs(string hashUpper, string[] sids)
        {
            System.Text.StringBuilder inList = new System.Text.StringBuilder();
            for (int i = 0; i < sids.Length; i++)
            {
                if (i > 0) inList.Append(',');
                inList.Append('"').Append(sids[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            string esc = hashUpper.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string body = GqlPost("{levels(filter:{hash:{equalTo:\"" + esc + "\"}}){nodes{" +
                "personalBestGlobals(filter:{user:{steamId:{in:[" + inList + "]}}}){" +
                "nodes{user{steamId} record{time}}}}}}");
            if (body == null) { Logger.LogWarning("[sd] GTR PB request failed/timed out for " + hashUpper); return null; }
            Dictionary<string, float> map = new Dictionary<string, float>();
            try
            {
                JToken nodes = JObject.Parse(body).SelectToken("data.levels.nodes[0].personalBestGlobals.nodes");
                JArray arr = nodes as JArray;
                if (arr != null)
                {
                    foreach (JToken n in arr)
                    {
                        JToken sidTok = n.SelectToken("user.steamId");
                        JToken tTok = n.SelectToken("record.time");
                        if (sidTok == null || tTok == null) continue;
                        map[(string)sidTok] = (float)(double)tTok;
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("[sd] PB parse failed: " + ex.Message); return null; }
            Logger.LogInfo(string.Format("[sd] {0} -> {1} PBs", hashUpper, map.Count));
            return map;
        }

        // A team colour is only a tint here, never a fill, but #000000 (Panthers) still vanishes on the
        // dark card and #ffefad (Silver Hydroxide) glares. Push very dark colours up and leave the rest.
        private static Color ReadableOn(Color c)
        {
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            if (lum >= 0.35f) return c;
            if (lum <= 0.02f) return new Color(0.72f, 0.74f, 0.80f); // pure black -> neutral grey
            float k = 0.35f / Mathf.Max(lum, 0.001f);
            return new Color(Mathf.Min(c.r * k, 1f), Mathf.Min(c.g * k, 1f), Mathf.Min(c.b * k, 1f));
        }

        private void OnCommand(string args)
        {
            args = (args ?? "").Trim();
            string lower = args.ToLowerInvariant();

            // Any explicit /overlay command (other than turning it off) wakes the mod for this session.
            if (cfgEnabled != null && !(lower == "off" || lower == "clear" || lower == "hide"))
                cfgEnabled.Value = true;

            if (lower == "off" || lower == "clear" || lower == "hide")
            {
                ClearAll();
                ChatApi.AddLocalMessage("Overlay off.");
                return;
            }
            // Diagnostic: print the live photomode camera state so we can pin which value is the
            // dynamic/smooth follow (the integers are unnamed). Must come before the "cam" prefix.
            if (lower.StartsWith("camstate"))
            {
                FlyingCameraScript fc = GetFlyingCamera();
                if (fc == null) ChatApi.AddLocalMessage("camstate: not in photomode (no fly camera).");
                else ChatApi.AddLocalMessage(string.Format(
                    "camstate: currentCameraState={0}, alternate={1}  (cycle cameras + re-run to find dynamic follow)",
                    fc.currentCameraState, fc.alternateCameraState));
                return;
            }
            if (lower.StartsWith("cam"))
            {
                string v = args.Substring(3).Trim().ToLowerInvariant();
                if (v == "on" || v == "off")
                {
                    camLink = (v == "on");
                    if (!camLink) shownFollowSid = null;
                    SaveLayout();
                    ChatApi.AddLocalMessage("Camera sync " + (camLink ? "on." : "off."));
                }
                else ChatApi.AddLocalMessage("Camera sync is " + (camLink ? "on" : "off") + ". Usage: /overlay cam on|off");
                return;
            }
            // Runtime toggle for the stay-in-photomode setting, so it works without a config-manager mod
            // (still backed by the .cfg, so the choice persists across restarts).
            if (lower.StartsWith("staycam"))
            {
                bool? b = ParseOnOff(args.Substring("staycam".Length));
                if (b.HasValue) { cfgStayInPhotomode.Value = b.Value; if (b.Value) stayEnterPending = true; ChatApi.AddLocalMessage("Stay in photomode: " + (b.Value ? "on" : "off")); }
                else ChatApi.AddLocalMessage("Stay in photomode is " + (cfgStayInPhotomode.Value ? "on" : "off") + ". Usage: /overlay staycam on|off");
                return;
            }
            if (lower == "test")
            {
                mode = Mode.Test;
                ChatApi.AddLocalMessage("Overlay test card shown.");
                return;
            }
            if (lower == "panel")
            {
                TogglePanel();
                ChatApi.AddLocalMessage("Control panel " + (showPanel ? "open." : "closed (mod still on; F4 turns the mod off)."));
                return;
            }
            if (lower == "wins" || lower == "roundwins")
            {
                mode = Mode.RoundWins;
                shownFollowSid = null;
                ChatApi.AddLocalMessage("Round wins card shown.");
                return;
            }
            if (lower.StartsWith("elim"))
            {
                int n;
                if (int.TryParse(args.Substring(4).Trim(), out n) && n >= 0)
                {
                    elimCount = n;
                    ChatApi.AddLocalMessage("Elim/round set to " + n + ".");
                }
                else ChatApi.AddLocalMessage("Elim/round is " + elimCount + ". Usage: /overlay elim <N>");
                return;
            }
            if (lower.StartsWith("pool"))   // [Stats] pool: which comp's numbers to show
            {
                string c = args.Substring(4).Trim().ToLowerInvariant();
                if (availableComps.Contains(c)) { selectedComp = c; SaveLayout(); ChatApi.AddLocalMessage("Stats pool: " + CompLabel(c)); }
                else ChatApi.AddLocalMessage("Stats pool is " + CompLabel(selectedComp) + ". Options: " + string.Join(", ", availableComps.ToArray()));
                return;
            }
            if (lower.StartsWith("comp"))   // [Comp] logic: which cup format orders the list
            {
                string c = args.Substring(4).Trim().ToLowerInvariant();
                if (c == "cup") { castMode = CastMode.Cup; SaveLayout(); ChatApi.AddLocalMessage("Comp: Cup"); }
                else if (c == "topout") { castMode = CastMode.Topout; SaveLayout(); ChatApi.AddLocalMessage("Comp: Topout"); }
                else if (c == "pursuit") { castMode = CastMode.Pursuit; SaveLayout(); ChatApi.AddLocalMessage("Comp: Pursuit"); }
                else if (c == "showdown" || c == "sd") { castMode = CastMode.Showdown; SaveLayout(); SdDetectMatchup(true); ChatApi.AddLocalMessage("Comp: Showdown"); }
                else ChatApi.AddLocalMessage("Comp is " + CastLabel(castMode) + ". Options: cup, topout, pursuit, showdown");
                return;
            }
            if (lower.StartsWith("stats"))
            {
                string name = args.Substring(5).Trim();
                if (name.Length == 0) { ChatApi.AddLocalMessage("Usage: /overlay stats <name>"); return; }
                Stat s = Resolve(name);
                if (s == null) { ChatApi.AddLocalMessage("No stats for '" + name + "'."); return; }
                target1 = s; mode = Mode.Stats; statsPinned = true; // pinned lookup; camera won't override
                ChatApi.AddLocalMessage("Stats: " + s.Name);
                return;
            }
            if (lower.StartsWith("h2h"))
            {
                string rest = args.Substring(3).Trim();
                string[] parts = SplitTwo(rest);
                if (parts == null) { ChatApi.AddLocalMessage("Usage: /overlay h2h <name1> <name2>"); return; }
                Stat a = Resolve(parts[0]);
                Stat b = Resolve(parts[1]);
                if (a == null) { ChatApi.AddLocalMessage("No stats for '" + parts[0] + "'."); return; }
                if (b == null) { ChatApi.AddLocalMessage("No stats for '" + parts[1] + "'."); return; }
                target1 = a; target2 = b; mode = Mode.H2H; shownFollowSid = null;
                droneOn = true; EnsureDrone(); // auto-open the VS compare cam (button still toggles)
                ChatApi.AddLocalMessage(string.Format("H2H: {0} vs {1}", a.Name, b.Name));
                return;
            }
            if (lower == "reset")
            {
                ResetLive();
                ChatApi.AddLocalMessage("Live cup times reset.");
                return;
            }
            if (lower == "resetpos" || lower == "resetwindows")
            {
                ResetPositions();
                showPanel = true; // open so the drag grips are visible to re-place the boxes
                ChatApi.AddLocalMessage("Overlay windows reset to default positions.");
                return;
            }
            if (lower.StartsWith("times"))
            {
                string name = args.Substring(5).Trim();
                if (name.Length == 0) { ChatApi.AddLocalMessage("Usage: /overlay times <name>"); return; }
                string key = ResolveLiveName(name);
                if (key == null)
                {
                    ChatApi.AddLocalMessage("No round times yet for '" + name + "' this cup.");
                    return;
                }
                liveTarget = key; mode = Mode.Times; shownFollowSid = null;
                ChatApi.AddLocalMessage("Times: " + key);
                return;
            }
            if (lower == "sd" || lower.StartsWith("sd "))
            {
                HandleSdCommand(args.Length > 2 ? args.Substring(2).Trim() : "");
                return;
            }
            if (lower == "help" || lower == "?" || lower == "commands")
            {
                ChatApi.AddLocalMessage("Tournament Casting UI - F4 = master on/off, F5 = clear cards.");
                ChatApi.AddLocalMessage("View: /overlay panel | stats <name> | h2h <a> <b> | times <name> | wins | test | off");
                ChatApi.AddLocalMessage("Setup: /overlay comp cup|topout|pursuit|showdown | pool <comp> | elim <N> | resetpos");
                ChatApi.AddLocalMessage("Camera: /overlay cam on|off | staycam on|off | camstate");
                ChatApi.AddLocalMessage("Showdown: most controls are BUTTONS in the panel (no typing needed).");
                ChatApi.AddLocalMessage("Showdown: /overlay sd (state) | sd <tagA> <tagB> | sd auto | sd reset | sd sim");
                return;
            }
            ChatApi.AddLocalMessage("F4 = master on/off. /overlay help for the full list. Quick: panel | stats <name> | h2h <a> <b> | times <name> | comp showdown | sd | off");
        }

        // Everything a caster might need to correct live. The mod auto-detects the matchup and scores
        // rounds on its own, so these are only for when reality drifts: a substitute nobody registered,
        // a mis-detected pairing, a round the game didn't end cleanly.
        private void HandleSdCommand(string rest)
        {
            rest = (rest ?? "").Trim();
            string low = rest.ToLowerInvariant();

            if (rest.Length == 0)
            {
                if (castMode != CastMode.Showdown) ChatApi.AddLocalMessage("Showdown mode is off. Turn it on with /overlay comp showdown");
                if (sdTeams.Count == 0) { ChatApi.AddLocalMessage("No showdown pool loaded (showdown_pool.json)."); return; }
                if (sdA == null || sdB == null)
                {
                    ChatApi.AddLocalMessage(string.Format("Pool: season {0}, {1} teams, {2} maps. No matchup detected yet.",
                        sdSeason, sdTeams.Count, sdMaps.Count));
                    return;
                }
                ChatApi.AddLocalMessage(string.Format("{0} {1} - {2} {3}   ({4})   teams from: {5}",
                    sdA.Tag, sdPtsA, sdPtsB, sdB.Tag,
                    sdCurMap != null ? ("#" + sdCurMap.N + " " + sdCurMap.Name) : "off-pool map",
                    SdTeamSource()));
                ChatApi.AddLocalMessage(string.Format("avg: {0} {1}  |  {2} {3}",
                    sdA.Tag, SdTime(sdAvgA), sdB.Tag, SdTime(sdAvgB)));
                return;
            }

            if (low == "reset")
            {
                sdPtsA = 0; sdPtsB = 0; sdScored.Clear(); sdWinSeq.Clear(); sdPickerTag = null; sdPickerRandom = false;
                ChatApi.AddLocalMessage("Showdown: new match (score and picks cleared).");
                return;
            }
            if (low == "auto")
            {
                sdMatchupForced = false; sdRosterSig = null; SdDetectMatchup(true);
                ChatApi.AddLocalMessage(sdA != null && sdB != null
                    ? ("Showdown: auto-detect on -> " + sdA.Tag + " vs " + sdB.Tag)
                    : "Showdown: auto-detect on (no matchup detected yet).");
                return;
            }
            if (low == "sim")
            {
                // Feed a sample @SDSTATE@ payload through the real receiver, to test the handshake path
                // before the Showdown mod emits it for real.
                castMode = CastMode.Showdown; SaveLayout();
                SdCaptureState(SdSimPayload());
                ChatApi.AddLocalMessage("Showdown: injected a simulated @SDSTATE@ payload (STBN vs AgOH, 1-0).");
                return;
            }
            if (low == "arrows")
            {
                // Debug: force the movement arrows so their look can be checked without a live result.
                sdDbgMove = (sdDbgMove + 1) % 3;
                ChatApi.AddLocalMessage("Showdown: debug arrows = " +
                    (sdDbgMove == 0 ? "off (=)" : sdDbgMove == 1 ? "top up / bottom down" : "sides swapped (down/up)"));
                return;
            }
            if (low == "random")
            {
                sdPickerRandom = true; sdPickerTag = null;
                ChatApi.AddLocalMessage("Showdown: current map marked as randomised.");
                return;
            }
            if (low.StartsWith("pick"))
            {
                string tag = rest.Substring(4).Trim();
                SdTeam t = SdTeamByTag(tag);
                if (t == null) { ChatApi.AddLocalMessage("Unknown team tag '" + tag + "'. Usage: /overlay sd pick <tag>"); return; }
                sdPickerTag = t.Tag; sdPickerRandom = false;
                ChatApi.AddLocalMessage("Showdown: current map picked by " + t.Tag + ".");
                return;
            }
            if (low.StartsWith("score"))
            {
                string[] parts = SplitTwo(rest.Substring(5).Trim());
                int a, b;
                if (parts == null ||
                    !int.TryParse(parts[0], out a) || !int.TryParse(parts[1], out b) ||
                    a < 0 || b < 0)
                { ChatApi.AddLocalMessage("Usage: /overlay sd score <a> <b>"); return; }
                sdPtsA = a; sdPtsB = b;
                ChatApi.AddLocalMessage(string.Format("Showdown score set: {0} {1} - {2} {3}",
                    sdA != null ? sdA.Tag : "A", a, b, sdB != null ? sdB.Tag : "B"));
                return;
            }
            if (low.StartsWith("move"))
            {
                // Prefer the panel buttons for this - the game has no clipboard, so typing a name mid-cast
                // is painful. Kept for scripted/setup use.
                string all = rest.Substring(4).Trim();
                int sp = all.LastIndexOf(' ');
                if (sp <= 0) { ChatApi.AddLocalMessage("Select a player in the panel and use the team buttons, or: /overlay sd move <player> <tag>"); return; }
                string who = all.Substring(0, sp).Trim();
                SdTeam t = SdTeamByTag(all.Substring(sp + 1).Trim());
                if (t == null) { ChatApi.AddLocalMessage("Unknown team tag."); return; }
                string sid = SdResolveSid(who);
                if (sid == null) { ChatApi.AddLocalMessage("No player in the lobby matching '" + who + "'."); return; }
                SdMoveSid(sid, t);
                return;
            }

            // "sd <tagA> <tagB>" - pin the matchup.
            string[] tags = SplitTwo(rest);
            if (tags != null)
            {
                SdTeam ta = SdTeamByTag(tags[0]);
                SdTeam tb = SdTeamByTag(tags[1]);
                if (ta == null) { ChatApi.AddLocalMessage("Unknown team tag '" + tags[0] + "'."); return; }
                if (tb == null) { ChatApi.AddLocalMessage("Unknown team tag '" + tags[1] + "'."); return; }
                if (ta == tb) { ChatApi.AddLocalMessage("Pick two different teams."); return; }
                sdA = ta; sdB = tb; sdMatchupForced = true;
                sdPtsA = 0; sdPtsB = 0; sdScored.Clear(); sdWinSeq.Clear(); sdPickerTag = null; sdPickerRandom = false;
                sdPbKey = null;
                castMode = CastMode.Showdown; SaveLayout();
                ChatApi.AddLocalMessage("Showdown: " + ta.Tag + " vs " + tb.Tag + " (pinned; /overlay sd auto to unpin)");
                return;
            }

            ChatApi.AddLocalMessage("Usage: /overlay sd | sd <tagA> <tagB> | sd auto | sd score <a> <b> | sd pick <tag> | sd random | sd reset | sd sim | sd arrows  (most of this is buttons in the panel)");
        }

        // Steam id for a typed name from the LIVE lobby roster. Unlike Resolve() this doesn't require
        // the player to be in the stats pool, which is the whole point: a last-minute substitute won't be.
        private string SdResolveSid(string query)
        {
            try
            {
                List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
                if (list == null) return null;
                string q = query.Trim().ToLowerInvariant();
                ZeepkistNetworkPlayer hit = MatchRoster(list, q, 0);
                if (hit == null) hit = MatchRoster(list, q, 1);
                if (hit == null) hit = MatchRoster(list, q, 2);
                return hit == null ? null : hit.SteamID.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // Parse an on/off argument (also accepts 1/0, true/false). null = neither (show current state).
        private static bool? ParseOnOff(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true") return true;
            if (s == "off" || s == "0" || s == "false") return false;
            return null;
        }

        // Split "alice bob" into two names. If both are single tokens this is trivial;
        // for now we require two space-separated tokens (quotes optional, stripped).
        private static string[] SplitTwo(string s)
        {
            s = s.Replace("\"", " ");
            string[] raw = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (raw.Length < 2) return null;
            // First token = name1, remainder = name2 (lets name2 contain spaces).
            string n1 = raw[0];
            string n2 = string.Join(" ", raw, 1, raw.Length - 1);
            return new string[] { n1, n2 };
        }

        // Resolve a typed name to a Stat: first match against the current lobby roster
        // (by SteamID, the reliable key), then fall back to a pool name search.
        private Stat Resolve(string query)
        {
            string q = query.Trim().ToLowerInvariant();
            try
            {
                List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
                if (list != null)
                {
                    // exact, then prefix, then contains
                    ZeepkistNetworkPlayer hit = MatchRoster(list, q, 0);
                    if (hit == null) hit = MatchRoster(list, q, 1);
                    if (hit == null) hit = MatchRoster(list, q, 2);
                    if (hit != null)
                    {
                        Stat s;
                        if (pool.TryGetValue(hit.SteamID.ToString(CultureInfo.InvariantCulture), out s))
                            return s;
                    }
                }
            }
            catch { }
            // Fallback: search the pool directly by name.
            return MatchPoolByName(q);
        }

        private ZeepkistNetworkPlayer MatchRoster(List<ZeepkistNetworkPlayer> list, string q, int kind)
        {
            foreach (ZeepkistNetworkPlayer p in list)
            {
                string n = SafeName(p);
                if (n == null) continue;
                n = n.ToLowerInvariant();
                if (kind == 0 && n == q) return p;
                if (kind == 1 && n.StartsWith(q)) return p;
                if (kind == 2 && n.Contains(q)) return p;
            }
            return null;
        }

        private Stat MatchPoolByName(string q)
        {
            Stat prefix = null;
            Stat contains = null;
            foreach (KeyValuePair<string, Stat> kv in pool)
            {
                string n = (kv.Value.Name ?? "").ToLowerInvariant();
                if (n == q) return kv.Value;
                if (prefix == null && n.StartsWith(q)) prefix = kv.Value;
                if (contains == null && n.Contains(q)) contains = kv.Value;
            }
            return prefix != null ? prefix : contains;
        }

        private static string SafeName(ZeepkistNetworkPlayer p)
        {
            try
            {
                MethodInfo mi = p.GetType().GetMethod("GetUserNameNoTag",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (mi != null)
                {
                    object r = mi.Invoke(p, null);
                    if (r != null) return r.ToString();
                }
            }
            catch { }
            return null;
        }

        // ---------------- Live cup tracking (BepInEx log listener) ----------------

        // ILogListener: receives every in-process BepInEx log event. We only act on
        // COTDTracker's. MUST NOT throw (would disrupt logging) -> wrapped in try/catch.
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null || eventArgs.Source == null) return;
                if (eventArgs.Source.SourceName != "COTDTracker") return;
                string msg = eventArgs.Data == null ? "" : eventArgs.Data.ToString();
                HandleCotd(msg);
            }
            catch { }
        }

        public void Dispose() { }

        // Ported verbatim from casting-tool/parser.py CupState.process_line.
        private void HandleCotd(string msg)
        {
            if (msg.IndexOf("Doing eliminations with leaderboard", StringComparison.Ordinal) >= 0)
            {
                // A fresh cup begins on the first leaderboard after a Winner.
                if (cupOver) ResetLive();
                pendingRound = true;
                roundTimes.Clear();
                curRoundMapUid = null; // round boundary: re-arm map capture (robust to an abandoned round)
                return;
            }

            Match m = Regex.Match(msg, @"Player (.+?): Time: (.+)");
            if (m.Success)
            {
                roundTimes[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
                // First time of the round: freeze the map this round is on (players have raced, so the
                // lobby is still on this level -> no race with the host advancing to the next map).
                if (curRoundMapUid == null)
                {
                    string uid = CurrentLevelUid();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        curRoundMapUid = uid;
                        if (!mapNames.ContainsKey(uid))
                        {
                            string nm = CurrentLevelName();
                            mapNames[uid] = string.IsNullOrEmpty(nm) ? uid : nm;
                            mapOrder.Add(uid);
                        }
                    }
                }
                return;
            }

            Match m2 = Regex.Match(msg, @"Eliminating (DNF|on time): (.+)");
            if (m2.Success)
            {
                // First elimination confirms a real (non-discovery) round.
                if (pendingRound) { liveRound++; pendingRound = false; }
                // Track who is OUT of the cup (drives the panel's alive/eliminated split).
                eliminatedLive.Add(m2.Groups[2].Value.Trim());
                return;
            }

            Match m3 = Regex.Match(msg, @"Eliminating (\d+) players:");
            if (m3.Success)
            {
                // Learn the elimination count for the red/yellow zones.
                int n;
                if (int.TryParse(m3.Groups[1].Value, out n) && n > 0) elimCount = n;
                // Round end: flush this round's times into the per-player history.
                foreach (KeyValuePair<string, string> kv in roundTimes)
                {
                    List<RoundTime> list;
                    if (!playerRoundTimes.TryGetValue(kv.Key, out list))
                    {
                        list = new List<RoundTime>();
                        playerRoundTimes[kv.Key] = list;
                    }
                    list.Add(new RoundTime(liveRound, kv.Value, curRoundMapUid));
                }
                curRoundMapUid = null; // re-arm: next round captures its own map on the first time
                return;
            }

            Match m4 = Regex.Match(msg, @"Winner[:\s]+(.+)");
            if (m4.Success)
            {
                cupOver = true;
                return;
            }
        }

        private void ResetLive()
        {
            liveRound = 0;
            pendingRound = false;
            cupOver = false;
            roundTimes.Clear();
            playerRoundTimes.Clear();
            eliminatedLive.Clear();
            curRoundMapUid = null;
            mapNames.Clear();
            mapOrder.Clear();
        }

        // Tag-tolerant match of a lobby name against a set of COTDTracker names (logged without the
        // clan tag; lobby names may carry one) -> suffix match either way.
        private static bool NameMatchesSet(string lobbyName, IEnumerable<string> names)
        {
            if (string.IsNullOrEmpty(lobbyName)) return false;
            string q = lobbyName.Trim().ToLowerInvariant();
            foreach (string n in names)
            {
                string e = (n ?? "").ToLowerInvariant();
                if (e.Length < 3 || q.Length < 3) { if (e == q) return true; continue; }
                if (e == q || q.EndsWith(e) || e.EndsWith(q)) return true;
            }
            return false;
        }

        // Is this lobby player already eliminated from the running cup?
        private bool IsOut(string lobbyName)
        {
            return eliminatedLive.Count != 0 && NameMatchesSet(lobbyName, eliminatedLive);
        }

        // Resolve a typed name to a key in playerRoundTimes (names as COTDTracker logged them).
        private string ResolveLiveName(string query)
        {
            string q = query.Trim().ToLowerInvariant();
            string prefix = null;
            string contains = null;
            foreach (KeyValuePair<string, List<RoundTime>> kv in playerRoundTimes)
            {
                string n = kv.Key.ToLowerInvariant();
                if (n == q) return kv.Key;
                if (prefix == null && n.StartsWith(q)) prefix = kv.Key;
                if (contains == null && n.Contains(q)) contains = kv.Key;
            }
            return prefix != null ? prefix : contains;
        }

        // ---------------- Control panel (click-to-cast) ----------------

        private void Update()
        {
            // Apply a freshly-fetched stats pool (downloaded on a background thread).
            if (pendingPoolJson != null)
            {
                string j = pendingPoolJson; pendingPoolJson = null;
                ApplyFetchedPool(j);
            }
            if (pendingSdJson != null)
            {
                string j = pendingSdJson; pendingSdJson = null;
                ApplySdPool(j, "repo");
            }
            // Also sweep the game's own chat list for a state payload, in case ZeepSDK doesn't re-raise
            // the host's custom chat message through ChatMessageReceived (Showdown4 08a0ac6 send path).
            try { SdPollChatMessages(); } catch { }
            // Apply a Showdown-mod state payload captured off-thread by the chat handlers.
            if (pendingSdState != null)
            {
                string b = pendingSdState; pendingSdState = null;
                try { SdApplyStatePayload(b); } catch (Exception ex) { Logger.LogWarning("[sd] state parse failed: " + ex.Message); }
            }

            // Commit a finished Showdown PB fetch from the background thread.
            if (sdPbPending)
            {
                sdPbPending = false;
                sdPbFetching = false;
                Dictionary<string, float> m = sdPbPendingMap;
                if (sdPbPendingKey != null && sdPbPendingKey == sdPbKey && m != null)
                {
                    sdPb.Clear();
                    foreach (KeyValuePair<string, float> kv in m) sdPb[kv.Key] = kv.Value;
                }
            }

            // Commit a finished world-record fetch from the background thread.
            if (wrPending)
            {
                wrPending = false;
                wrFetching = false;
                if (wrPendingUid != null && wrPendingUid == wrUid)
                {
                    wrHolder = wrPendingHolder != null ? wrPendingHolder : "";
                    wrTime = wrPendingTime != null ? wrPendingTime : "";
                }
            }

            // Master enable gate. OFF by default and re-armed OFF each launch, so the mod is fully
            // dormant (nothing renders, no camera tracking) until the caster opts in - this is what
            // stops a racer who merely installed it from seeing stat cards when they cycle photomode.
            bool en = cfgEnabled != null && cfgEnabled.Value;
            if (en != enabledApplied)
            {
                enabledApplied = en;
                // wipe + hand the mouse back on the on->off edge (Update returns early while off, so the
                // per-frame mouse-look reconcile won't run to restore a frozen sensitivity otherwise).
                if (!en) { showPanel = false; try { ClearAll(); } catch { } try { FreezeMouseLook(false); } catch { } }
            }
            if (!en)
            {
                // Dormant: the panel key (or the menu tick / a /overlay command) is the only thing that
                // wakes it. No rendering, no FindObjectOfType, no drone reflection while off.
                try { if (cfgEnabled != null && Input.GetKeyDown(cfgKeyPanel != null ? cfgKeyPanel.Value : KeyCode.F4)) { cfgEnabled.Value = true; showPanel = true; } }
                catch { }
                try { ReconcileCursor(); } catch { } // guarantee the mouse is handed back if it was held
                return;
            }

            // F4 is the MASTER switch: while the mod is enabled, pressing it turns the whole mod OFF
            // (next frame the gate above wipes everything and goes dormant), so nothing renders and
            // cycling players in photomode can't bring cards back until F4 re-enables. F5 clears the
            // cards but leaves the mod on. (The click-list can still be hidden mid-cast via
            // "/overlay panel" without disabling the mod.)
            try
            {
                if (Input.GetKeyDown(cfgKeyPanel != null ? cfgKeyPanel.Value : KeyCode.F4))
                { cfgEnabled.Value = false; showPanel = false; }
                if (Input.GetKeyDown(cfgKeyClear != null ? cfgKeyClear.Value : KeyCode.F5)) ClearAll();
            }
            catch { }

            // Cursor save/restore is reconciled from showPanel every frame, so the saved state is
            // restored the instant the panel closes by ANY path (F4, photomode exit, leaving the
            // lobby) - this is what prevents a "lost mouse". OnGUI does the actual freeing.
            try { ReconcileCursor(); }
            catch { }

            // Mouse-look freeze is reconciled every frame (cheap): the free-cam reads sensitivity
            // each frame and the game re-reads its settings at round transitions, so a 5 Hz re-assert
            // left ~200 ms windows where the camera still swung. Per-frame keeps it solid when on.
            try { ReconcileMouseFreeze(); }
            catch { }

            // Keep the Stats card bound to whoever the photomode camera is following, the compare
            // drone alive/targeted, and the stay-in-photomode auto-enter ticking (~5 Hz).
            try
            {
                camPollAccum += Time.deltaTime;
                if (camPollAccum >= 0.2f)
                {
                    camPollAccum = 0f;
                    // Camera/drone work only matters in photomode; gating it here means zero
                    // FindObjectOfType scans and zero drone reflection while just racing.
                    if (inPhotoMode) { PollCamera(); if (camRestorePending) RestoreCamera(); }
                    // Drone upkeep runs EVERY tick, in or out of photomode. Both of these destroy by
                    // drone ID when the scene isn't wanted, and a live PhotoDrone renders a camera
                    // whether or not anything of ours is on screen - so "hidden" is not good enough,
                    // it has to be gone. Gating these on inPhotoMode was leaving cameras running.
                    EnsureDrone();
                    EnsureQuadDrones();
                    TryEnterPhotomode(); // must run ungated: its whole job is to ENTER photomode
                    // World record: only fetch while the overlay bar/cards are actually up (the caster is
                    // casting, not racing) - never on round start, so zero race-time data use or hitches.
                    if (showPanel || mode != Mode.None) MaybeFetchWr();
                    // Rebuild the click-list + mode-bar text off the render path (see panelRowsCache).
                    if (showPanel) { try { panelRowsCache = BuildPanelRows(); } catch { } }
                    else panelRowsCache = null;
                    // Showdown state: matchup, live averages, PB projection. Same "compute here, format
                    // in OnGUI" rule as everything above. Runs before the bar refresh so the bar's
                    // Showdown lines see this tick's data.
                    if (castMode == CastMode.Showdown)
                    {
                        try { SdRefresh(); } catch { }
                        if (showPanel || mode != Mode.None || SdMatchLive()) { try { MaybeFetchSdPbs(); } catch { } }
                    }
                    if (showPanel || mode != Mode.None || SdMatchLive()) { try { RefreshModeBarLines(); } catch { } }
                }
            }
            catch { }
        }

        private void TogglePanel()
        {
            showPanel = !showPanel; // cursor save/restore is owned by ReconcileCursor (Update)
        }

        // Own the cursor lifecycle off a single source of truth (showPanel): save the game's cursor
        // state when the panel opens, and ALWAYS restore it when the panel closes - no matter which
        // path closed it. The freeing-while-open is done in OnGUI (latest in the frame, so it beats
        // the game re-locking the cursor). Without this guaranteed restore a missed close path leaves
        // the cursor freed = "lost mouse" (reported by Kilandor). Save runs before OnGUI's force
        // because Update precedes OnGUI, so we capture the real state, not the freed one.
        private void ReconcileCursor()
        {
            if (showPanel)
            {
                if (!cursorSaved) { prevLock = Cursor.lockState; prevCursorVisible = Cursor.visible; cursorSaved = true; }
            }
            else if (cursorSaved)
            {
                Cursor.lockState = prevLock;
                Cursor.visible = prevCursorVisible;
                cursorSaved = false;
            }
        }

        // Zero the photomode MOUSE look sensitivity while the panel is open (and restore after), so
        // moving the mouse to click doesn't swing the camera. Camera look = LookAxis * sensitivity, and
        // the game picks mouse-vs-controller sensitivity by LAST-ACTIVE device - so the controller keeps
        // its own (left alone) and the pad flies normally while the menu is up. The one gap is a
        // controller being last-active while the mouse is moved (the mouse delta would then ride the
        // controller sensitivity); the guard below closes it by also zeroing the controller sensitivity
        // ONLY on frames where the mouse actually moves, restoring it the moment the mouse is still.
        private void FreezeMouseLook(bool freeze)
        {
            try
            {
                PlayerManager pm = PlayerManager.Instance;
                GameSettingsScriptableObject s = (pm != null && pm.instellingen != null) ? pm.instellingen.Settings : null;
                if (s == null)
                {
                    if (freeze && !mouseFreezeWarned)
                    {
                        mouseFreezeWarned = true;
                        Logger.LogWarning("[mouselook] cannot freeze: PlayerManager/instellingen/Settings not reachable");
                    }
                    return;
                }
                if (freeze)
                {
                    if (savedMouseSens < 0f)
                    {
                        savedMouseSens = s.photo_mode_sensitivity;
                        Logger.LogInfo(string.Format("[mouselook] freeze ON (mouse sensitivity {0} -> 0)", savedMouseSens));
                    }
                    s.photo_mode_sensitivity = 0f; // re-asserted each frame; LateUpdate reads this

                    // Controller-routing guard: the free-cam picks ONE look sensitivity by last-active
                    // device, so when a controller is last-active the MOUSE delta is scaled by the
                    // CONTROLLER sensitivity (which we leave alone so the pad can keep flying). On frames
                    // where the mouse actually moves we also zero the controller sensitivity, so moving
                    // the mouse to click can never swing the cam no matter which device the game thinks
                    // is active; on still-mouse frames we restore it so controller-fly is unaffected.
                    bool mouseMoved = false;
                    try { mouseMoved = Input.GetAxisRaw("Mouse X") != 0f || Input.GetAxisRaw("Mouse Y") != 0f; }
                    catch { }
                    if (mouseMoved)
                    {
                        if (savedCtrlSens < 0f) savedCtrlSens = s.photo_mode_sensitivity_controller;
                        s.photo_mode_sensitivity_controller = 0f;
                    }
                    else if (savedCtrlSens >= 0f)
                    {
                        s.photo_mode_sensitivity_controller = savedCtrlSens;
                        savedCtrlSens = -1f;
                    }
                }
                else
                {
                    if (savedCtrlSens >= 0f) { s.photo_mode_sensitivity_controller = savedCtrlSens; savedCtrlSens = -1f; }
                    if (savedMouseSens >= 0f)
                    {
                        s.photo_mode_sensitivity = savedMouseSens;
                        Logger.LogInfo(string.Format("[mouselook] freeze OFF (restored {0})", savedMouseSens));
                        savedMouseSens = -1f;
                    }
                }
            }
            catch (Exception ex) { Logger.LogError("[mouselook] " + ex); }
        }

        // Reconcile the mouse-look freeze every frame, tied to the F4 panel ONLY (not inPhotoMode):
        // while the panel is open the mouse must NOT swing the free-cam, so the cursor is free to click;
        // with the panel closed the mouse flies the free-cam normally. Gating on the panel alone (rather
        // than panel && inPhotoMode) removes a whole failure mode - if the photomode-entered event were
        // ever missed, inPhotoMode would be stale-false and the freeze would silently never fire. Zeroing
        // photo_mode_sensitivity outside photomode is harmless (no free-cam is active). The freeze is
        // opt-out via the "Freeze mouse-look while panel open" setting (default ON); FlyingCameraScript
        // .LateUpdate reads the sensitivity each frame (after our Update), so this per-frame set keeps it
        // solid, and FreezeMouseLook(false) restores both mouse and controller sensitivities once.
        private void ReconcileMouseFreeze()
        {
            FreezeMouseLook(cfgFreezeMouse != null && cfgFreezeMouse.Value && showPanel);
        }

        private void ToggleSel(string sid, string name)
        {
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i].Sid == sid) { selected.RemoveAt(i); return; }
            }
            selected.Add(new Sel(sid, name));
            while (selected.Count > 2) selected.RemoveAt(0); // keep last two
        }

        // Right-click to compare: keep the left-clicked primary [0] and set the H2H partner in
        // slot [1]. Right-clicking an already-selected player removes it (un-compare).
        private void RightClickCompare(string sid, string name)
        {
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].Sid == sid) { selected.RemoveAt(i); ApplySelection(); return; }
            if (selected.Count >= 2) selected.RemoveAt(1); // keep the primary, replace the compare slot
            selected.Add(new Sel(sid, name));
            ApplySelection();
        }

        private void ApplySelection()
        {
            clearHoldSid = null; holdArmPending = false; // any explicit click resumes normal behaviour
            if (selected.Count == 0)
            {
                if (mode == Mode.Stats || mode == Mode.H2H || mode == Mode.Times) mode = Mode.None;
                return;
            }
            if (selected.Count == 1)
            {
                Sel one = selected[0];
                if (timesIntent)
                {
                    string key = ResolveLiveName(one.Name);
                    if (key != null)
                    {
                        liveTarget = key; mode = Mode.Times; shownFollowSid = null;
                        if (camLink) SetCameraFollow(one.Sid); // click = follow, same as Stats
                        return;
                    }
                    ChatApi.AddLocalMessage("No round times yet for " + one.Name + ".");
                }
                Stat s;
                if (!pool.TryGetValue(one.Sid, out s))
                {
                    // No data for this player -> still show a name-only card (and follow them).
                    s = new Stat(); s.SteamId = one.Sid; s.Name = one.Name;
                }
                target1 = s; mode = Mode.Stats; statsPinned = false;
                if (camLink) SetCameraFollow(one.Sid);
                shownFollowSid = one.Sid; // clicking points the camera and tracks it from here
                return;
            }
            // two selected -> H2H (camera can't follow two; leave it where it is). Players not in
            // the stats pool still compare: name-only card with "-" stats, like single-select.
            Stat a, b;
            if (!pool.TryGetValue(selected[0].Sid, out a))
            { a = new Stat(); a.SteamId = selected[0].Sid; a.Name = selected[0].Name; }
            if (!pool.TryGetValue(selected[1].Sid, out b))
            { b = new Stat(); b.SteamId = selected[1].Sid; b.Name = selected[1].Name; }
            target1 = a; target2 = b; mode = Mode.H2H; shownFollowSid = null;
            droneOn = true; EnsureDrone(); // forming an H2H auto-opens the VS compare cam (button still toggles)
        }

        // ---------------- Photomode follow-camera link ----------------

        // The active fly/spectator camera (only present while the caster is in it). Cached;
        // Unity's overloaded == reports a destroyed object as null, so we re-find on demand.
        private FlyingCameraScript GetFlyingCamera()
        {
            if (fcRef == null)
            {
                try { fcRef = (FlyingCameraScript)UnityEngine.Object.FindObjectOfType(typeof(FlyingCameraScript)); }
                catch { fcRef = null; }
            }
            return fcRef;
        }

        // The component that owns photomode (enter/exit + the server's can-enable rules). Cached;
        // a destroyed Unity object compares == null, so we re-find on demand (same as the fly cam).
        private EnableFlyingCamera2 FindEFC()
        {
            if (efcRef == null)
            {
                try { efcRef = (EnableFlyingCamera2)UnityEngine.Object.FindObjectOfType(typeof(EnableFlyingCamera2)); }
                catch { efcRef = null; }
            }
            return efcRef;
        }

        // Photomode entered (ZeepSDK fires this off EnableFlyingCamera2.ToggleFlyingCamera). This is
        // our authoritative "the fly/spectator camera is now active" signal, so the 5 Hz poll only
        // does camera/drone work while this is true - no FindObjectOfType scans while racing. Arming
        // holdArmPending here preserves the quiet-start that PollCameraPresence used to do on the
        // camera-on transition; warm fcRef once now (one scan per photomode session, not per tick).
        private void OnPhotoModeEntered()
        {
            inPhotoMode = true;
            holdArmPending = true;
            // Re-apply the camera mode used last photomode session (trailcam between rounds), unless
            // the caster opted out of the mod touching the camera (Follow camera mode = None). Done
            // once, in the poll, after the game has set up its own default camera on entry.
            camRestorePending = lastCamState >= 0 && cfgFollowCamState != null && cfgFollowCamState.Value != FollowCam.None;
            try { GetFlyingCamera(); } catch { }
        }

        // Casting integrity (un-disableable): leaving photomode force-clears the overlay so a racer
        // can't keep another player's cam/stats on screen. Fired by ZeepSDK on every photomode exit.
        // Drop the in-photomode flag (and the stale camera ref) BEFORE clearing so the poll stops
        // touching the camera immediately.
        private void OnPhotoModeExited()
        {
            // Capture the camera mode the caster ended on, to restore it on the next entry (trailcam
            // between rounds). One read here, before we drop fcRef - no per-frame polling needed.
            try
            {
                FlyingCameraScript fcEnd = GetFlyingCamera();
                if (fcEnd != null) { lastCamState = fcEnd.currentCameraState; lastCamAlt = fcEnd.alternateCameraState; }
            }
            catch { }
            inPhotoMode = false;
            fcRef = null;
            // Remember whether the panel was up so stay-in-photomode can reopen it on the next round's
            // auto re-entry (otherwise the caster would have to press F4 again every round).
            if (cfgStayInPhotomode != null && cfgStayInPhotomode.Value) stayPanelWanted = showPanel;
            showPanel = false; // close the panel too; ReconcileCursor then restores the mouse for racing
            try { ClearAll(); } catch { }
        }

        // Left the online lobby (disconnect / back to menu): wipe the whole overlay so nothing lingers
        // into the menu or the next lobby - hide the panel + cards, drop the compare cam, clear the live
        // leaderboard and cup state. Fired by ZeepSDK on every disconnect from a game.
        private void OnLeftLobby()
        {
            inPhotoMode = false;
            fcRef = null;
            showPanel = false; // ReconcileCursor restores the mouse next frame
            try { ClearAll(); } catch { }
            try { board.Clear(); } catch { }
            try { ResetLive(); } catch { }
            wrUid = null; wrHolder = ""; wrTime = ""; // forget the map WR; refetched on the next level
            // Forget the Showdown mod's broadcast state: it described THIS lobby's match. Without this
            // it could leak into the next lobby (wrong card for up to the TTL) and would keep the
            // manual match controls hidden in a cast where no handshake exists.
            sdRemote = null; sdRemoteAt = -999f; sdRemoteSigApplied = null;
        }

        // A round started: arm the stay-in-photomode (re)entry. The poll does the actual entering once
        // the server permits it. Harmless when the setting is off (the poll early-outs).
        // Clear the live leaderboard at round start so Cup mode begins with everyone timeless (red)
        // until they actually post a time this round - the game may not push an empty board itself.
        private void OnRoundStarted() { stayEnterPending = true; board.Clear(); }
        private void OnRoundEnded()
        {
            stayEnterPending = false;
            // Score the Showdown round here, before OnRoundStarted wipes `board`: this is the last
            // moment the round's times exist. Refresh first so we score the final averages, not the
            // ones from up to 200 ms ago.
            if (castMode == CastMode.Showdown)
            {
                try { SdRefresh(); SdTryScoreRound(); } catch { }
            }
        }

        // Stay-in-photomode: enter photomode as soon as the server allows, once per round, then leave
        // the caster alone (a later manual exit is not re-fought because isPhotoMode clears pending).
        // Gated by CanEnablePhotoMode so a comp that disables/finish-gates/time-gates photomode is
        // respected and a racer is never force-entered - this is what keeps it within the mod rules.
        private void TryEnterPhotomode()
        {
            if (cfgStayInPhotomode == null || !cfgStayInPhotomode.Value) { stayEnterPending = false; return; }
            if (!stayEnterPending) return;
            try
            {
                EnableFlyingCamera2 efc = FindEFC();
                if (efc == null) return;                                   // scene not ready: keep pending
                if (efc.isPhotoMode) { stayEnterPending = false; return; } // already in (any cause): done
                if (!efc.CanEnablePhotoMode()) return;                     // server not allowing yet: retry
                efc.ToggleFlyingCamera();                                  // enter (self-guarded too)
                if (stayPanelWanted) showPanel = true;                     // reopen the caster's F4 panel for the new round
                // pending clears next tick once isPhotoMode flips true (avoids a double toggle).
            }
            catch { }
        }

        // Who the camera is following right now ("" when none) - used to arm the Clear hold.
        private string CurrentCameraSid()
        {
            try
            {
                FlyingCameraScript fc = GetFlyingCamera();
                if (fc == null) return "";
                string sid = fc.GetCurrentZeepkistSteamID();
                return string.IsNullOrEmpty(sid) ? "" : sid;
            }
            catch { return ""; }
        }

        // Full reset from the Clear button / "/overlay off": hide every card, drop the compare
        // drone, and hold the camera auto-track so cam sync doesn't repaint the card next tick.
        private void ClearAll()
        {
            selected.Clear();
            timesIntent = false;
            mode = Mode.None;
            statsPinned = false;
            shownFollowSid = null;
            droneOn = false;
            EnsureDrone();                    // close the compare window right away
            sdQuadOn = false;
            EnsureQuadDrones();               // ...and every 4x cam feed
            clearHoldSid = CurrentCameraSid();
        }

        // Drive the Stats card from the camera's followed player. Only runs in the "follow" path
        // (mode None, or Stats that wasn't pinned by a typed /overlay stats). Leaves the card
        // untouched when the camera is off or not following anyone (no flicker).
        private void PollCamera()
        {
            if (!camLink) return;
            if (!(mode == Mode.None || (mode == Mode.Stats && !statsPinned))) return;
            FlyingCameraScript fc = GetFlyingCamera();
            if (fc == null) return;
            string sid;
            try { sid = fc.GetCurrentZeepkistSteamID(); }
            catch { return; }
            if (string.IsNullOrEmpty(sid)) return;
            // Fresh photomode entry: hold its auto-picked first target (quiet start).
            if (holdArmPending) { clearHoldSid = sid; holdArmPending = false; }
            // Clear/off hold: stay hidden while the camera is still on the same player it was on
            // when Clear was pressed; cycling to someone else (a deliberate act) releases it.
            if (clearHoldSid != null)
            {
                if (sid == clearHoldSid) return;
                clearHoldSid = null;
            }
            if (sid == shownFollowSid && mode == Mode.Stats) return; // already showing this player
            shownFollowSid = sid;
            Stat s;
            if (!pool.TryGetValue(sid, out s))
            {
                // Followed player isn't in our data -> show a name-only card (no crash).
                s = new Stat();
                s.SteamId = sid;
                try { if (fc.currentTarget != null) s.Name = fc.currentTarget.name; }
                catch { }
                if (string.IsNullOrEmpty(s.Name)) s.Name = sid;
            }
            target1 = s;
            mode = Mode.Stats;
            statsPinned = false;
        }

        // Re-apply the camera mode captured on the last photomode exit (trailcam between rounds). Runs
        // once on the first poll after entry - after the game has set its own default camera - then
        // clears the pending flag, so it never fights a mid-round camera change and does no other work.
        private void RestoreCamera()
        {
            camRestorePending = false;
            FlyingCameraScript fc = GetFlyingCamera();
            if (fc == null) return;
            try { fc.currentCameraState = lastCamState; fc.alternateCameraState = lastCamAlt; }
            catch { }
        }

        // Point the photomode camera at a lobby player (by steam id). No-op (returns false) if the
        // camera isn't active or the player has no ghost in the lobby. Setting currentTarget is
        // exactly how the game's own next/prev cycling steers the follow.
        private bool SetCameraFollow(string sidStr)
        {
            try
            {
                FlyingCameraScript fc = GetFlyingCamera();
                if (fc == null) return false;
                NetworkedZeepkistGhost ghost = null;
                List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
                if (list == null) return false;
                foreach (ZeepkistNetworkPlayer p in list)
                {
                    if (p.SteamID.ToString(CultureInfo.InvariantCulture) == sidStr) { ghost = p.Zeepkist; break; }
                }
                if (ghost == null) return false;
                // Refresh the follow list so newly-joined players are present (best effort).
                if (updateListMI != null)
                {
                    try { updateListMI.Invoke(fc, new object[] { false }); }
                    catch { }
                }
                List<SpectatorZeepkistTarget> tl = fc.targetList;
                if (tl == null) return false;
                foreach (SpectatorZeepkistTarget t in tl)
                {
                    if (t != null && ReferenceEquals(t.ghost, ghost))
                    {
                        fc.currentTarget = t;
                        // Force the default photomode camera mode (e.g. dynamic follow = state 6,
                        // alternate off) on a click. None leaves the caster's chosen mode alone;
                        // cycling with the game's own keys is never overridden (that path doesn't
                        // come through here).
                        if (cfgFollowCamState != null && cfgFollowCamState.Value != FollowCam.None)
                        {
                            fc.currentCameraState = (int)cfgFollowCamState.Value;
                            fc.alternateCameraState = false;
                        }
                        shownFollowSid = sidStr; // avoid an immediate poll override/flicker
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---------------- PhotoDrone bridge (compare drone) ----------------

        // Resolve PhotoDrone's public-static API once. Any miss -> feature off, button hidden,
        // zero behavior change for casters without the mod installed.
        private bool DroneApiReady()
        {
            if (droneChecked) return droneAvailable;
            droneChecked = true;
            try
            {
                Type cmdT = null, droneT = null, pdataT = null, presetT = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "PhotoDrone") continue;
                    cmdT = a.GetType("PhotomodeMultiview.DroneCommand");
                    droneT = a.GetType("PhotomodeMultiview.PhotoDrone");
                    pdataT = a.GetType("PhotomodeMultiview.PlayerData");
                    presetT = a.GetType("PhotomodeMultiview.DronePreset");
                    break;
                }
                if (cmdT == null || droneT == null || pdataT == null || presetT == null)
                {
                    DroneLog("PhotoDrone not found (mod missing or namespace changed) - compare cam off");
                    return false;
                }
                pdCreateMI = cmdT.GetMethod("CreateDrone",
                    new Type[] { typeof(string), presetT, typeof(bool) });
                pdGetMI = cmdT.GetMethod("GetDrone", new Type[] { typeof(string) });
                pdDestroyMI = cmdT.GetMethod("DestroyDrone", new Type[] { droneT });
                pdPlayersListFI = cmdT.GetField("players");
                pdSetTargetMI = droneT.GetMethod("SetTarget", new Type[] { pdataT });
                pdPlayerField = pdataT.GetField("zeepkistNetworkPlayer");
                droneAvailable = pdCreateMI != null && pdGetMI != null && pdDestroyMI != null &&
                                 pdPlayersListFI != null && pdSetTargetMI != null && pdPlayerField != null;
                DroneLog(droneAvailable ? "PhotoDrone API hooked"
                                        : "PhotoDrone found but its API changed - compare cam disabled");
                // Optional extras (any miss just keeps PhotoDrone's default for that bit):
                // follow mode Smooth, hidden window buttons, locked drag, canvas above chat,
                // and the preset path SetDroneRect uses to move/size the window (pixels, y-down).
                pdFollowModeFI = droneT.GetField("followMode");
                Type fmT = droneT.Assembly.GetType("PhotomodeMultiview.FollowMode");
                if (pdFollowModeFI != null && fmT != null)
                {
                    try { pdSmoothVal = Enum.Parse(fmT, "Smooth"); } catch { pdSmoothVal = null; }
                }
                Type winT = droneT.Assembly.GetType("PhotomodeMultiview.DroneWindowUI");
                pdDroneUIField = droneT.GetField("droneUI");
                if (winT != null)
                {
                    pdSetVisibilityMI = winT.GetMethod("SetVisibility", new Type[] { typeof(bool) });
                    pdSetLockedMI = winT.GetMethod("SetLocked", new Type[] { typeof(bool) });
                }
                pdCanvasField = cmdT.GetField("canvas");
                pdPresetCtor = presetT.GetConstructor(new Type[] { droneT, typeof(bool) });
                pdPresetSetX = presetT.GetMethod("set_X");
                pdPresetSetY = presetT.GetMethod("set_Y");
                pdPresetSetW = presetT.GetMethod("set_Width");
                pdPresetSetH = presetT.GetMethod("set_Height");
                pdApplyPresetMI = droneT.GetMethod("ApplyPreset", new Type[] { presetT });
            }
            catch { droneAvailable = false; }
            return droneAvailable;
        }

        // Keep the compare drone in sync with intent: exists + follows target2 while the toggle
        // is on and we're in H2H; destroyed otherwise. Runs at 5 Hz (and right after toggles),
        // which also re-creates it after PhotoDrone's own round-end/photomode-exit shutdowns.
        private void EnsureDrone()
        {
            try
            {
                if (!DroneApiReady()) return;
                // Nothing to create and nothing we have EVER created -> skip the GetDrone reflection.
                // Keyed off "did we ever make one", not off droneRef: PhotoDrone can rebuild the window
                // behind us, leaving droneRef null while a live drone with our id still renders.
                if (!droneOn && !vsDroneMade) return;
                // inPhotoMode is part of `want`: a drone left alive outside photomode still renders a
                // camera every frame, which is lag while racing for no visible benefit.
                bool want = droneOn && inPhotoMode && mode == Mode.H2H && target2 != null &&
                            !string.IsNullOrEmpty(target2.SteamId);
                object drone = pdGetMI.Invoke(null, new object[] { DroneId });
                bool alive = drone != null && !((UnityEngine.Object)drone == null);
                if (!want)
                {
                    if (alive) { pdDestroyMI.Invoke(null, new object[] { drone }); DroneLog("VS cam destroyed"); }
                    droneRef = null; droneSid = null; droneAppliedRect = new Rect();
                    vsDroneMade = false;
                    return;
                }
                vsDroneMade = true;
                if (!alive)
                {
                    // CreateDrone(null preset) calls SetInitialTarget() -> SetTarget(first lobby
                    // player) internally, which can throw (e.g. the local player has no car in
                    // photomode) AFTER the drone is registered. Swallow that and pick the
                    // half-built window up via GetDrone - we restyle and retarget it below.
                    try { drone = pdCreateMI.Invoke(null, new object[] { DroneId, null, false }); }
                    catch (Exception ce)
                    {
                        DroneLog("CreateDrone threw (recovering): " + Unwrap(ce));
                        drone = pdGetMI.Invoke(null, new object[] { DroneId });
                    }
                    alive = drone != null && !((UnityEngine.Object)drone == null);
                    // Not possible yet (between rounds / level loading) -> retried next tick.
                    if (!alive) return;
                }
                if (!ReferenceEquals(drone, droneRef))
                {
                    // New window instance (first create, or PhotoDrone rebuilt it after a round
                    // end): restyle it and re-apply target + rect from scratch.
                    droneRef = drone;
                    droneSid = null;
                    droneAppliedRect = new Rect();
                    SetupDroneWindow(drone);         // hide buttons, lock drag, raise above chat
                }
                if (target2.SteamId != droneSid)
                {
                    // Own try/catch: a failing SetTarget (ghost not spawned yet) must not block
                    // the rect pinning below; it just retries next tick.
                    try
                    {
                        object pd = FindDronePlayer(target2.SteamId);
                        if (pd != null)
                        {
                            // Smooth follow. Set BEFORE SetTarget so its own ApplyFOVForMode()
                            // call picks the right FOV; no private-method reflection needed.
                            if (pdFollowModeFI != null && pdSmoothVal != null)
                                pdFollowModeFI.SetValue(drone, pdSmoothVal);
                            pdSetTargetMI.Invoke(drone, new object[] { pd });
                            droneSid = target2.SteamId;
                            DroneLog("following " + target2.Name);
                        }
                        else DroneLog("not in PhotoDrone player list: " + target2.Name);
                    }
                    catch (Exception te) { DroneLog("SetTarget failed: " + Unwrap(te)); }
                }
                // Pin the window to the H2H card: same size, directly below (the card rect is
                // refreshed every OnGUI). Skipped mid-drag so it doesn't fight the caster, and
                // only once targeted: ApplyPreset re-targets from the captured preset, and with
                // no target that means SetInitialTarget() - the throwy path we just dodged.
                if (droneSid != null && draggingId == -1 && cardDrawRect.width > 0f)
                {
                    Rect wantRect = DroneWantRect();
                    if (wantRect != droneAppliedRect && ApplyDroneRect(drone, wantRect))
                        droneAppliedRect = wantRect;
                }
            }
            catch (Exception e) { DroneLog("EnsureDrone: " + Unwrap(e)); }
        }

        // ---- Showdown 4x cam ("show all") -----------------------------------------------------------
        // A second, independent multi-drone path rather than a rewrite of the single VS cam: selecting
        // Showdown must not change camera behaviour, so this only does anything once the operator turns
        // it on. Targets are the matchup's racers in order (team A top row, team B bottom row); a slot
        // with no racer simply isn't created, so a 3-player lobby shows 3 feeds.
        private class QuadSlot
        {
            public string Id;
            public object Ref;
            public string Sid;
            public Rect Applied;
            public string Label;
        }

        private void EnsureQuadDrones()
        {
            try
            {
                if (!DroneApiReady()) return;
                // Nothing on and nothing ever created -> no drones of ours can exist, skip the scan.
                if (!sdQuadOn && !quadMade) return;
                bool want = sdQuadOn && castMode == CastMode.Showdown && inPhotoMode &&
                            (cfgEnabled == null || cfgEnabled.Value) && sdA != null && sdB != null;

                List<SdPlayer> targets = new List<SdPlayer>();
                List<string> tags = new List<string>();
                if (want)
                {
                    for (int i = 0; i < sdA.Players.Count && targets.Count < 2; i++)
                    { targets.Add(sdA.Players[i]); tags.Add(sdA.Tag); }
                    for (int i = 0; i < sdB.Players.Count && targets.Count < 4; i++)
                    { targets.Add(sdB.Players[i]); tags.Add(sdB.Tag); }
                }

                // Make sure the slot list exists (4 fixed ids, created/destroyed on demand).
                while (quadSlots.Count < 4)
                {
                    QuadSlot s = new QuadSlot();
                    s.Id = "lobbyoverlay_sd" + quadSlots.Count;
                    quadSlots.Add(s);
                }

                for (int i = 0; i < 4; i++)
                {
                    QuadSlot s = quadSlots[i];
                    // A slot is only wanted if its racer is ACTUALLY IN THE LOBBY. Creating a drone for
                    // an absent roster entry produced a camera that rendered every frame, could never be
                    // targeted, and was never even positioned - invisible, but costing frames.
                    bool slotWanted = i < targets.Count && !string.IsNullOrEmpty(targets[i].SteamId) &&
                                      FindDronePlayer(targets[i].SteamId) != null;
                    object drone = pdGetMI.Invoke(null, new object[] { s.Id });
                    bool alive = drone != null && !((UnityEngine.Object)drone == null);

                    if (!slotWanted)
                    {
                        if (alive) { pdDestroyMI.Invoke(null, new object[] { drone }); DroneLog("quad slot destroyed"); }
                        s.Ref = null; s.Sid = null; s.Applied = new Rect(); s.Label = null;
                        continue;
                    }
                    if (!alive)
                    {
                        // Same recovery dance as the VS cam: CreateDrone can throw AFTER registering.
                        try { drone = pdCreateMI.Invoke(null, new object[] { s.Id, null, false }); }
                        catch (Exception ce)
                        {
                            DroneLog("quad CreateDrone threw (recovering): " + Unwrap(ce));
                            drone = pdGetMI.Invoke(null, new object[] { s.Id });
                        }
                        alive = drone != null && !((UnityEngine.Object)drone == null);
                        if (!alive) continue; // not possible yet (level loading) - retried next tick
                    }
                    quadMade = true;
                    if (!ReferenceEquals(drone, s.Ref))
                    {
                        s.Ref = drone; s.Sid = null; s.Applied = new Rect();
                        SetupDroneWindow(drone);
                    }
                    string sid = targets[i].SteamId;
                    if (sid != s.Sid)
                    {
                        try
                        {
                            object pd = FindDronePlayer(sid);
                            if (pd != null)
                            {
                                if (pdFollowModeFI != null && pdSmoothVal != null)
                                    pdFollowModeFI.SetValue(drone, pdSmoothVal);
                                pdSetTargetMI.Invoke(drone, new object[] { pd });
                                s.Sid = sid;
                            }
                        }
                        catch (Exception te) { DroneLog("quad SetTarget failed: " + Unwrap(te)); }
                    }
                    s.Label = tags[i] + "  " + SdShortName(targets[i]);
                    if (s.Sid != null)
                    {
                        Rect r = QuadCell(i);
                        if (r != s.Applied && ApplyDroneRect(drone, r)) s.Applied = r;
                    }
                }

                // Once every slot is gone, stop scanning until something is created again.
                bool anyLeft = false;
                for (int i = 0; i < quadSlots.Count; i++) if (quadSlots[i].Ref != null) { anyLeft = true; break; }
                if (!anyLeft) quadMade = false;
            }
            catch (Exception e) { DroneLog("EnsureQuadDrones: " + Unwrap(e)); }
        }

        // The S6 broadcast layout: a 2x2 that takes most of the screen, cells touching, with the right
        // column left clear for the game's own leaderboard. Sizes itself to the LARGEST 16:9 grid that
        // fits under the score box, so it adapts to wherever the caster dragged the box and to any
        // resolution. Cells deliberately share edges - a gap made it read as four widgets instead of
        // one shot.
        private Rect QuadCell(int i)
        {
            const float RightColumn = 0.21f;   // reserved for the game leaderboard
            float titleBar = Sc(30f);          // our per-feed name strip sits above each cell
            float x0 = Sc(12f);
            float availW = Screen.width * (1f - RightColumn) - x0;
            float top = (castMode == CastMode.Showdown && sdRect.height > 0f)
                        ? sdRect.yMax + titleBar + Sc(16f)
                        : Sc(150f);
            float availH = Screen.height - top - Sc(16f) - titleBar; // second row needs a strip too

            float cw = availW * 0.5f;
            float ch = cw * 9f / 16f;
            if (ch * 2f > availH) { ch = availH * 0.5f; cw = ch * 16f / 9f; }

            float gridW = cw * 2f, gridH = ch * 2f + titleBar;
            float ox = x0 + Mathf.Max(0f, (availW - gridW) * 0.5f);
            float oy = top + Mathf.Max(0f, (availH + titleBar - gridH) * 0.5f);

            int col = i % 2, row = i / 2;
            return new Rect(ox + col * cw, oy + row * (ch + titleBar), cw, ch);
        }

        private void DrawQuadChrome()
        {
            float t = Sc(2f);
            for (int i = 0; i < quadSlots.Count; i++)
            {
                QuadSlot s = quadSlots[i];
                if (s.Sid == null || s.Applied.width <= 0f) continue;
                Rect r = s.Applied;
                Color col = i < 2
                    ? (sdA != null ? ReadableOn(sdA.Col) : accentCol)
                    : (sdB != null ? ReadableOn(sdB.Col) : accentCol);
                Color prev = GUI.color;
                GUI.color = col;
                GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2f * t, t), whiteTex);
                GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2f * t, t), whiteTex);
                GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), whiteTex);
                GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), whiteTex);
                GUI.color = prev;
                Rect bar = new Rect(r.x, r.y - Sc(30f), r.width, Sc(26f));
                GUI.Box(bar, GUIContent.none, boxStyle);
                Rect txt = new Rect(bar.x + Sc(12f), bar.y, bar.width - Sc(24f), bar.height);
                GUI.contentColor = col;
                GUI.Label(txt, s.Label ?? "", vsTitleStyle);
                GUI.contentColor = Color.white;
            }
        }

        private bool QuadUp()
        {
            if (!sdQuadOn || castMode != CastMode.Showdown) return false;
            for (int i = 0; i < quadSlots.Count; i++) if (quadSlots[i].Sid != null) return true;
            return false;
        }

        private void SdToggleQuad()
        {
            sdQuadOn = !sdQuadOn;
            if (sdQuadOn)
            {
                // Mutually exclusive with the single VS cam: five live cameras is not a scene, it's a
                // framerate problem.
                droneOn = false;
                EnsureDrone();
            }
            EnsureQuadDrones();
            ChatApi.AddLocalMessage("Showdown 4x cam: " + (sdQuadOn ? "on" : "off"));
        }

        // One line per distinct message for the whole session - enough to diagnose without
        // spamming the BepInEx log from a 5 Hz poll.
        private void DroneLog(string msg)
        {
            if (droneLogged.Add(msg)) Logger.LogInfo("[compare cam] " + msg);
        }

        private static string Unwrap(Exception e)
        {
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        // PhotoDrone's PlayerData for a steam id (its own GetPlayer matches by display name;
        // the public zeepkistNetworkPlayer field lets us match by SteamID instead). Read from
        // the static `players` field: the GetPlayers() method returns NAME STRINGS, not data.
        private object FindDronePlayer(string sidStr)
        {
            System.Collections.IList players = pdPlayersListFI.GetValue(null) as System.Collections.IList;
            if (players == null) return null;
            foreach (object pd in players)
            {
                if (pd == null) continue;
                ZeepkistNetworkPlayer znp = pdPlayerField.GetValue(pd) as ZeepkistNetworkPlayer;
                if (znp != null && znp.SteamID.ToString(CultureInfo.InvariantCulture) == sidStr)
                    return pd;
            }
            return null;
        }

        // Title bar + frame for the compare window, drawn in our IMGUI pass so it matches the
        // cards. The frame is four thin strips AROUND the window rect (never on top of the
        // feed), so it works regardless of who renders first, IMGUI or PhotoDrone's canvas.
        private void DrawVsCamChrome()
        {
            Rect r = droneAppliedRect;
            float t = Sc(3f);
            // Frame in the compared player's COTD colour (amber when they have none yet).
            Color prev = GUI.color;
            GUI.color = LineColor(target2);
            GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2f * t, t), whiteTex);  // top
            GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2f * t, t), whiteTex);   // bottom
            GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), whiteTex);              // left
            GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), whiteTex);               // right
            GUI.color = prev;
            // Title bar bridges the gap between the H2H card and the feed.
            Rect bar = new Rect(r.x, r.y - Sc(34f), r.width, Sc(28f));
            GUI.Box(bar, GUIContent.none, boxStyle);
            Rect txt = new Rect(bar.x + Sc(14f), bar.y, bar.width - Sc(28f), bar.height);
            GUI.Label(txt, "VS CAM", vsTitleStyle);
            if (target2 != null)
            {
                string hex = string.IsNullOrEmpty(target2.ColHex) ? "#f3f4f6" : target2.ColHex;
                GUI.Label(txt, "<color=" + hex + "><b>" + target2.Name + "</b></color>", vsTitleRightStyle);
            }
        }

        // One-time styling for a freshly created drone window: hide the button row (Player /
        // Mode / Log / X / lock - the casters drive it from our panel), lock it against
        // accidental drags, and lift PhotoDrone's canvas above chat (it ships at order -1).
        private void SetupDroneWindow(object drone)
        {
            try
            {
                object ui = pdDroneUIField != null ? pdDroneUIField.GetValue(drone) : null;
                if (ui != null)
                {
                    if (pdSetVisibilityMI != null) pdSetVisibilityMI.Invoke(ui, new object[] { false });
                    if (pdSetLockedMI != null) pdSetLockedMI.Invoke(ui, new object[] { true });
                }
                else DroneLog("droneUI not found - window buttons stay visible");
                if (pdCanvasField != null)
                {
                    Canvas cv = pdCanvasField.GetValue(null) as Canvas;
                    if (cv != null && cv.sortingOrder < 1) cv.sortingOrder = 1;
                }
            }
            catch (Exception e) { DroneLog("window styling failed: " + Unwrap(e)); }
        }

        // Move/size the drone window via the same preset path PhotoDrone's own SetRect command
        // uses: pixels, origin top-left, y growing down (same convention as our IMGUI rects).
        // The preset is captured from the live drone, so mode/target are reapplied unchanged.
        // Where/how big the VS cam window should be. Placed (dragged/resized at least once) -> its own
        // free rect, untouched by any other UI. Unplaced -> follows under the card (the Sc(40) gap
        // hosts the "VS CAM" title bar) so it lands somewhere sensible out of the box.
        private Rect DroneWantRect()
        {
            if (camRect.x >= 0f && camRect.width > 1f) return camRect;
            return new Rect(cardDrawRect.x,
                            cardDrawRect.y + cardDrawRect.height + Sc(40f),
                            cardDrawRect.width,
                            cardDrawRect.height);
        }

        // Freeze the follow-the-card default into a free rect (first grab of either cam grip).
        private void CamFreeze()
        {
            if (camRect.x < 0f && droneAppliedRect.width > 0f) camRect = droneAppliedRect;
        }

        // Push the cam's current rect to the PhotoDrone window immediately (live during a drag).
        private void ApplyCamNow()
        {
            Rect nr = DroneWantRect();
            if (droneRef != null && ApplyDroneRect(droneRef, nr)) droneAppliedRect = nr;
        }

        // Is the VS cam PiP currently up (so its resize grip should be live/drawn)?
        private bool VsCamUp()
        {
            return mode == Mode.H2H && droneOn && droneAppliedRect.width > 0f &&
                   droneRef != null && !((UnityEngine.Object)droneRef == null);
        }

        private bool ApplyDroneRect(object drone, Rect r)
        {
            try
            {
                if (pdPresetCtor == null || pdApplyPresetMI == null || pdPresetSetX == null ||
                    pdPresetSetY == null || pdPresetSetW == null || pdPresetSetH == null)
                { DroneLog("rect API missing - window keeps its own position"); return false; }
                object preset = pdPresetCtor.Invoke(new object[] { drone, true }); // usePixels
                pdPresetSetX.Invoke(preset, new object[] { r.x });
                pdPresetSetY.Invoke(preset, new object[] { r.y });
                pdPresetSetW.Invoke(preset, new object[] { r.width });
                pdPresetSetH.Invoke(preset, new object[] { r.height });
                pdApplyPresetMI.Invoke(drone, new object[] { preset });
                return true;
            }
            catch (Exception e) { DroneLog("ApplyRect failed: " + Unwrap(e)); return false; }
        }

        // ---------------- Live current-map leaderboard (reflection) ----------------

        private void DiscoverLeaderboard()
        {
            Type znt = typeof(ZeepkistNetwork);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance;
            lbUpdatedField = znt.GetField("LeaderboardUpdated", flags);
            string[] names = new string[]
            {
                "<Leaderboard>k__BackingField", "<playersLeaderboard>k__BackingField",
                "Leaderboard", "playersLeaderboard", "leaderboard"
            };
            foreach (string n in names)
            {
                FieldInfo fi = znt.GetField(n, flags);
                if (fi != null && lbBackingField == null) lbBackingField = fi;
            }
        }

        private void SubscribeLeaderboard()
        {
            if (lbUpdatedField != null && lbUpdatedField.FieldType == typeof(Action) && lbUpdatedField.IsStatic)
            {
                Action existing = (Action)lbUpdatedField.GetValue(null);
                lbUpdatedField.SetValue(null, existing + new Action(OnLeaderboardUpdated));
                lbSubscribed = true;
            }
        }

        private void UnsubscribeLeaderboard()
        {
            try
            {
                if (lbSubscribed && lbUpdatedField != null)
                {
                    Action existing = (Action)lbUpdatedField.GetValue(null);
                    if (existing != null)
                        lbUpdatedField.SetValue(null, (Action)Delegate.Remove(existing, new Action(OnLeaderboardUpdated)));
                }
            }
            catch { }
        }

        private void OnLeaderboardUpdated()
        {
            try
            {
                if (lbBackingField == null) return;
                object val = lbBackingField.IsStatic ? lbBackingField.GetValue(null) : null;
                System.Collections.IEnumerable items = val as System.Collections.IEnumerable;
                if (items == null) return;
                board.Clear();
                int idx = 0;
                foreach (object item in items)
                {
                    if (item == null) continue;
                    string sid = GetStr(item, "SteamID");
                    if (sid == null) sid = GetStr(item, "steamID");
                    if (sid == null) sid = GetStr(item, "PlayerID");
                    string time = GetStr(item, "Time");
                    if (time == null) time = GetStr(item, "time");
                    if (time == null) time = GetStr(item, "BestTime");
                    string posStr = GetStr(item, "Position");
                    if (posStr == null) posStr = GetStr(item, "position");
                    int pos;
                    if (posStr == null || !int.TryParse(posStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pos))
                        pos = idx + 1;
                    ulong sidNum;
                    if (sid != null &&
                        ulong.TryParse(sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out sidNum))
                    {
                        LbEntry e = new LbEntry();
                        e.Position = pos;
                        e.Time = time;
                        board[sidNum] = e;
                    }
                    idx++;
                }
            }
            catch { }
        }

        private static string GetStr(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fi = t.GetField(name, f);
                if (fi != null) { object v = fi.GetValue(obj); return v == null ? null : FmtVal(v); }
                PropertyInfo pi = t.GetProperty(name, f);
                if (pi != null) { object v = pi.GetValue(obj, null); return v == null ? null : FmtVal(v); }
            }
            catch { }
            return null;
        }

        private static string FmtVal(object v)
        {
            if (v is float) return ((float)v).ToString("F3", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("F3", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        // Player's single best time across all captured cup rounds, or null. Matches by name
        // (COTDTracker logs names), so this only has data during a COTDTracker-run cup.
        // Current in-game name for a steam id from the live lobby roster (null if not present).
        // COTDTracker logs round times under this live name, which can differ from the stale pool
        // name after a player renames - so this is what we must match the times against.
        private string LobbyNameForSid(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return null;
            try
            {
                List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
                if (list == null) return null;
                foreach (ZeepkistNetworkPlayer p in list)
                    if (p.SteamID.ToString(CultureInfo.InvariantCulture) == sid) return SafeName(p);
            }
            catch { }
            return null;
        }

        // Resolve a Stat to its key in playerRoundTimes. Live times are logged by lobby name, so match
        // by the player's CURRENT lobby name (via steam id) first, then the pool name for players no
        // longer in the lobby. (Pool-name-only matching broke after renames.)
        private string LiveTimesKey(Stat s)
        {
            if (s == null) return null;
            string live = LobbyNameForSid(s.SteamId);
            string key = live != null ? ResolveLiveName(live) : null;
            if (key == null) key = ResolveLiveName(s.Name);
            return key;
        }

        // This player's live per-round times for the current cup (null if none / no match).
        private List<RoundTime> RoundTimesFor(Stat s)
        {
            string key = LiveTimesKey(s);
            List<RoundTime> times;
            if (key != null && playerRoundTimes.TryGetValue(key, out times)) return times;
            return null;
        }

        // Fastest lap for an H2H card player, scoped to the map currently up (multi-map cups) so it
        // compares like-for-like instead of blending both maps. Falls back to overall when no map is known.
        private string FastestInCup(Stat s)
        {
            List<RoundTime> times = RoundTimesFor(s);
            if (times == null) return null;
            string curUid = CurrentLevelUid();
            float best = -1f;
            for (int i = 0; i < times.Count; i++)
            {
                if (curUid != null && times[i].Uid != null && times[i].Uid != curUid) continue; // this map only
                float t = ParseTime(times[i].Time);
                if (t >= 0f && (best < 0f || t < best)) best = t;
            }
            return best < 0f ? null : best.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // which comps actually have data in the pool (plus cross-comp), in display order
        private void BuildAvailableComps()
        {
            availableComps.Clear();
            HashSet<string> present = new HashSet<string>();
            foreach (KeyValuePair<string, Stat> kv in pool)
                foreach (string c in kv.Value.Comps.Keys)
                    present.Add(c);
            foreach (string c in COMP_ORDER)
                if (c == "cotd" || c == "crosscomp" || present.Contains(c))
                    availableComps.Add(c);
            if (availableComps.Count == 0) availableComps.Add("cotd");
        }

        // Per-comp stats for a player; "crosscomp" aggregates all of their comps.
        private CompStat CompFor(Stat s, string comp)
        {
            if (s == null) return null;
            if (comp == "crosscomp")
            {
                if (s.Comps.Count == 0) return null;
                CompStat agg = new CompStat();
                agg.Best = 0;
                foreach (KeyValuePair<string, CompStat> kv in s.Comps)
                {
                    agg.Wins += kv.Value.Wins;
                    agg.Podiums += kv.Value.Podiums;
                    agg.Cups += kv.Value.Cups;
                    if (kv.Value.Best > 0 && (agg.Best == 0 || kv.Value.Best < agg.Best)) agg.Best = kv.Value.Best;
                }
                return agg;
            }
            CompStat cs;
            return s.Comps.TryGetValue(comp, out cs) ? cs : null;
        }

        // dir = +1 forward, -1 backward (right-click), wraps both ways.
        private void CycleComp(int dir)
        {
            if (availableComps.Count == 0) return;
            int n = availableComps.Count;
            int i = availableComps.IndexOf(selectedComp);
            if (i < 0) i = 0;
            int next = ((i + dir) % n + n) % n; // modulo that handles negatives
            selectedComp = availableComps[next];
            SaveLayout();
        }

        private void CycleCast(int dir)
        {
            int n = CAST_ORDER.Length;
            int i = Array.IndexOf(CAST_ORDER, castMode);
            if (i < 0) i = 0;
            castMode = CAST_ORDER[((i + dir) % n + n) % n];
            SaveLayout();
        }

        // count of shared events each player placed better in, for the chosen source.
        private void MutualRecord(Stat a, Stat b, string source, out int w1, out int w2)
        {
            w1 = 0; w2 = 0;
            if (source == "crosscomp")
            {
                foreach (string comp in a.Comps.Keys)
                {
                    if (!b.Comps.ContainsKey(comp)) continue;
                    int x, y; MutualInComp(a.Comps[comp], b.Comps[comp], out x, out y);
                    w1 += x; w2 += y;
                }
                return;
            }
            CompStat ca, cb;
            if (a.Comps.TryGetValue(source, out ca) && b.Comps.TryGetValue(source, out cb))
                MutualInComp(ca, cb, out w1, out w2);
        }

        private static void MutualInComp(CompStat a, CompStat b, out int w1, out int w2)
        {
            w1 = 0; w2 = 0;
            if (a.Hist == null || b.Hist == null) return;
            foreach (KeyValuePair<string, int> kv in a.Hist)
            {
                int posB;
                if (b.Hist.TryGetValue(kv.Key, out posB))
                {
                    if (kv.Value < posB) w1++;        // lower position = better placement
                    else if (posB < kv.Value) w2++;
                }
            }
        }

        // ---------------- Rendering (Unity IMGUI) ----------------

        private void OnGUI()
        {
            if (cfgEnabled == null || !cfgEnabled.Value) return; // master switch off -> draw nothing
            UpdateScale();
            EnsureStyles();
            HandleDrag();

            DrawCardForMode();

            // Broadcast chrome around the (UGUI) compare window: title bar + accent frame.
            if (mode == Mode.H2H && droneOn && droneSid != null && droneAppliedRect.width > 0f &&
                droneRef != null && !((UnityEngine.Object)droneRef == null))
                DrawVsCamChrome();
            if (QuadUp()) DrawQuadChrome();

            if (showPanel)
            {
                // Hold the cursor free while the panel is open so buttons are clickable.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                DrawPanel();
            }

            // Showdown score box: a permanent broadcast element once a matchup is detected. Before that
            // it only shows (as a setup hint) while the control panel is open, so a half-configured
            // "waiting for teams" never sits on stream.
            if (castMode == CastMode.Showdown && (SdMatchLive() || showPanel)) DrawShowdownCard();

            // Always-visible mode bar (when there's anything to control) + drag grips while editing.
            // In Showdown it carries WR + fastest-this-round, so it stays up for the whole match even
            // with nothing selected.
            if (showPanel || mode != Mode.None || SdMatchLive()) DrawModeBar();
            if (showPanel) DrawGrips();
        }

        // Draws whatever card the current mode calls for, and records its on-screen rect in
        // cardDrawRect so the drag grip can sit in its bottom-right corner.
        private void DrawCardForMode()
        {
            if (mode == Mode.None) return;
            // Showdown shows no comp stats at all, so clicking a racer must NOT pop a Stats or H2H card.
            // `mode`/target1/target2 are still set by the click, because the camera follow and the VS cam
            // are driven off them - only the card drawing is suppressed. The VS cam re-anchors to the
            // score box (see DrawShowdownCard), which is what cardDrawRect feeds.
            if (castMode == CastMode.Showdown) return;

            if (mode == Mode.Test)
            {
                cardRect.width = Sc(320f); cardRect.height = Sc(120f);
                cardDrawRect = cardRect;
                GUILayout.BeginArea(cardRect, boxStyle);
                GUILayout.Label("Lobby Overlay", headerStyle);
                GUILayout.Label("test card OK", labelStyle);
                GUILayout.Label("pool v" + poolVersion + "  (" + pool.Count + ")", labelStyle);
                GUILayout.EndArea();
                return;
            }

            if (mode == Mode.Stats && target1 != null)
            {
                cardRect.width = Sc(320f); cardRect.height = Sc(290f);
                DrawCard(cardRect, target1);
                return;
            }

            if (mode == Mode.H2H && target1 != null && target2 != null)
            {
                cardRect.width = Sc(410f); cardRect.height = Sc(320f);
                // Stats/Times toggle works with two players too: Times shows a side-by-side round-time
                // comparison instead of the stat card (VS cam / mode stay H2H, only the card swaps).
                if (timesIntent) DrawTimesH2H(cardRect.x, cardRect.y, cardRect.width, target1, target2);
                else DrawH2H(cardRect.x, cardRect.y, cardRect.width, target1, target2);
                return;
            }

            if (mode == Mode.Times && liveTarget != null)
            {
                cardRect.width = Sc(320f);
                DrawTimesCard(cardRect.x, cardRect.y, cardRect.width, liveTarget);
                return;
            }

            if (mode == Mode.RoundWins)
            {
                cardRect.width = Sc(320f);
                DrawRoundWinsCard(cardRect.x, cardRect.y, cardRect.width);
            }
        }

        // The compact always-on mode bar: 3 separate buttons (active one highlighted). It only
        // needs the cursor free to click (same as the panel), but stays visible for at-a-glance mode.
        // Recompute the mode bar's two text lines. Called from the 5 Hz Update poll, never from OnGUI:
        // the cup-best scan walks every player's round times, and the bar is drawn on every OnGUI pass.
        private void RefreshModeBarLines()
        {
            // Showdown replaces the whole bar: "Best time" is a COTDTracker cup-best and Round Wins is a
            // COTD concept, neither of which exists here. Fastest-this-round comes off the live board.
            if (castMode == CastMode.Showdown)
            {
                string fName = null; float fBest = -1f;
                foreach (KeyValuePair<ulong, LbEntry> kv in board)
                {
                    float t = ParseTime(kv.Value.Time);
                    if (t < 0f || (fBest >= 0f && t >= fBest)) continue;
                    fBest = t;
                    fName = LobbyNameForSid(kv.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (fName != null && fName.Length > 18) fName = fName.Substring(0, 17) + "..";
                barBestLine = fBest >= 0f
                    ? ("Fastest: " + (fName ?? "?") + " - " + SdTime(fBest))
                    : "Fastest: -";
                string wrH = wrHolder;
                if (wrH != null && wrH.Length > 18) wrH = wrH.Substring(0, 17) + "..";
                barWrLine = !string.IsNullOrEmpty(wrTime)
                    ? ("WR: " + (string.IsNullOrEmpty(wrH) ? "" : wrH + " - ") + wrTime)
                    : "WR: -";
                return;
            }

            // Cup best, scoped to the map currently up (alternating multi-map cups): "-" until a time
            // is set. Labeled with the map name inline so it's clear which map the best belongs to.
            string curUid = CurrentLevelUid();
            string bestName, bestTime;
            bool hasBest = TryChampionshipBestForMap(curUid, out bestName, out bestTime);
            if (hasBest && bestName != null && bestName.Length > 18) bestName = bestName.Substring(0, 17) + "..";
            string bestMapName = null;
            if (curUid != null) mapNames.TryGetValue(curUid, out bestMapName);
            if (bestMapName != null && bestMapName.Length > 18) bestMapName = bestMapName.Substring(0, 17) + "..";
            string bestPrefix = string.IsNullOrEmpty(bestMapName) ? "Best time:" : ("Best (" + bestMapName + "):");
            barBestLine = hasBest
                ? (bestPrefix + " " + bestName + " - " + FmtClock(bestTime))
                : (bestPrefix + " -");

            // Map world record (GTR): a separate line, since topout doesn't persist a cup-best.
            string wrHolderShort = wrHolder;
            if (wrHolderShort != null && wrHolderShort.Length > 18) wrHolderShort = wrHolderShort.Substring(0, 17) + "..";
            barWrLine = !string.IsNullOrEmpty(wrTime)
                ? ("WR: " + (string.IsNullOrEmpty(wrHolderShort) ? "" : wrHolderShort + " - ") + wrTime)
                : "WR: -";
        }

        private void DrawModeBar()
        {
            string bestLine = barBestLine != null ? barBestLine : "Best time: -";
            string wrLine = barWrLine != null ? barWrLine : "WR: -";

            // In Showdown the three cup buttons are meaningless (Round Wins is a COTD cup concept and
            // Stats/Times both read COTDTracker data that doesn't exist here), so the bar collapses to
            // just the two live lines.
            bool sdBar = castMode == CastMode.Showdown;
            float w = Sc(300f), h = sdBar ? Sc(76f) : Sc(124f);
            if (barRect.x < 0f) { barRect.x = Sc(24f); barRect.y = Screen.height - h - Sc(120f); } // bottom-left default
            barRect.width = w; barRect.height = h;

            GUILayout.BeginArea(barRect, boxStyle);
            if (!sdBar)
            {
                bool rwActive = mode == Mode.RoundWins;
                bool timesActive = !rwActive && timesIntent;
                bool statsActive = !rwActive && !timesIntent;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Stats", statsActive ? buttonSelStyle : buttonStyle)) SetBarMode(false);
                if (GUILayout.Button("Times", timesActive ? buttonSelStyle : buttonStyle)) SetBarMode(true);
                if (GUILayout.Button("Round Wins", rwActive ? buttonSelStyle : buttonStyle))
                { mode = Mode.RoundWins; shownFollowSid = null; }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(bestLine, bestStyle); // cup best (COTD) / fastest this round (Showdown)
            GUILayout.Label(wrLine, bestStyle);   // map world record (GTR)
            GUILayout.EndArea();
        }

        // ---- Showdown broadcast layout (default, no cam scene) -------------------------------------
        // Centre banner (logo | team | score/round | team | logo), a best-of-3 pip strip, the
        // wincondition row, and a logo leaderboard with racers listed by real finish position. Drawn
        // with manual rects for precise centring and logo placement. Everything is pre-computed by
        // SdRefresh; this only formats. Styles are built once, lazily.
        private GUIStyle sdBcScore, sdBcRound, sdBcTeam, sdBcTeamR, sdBcRank, sdBcAvg, sdBcAvgLbl,
                         sdBcWincon, sdBcName, sdBcTime, sdBcPos, sdBcSdTitle,
                         sdBcPipLbl, sdBcMove, sdBcNote, sdBcMetric, sdBcMetricWord, sdBcDiff, sdBcTeamName,
                         sdBcMetricL, sdBcMetricWordL;

        private void EnsureSdBcStyles()
        {
            if (sdBcScore != null) return;
            Font f = uiFont;
            sdBcScore = new GUIStyle(GUI.skin.label);
            if (f != null) sdBcScore.font = f;
            sdBcScore.fontSize = Sci(34); sdBcScore.fontStyle = FontStyle.Bold;
            sdBcScore.alignment = TextAnchor.MiddleCenter; sdBcScore.normal.textColor = Color.white;
            sdBcScore.wordWrap = false; sdBcScore.clipping = TextClipping.Overflow;

            sdBcRound = new GUIStyle(sdBcScore);
            sdBcRound.fontSize = Sci(15); sdBcRound.fontStyle = FontStyle.Normal; sdBcRound.normal.textColor = dimColor;

            sdBcTeam = new GUIStyle(sdBcScore); sdBcTeam.fontSize = Sci(24); sdBcTeam.alignment = TextAnchor.MiddleLeft;
            sdBcTeamR = new GUIStyle(sdBcTeam); sdBcTeamR.alignment = TextAnchor.MiddleRight;
            sdBcRank = new GUIStyle(sdBcScore); sdBcRank.fontSize = Sci(28);
            sdBcAvg = new GUIStyle(sdBcScore); sdBcAvg.fontSize = Sci(26); sdBcAvg.alignment = TextAnchor.MiddleRight;
            sdBcAvgLbl = new GUIStyle(sdBcRound); sdBcAvgLbl.alignment = TextAnchor.MiddleRight;
            sdBcWincon = new GUIStyle(sdBcRound); sdBcWincon.alignment = TextAnchor.MiddleLeft; sdBcWincon.richText = true;
            sdBcName = new GUIStyle(sdBcScore); sdBcName.fontSize = Sci(18); sdBcName.fontStyle = FontStyle.Normal; sdBcName.alignment = TextAnchor.MiddleLeft;
            sdBcTime = new GUIStyle(sdBcName); sdBcTime.fontStyle = FontStyle.Bold; sdBcTime.alignment = TextAnchor.MiddleRight;
            sdBcPos = new GUIStyle(sdBcName); sdBcPos.alignment = TextAnchor.MiddleCenter; sdBcPos.normal.textColor = dimColor; sdBcPos.fontSize = Sci(16);
            sdBcSdTitle = new GUIStyle(sdBcScore); sdBcSdTitle.fontSize = Sci(12); sdBcSdTitle.normal.textColor = accentCol;
            sdBcPipLbl = new GUIStyle(sdBcScore); sdBcPipLbl.fontSize = Sci(17); sdBcPipLbl.alignment = TextAnchor.MiddleCenter;
            sdBcMove = new GUIStyle(sdBcScore); sdBcMove.fontSize = Sci(38);
            sdBcNote = new GUIStyle(sdBcScore); sdBcNote.fontSize = Sci(12); sdBcNote.fontStyle = FontStyle.Bold; sdBcNote.alignment = TextAnchor.MiddleRight; sdBcNote.normal.textColor = dimColor;
            sdBcMetric = new GUIStyle(sdBcScore); sdBcMetric.fontSize = Sci(28); sdBcMetric.alignment = TextAnchor.MiddleRight;
            sdBcMetricWord = new GUIStyle(sdBcMetric); sdBcMetricWord.fontSize = Sci(20);
            sdBcMetricL = new GUIStyle(sdBcMetric); sdBcMetricL.alignment = TextAnchor.MiddleLeft;
            sdBcMetricWordL = new GUIStyle(sdBcMetricWord); sdBcMetricWordL.alignment = TextAnchor.MiddleLeft;
            sdBcDiff = new GUIStyle(sdBcScore); sdBcDiff.fontSize = Sci(18); sdBcDiff.fontStyle = FontStyle.Bold; sdBcDiff.alignment = TextAnchor.MiddleRight;
            sdBcTeamName = new GUIStyle(sdBcScore); sdBcTeamName.fontSize = Sci(24); sdBcTeamName.alignment = TextAnchor.MiddleLeft; sdBcTeamName.normal.textColor = Color.white;
        }

        // Fill a rect with a solid colour (Repaint only) - logo fallbacks, colour spines, pip backgrounds.
        private void SdFill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint || whiteTex == null) return;
            Color p = GUI.color; GUI.color = c; GUI.DrawTexture(r, whiteTex); GUI.color = p;
        }

        private void SdFrame(Rect r, Color c, float t)
        {
            SdFill(new Rect(r.x, r.y, r.width, t), c);
            SdFill(new Rect(r.x, r.yMax - t, r.width, t), c);
            SdFill(new Rect(r.x, r.y, t, r.height), c);
            SdFill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        // A team colour lifted to stay readable on the dark panel (Panthers' #000000 would vanish).
        private static Color SdOnDark(Color c)
        {
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return lum < 0.28f ? Color.Lerp(c, Color.white, 0.72f) : c;
        }

        // Banner label: the team name when short enough, otherwise the tag (names can run 200+ chars).
        private static string SdBannerLabel(SdTeam t)
        {
            if (t == null) return "";
            if (!string.IsNullOrEmpty(t.Name) && t.Name.Length <= 12) return t.Name;
            return t.Tag ?? "";
        }

        // M:SS.mmm, matching the game's own time display (a 42s lap reads "0:42.725").
        private static string SdTime(float secs)
        {
            if (secs < 0f) return "--";
            int total = Mathf.Max(0, Mathf.RoundToInt(secs * 1000f));
            int m = total / 60000, s = (total % 60000) / 1000, ms = total % 1000;
            return m + ":" + s.ToString("00") + "." + ms.ToString("000");
        }

        // The broadcast is two independent, separately-draggable blocks: the HEADER (score banner + Bo3
        // pips) and the team-times CARD. The caster can centre the header and shove the times to a side.
        private void DrawShowdownBroadcast()
        {
            EnsureSdBcStyles();

            float W = Sc(940f), Wb = Sc(560f);       // header block (banner + pips) keeps its width
            float Wc = Sc(380f);                     // team card: narrow, teams stacked vertically
            float bannerH = Sc(76f), pipsH = Sc(40f), blockH = Sc(142f);
            float gap = Sc(8f), bigGap = Sc(16f), panelPad = Sc(12f), teamGap = Sc(10f);
            float headerH = bannerH + gap + pipsH;
            float panelH = panelPad + 2f * blockH + teamGap + panelPad;

            float ccx = (Screen.width - W) * 0.5f;   // each block centres on its OWN width
            float ccxC = (Screen.width - Wc) * 0.5f;
            if (sdRect.x < 0f) { sdRect.x = ccx; sdRect.y = Sc(16f); }                        // header: top-centre
            if (sdCardRect.x < 0f) { sdCardRect.x = ccxC; sdCardRect.y = sdRect.y + headerH + bigGap; } // card: just below

            sdRect.width = W; sdRect.height = headerH;
            sdCardRect.width = Wc; sdCardRect.height = panelH;
            cardDrawRect = sdRect;

            // ---- HEADER: score banner (centred within the block) + Bo3 pips ----
            Rect banner = new Rect(sdRect.x + (W - Wb) * 0.5f, sdRect.y, Wb, bannerH);
            Rect pips = new Rect(sdRect.x, banner.yMax + gap, W, pipsH);
            SdBcBanner(banner);
            SdBcPips(pips);

            // ---- CARD: a stacked block per team (leader on top) ----
            Rect panel = new Rect(sdCardRect.x, sdCardRect.y, Wc, panelH);

            bool aTop; int move1, move2;
            SdArrows(out aTop, out move1, out move2);

            SdTeam t1 = aTop ? sdA : sdB, t2 = aTop ? sdB : sdA;
            // Per-team deciding metric / note / diff, resolved to the ranked order.
            string m1 = aTop ? sdMetricA : sdMetricB, m2 = aTop ? sdMetricB : sdMetricA;
            string n1 = aTop ? sdNoteA : sdNoteB, n2 = aTop ? sdNoteB : sdNoteA;
            float d1 = aTop ? sdDiffA : sdDiffB, d2 = aTop ? sdDiffB : sdDiffA;

            GUI.Box(panel, GUIContent.none, boxStyle);
            float py = panel.y + panelPad;
            SdBcTeamBlock(new Rect(panel.x, py, Wc, blockH), t1, move1, m1, n1, d1);
            py += blockH + teamGap;
            SdBcTeamBlock(new Rect(panel.x, py, Wc, blockH), t2, move2, m2, n2, d2);
        }

        // Movement arrows are a transient cue, not a permanent badge: they appear only for
        // SD_ARROW_HOLD seconds after the lead actually flips, and always as a symmetric pair (the new
        // leader ^ on top, the other v below). Debug (sdDbgMove) pins them on for tuning.
        private void SdArrows(out bool aTop, out int move1, out int move2)
        {
            if (sdDbgMove == 1) { aTop = true; move1 = 1; move2 = -1; return; }
            if (sdDbgMove == 2) { aTop = false; move1 = 1; move2 = -1; return; }

            aTop = sdLead <= 0; // winner on top; A stays on top while undecided
            bool show = sdLead != 0 && (Time.time - sdLeadChangedAt) < SD_ARROW_HOLD;
            move1 = show ? 1 : 0;
            move2 = show ? -1 : 0;
        }

        private void SdBcBanner(Rect b)
        {
            GUI.Box(b, GUIContent.none, boxStyle);
            float scoreW = Sc(160f);
            float midL = b.center.x - scoreW * 0.5f, midR = b.center.x + scoreW * 0.5f;
            // Team-colour tint on each half (low alpha keeps text readable; #000000 just stays dark).
            SdFill(new Rect(b.x, b.y, midL - b.x, b.height), new Color(sdA.Col.r, sdA.Col.g, sdA.Col.b, 0.14f));
            SdFill(new Rect(midR, b.y, b.xMax - midR, b.height), new Color(sdB.Col.r, sdB.Col.g, sdB.Col.b, 0.14f));

            float ip = Sc(9f);
            float lw = b.height - 2f * ip;
            Rect logoA = new Rect(b.x + ip + Sc(4f), b.y + ip, lw, lw);
            Rect logoB = new Rect(b.xMax - ip - lw - Sc(4f), b.y + ip, lw, lw);
            SdFill(new Rect(b.x + Sc(4f), b.y + ip, Sc(4f), b.height - 2f * ip), SdOnDark(sdA.Col));
            SdFill(new Rect(b.xMax - Sc(8f), b.y + ip, Sc(4f), b.height - 2f * ip), SdOnDark(sdB.Col));
            if (!SdDrawLogo(sdA, logoA)) SdFill(logoA, sdA.Col);
            if (!SdDrawLogo(sdB, logoB)) SdFill(logoB, sdB.Col);

            Rect score = new Rect(midL, b.y, scoreW, b.height);
            GUI.contentColor = accentCol;
            GUI.Label(new Rect(score.x, score.y + Sc(3f), score.width, Sc(15f)), "SHOWDOWN", sdBcSdTitle);
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(score.x, score.y + Sc(16f), score.width, Sc(36f)), sdPtsA + " - " + sdPtsB, sdBcScore);
            GUI.Label(new Rect(score.x, score.y + Sc(50f), score.width, Sc(14f)), "Round " + (sdPtsA + sdPtsB + 1), sdBcRound);

            float nameAx = logoA.xMax + Sc(10f);
            Rect nameA = new Rect(nameAx, b.y, score.x - nameAx - Sc(8f), b.height);
            float nameBx = score.xMax + Sc(8f);
            Rect nameB = new Rect(nameBx, b.y, (logoB.x - Sc(10f)) - nameBx, b.height);
            GUI.contentColor = SdOnDark(sdA.Col); GUI.Label(nameA, SdBannerLabel(sdA), sdBcTeam);
            GUI.contentColor = SdOnDark(sdB.Col); GUI.Label(nameB, SdBannerLabel(sdB), sdBcTeamR);
            GUI.contentColor = Color.white;
        }

        private static readonly Color sdActiveCol = new Color(1f, 0.898f, 0f); // #ffe500, Yolo's active yellow

        private void SdBcPips(Rect area)
        {
            int slots = Mathf.Max(1, sdTarget * 2 - 1); // best-of-3 -> 3 steps
            List<string> seq = new List<string>();
            if (sdWinSeq.Count == sdPtsA + sdPtsB) seq.AddRange(sdWinSeq);
            else { for (int i = 0; i < sdPtsA; i++) seq.Add(sdA.Tag); for (int i = 0; i < sdPtsB; i++) seq.Add(sdB.Tag); }

            int current = sdPtsA + sdPtsB;                         // the round in progress
            bool matchOver = sdPtsA >= sdTarget || sdPtsB >= sdTarget;
            string[] labels = { "Round 1", "Round 2", "Tiebreaker", "Round 4", "Round 5" };

            float pw = Sc(210f), sp = Sc(14f);
            float totalW = slots * pw + (slots - 1) * sp;
            float sx = area.x + (area.width - totalW) * 0.5f;
            for (int i = 0; i < slots; i++)
            {
                Rect pr = new Rect(sx + i * (pw + sp), area.y, pw, area.height);
                bool done = i < seq.Count;
                bool activeNow = i == current && !matchOver;
                SdTeam w = done ? SdTeamByTag(seq[i]) : null;

                if (activeNow) { SdFill(pr, new Color(sdActiveCol.r, sdActiveCol.g, sdActiveCol.b, 0.10f)); SdFrame(pr, sdActiveCol, Sc(2f)); }
                else if (w != null) { SdFill(pr, new Color(w.Col.r, w.Col.g, w.Col.b, 0.16f)); SdFrame(pr, SdOnDark(w.Col), Sc(1f)); }
                else { SdFill(pr, new Color(1f, 1f, 1f, 0.04f)); SdFrame(pr, new Color(1f, 1f, 1f, 0.14f), Sc(1f)); }

                string lbl = i < labels.Length ? labels[i] : ("Round " + (i + 1));
                Rect labR = new Rect(pr.x + Sc(12f), pr.y, pr.width - Sc(24f), pr.height);
                if (w != null)
                {
                    GUI.contentColor = SdOnDark(w.Col);
                    GUI.Label(labR, lbl + "  -  " + w.Tag, sdBcPipLbl);
                }
                else
                {
                    GUI.contentColor = activeNow ? sdActiveCol : dimColor;
                    GUI.Label(labR, lbl, sdBcPipLbl);
                }
                GUI.contentColor = Color.white;
            }
        }

        // One stacked team block (aizpun's broadcast sketch): logo + name header, the deciding
        // metric line, then a row per player. Replaces the old wide side-by-side row - at 940px the
        // card ate half the stream; stacked it hugs a corner. move: +1 up/green, -1 down/red, 0 none.
        private void SdBcTeamBlock(Rect r, SdTeam t, int move, string metric, string note, float diff)
        {
            Color tc = SdOnDark(t.Col);
            SdFill(r, new Color(t.Col.r, t.Col.g, t.Col.b, 0.13f));       // team-colour tint
            SdFill(new Rect(r.x, r.y + Sc(4f), Sc(4f), r.height - Sc(8f)), tc); // colour spine

            float pad = Sc(14f);
            float headH = Sc(46f), metH = Sc(34f), rowH = Sc(26f);
            float y = r.y + Sc(4f);

            // Header: logo + team name, the transient lead arrow on the right edge.
            Rect logo = new Rect(r.x + pad, y + Sc(5f), Sc(36f), Sc(36f));
            if (!SdDrawLogo(t, logo)) SdFill(logo, t.Col);
            GUI.Label(new Rect(logo.xMax + Sc(10f), y, r.xMax - logo.xMax - Sc(58f), headH),
                      SdBannerLabel(t), sdBcTeamName);
            GUI.contentColor = move > 0 ? goodColor : (move < 0 ? elimColor : dimColor);
            GUI.Label(new Rect(r.xMax - Sc(44f), y, Sc(36f), headH), move > 0 ? "▲" : (move < 0 ? "▼" : ""), sdBcMove);
            GUI.contentColor = Color.white;
            y += headH;

            // Metric line: the deciding value on the left, the winner's note or the loser's +diff
            // right-aligned on the same line (a team shows one or the other, never both).
            GUI.contentColor = tc;
            GUI.Label(new Rect(r.x + pad, y, r.width * 0.55f, metH),
                      metric, sdMetricWord ? sdBcMetricWordL : sdBcMetricL);
            if (diff >= 0f)
            {
                GUI.Label(new Rect(r.xMax - pad - Sc(120f), y + Sc(6f), Sc(120f), Sc(22f)),
                          "+" + diff.ToString("0.000", CultureInfo.InvariantCulture), sdBcDiff);
            }
            else
            {
                GUI.contentColor = string.IsNullOrEmpty(note) ? dimColor : sdActiveCol;
                GUI.Label(new Rect(r.xMax - pad - Sc(150f), y + Sc(9f), Sc(150f), Sc(16f)), note, sdBcNote);
            }
            GUI.contentColor = Color.white;
            y += metH;

            // Players, ordered by real finish position: [#pos] name ........ time
            List<SdPlayer> ps = new List<SdPlayer>(t.Players);
            ps.Sort(delegate (SdPlayer a, SdPlayer b)
            {
                int pa = SdFinishPos(a); if (pa == 0) pa = 99;
                int pb = SdFinishPos(b); if (pb == 0) pb = 99;
                return pa.CompareTo(pb);
            });
            for (int i = 0; i < ps.Count && i < 2; i++)
            {
                SdPlayer p = ps[i];
                Rect row = new Rect(r.x + pad, y + i * rowH, r.width - 2f * pad, rowH);
                if (i > 0) SdFill(new Rect(row.x, row.y, row.width, Sc(1f)), new Color(1f, 1f, 1f, 0.08f));
                int pos = SdFinishPos(p);
                float timeW = Sc(110f), posW = Sc(30f);
                GUI.contentColor = tc;
                GUI.Label(new Rect(row.x, row.y, posW, row.height), pos > 0 ? ("#" + pos) : "", sdBcPos);
                GUI.contentColor = Color.white;
                GUI.Label(new Rect(row.x + posW + Sc(6f), row.y, row.width - posW - timeW - Sc(12f), row.height),
                          SdShortName(p), sdBcName);
                float lt = SdLiveTime(p);
                GUI.contentColor = lt >= 0f ? tc : dimColor;
                GUI.Label(new Rect(row.xMax - timeW, row.y, timeW, row.height), lt >= 0f ? SdTime(lt) : "--:--.---", sdBcTime);
                GUI.contentColor = Color.white;
            }
        }

        // ---- Showdown score box --------------------------------------------------------------------
        // A permanent broadcast element (like the S6 stream's score box), not one of the click-cards:
        // it has its own rect so Stats/H2H/Times keep working underneath it. Everything here is
        // pre-computed by SdRefresh on the Update poll - this only formats.
        private void DrawShowdownCard()
        {
            bool haveMatch = sdA != null && sdB != null;
            int rowsA = haveMatch ? sdA.Players.Count : 0;
            int rowsB = haveMatch ? sdB.Players.Count : 0;

            // With a cam scene running the box collapses to the S6 broadcast form (score + the two team
            // times + the gap). The per-racer PB/skill detail is worth a lot when the box is the only
            // thing on screen, and worth nothing when it's stealing room from four camera feeds.
            bool compact = QuadUp();

            // Default (no cam scene): the full broadcast layout - centre banner, Bo3 pips, wincondition
            // row and the logo leaderboard. The compact box below is only for the 4x cam view.
            if (!compact && haveMatch) { DrawShowdownBroadcast(); return; }

            // The separate team-times card only exists in the full broadcast layout; make sure its grip
            // doesn't linger over the compact/quad box.
            sdCardRect.width = 0f;

            float w = compact ? Sc(560f) : Sc(420f); // narrower since the skill column came out
            float h = !haveMatch
                ? Sc(32f) + Sc(12f) + Sc(30f) + Sc(26f)
                : (compact
                    ? Sc(32f) + Sc(12f) + 2 * Sc(27f) + Sc(26f)
                    : Sc(32f) + Sc(24f) + Sc(12f) + (rowsA + rowsB) * Sc(27f) + 2 * Sc(27f)
                      + Sc(14f) + Sc(32f) + Sc(26f));
            if (sdRect.x < 0f) { sdRect.x = Sc(24f); sdRect.y = Sc(130f); }
            sdRect.width = w; sdRect.height = h;
            // The VS cam pins itself under cardDrawRect; with the stat cards suppressed in Showdown, the
            // score box becomes its anchor so the PiP still lands somewhere sensible.
            cardDrawRect = sdRect;

            GUILayout.BeginArea(sdRect, boxStyle);

            if (!haveMatch)
            {
                GUILayout.Label("Showdown", headerStyle);
                AccentLine(accentCol, null);
                GUI.contentColor = dimColor;
                GUILayout.Label(sdTeams.Count == 0
                    ? "No showdown_pool.json loaded."
                    : "Waiting for two teams in the lobby...", sdSubStyle);
                GUI.contentColor = Color.white;
                GUILayout.EndArea();
                return;
            }

            // Line 1: "STBN 0 - 0 AgOH". Line 2: the round/map. Keeping the map off the tag row is what
            // stopped both from wrapping and un-cramped the whole box.
            Color ca = ReadableOn(sdA.Col), cb = ReadableOn(sdB.Col);
            GUILayout.BeginHorizontal();
            GUI.contentColor = ca;
            GUILayout.Label(sdA.Tag, sdTagStyle, GUILayout.Width(Sc(120f)));
            GUI.contentColor = Color.white;
            GUILayout.Label(sdPtsA + " - " + sdPtsB, sdScoreStyle, GUILayout.Width(Sc(76f)));
            GUI.contentColor = cb;
            GUILayout.Label(sdB.Tag, sdTagStyle, GUILayout.Width(Sc(120f)));
            GUI.contentColor = Color.white;
            GUILayout.FlexibleSpace();
            if (compact)
            {
                // Compact has no second line to spare, so the map rides the header row.
                GUI.contentColor = dimColor;
                GUILayout.Label(SdMapLine(), sdSubStyle);
                GUI.contentColor = Color.white;
            }
            GUILayout.EndHorizontal();

            if (compact)
            {
                AccentLine(ca, cb);
                SdCompactRows(ca, cb);
                GUILayout.EndArea();
                return;
            }

            GUI.contentColor = dimColor;
            GUILayout.Label(SdMapLine(), sdSubStyle);
            GUI.contentColor = Color.white;
            AccentLine(ca, cb);

            SdTeamBlock(sdA, ca, sdAvgA);
            GUILayout.Space(Sc(10f));
            SdTeamBlock(sdB, cb, sdAvgB);

            // Footer: the live gap, or the GTR-PB projection until both teams are complete.
            GUILayout.Space(Sc(6f));
            string foot; Color footCol = dimColor;
            if (sdPtsA >= sdTarget || sdPtsB >= sdTarget)
            {
                SdTeam won = sdPtsA >= sdTarget ? sdA : sdB;
                foot = won.Tag + " wins the match";
                footCol = ReadableOn(won.Col);
            }
            else if (sdLead != 0)
            {
                SdTeam lead = sdLead < 0 ? sdA : sdB;
                foot = sdLeadGap >= 0f
                    ? (lead.Tag + " ahead by " + sdLeadGap.ToString("0.000", CultureInfo.InvariantCulture))
                    : (lead.Tag + " ahead on " + SdMethodLabel(sdLeadMethod));
                footCol = ReadableOn(lead.Col);
            }
            else if (sdFinA > 0 || sdFinB > 0) foot = "dead level";
            else if (sdProjA >= 0f && sdProjB >= 0f)
            {
                float d = Mathf.Abs(sdProjA - sdProjB);
                SdTeam fav = sdProjA < sdProjB ? sdA : sdB;
                foot = "on GTR PBs: " + fav.Tag + " favoured by " + d.ToString("0.000", CultureInfo.InvariantCulture);
            }
            else foot = "waiting for times";
            GUI.contentColor = footCol;
            GUILayout.Label(foot, bestStyle);
            GUI.contentColor = Color.white;

            GUILayout.EndArea();
        }

        // The S6 broadcast form: both teams' averages ranked, with the gap on the trailing row. Falls
        // back to the declared A/B order until both averages exist, so it never implies a lead it can't
        // support.
        private void SdCompactRows(Color ca, Color cb)
        {
            // Ranked by the real rule (finishers, then time), not by the displayed average.
            bool aLeads = sdLead <= 0;
            SdTeam t1 = aLeads ? sdA : sdB, t2 = aLeads ? sdB : sdA;
            Color c1 = aLeads ? ca : cb, c2 = aLeads ? cb : ca;
            float v1 = aLeads ? sdAvgA : sdAvgB, v2 = aLeads ? sdAvgB : sdAvgA;
            int f1 = aLeads ? sdFinA : sdFinB, f2 = aLeads ? sdFinB : sdFinA;

            SdCompactRow("#1", t1, c1, v1, f1, -1f);
            SdCompactRow("#2", t2, c2, v2, f2, sdLead != 0 ? sdLeadGap : -1f);
        }

        private void SdCompactRow(string pos, SdTeam t, Color col, float avg, int fin, float gap)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(Sc(26f)));
            GUI.contentColor = dimColor;
            GUILayout.Label(pos, sdSubStyle, GUILayout.Width(Sc(34f)));
            GUI.contentColor = col;
            GUILayout.Label(t.Tag, sdNameStyle, GUILayout.Width(Sc(96f)));
            GUI.contentColor = avg >= 0f ? Color.white : dimColor;
            GUILayout.Label(SdTime(avg), valueStyle, GUILayout.Width(Sc(100f)));
            GUI.contentColor = gap > 0f ? elimColor : dimColor;
            GUILayout.Label(gap > 0f ? ("+" + gap.ToString("0.000", CultureInfo.InvariantCulture)) : "",
                valueStyle, GUILayout.Width(Sc(96f)));
            // Only called out when someone is still missing - it decides the round, so it can't hide.
            GUI.contentColor = fin < t.Players.Count ? bubbleColor : dimColor;
            GUILayout.Label(fin + "/" + t.Players.Count, valueStyle, GUILayout.Width(Sc(52f)));
            GUI.contentColor = Color.white;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // One team: a row per racer, then the average line. Average is blank unless EVERY member has a
        // time - the rules score a round on the team average and say nothing about a missing time, so
        // the overlay refuses to invent a convention.
        private void SdTeamBlock(SdTeam t, Color col, float avg)
        {
            foreach (SdPlayer p in t.Players)
            {
                float live = SdLiveTime(p);
                float pb = -1f;
                string psid = SdActiveSid(p);
                if (string.IsNullOrEmpty(psid) || !sdPb.TryGetValue(psid, out pb)) pb = -1f;

                string timeStr = SdTime(live); // M:SS.mmm like the game ("--" when no time yet)
                string dStr = "PB --";
                Color dCol = dimColor;
                if (live >= 0f && pb >= 0f)
                {
                    float d = live - pb;
                    dStr = "PB " + (d >= 0f ? "+" : "-") + Mathf.Abs(d).ToString("0.00", CultureInfo.InvariantCulture);
                    dCol = d < 0f ? goodColor : (d > 0f ? elimColor : dimColor);
                }
                else if (pb >= 0f) { dStr = "PB " + SdTime(pb); }

                GUILayout.BeginHorizontal(GUILayout.Height(Sc(26f)));
                GUI.contentColor = col;
                GUILayout.Label(SdShortName(p), sdNameStyle, GUILayout.ExpandWidth(true));
                GUI.contentColor = live >= 0f ? Color.white : dimColor;
                GUILayout.Label(timeStr, valueStyle, GUILayout.Width(Sc(80f)));
                GUI.contentColor = dCol;
                GUILayout.Label(dStr, valueStyle, GUILayout.Width(Sc(96f)));
                GUI.contentColor = Color.white;
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal(GUILayout.Height(Sc(26f)));
            GUILayout.FlexibleSpace();
            GUI.contentColor = dimColor;
            // Finisher count is shown because it OUTRANKS the time: a team ahead on average with one
            // racer still out is losing the round, and that must not read as a lead.
            int fin = SdFinishers(t);
            GUILayout.Label(fin + "/" + t.Players.Count + " fin", sdSubStyle);
            GUILayout.Space(Sc(8f));
            GUI.contentColor = dimColor;
            GUILayout.Label("avg", sdSubStyle);
            GUI.contentColor = avg >= 0f ? col : dimColor;
            GUILayout.Label(SdTime(avg), valueStyle, GUILayout.Width(Sc(80f)));
            GUILayout.Space(Sc(96f));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
        }

        // Prefer the racer's live lobby name (they may have renamed since the JSON was written).
        private string SdShortName(SdPlayer p)
        {
            string n = LobbyNameForSid(SdActiveSid(p));
            if (string.IsNullOrEmpty(n)) n = p.Name;
            if (string.IsNullOrEmpty(n)) n = "?";
            return n.Length > 16 ? n.Substring(0, 15) + ".." : n;
        }

        private string SdMapLine()
        {
            string mapPart;
            if (sdCurMap != null) mapPart = "#" + sdCurMap.N + " " + sdCurMap.Name;
            else
            {
                string nm = CurrentLevelName();
                mapPart = string.IsNullOrEmpty(nm) ? "off-pool map" : nm;
            }
            if (mapPart.Length > 22) mapPart = mapPart.Substring(0, 21) + "..";
            string pick = sdPickerRandom ? " (random)"
                        : (!string.IsNullOrEmpty(sdPickerTag) ? " (" + sdPickerTag + " pick)" : "");
            return "R" + (sdPtsA + sdPtsB + 1) + " - " + mapPart + pick;
        }

        // Stats/Times bar buttons: set the click intent and re-apply to the current selection
        // (or let the camera follow take over). Also breaks out of the Round Wins card.
        private void SetBarMode(bool times)
        {
            timesIntent = times;
            if (mode == Mode.RoundWins) { mode = Mode.None; shownFollowSid = null; }
            ApplySelection();
        }

        // Small rounded grip in each box's bottom-right corner; drag from here (replaces the
        // fiddly title-strip drag). Only shown while the panel is open (edit mode).
        private void DrawGrips()
        {
            float hs = Sc(16f);
            if (mode != Mode.None && castMode != CastMode.Showdown) DrawGrip(cardDrawRect, hs);
            DrawGrip(panelRect, hs);
            if (showPanel || mode != Mode.None) DrawGrip(barRect, hs);
            if (castMode == CastMode.Showdown && sdRect.width > 0f) DrawGrip(sdRect, hs);
            if (castMode == CastMode.Showdown && sdCardRect.width > 0f) DrawGrip(sdCardRect, hs);
            if (VsCamUp())
            {
                DrawGripBL(droneAppliedRect, hs);       // bottom-left: move (frees it from the card)
                DrawResizeGrip(droneAppliedRect, hs);   // bottom-right: resize, free aspect
            }
        }

        // Resize grip in the VS cam's bottom-right corner: a faint square with a diagonal staircase of
        // dots (the universal "drag to resize" glyph), brightening on hover. Distinct from the move
        // grips so it reads as resize. Drag it to scale the PiP (see HandleDrag, draggingId 3).
        private void DrawResizeGrip(Rect box, float hs)
        {
            Rect g = GripRect(box, hs);
            GUI.Box(g, GUIContent.none, boxStyle);
            Vector2 m = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
            bool hover = GripRect(box, Sc(22f)).Contains(m);
            Color prev = GUI.color;
            GUI.color = hover ? accentCol : new Color(0.72f, 0.80f, 0.94f, 0.9f);
            float d = Sc(2.5f), sp = Sc(4.5f);
            for (int row = 0; row < 3; row++)            // 3-2-1 staircase toward the corner
                for (int col = 0; col <= 2 - row; col++)
                    GUI.DrawTexture(new Rect(g.xMax - d - col * sp, g.yMax - d - row * sp, d, d), whiteTex);
            GUI.color = prev;
        }

        // A grip in the box's bottom-right corner. Always a faint square; when the mouse is over it
        // (using the same enlarged hit area HandleDrag grabs) it lights up with an accent outline, so
        // it reads as "grab here to move this window".
        private void DrawGrip(Rect box, float hs)
        {
            Rect g = GripRect(box, hs);
            GUI.Box(g, GUIContent.none, boxStyle);
            Vector2 m = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
            if (!GripRect(box, Sc(22f)).Contains(m)) return; // 22 = HandleDrag's hit size
            Color prev = GUI.color;
            GUI.color = accentCol;
            float t = Sc(2f);
            GUI.DrawTexture(new Rect(g.x, g.y, g.width, t), whiteTex);            // top
            GUI.DrawTexture(new Rect(g.x, g.yMax - t, g.width, t), whiteTex);     // bottom
            GUI.DrawTexture(new Rect(g.x, g.y, t, g.height), whiteTex);           // left
            GUI.DrawTexture(new Rect(g.xMax - t, g.y, t, g.height), whiteTex);    // right
            GUI.color = prev;
        }

        private Rect GripRect(Rect box, float hs)
        {
            return new Rect(box.xMax - hs - Sc(3f), box.yMax - hs - Sc(3f), hs, hs);
        }

        // Bottom-LEFT grip rect: the VS cam's move handle. Its bottom-right corner is the resize
        // grip, so the move grip lives in the opposite corner.
        private Rect GripRectBL(Rect box, float hs)
        {
            return new Rect(box.x + Sc(3f), box.yMax - hs - Sc(3f), hs, hs);
        }

        // DrawGrip's bottom-left twin (move affordance: accent outline on hover).
        private void DrawGripBL(Rect box, float hs)
        {
            Rect g = GripRectBL(box, hs);
            GUI.Box(g, GUIContent.none, boxStyle);
            Vector2 m = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
            if (!GripRectBL(box, Sc(22f)).Contains(m)) return;
            Color prev = GUI.color;
            GUI.color = accentCol;
            float t = Sc(2f);
            GUI.DrawTexture(new Rect(g.x, g.y, g.width, t), whiteTex);
            GUI.DrawTexture(new Rect(g.x, g.yMax - t, g.width, t), whiteTex);
            GUI.DrawTexture(new Rect(g.x, g.y, t, g.height), whiteTex);
            GUI.DrawTexture(new Rect(g.xMax - t, g.y, t, g.height), whiteTex);
            GUI.color = prev;
        }

        // HUD scale: 1.0 at 1080p, proportional at other resolutions so text/boxes keep their
        // relative size. Positions stay in real pixels (drag/hit-testing untouched). Rebuild the
        // cached styles whenever the scale changes (e.g. resolution/window change).
        private void UpdateScale()
        {
            uiScale = Mathf.Clamp(Screen.height / 1080f, 0.6f, 3f);
            if (Mathf.Abs(uiScale - builtScale) > 0.001f) stylesReady = false;
        }

        private float Sc(float v) { return v * uiScale; }
        private int Sci(float v) { return Mathf.Max(1, Mathf.RoundToInt(v * uiScale)); } // font sizes (never 0)
        private int ScPad(int v) { return v <= 0 ? 0 : Mathf.Max(1, Mathf.RoundToInt(v * uiScale)); } // keeps 0 = 0
        private RectOffset ScRO(int l, int r, int t, int b)
        { return new RectOffset(ScPad(l), ScPad(r), ScPad(t), ScPad(b)); }

        // Rounds-won tally for the current cup: winner of each round = fastest valid time.
        private List<KeyValuePair<string, int>> ComputeRoundWins()
        {
            Dictionary<string, int> wins = new Dictionary<string, int>();
            for (int r = 1; r <= liveRound; r++)
            {
                string winner = null;
                float best = -1f;
                foreach (KeyValuePair<string, List<RoundTime>> kv in playerRoundTimes)
                {
                    List<RoundTime> ts = kv.Value;
                    for (int i = 0; i < ts.Count; i++)
                    {
                        if (ts[i].Round != r) continue;
                        float t = ParseTime(ts[i].Time);
                        if (t >= 0f && (best < 0f || t < best)) { best = t; winner = kv.Key; }
                    }
                }
                if (winner != null)
                {
                    int c; wins.TryGetValue(winner, out c); wins[winner] = c + 1;
                }
            }
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(wins);
            list.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            { return b.Value.CompareTo(a.Value); });
            return list;
        }

        // Fastest single time recorded across the whole current championship/cup: every completed
        // round (playerRoundTimes) plus the in-progress round (roundTimes, not yet flushed). COTD
        // only for now - it is the only comp we receive live per-round times for. false = no time yet.
        private bool TryChampionshipBest(out string name, out string timeStr)
        {
            name = null; timeStr = null;
            float best = -1f;
            foreach (KeyValuePair<string, List<RoundTime>> kv in playerRoundTimes)
            {
                List<RoundTime> ts = kv.Value;
                for (int i = 0; i < ts.Count; i++)
                {
                    float t = ParseTime(ts[i].Time);
                    if (t >= 0f && (best < 0f || t < best)) { best = t; name = kv.Key; timeStr = ts[i].Time; }
                }
            }
            foreach (KeyValuePair<string, string> kv in roundTimes)
            {
                float t = ParseTime(kv.Value);
                if (t >= 0f && (best < 0f || t < best)) { best = t; name = kv.Key; timeStr = kv.Value; }
            }
            return best >= 0f;
        }

        // Cup best scoped to one map (for alternating multi-map cups). Only counts stored times tagged
        // with this uid (untagged legacy times still count); the in-progress round counts only when it
        // belongs to this map. Falls back to the global best when no uid is known.
        private bool TryChampionshipBestForMap(string uid, out string name, out string timeStr)
        {
            name = null; timeStr = null;
            if (string.IsNullOrEmpty(uid)) return TryChampionshipBest(out name, out timeStr);
            float best = -1f;
            foreach (KeyValuePair<string, List<RoundTime>> kv in playerRoundTimes)
            {
                List<RoundTime> ts = kv.Value;
                for (int i = 0; i < ts.Count; i++)
                {
                    if (ts[i].Uid != null && ts[i].Uid != uid) continue; // skip the other map
                    float t = ParseTime(ts[i].Time);
                    if (t >= 0f && (best < 0f || t < best)) { best = t; name = kv.Key; timeStr = ts[i].Time; }
                }
            }
            if (curRoundMapUid == null || curRoundMapUid == uid)
            {
                foreach (KeyValuePair<string, string> kv in roundTimes)
                {
                    float t = ParseTime(kv.Value);
                    if (t >= 0f && (best < 0f || t < best)) { best = t; name = kv.Key; timeStr = kv.Value; }
                }
            }
            return best >= 0f;
        }

        // ---------------- GTR world record for the current map (graphql.zeepki.st) ----------------

        // Current online level's UID (the GTR level hash), or null when not in a lobby/level.
        private string CurrentLevelUid()
        {
            try
            {
                ZeepkistLobby lobby = ZeepkistNetwork.CurrentLobby;
                if (lobby == null) return null;
                return string.IsNullOrEmpty(lobby.LevelUID) ? null : lobby.LevelUID;
            }
            catch { return null; }
        }

        // Human-readable name of the currently loaded level (e.g. "PCDJ #30 - Horncrawl"), or null.
        // Local + in-process via ZeepSDK (no network) - same source the TopoutTracker mod uses.
        private string CurrentLevelName()
        {
            try
            {
                var lvl = LevelApi.CurrentLevel;
                return lvl != null ? lvl.Name : null;
            }
            catch { return null; }
        }

        // GTR level hash of the currently loaded level (the uppercase SHA-1 graphql.zeepki.st keys on).
        // IMPORTANT: CurrentLobby.LevelUID is "<code>_<author>", NOT the GTR hash - using it for WR
        // lookups always missed (levels.nodes = []). LevelApi.CurrentHash is the real hash.
        private string CurrentLevelHash()
        {
            try
            {
                string h = LevelApi.CurrentHash;
                return string.IsNullOrEmpty(h) ? null : h;
            }
            catch { return null; }
        }

        // Kick off a WR fetch when the current level changes (once per level, on a bg thread). Called
        // from the poll ONLY while the overlay bar/cards are up (so we never fetch for a racer or touch
        // round start). One request per level per session; a failure shows "WR: -" until the level changes.
        private void MaybeFetchWr()
        {
            if (wrFetching) return;
            string uid = CurrentLevelHash(); // the GTR hash, NOT LevelUID (which is "<code>_<author>")
            if (string.IsNullOrEmpty(uid)) return;
            if (uid == wrUid) return; // already have it (or already tried) for this level
            StartWrFetch(uid);
        }

        private void StartWrFetch(string uid)
        {
            wrFetching = true;
            wrUid = uid;                  // claim now so the poll doesn't re-fire; result commits via wrPending
            wrHolder = ""; wrTime = "";   // drop the previous map's WR immediately
            Logger.LogInfo("[wr] fetch start for hash " + uid);
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                string holder = "", time = "";
                try { FetchWrBlocking(uid, out holder, out time); }
                catch { holder = ""; time = ""; }
                wrPendingHolder = holder; wrPendingTime = time; wrPendingUid = uid; wrPending = true;
            });
            t.IsBackground = true;
            try { t.Start(); }
            catch { wrFetching = false; }
        }

        // One nested GraphQL query: level by hash -> its single fastest record (holder + time). The
        // hash is uppercased to match GTR's stored form. Any miss/error leaves holder/time empty.
        private void FetchWrBlocking(string uid, out string holder, out string time)
        {
            holder = ""; time = "";
            string hash = uid.ToUpperInvariant();
            if (TryWr(hash, out holder, out time)) return;
            // Adjusted/versioned levels carry a "-N" suffix on the hash; GTR may key the base hash.
            int dash = hash.LastIndexOf('-');
            if (dash > 0 && dash < hash.Length - 1)
            {
                bool allDigits = true;
                for (int i = dash + 1; i < hash.Length; i++) if (!char.IsDigit(hash[i])) { allDigits = false; break; }
                if (allDigits && TryWr(hash.Substring(0, dash), out holder, out time)) return;
            }
            Logger.LogInfo("[wr] no record for hash " + hash);
        }

        // Query GTR for one level's fastest record. Returns true (with holder/time) if the level and a
        // record were found; false on network error or no match. Assumes hashUpper is already uppercase.
        private bool TryWr(string hashUpper, out string holder, out string time)
        {
            holder = ""; time = "";
            string esc = hashUpper.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string r = GqlPost("{levels(filter:{hash:{equalTo:\"" + esc +
                "\"}}){nodes{records(orderBy:TIME_ASC,first:1){nodes{time user{steamName}}}}}}");
            if (r == null) { Logger.LogWarning("[wr] GTR request failed/timed out for " + hashUpper); return false; }
            JToken rec = JObject.Parse(r).SelectToken("data.levels.nodes[0].records.nodes[0]");
            if (rec == null) return false;
            JToken tTok = rec["time"];
            JToken nameTok = rec.SelectToken("user.steamName");
            if (tTok != null) time = FmtClockSec((double)tTok);
            holder = nameTok != null ? (string)nameTok : "";
            Logger.LogInfo(string.Format("[wr] {0} -> {1} {2}", hashUpper, holder, time));
            return true;
        }

        // POST a GraphQL query to GTR with a short timeout (a Spanish-ISP block must not hang the bg
        // thread on the default 100 s). Returns the raw JSON body, or null on any error.
        private string GqlPost(string query)
        {
            try
            {
                string body = "{\"query\":" + Newtonsoft.Json.JsonConvert.ToString(query) + "}";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://graphql.zeepki.st");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.UserAgent = "TournamentCastingUI/1.0 (+zeepkist casting mod)";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.ContentLength = data.Length;
                using (Stream rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static string FmtClockSec(double seconds)
        {
            if (seconds < 0.0) return "";
            int mins = (int)(seconds / 60.0);
            double secs = seconds - mins * 60;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", mins, secs);
        }

        private void DrawRoundWinsCard(float x, float y, float w)
        {
            List<KeyValuePair<string, int>> wins = ComputeRoundWins();
            int shown = Mathf.Min(wins.Count, 12);
            float h = Sc(64f) + (shown == 0 ? Sc(28f) : shown * Sc(26f));

            cardDrawRect = new Rect(x, y, w, h);
            GUILayout.BeginArea(new Rect(x, y, w, h), boxStyle);
            GUILayout.Label("Round Wins", headerStyle);
            if (shown == 0)
            {
                GUILayout.Label("no rounds won yet", labelStyle);
            }
            else
            {
                for (int i = 0; i < shown; i++)
                    Row(wins[i].Key, wins[i].Value.ToString());
            }
            GUILayout.EndArea();
        }

        // Drag a card/panel/bar by the grip in its bottom-right corner while the panel is open.
        // The grip hit area is a touch larger than the drawn square for easier grabbing.
        private void HandleDrag()
        {
            if (!showPanel) { draggingId = -1; return; }
            Event e = Event.current;
            if (e == null) return;
            float hs = Sc(22f);
            bool barShown = showPanel || mode != Mode.None;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // No stat card is drawn in Showdown, and cardDrawRect aliases the score box there, so the
                // card branch must stand down or it would hijack the score box's own grip (id 4).
                if (mode != Mode.None && castMode != CastMode.Showdown &&
                    GripRect(cardDrawRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 0;
                    dragOffset = new Vector2(e.mousePosition.x - cardRect.x, e.mousePosition.y - cardRect.y);
                    e.Use();
                }
                else if (GripRect(panelRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 1;
                    dragOffset = new Vector2(e.mousePosition.x - panelRect.x, e.mousePosition.y - panelRect.y);
                    e.Use();
                }
                else if (barShown && GripRect(barRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 2;
                    dragOffset = new Vector2(e.mousePosition.x - barRect.x, e.mousePosition.y - barRect.y);
                    e.Use();
                }
                else if (castMode == CastMode.Showdown && sdRect.width > 0f &&
                         GripRect(sdRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 4;
                    dragOffset = new Vector2(e.mousePosition.x - sdRect.x, e.mousePosition.y - sdRect.y);
                    e.Use();
                }
                else if (castMode == CastMode.Showdown && sdCardRect.width > 0f &&
                         GripRect(sdCardRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 5;
                    dragOffset = new Vector2(e.mousePosition.x - sdCardRect.x, e.mousePosition.y - sdCardRect.y);
                    e.Use();
                }
                else if (VsCamUp() && GripRect(droneAppliedRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 3; // VS cam resize (no offset: the corner tracks the mouse directly)
                    CamFreeze();    // grabbing either grip unpins it from the card for good
                    e.Use();
                }
                else if (VsCamUp() && GripRectBL(droneAppliedRect, hs).Contains(e.mousePosition))
                {
                    draggingId = 6; // VS cam move
                    CamFreeze();
                    dragOffset = new Vector2(e.mousePosition.x - camRect.x, e.mousePosition.y - camRect.y);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && draggingId >= 0)
            {
                if (draggingId == 0) { cardRect.x = e.mousePosition.x - dragOffset.x; cardRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 1) { panelRect.x = e.mousePosition.x - dragOffset.x; panelRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 2) { barRect.x = e.mousePosition.x - dragOffset.x; barRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 4) { sdRect.x = e.mousePosition.x - dragOffset.x; sdRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 5) { sdCardRect.x = e.mousePosition.x - dragOffset.x; sdCardRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 6)
                {
                    camRect.x = e.mousePosition.x - dragOffset.x;
                    camRect.y = e.mousePosition.y - dragOffset.y;
                    ApplyCamNow();
                }
                else { ResizeVsCam(e.mousePosition); }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && draggingId >= 0)
            {
                draggingId = -1;
                SaveLayout();
                e.Use();
            }
        }

        // Live VS cam resize: the bottom-right corner tracks the mouse, so width AND height move
        // independently - the caster picks the aspect (16:9 for a broadcast slot, square for a PiP),
        // instead of the old uniform scale that was locked to the card's shape. Clamped so the window
        // can't vanish or balloon. Applied right away; persisted on mouse-up by SaveLayout.
        private void ResizeVsCam(Vector2 mouse)
        {
            if (camRect.x < 0f) return; // CamFreeze on grab makes this the normal path
            camRect.width = Mathf.Clamp(mouse.x - camRect.x, Sc(120f), Screen.width);
            camRect.height = Mathf.Clamp(mouse.y - camRect.y, Sc(80f), Screen.height);
            ApplyCamNow();
        }

        // Persisted overlay layout (window positions + a few sticky panel choices). Property defaults
        // (set in the ctor) match the in-code defaults, so a missing/partial file falls back cleanly.
        // Auto-properties (not fields) so Newtonsoft serializes them reliably; ctor defaults because
        // C# 5 has no auto-property initializers.
        private class LayoutData
        {
            public float cardX { get; set; }
            public float cardY { get; set; }
            public float panelX { get; set; }
            public float panelY { get; set; }
            public float barX { get; set; }
            public float barY { get; set; }
            public string comp { get; set; }
            public bool cam { get; set; }
            public string castmode { get; set; }
            // VS cam free rect; w/h <= 1 (incl. layouts saved before this existed) = not placed,
            // follow under the card.
            public float vscamX { get; set; }
            public float vscamY { get; set; }
            public float vscamW { get; set; }
            public float vscamH { get; set; }
            public float sdX { get; set; }
            public float sdY { get; set; }
            public float sdCardX { get; set; }
            public float sdCardY { get; set; }
            public LayoutData()
            {
                cardX = 24f; cardY = 130f;
                panelX = -1f; panelY = 130f;
                barX = -1f; barY = 0f;
                comp = "cotd"; cam = true; castmode = "cup";
                vscamX = -1f; vscamY = 0f; vscamW = 0f; vscamH = 0f;
                sdX = -1f; sdY = 0f;
                sdCardX = -1f; sdCardY = 0f;
            }
        }

        private const string LayoutFile = "layout";

        // Load via ZeepSDK mod storage, matching the BrokenTracks / HNZConfig model: if the file
        // exists, load + apply it; otherwise apply defaults and write them so the file exists right
        // away. Replaces the old hand-rolled BepInEx/config file.
        private void LoadLayout()
        {
            try
            {
                if (Storage != null && Storage.JsonFileExists(LayoutFile))
                {
                    ApplyLayout(Storage.LoadFromJson(LayoutFile, typeof(LayoutData)) as LayoutData);
                }
                else
                {
                    ApplyLayout(new LayoutData()); // defaults
                    SaveLayout();                  // materialize the file on first run
                }
            }
            catch { }
        }

        // Copy a loaded LayoutData onto the live overlay state (positions are Unity Rects, hence the
        // map rather than holding the POCO as the live object).
        private void ApplyLayout(LayoutData d)
        {
            if (d == null) d = new LayoutData();
            cardRect.x = d.cardX; cardRect.y = d.cardY;
            panelRect.x = d.panelX; panelRect.y = d.panelY;
            barRect.x = d.barX; barRect.y = d.barY;
            sdRect.x = d.sdX; sdRect.y = d.sdY;
            sdCardRect.x = d.sdCardX; sdCardRect.y = d.sdCardY;
            if (!string.IsNullOrEmpty(d.comp)) selectedComp = d.comp;
            camLink = d.cam;
            string cm = (d.castmode ?? "cup").ToLowerInvariant();
            castMode = cm == "topout" ? CastMode.Topout
                     : (cm == "pursuit" ? CastMode.Pursuit
                     : (cm == "showdown" ? CastMode.Showdown : CastMode.Cup));
            // Sanity: a saved rect needs a real size to be trusted; anything else falls back to the
            // follow-the-card default (also what layouts saved before the free-cam change load as).
            camRect = (d.vscamW > 1f && d.vscamH > 1f)
                ? new Rect(d.vscamX, d.vscamY, d.vscamW, d.vscamH)
                : new Rect(-1f, 0f, 0f, 0f);
            if (!availableComps.Contains(selectedComp)) selectedComp = "cotd";
        }

        private void SaveLayout()
        {
            try
            {
                if (Storage == null) return;
                LayoutData d = new LayoutData();
                d.cardX = cardRect.x; d.cardY = cardRect.y;
                d.panelX = panelRect.x; d.panelY = panelRect.y;
                d.barX = barRect.x; d.barY = barRect.y;
                d.sdX = sdRect.x; d.sdY = sdRect.y;
                d.sdCardX = sdCardRect.x; d.sdCardY = sdCardRect.y;
                d.comp = selectedComp; d.cam = camLink; d.castmode = CastLabel(castMode).ToLowerInvariant();
                d.vscamX = camRect.x; d.vscamY = camRect.y; d.vscamW = camRect.width; d.vscamH = camRect.height;
                Storage.SaveToJson(LayoutFile, d);
            }
            catch { }
        }

        // Bring every draggable box back on-screen (recover one dragged off the edge and "lost").
        // The x<0 sentinels make DrawPanel/DrawModeBar recompute their screen-relative defaults on the
        // next draw; the card uses the fixed top-left default. Persisted so it survives a relaunch.
        private void ResetPositions()
        {
            cardRect.x = 24f; cardRect.y = 130f;   // card: top-left
            panelRect.x = -1f; panelRect.y = 130f; // panel: x<0 -> right side
            barRect.x = -1f; barRect.y = 0f;       // mode bar: x<0 -> bottom-left
            sdRect.x = -1f; sdRect.y = 0f;         // Showdown header: x<0 -> top-centre default
            sdCardRect.x = -1f; sdCardRect.y = 0f; // Showdown times card: x<0 -> centred below the header
            camRect = new Rect(-1f, 0f, 0f, 0f);   // VS cam: back to following under the card
            SaveLayout();
        }

        private bool IsSelected(string sid)
        {
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].Sid == sid) return true;
            return false;
        }

        // A cycle button: left-click returns +1 (forward), right-click returns -1 (reverse, to
        // undo an overshoot), no click returns 0. GUILayout.Button only reacts to the left mouse,
        // so we sniff the right mouse-up over the button's own rect.
        private int CycleButton(string label)
        {
            int k = LeftRightClick(label, buttonStyle);
            return k == 1 ? 1 : (k == 2 ? -1 : 0);
        }

        // Left/right click on a button: 1 = left, 2 = right, 0 = none. We reserve the rect and
        // handle the mouse ourselves (drawing the button with style.Draw) because GUILayout.Button
        // swallows the right mouse-button in this IMGUI build, which made right-click read as left.
        private int LeftRightClick(string label, GUIStyle style)
        {
            GUIContent c = new GUIContent(label);
            Rect r = GUILayoutUtility.GetRect(c, style, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            int result = 0;
            if (e != null && e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                if (e.button == 0) { result = 1; e.Use(); }
                else if (e.button == 1) { result = 2; e.Use(); }
            }
            if (Event.current.type == EventType.Repaint)
            {
                bool hover = r.Contains(Event.current.mousePosition);
                style.Draw(r, c, hover, false, false, false);
            }
            return result;
        }

        private void DrawPanel()
        {
            panelRect.width = Sc(280f);   // size scales; x/y stay where the caster dragged them
            panelRect.height = Sc(440f);
            if (panelRect.x < 0f) panelRect.x = Screen.width - panelRect.width - Sc(24f);
            GUILayout.BeginArea(panelRect, boxStyle);

            GUILayout.Label("Overlay Controls", headerStyle);
            AccentLine(accentCol, null);

            // Stats / Times / Round Wins now live on the always-visible mode bar (below).
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear", buttonStyle))
                ClearAll();
            GUILayout.EndHorizontal();

            // [Comp] = which cup format orders the player list below; it comes FIRST because it's the
            // setting a caster picks once at the start. [Stats] = which comp's numbers the cards show,
            // and it's hidden in Showdown, which shows no comp stats at all.
            // Left-click cycles forward, right-click back. Persisted.
            GUILayout.BeginHorizontal();
            int md = CycleButton("Comp: " + CastLabel(castMode) + " ◂▸");
            if (md != 0) CycleCast(md);
            GUILayout.EndHorizontal();
            if (castMode != CastMode.Showdown)
            {
                GUILayout.BeginHorizontal();
                int sd = CycleButton("Stats: " + CompLabel(selectedComp) + " ◂▸");
                if (sd != 0) CycleComp(sd);
                GUILayout.EndHorizontal();
            }
            // Bind the Stats card to the photomode follow-camera (click a player below to also
            // steer the camera; with nothing selected the card tracks whoever you're following).
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cam sync: " + (camLink ? "On" : "Off"), buttonStyle))
            { camLink = !camLink; if (!camLink) shownFollowSid = null; SaveLayout(); }
            GUILayout.EndHorizontal();
            // PiP window on the compared player (needs Metalted's PhotoDrone mod). Only shown
            // while a compare is active; everything else stays PhotoDrone defaults.
            if (mode == Mode.H2H && target2 != null && DroneApiReady())
            {
                DroneLog("compare cam button visible");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("VS cam: " + (droneOn ? "On" : "Off"), buttonStyle))
                { droneOn = !droneOn; DroneLog("toggle: " + (droneOn ? "on" : "off")); EnsureDrone(); }
                GUILayout.EndHorizontal();
            }
            if (castMode == CastMode.Showdown) DrawShowdownControls();
            GUILayout.Space(Sc(4f));

            panelScroll = GUILayout.BeginScrollView(panelScroll);
            try
            {
                // Cached by the 5 Hz Update poll; only built here on the very first frame the panel
                // opens (and then cached, so Layout and Repaint in the same frame agree - GUILayout
                // requires the row count to match across passes).
                List<PRow> rows = panelRowsCache;
                if (rows == null) { rows = BuildPanelRows(); panelRowsCache = rows; }
                foreach (PRow r in rows)
                {
                    bool sel = IsSelected(r.Sid);
                    string label = (sel ? "✓ " : "    ") + r.Name;
                    if (!sel) GUI.contentColor = StatusColor(r.Status);
                    int click = LeftRightClick(label, sel ? buttonSelStyle : buttonStyle);
                    GUI.contentColor = Color.white;
                    if (click == 1) { selected.Clear(); selected.Add(new Sel(r.Sid, r.Name)); ApplySelection(); } // left = follow
                    else if (click == 2) RightClickCompare(r.Sid, r.Name);                                        // right = compare
                }
            }
            catch { }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // Every live Showdown correction as a BUTTON. Zeepkist has no clipboard, so anything that would
        // need a name typed into chat is unusable mid-cast: click a player in the list below, then click
        // the team to move them to.
        private void DrawShowdownControls()
        {
            GUILayout.Space(Sc(4f));
            if (sdA == null || sdB == null)
            {
                GUI.contentColor = dimColor;
                GUILayout.Label("Waiting for two teams in the lobby...", sdSubStyle);
                GUI.contentColor = Color.white;
                return;
            }

            // Once the Showdown mod's handshake has spoken this lobby, IT owns score / picker / rosters /
            // sides - the manual buttons would be silently overwritten by the next broadcast, which reads
            // as "the buttons are broken". So they hide, replaced by a one-line source note. They come
            // back automatically in fallback casts (host without the broadcasting build).
            if (sdRemote != null)
            {
                GUI.contentColor = dimColor;
                GUILayout.Label("match state: Showdown mod (auto)", sdSubStyle);
                GUI.contentColor = Color.white;
            }
            else
            {
                // Score: click a team to give it a point, right-click to take one back.
                GUILayout.BeginHorizontal();
                int ca = LeftRightClick("+1 " + sdA.Tag, buttonStyle);
                if (ca == 1) sdPtsA++; else if (ca == 2 && sdPtsA > 0) sdPtsA--;
                int cb = LeftRightClick("+1 " + sdB.Tag, buttonStyle);
                if (cb == 1) sdPtsB++; else if (cb == 2 && sdPtsB > 0) sdPtsB--;
                GUILayout.EndHorizontal();

                // Who picked the map currently up: cycles A -> B -> random -> unset.
                GUILayout.BeginHorizontal();
                string pickLbl = sdPickerRandom ? "random"
                               : (string.IsNullOrEmpty(sdPickerTag) ? "-" : sdPickerTag);
                if (GUILayout.Button("Pick: " + pickLbl + " ▸", buttonStyle)) SdCyclePicker();
                GUILayout.EndHorizontal();

                // Move the selected player between teams (covers a late substitute with zero typing).
                if (selected.Count == 1)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("→ " + sdA.Tag, buttonStyle)) SdMoveSid(selected[0].Sid, sdA);
                    if (GUILayout.Button("→ " + sdB.Tag, buttonStyle)) SdMoveSid(selected[0].Sid, sdB);
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("New match", buttonStyle))
                {
                    sdPtsA = 0; sdPtsB = 0; sdScored.Clear(); sdWinSeq.Clear(); sdPickerTag = null; sdPickerRandom = false;
                }
                if (GUILayout.Button(sdMatchupForced ? "Auto teams" : "Swap sides", buttonStyle))
                {
                    if (sdMatchupForced) { sdMatchupForced = false; sdRosterSig = null; SdDetectMatchup(true); }
                    else { SdTeam t = sdA; sdA = sdB; sdB = t; int p = sdPtsA; sdPtsA = sdPtsB; sdPtsB = p; }
                }
                GUILayout.EndHorizontal();
            }

            // "Show all": a PhotoDrone per racer in a 2x2. Slots with no racer are simply not created,
            // so a 3-player lobby shows 3 feeds and a 2-player one shows 2.
            if (DroneApiReady())
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(sdQuadOn ? "4x cam: On" : "4x cam: Off",
                                     sdQuadOn ? buttonSelStyle : buttonStyle))
                    SdToggleQuad();
                GUILayout.EndHorizontal();
            }
        }

        private void SdCyclePicker()
        {
            if (sdPickerRandom) { sdPickerRandom = false; sdPickerTag = null; }
            else if (string.IsNullOrEmpty(sdPickerTag)) sdPickerTag = sdA != null ? sdA.Tag : null;
            else if (sdA != null && sdPickerTag == sdA.Tag) sdPickerTag = sdB != null ? sdB.Tag : null;
            else { sdPickerTag = null; sdPickerRandom = true; }
        }

        private enum PStatus { NoTime, Bubble, Safe, Out, NonRacer, Win, LastLife }

        private class PRow
        {
            public string Sid;
            public string Name;
            public PStatus Status;
            public int Tier;
            public int Pos;
            public float T;      // round time (seconds), for ordering finished racers fastest-first
            public float Elo;
            public bool InBoard; // has a live current-map leaderboard entry
            public bool HasTime; // ...and that entry is a valid (finished) time
            public int Points;   // topout championship points (native, replicated)
        }

        private Color StatusColor(PStatus s)
        {
            if (s == PStatus.NoTime) return elimColor;
            if (s == PStatus.Bubble) return bubbleColor;
            if (s == PStatus.Out) return outColor;
            if (s == PStatus.Win) return goodColor; // green: crowned winner
            if (s == PStatus.LastLife) return lastLifeColor; // orange: TyO last life
            return safeColor;
        }

        // Dispatch the player-list ordering by the [Comp] casting mode (Cup / Topout / Pursuit).
        private List<PRow> BuildPanelRows()
        {
            List<ZeepkistNetworkPlayer> list = ZeepkistNetwork.PlayerList;
            if (list == null) return new List<PRow>();
            if (castMode == CastMode.Topout) return BuildTopoutRows(list);
            if (castMode == CastMode.Pursuit) return BuildPursuitRows(list);
            if (castMode == CastMode.Showdown) return BuildShowdownRows(list);
            return BuildCupRows(list);
        }

        // Showdown click-list: the four racers grouped by team (team A first, then team B), then anyone
        // else in the lobby (casters, spectators, an unregistered substitute) greyed at the bottom so
        // they can still be followed. Red = no time yet this round, white = timed.
        private List<PRow> BuildShowdownRows(List<ZeepkistNetworkPlayer> list)
        {
            List<PRow> rows = new List<PRow>();
            List<PRow> others = new List<PRow>();
            Dictionary<string, ZeepkistNetworkPlayer> byId = new Dictionary<string, ZeepkistNetworkPlayer>();
            foreach (ZeepkistNetworkPlayer p in list)
                byId[p.SteamID.ToString(CultureInfo.InvariantCulture)] = p;

            HashSet<string> placed = new HashSet<string>();
            for (int side = 0; side < 2; side++)
            {
                SdTeam t = side == 0 ? sdA : sdB;
                if (t == null) continue;
                foreach (SdPlayer sp in t.Players)
                {
                    if (string.IsNullOrEmpty(sp.SteamId)) continue;
                    placed.Add(sp.SteamId);
                    ZeepkistNetworkPlayer p;
                    bool present = byId.TryGetValue(sp.SteamId, out p);
                    PRow r = new PRow();
                    r.Sid = sp.SteamId;
                    r.Name = present ? SafeName(p) : sp.Name;
                    if (string.IsNullOrEmpty(r.Name)) r.Name = sp.Name != null ? sp.Name : "?";
                    r.Name = t.Tag + " " + r.Name;
                    float tm = SdLiveTime(sp);
                    r.HasTime = tm >= 0f;
                    r.InBoard = present;
                    r.T = tm;
                    r.Status = !present ? PStatus.Out : (r.HasTime ? PStatus.Safe : PStatus.NoTime);
                    rows.Add(r);
                }
            }
            foreach (ZeepkistNetworkPlayer p in list)
            {
                string sid = p.SteamID.ToString(CultureInfo.InvariantCulture);
                if (placed.Contains(sid)) continue;
                PRow r = new PRow();
                r.Sid = sid;
                r.Name = SafeName(p); if (r.Name == null) r.Name = "?";
                r.Status = PStatus.NonRacer;
                others.Add(r);
            }
            rows.AddRange(others);
            return rows;
        }

        // Once the finals start taking shape - a winner is crowned, or this many finalists are
        // locked in - the nuisances stop being the show and drop off the list.
        private const int TopoutNuisanceDropFinalists = 2;

        // Topout casting order (aizpun's spec), tuned for "click whoever you'd follow next",
        // top -> bottom:
        //   1. Nuisances (\o7): eliminated players still racing as blockers (e.g. Maki) - red,
        //      pinned on TOP because a chaos-agent is exactly who you'd click... but only until
        //      the finals take shape (a winner exists, or 2 finalists set), then they drop.
        //   2. Finalists (FIN): topped out, locked into the finals - yellow.
        //   3. Everyone else (the live points race): white, by championship points descending.
        //   4. Winners (WIN): green, at the bottom, always kept (you may still want their runs).
        // Reads the game's native custom-leaderboard fields the host pushes, so it works for a
        // non-host caster.
        private List<PRow> BuildTopoutRows(List<ZeepkistNetworkPlayer> list)
        {
            EnsureTopoutApi();
            List<PRow> rows = new List<PRow>();
            List<PRow> nuisances = new List<PRow>();
            int winnerCount = 0, finalistCount = 0;
            foreach (ZeepkistNetworkPlayer p in list)
            {
                string txt = ToOverrideText(p.SteamID) ?? "";
                bool win = txt.Contains("WIN");
                bool fin = !win && txt.Contains("FIN");
                bool nui = !win && !fin && txt.Contains("\\o7");
                PRow r = new PRow();
                r.Sid = p.SteamID.ToString(CultureInfo.InvariantCulture);
                r.Name = SafeName(p); if (r.Name == null) r.Name = "?";
                Stat st; r.Elo = pool.TryGetValue(r.Sid, out st) ? st.Elo : 0f;
                r.Points = ToChampPoints(p);
                if (win) { winnerCount++; r.Tier = 3; r.Status = PStatus.Win; rows.Add(r); }          // winners: green, bottom
                else if (fin) { finalistCount++; r.Tier = 1; r.Status = PStatus.Bubble; rows.Add(r); } // finalists: yellow
                else if (nui) { r.Tier = 0; r.Status = PStatus.NoTime; nuisances.Add(r); }            // nuisances: red, top (until finals form)
                else { r.Tier = 2; r.Status = PStatus.Safe; rows.Add(r); }                            // rest: white, points race
            }
            bool finalsForming = winnerCount >= 1 || finalistCount >= TopoutNuisanceDropFinalists;
            if (!finalsForming) rows.AddRange(nuisances);
            rows.Sort(delegate (PRow a, PRow b)
            {
                if (a.Tier != b.Tier) return a.Tier.CompareTo(b.Tier);
                return b.Points.CompareTo(a.Points); // championship points desc within tier
            });
            return rows;
        }

        // Pursuit (Tag You're Out) casting order. PursuitZK marks each player with a pursuer (who hunts
        // them) and a target (who they hunt) by Steam ID; a player loses a life when their pursuer beats
        // their time. List spec (aizpun): alive non-spectators only (eliminated dropped), ordered by the
        // live round leaderboard fastest-first, colored ORANGE on the last life (L:1), else YELLOW when
        // "in danger" (their pursuer has beaten their time this round), else WHITE. Falls back to the
        // Cup/leaderboard logic when no PursuitZK tournament is running.
        private List<PRow> BuildPursuitRows(List<ZeepkistNetworkPlayer> list)
        {
            List<PRow> tracked = BuildPursuitRowsFromTracker(list);
            return tracked != null ? tracked : BuildCupRows(list);
        }

        private List<PRow> BuildPursuitRowsFromTracker(List<ZeepkistNetworkPlayer> list)
        {
            if (!EnsurePursuitApi()) return null;
            try
            {
                System.Collections.IEnumerable parts = puParticipantsFI.GetValue(null) as System.Collections.IEnumerable;
                if (parts == null) return null;
                Dictionary<ulong, ZeepkistNetworkPlayer> byId = new Dictionary<ulong, ZeepkistNetworkPlayer>();
                if (list != null) foreach (ZeepkistNetworkPlayer p in list) byId[p.SteamID] = p;

                List<PRow> rows = new List<PRow>();
                foreach (object pp in parts)
                {
                    if (pp == null) continue;
                    if ((bool)puElimFI.GetValue(pp)) continue;  // eliminated -> drop
                    if ((bool)puSpecFI.GetValue(pp)) continue;  // spectator -> drop
                    ulong sid = (ulong)puSidFI.GetValue(pp);
                    int lives = (int)puLivesFI.GetValue(pp);
                    ulong pursuer = (ulong)puPursuerFI.GetValue(pp);
                    PRow r = new PRow();
                    r.Sid = sid.ToString(CultureInfo.InvariantCulture);
                    r.Name = PursuitName(sid, byId);
                    Stat st; r.Elo = pool.TryGetValue(r.Sid, out st) ? st.Elo : 0f;
                    float myTime = GetRoundTime(sid);
                    r.HasTime = myTime >= 0f;
                    if (r.HasTime) r.T = myTime;
                    // In danger = your pursuer has a time this round that beats yours (or you have none).
                    float pTime = GetRoundTime(pursuer);
                    bool inDanger = pTime >= 0f && (!r.HasTime || pTime < myTime);
                    if (lives <= 1) r.Status = PStatus.LastLife;   // orange: one hit from out
                    else if (inDanger) r.Status = PStatus.Bubble;  // yellow: about to lose a life
                    else r.Status = PStatus.Safe;                  // white
                    rows.Add(r);
                }
                if (rows.Count == 0) return null; // no active pursuit roster -> fall back
                // Order strictly by the leaderboard: timed fastest-first, untimed at the bottom (elo desc).
                rows.Sort(delegate (PRow a, PRow b)
                {
                    if (a.HasTime != b.HasTime) return a.HasTime ? -1 : 1;
                    if (a.HasTime) return a.T.CompareTo(b.T);
                    return b.Elo.CompareTo(a.Elo);
                });
                return rows;
            }
            catch { return null; }
        }

        // This round's finish time for a Steam ID from the live leaderboard, or -1 if none yet.
        private float GetRoundTime(ulong sid)
        {
            try
            {
                LbEntry e;
                if (board.TryGetValue(sid, out e)) { float t = ParseTime(e.Time); if (t >= 0f) return t; }
            }
            catch { }
            return -1f;
        }

        // PursuitPlayer carries only a Steam ID; resolve a display name from the lobby roster, then the
        // stats pool, then fall back to the raw id.
        private string PursuitName(ulong sid, Dictionary<ulong, ZeepkistNetworkPlayer> byId)
        {
            ZeepkistNetworkPlayer p;
            if (byId != null && byId.TryGetValue(sid, out p)) { string n = SafeName(p); if (!string.IsNullOrEmpty(n)) return n; }
            Stat st;
            if (pool.TryGetValue(sid.ToString(CultureInfo.InvariantCulture), out st) && !string.IsNullOrEmpty(st.Name)) return st.Name;
            return sid.ToString(CultureInfo.InvariantCulture);
        }

        // ---- PursuitZK bridge (TyO roster + pursuer/target/lives by Steam ID; soft dep, reflection) ----
        // PursuitTracker.pursuitParticipants is the live List<PursuitPlayer>; each carries steamID,
        // livesRemaining, targetedBySteamID (pursuer), targetSteamID (target), eliminated, spectator.
        // Replicated to every client (the mod Harmony-patches DrawIngameLeaderboard), so a non-host reads it.
        private bool puChecked, puAvailable;
        private FieldInfo puParticipantsFI; // static List<PursuitPlayer> PursuitTracker.pursuitParticipants
        private FieldInfo puSidFI, puLivesFI, puPursuerFI, puTargetFI, puElimFI, puSpecFI;

        private bool EnsurePursuitApi()
        {
            if (puChecked) return puAvailable;
            puChecked = true;
            try
            {
                Type ptT = null, ppT = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "PursuitZK") continue;
                    ptT = a.GetType("PursuitTracker");
                    ppT = a.GetType("PursuitPlayer");
                    break;
                }
                if (ptT == null || ppT == null) return false;
                BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                puParticipantsFI = ptT.GetField("pursuitParticipants", sf);
                BindingFlags inf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                puSidFI = ppT.GetField("steamID", inf);
                puLivesFI = ppT.GetField("livesRemaining", inf);
                puPursuerFI = ppT.GetField("targetedBySteamID", inf);
                puTargetFI = ppT.GetField("targetSteamID", inf); // used by the dual-cam (feature #2)
                puElimFI = ppT.GetField("eliminated", inf);
                puSpecFI = ppT.GetField("spectator", inf);
                puAvailable = puParticipantsFI != null && puSidFI != null && puLivesFI != null &&
                              puPursuerFI != null && puElimFI != null && puSpecFI != null;
            }
            catch { puAvailable = false; }
            return puAvailable;
        }

        // ---- COTDTracker bridge (authoritative cup roster + elimination state; soft dep, reflection) ----
        // CupPlayerTracker.CupPlayers is a static Dictionary<ulong steamID, CupPlayer> populated at cup
        // start, so it is the exact "who is in the championship" set (round 1 included), SID-keyed - no
        // name matching. Each CupPlayer carries isStillIn (not eliminated), hasFinished (timed this
        // round) and Time. GetNumEliminations() is the live per-round elimination count.
        private bool cotdChecked;
        private bool cotdAvailable;
        private FieldInfo cotdCupPlayersFI;   // static Dictionary<ulong, CupPlayer> CupPlayers
        private FieldInfo cotdIsCupRunningFI; // static bool isCupRunning
        private MethodInfo cotdNumElimMI;     // static int GetNumEliminations()
        private FieldInfo cpSteamIdFI, cpNameFI, cpStillInFI, cpFinishedFI, cpTimeFI;

        private bool EnsureCotdApi()
        {
            if (cotdChecked) return cotdAvailable;
            cotdChecked = true;
            try
            {
                Type cptT = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "COTDTracker") continue;
                    cptT = a.GetType("COTDTracker.CupPlayerTracker");
                    break;
                }
                if (cptT == null) return false;
                BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                cotdCupPlayersFI = cptT.GetField("CupPlayers", sf);
                cotdIsCupRunningFI = cptT.GetField("isCupRunning", sf);
                cotdNumElimMI = cptT.GetMethod("GetNumEliminations", sf, null, Type.EmptyTypes, null);
                Type cpT = cptT.GetNestedType("CupPlayer", BindingFlags.Public | BindingFlags.NonPublic);
                if (cpT != null)
                {
                    BindingFlags inf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    cpSteamIdFI = cpT.GetField("SteamID", inf);
                    cpNameFI = cpT.GetField("Name", inf);
                    cpStillInFI = cpT.GetField("isStillIn", inf);
                    cpFinishedFI = cpT.GetField("hasFinished", inf);
                    cpTimeFI = cpT.GetField("Time", inf);
                }
                cotdAvailable = cotdCupPlayersFI != null && cotdIsCupRunningFI != null && cotdNumElimMI != null &&
                                cpSteamIdFI != null && cpStillInFI != null && cpFinishedFI != null && cpTimeFI != null;
            }
            catch { cotdAvailable = false; }
            return cotdAvailable;
        }

        // ---- Topout native data (custom-leaderboard override text + championship points) ----
        private bool toChecked;
        private MethodInfo toGetOverrideMI;  // ZeepkistNetwork.GetLeaderboardOverride(ulong)
        private FieldInfo[] toOverrideStrFIs;// every string field on that override struct
        private FieldInfo toOverrideTextFI;  // LeaderboardOverrideItem.overridePositionText
        private FieldInfo toChampFI;         // ZeepkistNetworkPlayer/PlayerBase.ChampionshipPoints

        private void EnsureTopoutApi()
        {
            if (toChecked) return;
            toChecked = true;
            try
            {
                Type zn = typeof(ZeepkistNetwork);
                toGetOverrideMI = zn.GetMethod("GetLeaderboardOverride", new Type[] { typeof(ulong) });
                if (toGetOverrideMI != null && toGetOverrideMI.ReturnType != null)
                {
                    toOverrideTextFI = toGetOverrideMI.ReturnType.GetField("overridePositionText");
                    // Every string field on the override struct, so the Showdown team colour can be
                    // found without hardcoding a field name we can't verify from here.
                    List<FieldInfo> strs = new List<FieldInfo>();
                    FieldInfo[] all = toGetOverrideMI.ReturnType.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i].FieldType == typeof(string)) strs.Add(all[i]);
                    toOverrideStrFIs = strs.ToArray();
                }
                Type t = typeof(ZeepkistNetworkPlayer);
                while (t != null && toChampFI == null)
                {
                    toChampFI = t.GetField("ChampionshipPoints",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    t = t.BaseType;
                }
            }
            catch { }
        }

        // The mod writes WIN/FIN/\o7/<points> into overridePositionText; we test the same
        // substrings it sorts on, so detection stays correct even if the surrounding text changes.
        private string ToOverrideText(ulong sid)
        {
            try
            {
                if (toGetOverrideMI == null || toOverrideTextFI == null) return null;
                object ov = toGetOverrideMI.Invoke(null, new object[] { sid });
                if (ov == null) return null;
                return toOverrideTextFI.GetValue(ov) as string;
            }
            catch { return null; }
        }

        private int ToChampPoints(ZeepkistNetworkPlayer p)
        {
            try
            {
                if (toChampFI == null) return 0;
                object v = toChampFI.GetValue(p);
                if (v is Vector2Int) return ((Vector2Int)v).x;
            }
            catch { }
            return 0;
        }

        // Cup casting order. The list IS COTDTracker's championship roster (everyone still in the cup,
        // round 1 included), read straight from CupPlayerTracker.CupPlayers by SteamID - so spectators
        // and the casting account, who are never in the cup, never appear, and eliminated players drop
        // the instant COTDTracker marks them out. Tiers (lower = higher priority, shown first):
        //   0 RED    - no time yet this round, OR (once everyone has posted) the slowest `elim` racers
        //   1 YELLOW - the next `elim` up: on the bubble, at risk of being sniped
        //   2 WHITE  - safe, shown by time (fastest first)
        // When no COTDTracker cup is running (plain lobby), fall back to the live leaderboard.
        private List<PRow> BuildCupRows(List<ZeepkistNetworkPlayer> list)
        {
            List<PRow> tracked = BuildCupRowsFromTracker();
            return tracked != null ? tracked : BuildCupRowsFallback(list);
        }

        // Authoritative path: COTDTracker's own roster + elimination state + per-round elim count.
        // Returns null when there is no running cup to read, so the caller uses the lobby fallback.
        private List<PRow> BuildCupRowsFromTracker()
        {
            if (!EnsureCotdApi()) return null;
            try
            {
                if (!(bool)cotdIsCupRunningFI.GetValue(null)) return null;
                System.Collections.IDictionary cps = cotdCupPlayersFI.GetValue(null) as System.Collections.IDictionary;
                if (cps == null || cps.Count == 0) return null;
                int x = 0;
                try { x = (int)cotdNumElimMI.Invoke(null, null); } catch { }
                if (x < 0) x = 0;

                List<PRow> racers = new List<PRow>();
                int finishedCount = 0;
                foreach (object cp in cps.Values)
                {
                    if (cp == null) continue;
                    if (!(bool)cpStillInFI.GetValue(cp)) continue; // eliminated from the cup -> drop
                    PRow r = new PRow();
                    ulong sid = (ulong)cpSteamIdFI.GetValue(cp);
                    r.Sid = sid.ToString(CultureInfo.InvariantCulture);
                    r.Name = (cpNameFI != null ? cpNameFI.GetValue(cp) as string : null) ?? "?";
                    Stat st; r.Elo = pool.TryGetValue(r.Sid, out st) ? st.Elo : 0f;
                    r.HasTime = (bool)cpFinishedFI.GetValue(cp);
                    if (r.HasTime) { r.T = (float)cpTimeFI.GetValue(cp); finishedCount++; }
                    racers.Add(r);
                }
                // Position the finished racers by time (fastest first); unfinished sink to the bottom.
                List<PRow> finished = new List<PRow>();
                foreach (PRow r in racers) if (r.HasTime) finished.Add(r);
                finished.Sort(delegate (PRow a, PRow b) { return a.T.CompareTo(b.T); });
                for (int i = 0; i < finished.Count; i++) finished[i].Pos = i + 1;
                foreach (PRow r in racers) if (!r.HasTime) r.Pos = 99999;

                ColorAndSortCupRows(racers, finishedCount, x);
                return racers;
            }
            catch { return null; }
        }

        // Plain-lobby fallback (no COTDTracker cup): show everyone, timed from the live leaderboard.
        private List<PRow> BuildCupRowsFallback(List<ZeepkistNetworkPlayer> list)
        {
            List<PRow> racers = new List<PRow>();
            int timedCount = 0;
            foreach (ZeepkistNetworkPlayer p in list)
            {
                PRow r = new PRow();
                r.Sid = p.SteamID.ToString(CultureInfo.InvariantCulture);
                r.Name = SafeName(p); if (r.Name == null) r.Name = "?";
                if (IsOut(r.Name)) continue; // eliminated (if a cup was tracked earlier) -> drop
                Stat st; r.Elo = pool.TryGetValue(r.Sid, out st) ? st.Elo : 0f;
                LbEntry e;
                if (board.TryGetValue(p.SteamID, out e) && ParseTime(e.Time) >= 0f)
                { r.InBoard = true; r.HasTime = true; r.Pos = e.Position; timedCount++; }
                else r.Pos = 99999;
                racers.Add(r);
            }
            ColorAndSortCupRows(racers, timedCount, elimCount > 0 ? elimCount : 0);
            return racers;
        }

        // Shared red/yellow/white assignment + ordering (the user's spec). `timedCount` is how many of
        // `racers` have a time; `x` is the elimination count. Rows must have Pos set (fastest = 1,
        // unfinished = large) and HasTime/Elo populated.
        private void ColorAndSortCupRows(List<PRow> racers, int timedCount, int x)
        {
            if (timedCount < racers.Count)
            {
                // Populating: no-time = RED, timed = WHITE. No bubble until the field is complete.
                foreach (PRow r in racers)
                {
                    if (r.HasTime) { r.Status = PStatus.Safe; r.Tier = 2; }
                    else { r.Status = PStatus.NoTime; r.Tier = 0; }
                }
            }
            else if (x > 0)
            {
                // Everyone timed: slowest x = RED (elim zone), next x up = YELLOW (at risk), rest WHITE.
                racers.Sort(delegate (PRow a, PRow b) { return a.Pos.CompareTo(b.Pos); }); // fastest first
                int total = racers.Count;
                for (int i = 0; i < total; i++)
                {
                    int fromBottom = total - 1 - i; // 0 = slowest
                    PRow r = racers[i];
                    if (fromBottom < x) { r.Status = PStatus.NoTime; r.Tier = 0; }          // elimination zone
                    else if (fromBottom < 2 * x) { r.Status = PStatus.Bubble; r.Tier = 1; } // at risk
                    else { r.Status = PStatus.Safe; r.Tier = 2; }
                }
            }
            else
            {
                foreach (PRow r in racers) { r.Status = PStatus.Safe; r.Tier = 2; }
            }

            // Tier first (red -> yellow -> white); within a tier timed players come first by position
            // (fastest first), no-time players after them by ELO desc.
            racers.Sort(delegate (PRow a, PRow b)
            {
                if (a.Tier != b.Tier) return a.Tier.CompareTo(b.Tier);
                if (a.HasTime != b.HasTime) return a.HasTime ? -1 : 1;
                if (a.HasTime) return a.Pos.CompareTo(b.Pos);
                return b.Elo.CompareTo(a.Elo);
            });
        }

        // ---- Multi-map grouping for the Times view: split a player's rounds by the map raced on ----
        private static string MapKey(string uid) { return uid == null ? "" : uid; }

        // Distinct map keys present across the given players' times, ordered by first-seen (mapOrder);
        // any key not in mapOrder (e.g. the null-uid bucket) is appended last.
        private List<string> OrderedMapKeys(List<RoundTime> a, List<RoundTime> b)
        {
            HashSet<string> present = new HashSet<string>();
            CollectMapKeys(present, a);
            CollectMapKeys(present, b);
            List<string> ordered = new List<string>();
            for (int i = 0; i < mapOrder.Count; i++)
                if (present.Remove(mapOrder[i])) ordered.Add(mapOrder[i]);
            foreach (string k in present) ordered.Add(k);
            return ordered;
        }
        private static void CollectMapKeys(HashSet<string> set, List<RoundTime> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) set.Add(MapKey(list[i].Uid));
        }
        // Sorted round numbers in this player's list that belong to the given map key.
        private static void CollectRounds(SortedDictionary<int, bool> set, List<RoundTime> list, string mapKey)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
                if (MapKey(list[i].Uid) == mapKey) set[list[i].Round] = true;
        }
        // Display name for a map key (its real level name, or "Map" when unknown/untagged).
        private string MapLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Map";
            string nm;
            if (mapNames.TryGetValue(key, out nm) && !string.IsNullOrEmpty(nm)) return nm;
            return "Map";
        }

        private void DrawTimesCard(float x, float y, float w, string name)
        {
            List<RoundTime> times;
            playerRoundTimes.TryGetValue(name, out times);

            // Group this player's rounds by map (each map a subheader), so an alternating cup shows a
            // player's per-map progression (R1, R3, R5) together instead of interleaved with the other map.
            List<string> maps = OrderedMapKeys(times, null);
            Dictionary<string, List<int>> byMap = new Dictionary<string, List<int>>();
            int totalRows = 0;
            for (int mi = 0; mi < maps.Count; mi++)
            {
                SortedDictionary<int, bool> set = new SortedDictionary<int, bool>();
                CollectRounds(set, times, maps[mi]);
                List<int> rl = new List<int>(set.Keys);
                byMap[maps[mi]] = rl;
                totalRows += rl.Count;
            }

            float h = Sc(64f);
            if (totalRows == 0) h += Sc(28f);
            else for (int mi = 0; mi < maps.Count; mi++) h += Sc(24f) + byMap[maps[mi]].Count * Sc(28f);

            cardDrawRect = new Rect(x, y, w, h);
            GUILayout.BeginArea(new Rect(x, y, w, h), boxStyle);
            GUILayout.Label(name, pnameStyle); // player name: site default white (live name, no pool lookup)
            if (totalRows == 0)
            {
                GUILayout.Label("no times yet", labelStyle);
            }
            else
            {
                for (int mi = 0; mi < maps.Count; mi++)
                {
                    List<int> rl = byMap[maps[mi]];
                    if (rl.Count == 0) continue;
                    GUI.contentColor = accentCol;
                    GUILayout.Label(MapLabel(maps[mi]), centerStyle); // map divider
                    GUI.contentColor = Color.white;
                    for (int i = rl.Count - 1; i >= 0; i--) // newest round first
                    {
                        // FmtClock normalises the comma decimals COTDTracker logs AND renders the
                        // game's M:SS.mmm, so the card matches the in-game leaderboard beside it.
                        Row("R" + rl[i], FmtClock(TimeForRound(times, rl[i])));
                    }
                }
            }
            GUILayout.EndArea();
        }

        // Two-player TIMES comparison (the Times toggle with two selected): each round's time side by
        // side, faster highlighted. Same header/colours as DrawH2H but lists live per-round times.
        private void DrawTimesH2H(float x, float y, float w, Stat a, Stat b)
        {
            List<RoundTime> ta = RoundTimesFor(a);
            List<RoundTime> tb = RoundTimesFor(b);

            // Group rounds by map (each a subheader), union of both players per map. Rows keep their real
            // round number, so a map's rows read R1, R3, R5 in an alternating cup - still like-for-like
            // (everyone races the same map each round), just no longer interleaved with the other map.
            List<string> maps = OrderedMapKeys(ta, tb);
            Dictionary<string, List<int>> byMap = new Dictionary<string, List<int>>();
            int totalRows = 0;
            for (int mi = 0; mi < maps.Count; mi++)
            {
                SortedDictionary<int, bool> set = new SortedDictionary<int, bool>();
                CollectRounds(set, ta, maps[mi]);
                CollectRounds(set, tb, maps[mi]);
                List<int> rl = new List<int>(set.Keys);
                byMap[maps[mi]] = rl;
                totalRows += rl.Count;
            }

            float h = Sc(64f);
            if (totalRows == 0) h += Sc(28f);
            else for (int mi = 0; mi < maps.Count; mi++) h += Sc(24f) + byMap[maps[mi]].Count * Sc(28f);

            cardDrawRect = new Rect(x, y, w, h);
            GUILayout.BeginArea(new Rect(x, y, w, h), boxStyle);
            GUILayout.BeginHorizontal();
            GUI.contentColor = NameColor(a);
            GUILayout.Label(a.Name, nameLeftStyle, GUILayout.Width(Sc(170f)));
            GUILayout.FlexibleSpace();
            GUI.contentColor = NameColor(b);
            GUILayout.Label(b.Name, nameRightStyle, GUILayout.Width(Sc(170f)));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
            AccentLine(LineColor(a), LineColor(b));
            if (totalRows == 0) { GUILayout.Label("no times yet", labelStyle); GUILayout.EndArea(); return; }

            for (int mi = 0; mi < maps.Count; mi++)
            {
                List<int> rl = byMap[maps[mi]];
                if (rl.Count == 0) continue;
                GUI.contentColor = accentCol;
                GUILayout.Label(MapLabel(maps[mi]), centerStyle); // map divider
                GUI.contentColor = Color.white;
                for (int i = rl.Count - 1; i >= 0; i--) // newest round first
                {
                    int rd = rl[i];
                    string sa = TimeForRound(ta, rd);
                    string sb = TimeForRound(tb, rd);
                    float fa = ParseTime(sa);
                    float fb = ParseTime(sb);
                    int better = 0;
                    if (fa >= 0f && fb >= 0f) better = fa < fb ? 1 : (fb < fa ? 2 : 0);
                    else if (fa >= 0f) better = 1;
                    else if (fb >= 0f) better = 2;
                    CompRow(FmtClock(sa), "R" + rd, FmtClock(sb), better);
                }
            }
            GUILayout.EndArea();
        }

        private static string TimeForRound(List<RoundTime> list, int round)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++) if (list[i].Round == round) return list[i].Time;
            return null;
        }

        // Kerki is topout: it has no podium, so its second headline stat is FINALIST appearances
        // (reached the final). The pool builder maps only w1-w5/f finishes into the finish history and
        // drops "o" (out before the final), so the size of that history IS the finalist count. Other
        // comps keep podiums (top 3). ("Wins" for Kerki is the pool's top-5 count, left as-is.)
        private bool CompUsesFinalists(string comp) { return comp == "kerki"; }
        private string SecondStatLabel(string comp) { return CompUsesFinalists(comp) ? "finalists" : "podiums"; }
        private int SecondStatValue(CompStat c)
        {
            if (c == null) return 0;
            if (CompUsesFinalists(selectedComp)) return c.Hist != null ? c.Hist.Count : 0;
            return c.Podiums;
        }

        private void DrawCard(Rect r, Stat s)
        {
            // Height auto-sizes via GUILayout; we give a generous area.
            cardDrawRect = new Rect(r.x, r.y, r.width, Sc(300f));
            GUILayout.BeginArea(new Rect(r.x, r.y, r.width, Sc(300f)), boxStyle);
            GUI.contentColor = NameColor(s);
            GUILayout.Label(s.Name, pnameStyle);
            GUI.contentColor = Color.white;
            AccentLine(LineColor(s), null);
            // ELO/peak = COTD weighted, always. ELO value is tinted by its COTD tier.
            // Weighted ELO is the card's headline: it's the one true cross-comp skill number, so it
            // gets the big type and the rank rides along as a small suffix. Peak keeps the default
            // size and the career totals are demoted, so the eye lands on ELO first.
            string rankStr = s.Rank > 0 ? ("#" + s.Rank) : "";
            if (s.Elo > 0) RowHeadline("Weighted ELO", F1(s.Elo), rankStr, TierColor(s.Elo));
            else Row("Weighted ELO", "-");
            if (s.Peak > 0) RowColored("Peak ELO", F1(s.Peak), TierColor(s.Peak));
            else Row("Peak ELO", "-");
            // wins/podiums/cups/best from the selected comp.
            GUILayout.Label(CompLabel(selectedComp) + " record", centerStyle);
            CompStat c = CompFor(s, selectedComp);
            RowSmall("Wins", c != null ? c.Wins.ToString() : "-");
            RowSmall(CompUsesFinalists(selectedComp) ? "Finalists" : "Podiums", c != null ? SecondStatValue(c).ToString() : "-");
            RowSmall("Cups", c != null ? c.Cups.ToString() : "-");
            RowSmall("Best finish", (c != null && c.Best > 0) ? ("#" + c.Best) : "-");
            GUILayout.EndArea();
        }

        private void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, valueStyle);
            GUILayout.EndHorizontal();
        }

        // Same as Row but tints the value (used for the tier-coloured ELO).
        private void RowColored(string label, string value, Color valueColor)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle);
            GUILayout.FlexibleSpace();
            Color prev = GUI.contentColor;
            GUI.contentColor = valueColor;
            GUILayout.Label(value, valueStyle);
            GUI.contentColor = prev;
            GUILayout.EndHorizontal();
        }

        // The card's headline number: big and tier-coloured, with an optional smaller, dimmer suffix
        // (the rank) parked in front of it. IMGUI can't mix font sizes inside a single Label, so the
        // suffix has to be its own label.
        private void RowHeadline(string label, string value, string suffix, Color valueColor)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle);
            GUILayout.FlexibleSpace();
            Color prev = GUI.contentColor;
            if (!string.IsNullOrEmpty(suffix))
            {
                GUI.contentColor = dimColor;
                GUILayout.Label(suffix, labelSmallStyle, GUILayout.ExpandWidth(false));
            }
            GUI.contentColor = valueColor;
            GUILayout.Label(value, valueBigStyle, GUILayout.ExpandWidth(false));
            GUI.contentColor = prev;
            GUILayout.EndHorizontal();
        }

        // Demoted row: supporting numbers (career totals) that shouldn't compete with the headline.
        private void RowSmall(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelSmallStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, valueSmallStyle);
            GUILayout.EndHorizontal();
        }

        // COTD weighted-ELO tier colours (matches the site legend): Gold 1600+, Master 1700+,
        // Pro 1800+, Legend 2000+; below 1600 stays the neutral value colour.
        private static Color TierColor(float elo)
        {
            if (elo >= 2000f) return new Color(0.86f, 0.15f, 0.15f); // #dc2626 Legend (red)
            if (elo >= 1800f) return new Color(0.66f, 0.33f, 0.97f); // #a855f7 Pro (purple)
            if (elo >= 1700f) return new Color(0.23f, 0.51f, 0.96f); // #3b82f6 Master (blue)
            if (elo >= 1600f) return new Color(0.06f, 0.73f, 0.51f); // #10b981 Gold (green)
            return Color.white;                                       // below tiers: neutral
        }

        private static string F1(float v)
        {
            return v.ToString("F1", CultureInfo.InvariantCulture);
        }

        // Single comparison card: name1 | (label) | name2, with the better side highlighted.
        private void DrawH2H(float x, float y, float w, Stat a, Stat b)
        {
            cardDrawRect = new Rect(x, y, w, Sc(320f));
            GUILayout.BeginArea(new Rect(x, y, w, Sc(320f)), boxStyle);

            // Header: name1 (left) ... name2 (right)
            GUILayout.BeginHorizontal();
            GUI.contentColor = NameColor(a);
            GUILayout.Label(a.Name, nameLeftStyle, GUILayout.Width(Sc(170f)));
            GUILayout.FlexibleSpace();
            GUI.contentColor = NameColor(b);
            GUILayout.Label(b.Name, nameRightStyle, GUILayout.Width(Sc(170f)));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
            AccentLine(LineColor(a), LineColor(b)); // each half in that player's COTD colour

            // fastest time in the cup (lower better); slower side shows the gap
            string lapA = FastestInCup(a);
            string lapB = FastestInCup(b);
            // Game-format clock (M:SS.mmm) so the card reads the same as the leaderboard next to it.
            string dispA = FmtClock(lapA);
            string dispB = FmtClock(lapB);
            float fa = ParseTime(lapA);
            float fb = ParseTime(lapB);
            string gapStr = null;
            int slower = 0;
            if (fa >= 0 && fb >= 0 && fa != fb)
            {
                gapStr = FmtGap(Math.Abs(fa - fb));
                slower = fa > fb ? 1 : 2;
            }
            // The live lap times are what a caster is actually reading mid-race: headline row.
            CompRowBig(dispA, "fastest lap", dispB, BetterTime(lapA, lapB));
            GapLine(gapStr, slower);

            // mutual record (more wins better) from the chosen h2h source; center shows total shared
            int w1, w2;
            MutualRecord(a, b, selectedComp, out w1, out w2);
            CompRowSmall(w1.ToString(), "mutual (" + (w1 + w2) + ", " + CompLabel(selectedComp) + ")", w2.ToString(),
                w1 > w2 ? 1 : (w2 > w1 ? 2 : 0));

            // "-" for players with no pool data (mirrors the Stats card)
            CompRowSmall(a.Peak > 0 ? F1(a.Peak) : "-", "peak elo",
                    b.Peak > 0 ? F1(b.Peak) : "-", Better(a.Peak, b.Peak, true));
            CompRowSmall(a.Elo > 0 ? F1(a.Elo) : "-", "current elo",
                    b.Elo > 0 ? F1(b.Elo) : "-", Better(a.Elo, b.Elo, true));
            // wins/podiums/pb from the selected comp
            CompStat ca = CompFor(a, selectedComp);
            CompStat cb = CompFor(b, selectedComp);
            int aw = ca != null ? ca.Wins : 0, bw = cb != null ? cb.Wins : 0;
            int ap = SecondStatValue(ca), bp = SecondStatValue(cb);
            int ab = ca != null ? ca.Best : 0, bb = cb != null ? cb.Best : 0;
            CompRowSmall(aw.ToString(), CompLabel(selectedComp) + " wins", bw.ToString(), Better(aw, bw, true));
            CompRowSmall(ap.ToString(), SecondStatLabel(selectedComp), bp.ToString(), Better(ap, bp, true));
            CompRowSmall(Pb(ab), "pb", Pb(bb), BetterPb(ab, bb));

            GUILayout.EndArea();
        }

        private void CompRow(string left, string label, string right, int better)
        {
            GUILayout.BeginHorizontal();
            GUI.contentColor = (better == 1) ? goodColor : dimColor;
            GUILayout.Label(left, valLeftStyle, GUILayout.Width(Sc(110f)));
            GUI.contentColor = Color.white;
            GUILayout.Label(label, centerStyle, GUILayout.ExpandWidth(true));
            GUI.contentColor = (better == 2) ? goodColor : dimColor;
            GUILayout.Label(right, valRightStyle, GUILayout.Width(Sc(110f)));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
        }

        // The comparison's headline row (the live lap times). The losing side stays WHITE rather than
        // dim: at this size it's the number the caster reads second, and dimming it hurt legibility.
        private void CompRowBig(string left, string label, string right, int better)
        {
            GUILayout.BeginHorizontal();
            GUI.contentColor = (better == 1) ? goodColor : Color.white;
            GUILayout.Label(left, valBigLeftStyle, GUILayout.Width(Sc(130f)));
            GUI.contentColor = Color.white;
            GUILayout.Label(label, centerStyle, GUILayout.ExpandWidth(true));
            GUI.contentColor = (better == 2) ? goodColor : Color.white;
            GUILayout.Label(right, valBigRightStyle, GUILayout.Width(Sc(130f)));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
        }

        // The gap, parked on its own line directly under the SLOWER player's time (1 = left, 2 = right)
        // so it needs no label to be understood. It used to be appended to the time string, but at the
        // headline font size "0:43.180  (+.455)" overran the 320px card and clipped the number itself.
        private void GapLine(string gap, int slower)
        {
            if (string.IsNullOrEmpty(gap) || slower == 0) return;
            GUILayout.BeginHorizontal();
            if (slower == 1)
            {
                GUI.contentColor = elimColor;
                GUILayout.Label(gap, valSmallLeftStyle, GUILayout.Width(Sc(130f)));
                GUI.contentColor = Color.white;
                GUILayout.FlexibleSpace();
            }
            else
            {
                GUILayout.FlexibleSpace();
                GUI.contentColor = elimColor;
                GUILayout.Label(gap, valSmallRightStyle, GUILayout.Width(Sc(130f)));
                GUI.contentColor = Color.white;
            }
            GUILayout.EndHorizontal();
        }

        // Demoted comparison row: the static pool stats under the headline.
        private void CompRowSmall(string left, string label, string right, int better)
        {
            GUILayout.BeginHorizontal();
            GUI.contentColor = (better == 1) ? goodColor : dimColor;
            GUILayout.Label(left, valSmallLeftStyle, GUILayout.Width(Sc(110f)));
            GUI.contentColor = Color.white;
            GUILayout.Label(label, centerStyle, GUILayout.ExpandWidth(true));
            GUI.contentColor = (better == 2) ? goodColor : dimColor;
            GUILayout.Label(right, valSmallRightStyle, GUILayout.Width(Sc(110f)));
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
        }

        private static int Better(float a, float b, bool higherBetter)
        {
            if (a == b) return 0;
            bool aWins = higherBetter ? a > b : a < b;
            return aWins ? 1 : 2;
        }

        private static int BetterPb(int a, int b)
        {
            // best finish: 1 is best; 0 means none -> worst
            int aa = a > 0 ? a : int.MaxValue;
            int bb = b > 0 ? b : int.MaxValue;
            if (aa == bb) return 0;
            return aa < bb ? 1 : 2;
        }

        private static int BetterTime(string a, string b)
        {
            float fa = ParseTime(a);
            float fb = ParseTime(b);
            if (fa < 0 && fb < 0) return 0;
            if (fa < 0) return 2;
            if (fb < 0) return 1;
            if (fa == fb) return 0;
            return fa < fb ? 1 : 2;
        }

        private static float ParseTime(string t)
        {
            if (string.IsNullOrEmpty(t)) return -1f;
            float v;
            if (float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return -1f;
        }

        private static string FmtLap(string t)
        {
            if (string.IsNullOrEmpty(t)) return "-";
            return t.Replace(',', '.');
        }

        // Seconds-string -> M:SS.mmm clock (e.g. "23.456" -> "0:23.456", "85.2" -> "1:25.200").
        private static string FmtClock(string raw)
        {
            float t = ParseTime(raw);
            if (t < 0f) return FmtLap(raw);
            int mins = (int)(t / 60f);
            float secs = t - mins * 60f;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", mins, secs);
        }

        private static string Pb(int best)
        {
            return best > 0 ? ("#" + best) : "-";
        }

        // Time gap: under 1s drops the leading zero -> "(+.759)"; 1s+ -> "(+1.234)".
        private static string FmtGap(float d)
        {
            string s = d.ToString("0.000", CultureInfo.InvariantCulture);
            if (d < 1f) s = s.Substring(1); // "0.759" -> ".759"
            return "(+" + s + ")";
        }

        private Font uiFont;

        // Builds a filled rounded-rectangle texture with 1px anti-aliased corners.
        private static Texture2D MakeRoundedRect(int size, int radius, Color fill)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f;
                    float fy = y + 0.5f;
                    float dx = 0f, dy = 0f;
                    if (fx < radius) dx = radius - fx; else if (fx > size - radius) dx = fx - (size - radius);
                    if (fy < radius) dy = radius - fy; else if (fy > size - radius) dy = fy - (size - radius);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - dist + 0.5f); // smooth 1px edge
                    tex.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * a));
                }
            }
            tex.Apply();
            return tex;
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;
            builtScale = uiScale; // these styles are built for the current scale

            // Solid (opaque) dark navy panel with soft, anti-aliased rounded corners.
            if (bgTex != null) UnityEngine.Object.Destroy(bgTex); // free the old one on a scale rebuild
            int radius = Sci(14);
            bgTex = MakeRoundedRect(radius * 2 + 4, radius, new Color(0.04f, 0.06f, 0.11f, 1f));

            // One white solid, tinted at draw time (underlines, VS cam frame).
            if (whiteTex != null) UnityEngine.Object.Destroy(whiteTex);
            whiteTex = MakeSolid(Color.white);

            // A sporty condensed sans that suits a racing HUD; falls back gracefully.
            try
            {
                uiFont = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Bahnschrift", "Segoe UI Semibold", "Segoe UI", "Arial" }, Sci(18));
            }
            catch
            {
                try { uiFont = Font.CreateDynamicFontFromOSFont("Arial", Sci(18)); } catch { uiFont = null; }
            }

            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = bgTex;
            boxStyle.padding = ScRO(16, 16, 12, 14);
            // 9-slice border = corner radius so corners stay crisp while edges stretch.
            boxStyle.border = new RectOffset(radius, radius, radius, radius);

            headerStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) headerStyle.font = uiFont;
            headerStyle.fontSize = Sci(26);
            headerStyle.fontStyle = FontStyle.Bold;
            headerStyle.normal.textColor = accentCol; // COTD site accent
            headerStyle.margin = ScRO(0, 0, 0, 10);

            // Player-name headers: white base so GUI.contentColor can tint them with the
            // player's COTD custom colour (winners) or the site's default name colour.
            pnameStyle = new GUIStyle(headerStyle);
            pnameStyle.normal.textColor = Color.white;

            labelStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) labelStyle.font = uiFont;
            labelStyle.fontSize = Sci(19);
            labelStyle.normal.textColor = new Color(0.80f, 0.84f, 0.92f);

            // Mode-bar "Best time:" line: plain white, left-aligned, sits under the buttons.
            bestStyle = new GUIStyle(labelStyle);
            bestStyle.normal.textColor = Color.white;
            bestStyle.alignment = TextAnchor.MiddleLeft;
            bestStyle.margin = ScRO(2, 0, 4, 0);

            valueStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) valueStyle.font = uiFont;
            valueStyle.fontSize = Sci(19);
            valueStyle.fontStyle = FontStyle.Bold;
            valueStyle.normal.textColor = Color.white;
            valueStyle.alignment = TextAnchor.MiddleRight;

            // Showdown score box. wordWrap is OFF everywhere here: a wrapped team tag rendered as
            // "STB / N" and a wrapped map name ate two lines of the card.
            sdTagStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) sdTagStyle.font = uiFont;
            sdTagStyle.fontSize = Sci(25);
            sdTagStyle.fontStyle = FontStyle.Bold;
            sdTagStyle.normal.textColor = Color.white;
            sdTagStyle.alignment = TextAnchor.MiddleLeft;
            sdTagStyle.wordWrap = false;
            sdTagStyle.clipping = TextClipping.Overflow;

            sdScoreStyle = new GUIStyle(sdTagStyle);
            sdScoreStyle.alignment = TextAnchor.MiddleCenter;
            sdScoreStyle.margin = ScRO(10, 10, 0, 0);

            // Map / round line under the tags, and the racer-name column.
            sdSubStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) sdSubStyle.font = uiFont;
            sdSubStyle.fontSize = Sci(17);
            sdSubStyle.normal.textColor = dimColor;
            sdSubStyle.alignment = TextAnchor.MiddleLeft;
            sdSubStyle.wordWrap = false;
            sdSubStyle.clipping = TextClipping.Overflow;

            sdNameStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) sdNameStyle.font = uiFont;
            sdNameStyle.fontSize = Sci(19);
            sdNameStyle.normal.textColor = Color.white;
            sdNameStyle.alignment = TextAnchor.MiddleLeft;
            sdNameStyle.wordWrap = false;
            sdNameStyle.clipping = TextClipping.Overflow;

            // H2H comparison styles (white base, tinted per player via GUI.contentColor)
            nameLeftStyle = new GUIStyle(pnameStyle);
            nameLeftStyle.fontSize = Sci(22);
            nameLeftStyle.alignment = TextAnchor.MiddleLeft;
            nameLeftStyle.margin = new RectOffset(0, 0, 0, 0);

            nameRightStyle = new GUIStyle(nameLeftStyle);
            nameRightStyle.alignment = TextAnchor.MiddleRight;

            centerStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) centerStyle.font = uiFont;
            centerStyle.fontSize = Sci(16);
            centerStyle.alignment = TextAnchor.MiddleCenter;
            centerStyle.normal.textColor = new Color(0.62f, 0.66f, 0.74f);

            valLeftStyle = new GUIStyle(valueStyle);
            valLeftStyle.alignment = TextAnchor.MiddleLeft;

            valRightStyle = new GUIStyle(valueStyle);
            valRightStyle.alignment = TextAnchor.MiddleRight;

            // Three tiers of number, so a card has an obvious place to look first (feedback from Yolo
            // after a live cast: "they look all the same, I didn't know where to look"). Same scale the
            // Showdown broadcast card already uses: one headline value, everything else demoted.
            valueBigStyle = new GUIStyle(valueStyle);
            valueBigStyle.fontSize = Sci(30);

            valueSmallStyle = new GUIStyle(valueStyle);
            valueSmallStyle.fontSize = Sci(16);

            labelSmallStyle = new GUIStyle(labelStyle);
            labelSmallStyle.fontSize = Sci(16);
            labelSmallStyle.normal.textColor = new Color(0.62f, 0.66f, 0.74f);

            valBigLeftStyle = new GUIStyle(valueStyle);
            valBigLeftStyle.fontSize = Sci(26);
            valBigLeftStyle.alignment = TextAnchor.MiddleLeft;

            valBigRightStyle = new GUIStyle(valBigLeftStyle);
            valBigRightStyle.alignment = TextAnchor.MiddleRight;

            valSmallLeftStyle = new GUIStyle(valueSmallStyle);
            valSmallLeftStyle.alignment = TextAnchor.MiddleLeft;

            valSmallRightStyle = new GUIStyle(valueSmallStyle);
            valSmallRightStyle.alignment = TextAnchor.MiddleRight;

            buttonStyle = new GUIStyle(GUI.skin.button);
            if (uiFont != null) buttonStyle.font = uiFont;
            buttonStyle.fontSize = Sci(16);
            buttonStyle.alignment = TextAnchor.MiddleLeft;
            buttonStyle.margin = ScRO(0, 0, 2, 2);
            buttonStyle.padding = ScRO(8, 8, 5, 5);

            buttonSelStyle = new GUIStyle(buttonStyle);
            buttonSelStyle.fontStyle = FontStyle.Bold;
            buttonSelStyle.normal.textColor = goodColor;
            buttonSelStyle.hover.textColor = goodColor;

            vsTitleStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) vsTitleStyle.font = uiFont;
            vsTitleStyle.fontSize = Sci(18);
            vsTitleStyle.fontStyle = FontStyle.Bold;
            vsTitleStyle.alignment = TextAnchor.MiddleLeft;
            vsTitleStyle.normal.textColor = Color.white;

            vsTitleRightStyle = new GUIStyle(vsTitleStyle);
            vsTitleRightStyle.alignment = TextAnchor.MiddleRight;
            vsTitleRightStyle.richText = true;
        }

        private static Texture2D MakeSolid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Color ParseHex(string hex, Color fallback)
        {
            Color c;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out c)) return c;
            return fallback;
        }

        // Player display colours, COTD-site style: cup winners get their custom colour;
        // everyone else gets the site defaults (near-white names, amber lines).
        private static Color NameColor(Stat s) { return ParseHex(s != null ? s.ColHex : null, pnameCol); }
        private static Color LineColor(Stat s) { return ParseHex(s != null ? s.ColHex : null, accentCol); }

        // Thin coloured underline inside a GUILayout flow. Pass a second colour to split it
        // 50/50 (the H2H divider: each half in that player's colour); null = single colour.
        private void AccentLine(Color left, Color? right)
        {
            Rect r = GUILayoutUtility.GetRect(1f, Sc(3f), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && whiteTex != null)
            {
                Color prev = GUI.color;
                if (right == null) { GUI.color = left; GUI.DrawTexture(r, whiteTex); }
                else
                {
                    GUI.color = left;
                    GUI.DrawTexture(new Rect(r.x, r.y, r.width / 2f, r.height), whiteTex);
                    GUI.color = right.Value;
                    GUI.DrawTexture(new Rect(r.x + r.width / 2f, r.y, r.width / 2f, r.height), whiteTex);
                }
                GUI.color = prev;
            }
            GUILayout.Space(Sc(6f));
        }

        private void OnDestroy()
        {
            try { BepInEx.Logging.Logger.Listeners.Remove(this); }
            catch { }
            UnsubscribeLeaderboard();
            try
            {
                PhotoModeApi.PhotoModeEntered -= OnPhotoModeEntered;
                PhotoModeApi.PhotoModeExited -= OnPhotoModeExited;
                RacingApi.RoundStarted -= OnRoundStarted;
                RacingApi.RoundEnded -= OnRoundEnded;
                MultiplayerApi.DisconnectedFromGame -= OnLeftLobby;
                ChatApi.ServerMessageReceived -= OnSdServerMessage;
                ChatApi.ChatMessageReceived -= OnSdChatMessage;
            }
            catch { }
            try { FreezeMouseLook(false); } catch { } // restore mouse sensitivity if we zeroed it
            try { if (cursorSaved) { Cursor.lockState = prevLock; Cursor.visible = prevCursorVisible; } }
            catch { }
        }
    }
}
