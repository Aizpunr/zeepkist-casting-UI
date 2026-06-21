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

        // ---- Live cup state (ported from casting-tool/parser.py CupState) ----
        private int liveRound;
        private bool pendingRound;
        private bool cupOver;
        private readonly Dictionary<string, string> roundTimes = new Dictionary<string, string>();
        private readonly HashSet<string> eliminatedLive = new HashSet<string>(); // out of the cup
        private readonly Dictionary<string, List<RoundTime>> playerRoundTimes =
            new Dictionary<string, List<RoundTime>>();

        // ---- Layout (drag-to-move while panel is open; persists to disk) ----
        private Rect cardRect = new Rect(24f, 130f, 320f, 290f);
        private Rect panelRect = new Rect(-1f, 130f, 280f, 440f);
        private Rect barRect = new Rect(-1f, 0f, 0f, 0f); // mode bar (Stats/Times/Round Wins); x<0 = default pos
        private Rect cardDrawRect;                          // actual drawn rect of the current card (for its drag grip)
        private int draggingId = -1; // 0 = card, 1 = panel, 2 = bar
        private Vector2 dragOffset;

        // ---- Click-to-cast control panel ----
        private bool showPanel;
        public IModStorage Storage { get; private set; } // ZeepSDK mod-scoped JSON store (Plugin.Instance.Storage)
        private bool timesIntent;             // single-select shows Times instead of Stats
        private readonly List<Sel> selected = new List<Sel>();
        private Vector2 panelScroll;
        private CursorLockMode prevLock;
        private bool prevCursorVisible;
        private bool cursorSaved;
        private float savedMouseSens = -1f;  // photomode MOUSE look sensitivity, zeroed while frozen
        private bool mouseFreezeWarned;      // logged the "can't reach settings" warning once

        // ---- Config (BepInEx; persists to BepInEx/config/com.aizpun.lobbyoverlay.cfg) ----
        // Both default OFF. Mouse-look freeze is opt-in (it can fight the free-cam); stay-in-photomode
        // only makes sense for a dedicated caster, never on for a racer.
        private ConfigEntry<bool> cfgDisableMouseLook; // freeze mouse-look while the panel is open
        private ConfigEntry<bool> cfgStayInPhotomode;  // auto re-enter photomode each round (server-gated)
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
            public RoundTime(int round, string time) { Round = round; Time = time; }
        }

        // Lazily-built IMGUI styles (GUI.skin is only valid inside OnGUI).
        private bool stylesReady;
        private GUIStyle boxStyle;
        private GUIStyle headerStyle;
        private GUIStyle labelStyle;
        private GUIStyle valueStyle;
        private GUIStyle nameLeftStyle;
        private GUIStyle nameRightStyle;
        private GUIStyle centerStyle;
        private GUIStyle valLeftStyle;
        private GUIStyle valRightStyle;
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
        private enum CastMode { Cup, Topout, Pursuit }
        private CastMode castMode = CastMode.Cup;
        private static readonly CastMode[] CAST_ORDER = { CastMode.Cup, CastMode.Topout, CastMode.Pursuit };
        private static string CastLabel(CastMode m)
        {
            if (m == CastMode.Topout) return "Topout";
            if (m == CastMode.Pursuit) return "Pursuit";
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
            cfgDisableMouseLook = Config.Bind("General", "Disable mouse look in photomode", false,
                "Freezes photomode mouse-look (sets the mouse sensitivity to 0) while the overlay " +
                "control panel is open, so you can click without swinging the camera. Controller look " +
                "is unaffected. Off by default.");
            cfgStayInPhotomode = Config.Bind("General", "Stay in photomode", false,
                "Auto re-enters photomode at the start of each round (for casters/spectators). Respects " +
                "the server's photomode rules and never forces it when a comp disables or gates " +
                "photomode. Off by default.");
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
                // Self-correct if we somehow load while already in photomode (events only fire on
                // the transition). Best-effort: efc may be null this early, which is fine.
                EnableFlyingCamera2 efc0 = FindEFC();
                if (efc0 != null && efc0.isPhotoMode) inPhotoMode = true;
            }
            catch (Exception ex) { Logger.LogError("Photomode/racing hooks failed: " + ex); }
            Logger.LogInfo(string.Format("Tournament Casting UI 1.0.0-beta.3 loaded. Local pool v{0}, {1} players (refreshing from repo).",
                poolVersion, pool.Count));
            Logger.LogInfo(string.Format("[config] Disable mouse look in photomode = {0}; Stay in photomode = {1}",
                cfgDisableMouseLook.Value, cfgStayInPhotomode.Value));
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

        private void OnCommand(string args)
        {
            args = (args ?? "").Trim();
            string lower = args.ToLowerInvariant();

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
            // Runtime toggles for the two BepInEx settings, so they work without a config-manager mod
            // (still backed by the .cfg, so the choice persists across restarts).
            if (lower.StartsWith("mouselock"))
            {
                bool? b = ParseOnOff(args.Substring("mouselock".Length));
                if (b.HasValue) { cfgDisableMouseLook.Value = b.Value; ChatApi.AddLocalMessage("Disable mouse look in photomode: " + (b.Value ? "on" : "off")); }
                else ChatApi.AddLocalMessage("Mouse-look freeze is " + (cfgDisableMouseLook.Value ? "on" : "off") + ". Usage: /overlay mouselock on|off");
                return;
            }
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
                ChatApi.AddLocalMessage("Control panel " + (showPanel ? "open (F4 to toggle)." : "closed."));
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
                else ChatApi.AddLocalMessage("Comp is " + CastLabel(castMode) + ". Options: cup, topout, pursuit");
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
            ChatApi.AddLocalMessage("Usage: /overlay panel (F4) | stats <name> | h2h <a> <b> | times <name> | pool <comp> | comp cup|topout|pursuit | cam on|off | mouselock on|off | staycam on|off | camstate | reset | resetpos | test | off");
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
                return;
            }

            Match m = Regex.Match(msg, @"Player (.+?): Time: (.+)");
            if (m.Success)
            {
                roundTimes[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
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
                    list.Add(new RoundTime(liveRound, kv.Value));
                }
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

            // F4 toggles the control panel (no typing needed mid-cast); F5 clears everything.
            try
            {
                if (Input.GetKeyDown(cfgKeyPanel != null ? cfgKeyPanel.Value : KeyCode.F4)) TogglePanel();
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
                    if (inPhotoMode) { PollCamera(); EnsureDrone(); }
                    TryEnterPhotomode(); // must run ungated: its whole job is to ENTER photomode
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
        // moving the mouse to click doesn't swing the camera. The controller has its OWN photomode
        // sensitivity (untouched), and translation/movement is never sensitivity-scaled, so you can
        // keep flying with the pad while the menu is up. (Camera look = LookAxis * sensitivity, where
        // the sensitivity is mouse-vs-controller by last-active device; touching the mouse selects
        // the zeroed mouse one.)
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
                }
                else if (savedMouseSens >= 0f)
                {
                    s.photo_mode_sensitivity = savedMouseSens;
                    Logger.LogInfo(string.Format("[mouselook] freeze OFF (restored {0})", savedMouseSens));
                    savedMouseSens = -1f;
                }
            }
            catch (Exception ex) { Logger.LogError("[mouselook] " + ex); }
        }

        // Reconcile the mouse-look freeze every frame. When the setting is on we freeze for the WHOLE
        // time we're in photomode (not just while the panel is open): the caster looks/flies with the
        // controller and uses the mouse only to click, so a frozen mouse never swings the camera.
        // FlyingCameraScript.LateUpdate reads photo_mode_sensitivity each frame (and runs after our
        // Update), so this per-frame set is what keeps the freeze solid across the game re-reading its
        // settings. FreezeMouseLook is idempotent (saves/restores once), so the restore branch also
        // handles leaving photomode or toggling the setting off.
        private void ReconcileMouseFreeze()
        {
            bool on = cfgDisableMouseLook != null && cfgDisableMouseLook.Value;
            // inPhotoMode is event-driven, so this per-frame reconcile no longer needs a FindEFC.
            FreezeMouseLook(on && inPhotoMode);
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
            try { GetFlyingCamera(); } catch { }
        }

        // Casting integrity (un-disableable): leaving photomode force-clears the overlay so a racer
        // can't keep another player's cam/stats on screen. Fired by ZeepSDK on every photomode exit.
        // Drop the in-photomode flag (and the stale camera ref) BEFORE clearing so the poll stops
        // touching the camera immediately.
        private void OnPhotoModeExited()
        {
            inPhotoMode = false;
            fcRef = null;
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
        }

        // A round started: arm the stay-in-photomode (re)entry. The poll does the actual entering once
        // the server permits it. Harmless when the setting is off (the poll early-outs).
        // Clear the live leaderboard at round start so Cup mode begins with everyone timeless (red)
        // until they actually post a time this round - the game may not push an empty board itself.
        private void OnRoundStarted() { stayEnterPending = true; board.Clear(); }
        private void OnRoundEnded() { stayEnterPending = false; }

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
                // Nothing to create and nothing to tear down -> skip the GetDrone reflection.
                // (Teardown still runs when the toggle flips off, because droneRef is still set.)
                if (!droneOn && droneRef == null) return;
                bool want = droneOn && mode == Mode.H2H && target2 != null &&
                            !string.IsNullOrEmpty(target2.SteamId);
                object drone = pdGetMI.Invoke(null, new object[] { DroneId });
                bool alive = drone != null && !((UnityEngine.Object)drone == null);
                if (!want)
                {
                    if (alive) pdDestroyMI.Invoke(null, new object[] { drone });
                    droneRef = null; droneSid = null;
                    return;
                }
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
                    // The Sc(40) gap hosts the "VS CAM" title bar drawn by DrawVsCamChrome.
                    Rect wantRect = new Rect(cardDrawRect.x,
                                             cardDrawRect.y + cardDrawRect.height + Sc(40f),
                                             cardDrawRect.width, cardDrawRect.height);
                    if (wantRect != droneAppliedRect && ApplyDroneRect(drone, wantRect))
                        droneAppliedRect = wantRect;
                }
            }
            catch (Exception e) { DroneLog("EnsureDrone: " + Unwrap(e)); }
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
        private string FastestInCup(string playerName)
        {
            string key = ResolveLiveName(playerName);
            if (key == null) return null;
            List<RoundTime> times;
            if (!playerRoundTimes.TryGetValue(key, out times) || times == null) return null;
            float best = -1f;
            for (int i = 0; i < times.Count; i++)
            {
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
            UpdateScale();
            EnsureStyles();
            HandleDrag();

            DrawCardForMode();

            // Broadcast chrome around the (UGUI) compare window: title bar + accent frame.
            if (mode == Mode.H2H && droneOn && droneSid != null && droneAppliedRect.width > 0f &&
                droneRef != null && !((UnityEngine.Object)droneRef == null))
                DrawVsCamChrome();

            if (showPanel)
            {
                // Hold the cursor free while the panel is open so buttons are clickable.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                DrawPanel();
            }

            // Always-visible mode bar (when there's anything to control) + drag grips while editing.
            if (showPanel || mode != Mode.None) DrawModeBar();
            if (showPanel) DrawGrips();
        }

        // Draws whatever card the current mode calls for, and records its on-screen rect in
        // cardDrawRect so the drag grip can sit in its bottom-right corner.
        private void DrawCardForMode()
        {
            if (mode == Mode.None) return;

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
                DrawH2H(cardRect.x, cardRect.y, cardRect.width, target1, target2);
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
        private void DrawModeBar()
        {
            float w = Sc(300f), h = Sc(72f);
            if (barRect.x < 0f) { barRect.x = Sc(24f); barRect.y = Screen.height - h - Sc(120f); } // bottom-left default
            barRect.width = w; barRect.height = h;

            bool rwActive = mode == Mode.RoundWins;
            bool timesActive = !rwActive && timesIntent;
            bool statsActive = !rwActive && !timesIntent;

            GUILayout.BeginArea(barRect, boxStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stats", statsActive ? buttonSelStyle : buttonStyle)) SetBarMode(false);
            if (GUILayout.Button("Times", timesActive ? buttonSelStyle : buttonStyle)) SetBarMode(true);
            if (GUILayout.Button("Round Wins", rwActive ? buttonSelStyle : buttonStyle))
            { mode = Mode.RoundWins; shownFollowSid = null; }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
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
            if (mode != Mode.None) GUI.Box(GripRect(cardDrawRect, hs), GUIContent.none, boxStyle);
            GUI.Box(GripRect(panelRect, hs), GUIContent.none, boxStyle);
            if (showPanel || mode != Mode.None) GUI.Box(GripRect(barRect, hs), GUIContent.none, boxStyle);
        }

        private Rect GripRect(Rect box, float hs)
        {
            return new Rect(box.xMax - hs - Sc(3f), box.yMax - hs - Sc(3f), hs, hs);
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
                if (mode != Mode.None && GripRect(cardDrawRect, hs).Contains(e.mousePosition))
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
            }
            else if (e.type == EventType.MouseDrag && draggingId >= 0)
            {
                if (draggingId == 0) { cardRect.x = e.mousePosition.x - dragOffset.x; cardRect.y = e.mousePosition.y - dragOffset.y; }
                else if (draggingId == 1) { panelRect.x = e.mousePosition.x - dragOffset.x; panelRect.y = e.mousePosition.y - dragOffset.y; }
                else { barRect.x = e.mousePosition.x - dragOffset.x; barRect.y = e.mousePosition.y - dragOffset.y; }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && draggingId >= 0)
            {
                draggingId = -1;
                SaveLayout();
                e.Use();
            }
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
            public LayoutData()
            {
                cardX = 24f; cardY = 130f;
                panelX = -1f; panelY = 130f;
                barX = -1f; barY = 0f;
                comp = "cotd"; cam = true; castmode = "cup";
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
            if (!string.IsNullOrEmpty(d.comp)) selectedComp = d.comp;
            camLink = d.cam;
            string cm = (d.castmode ?? "cup").ToLowerInvariant();
            castMode = cm == "topout" ? CastMode.Topout : (cm == "pursuit" ? CastMode.Pursuit : CastMode.Cup);
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
                d.comp = selectedComp; d.cam = camLink; d.castmode = CastLabel(castMode).ToLowerInvariant();
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

            // [Stats] = which comp's numbers the cards + H2H show. [Comp] = which cup format
            // orders the player list below. Left-click cycles forward, right-click back. Persisted.
            GUILayout.BeginHorizontal();
            int sd = CycleButton("Stats: " + CompLabel(selectedComp) + " ◂▸");
            if (sd != 0) CycleComp(sd);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            int md = CycleButton("Comp: " + CastLabel(castMode) + " ◂▸");
            if (md != 0) CycleCast(md);
            GUILayout.EndHorizontal();
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
            GUILayout.Space(Sc(4f));

            panelScroll = GUILayout.BeginScrollView(panelScroll);
            try
            {
                List<PRow> rows = BuildPanelRows();
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
            return BuildCupRows(list);
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
                    toOverrideTextFI = toGetOverrideMI.ReturnType.GetField("overridePositionText");
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

        private void DrawTimesCard(float x, float y, float w, string name)
        {
            List<RoundTime> times;
            playerRoundTimes.TryGetValue(name, out times);
            int rows = times != null ? times.Count : 0;
            float h = Sc(64f) + rows * Sc(28f) + (rows == 0 ? Sc(28f) : 0f);

            cardDrawRect = new Rect(x, y, w, h);
            GUILayout.BeginArea(new Rect(x, y, w, h), boxStyle);
            GUILayout.Label(name, pnameStyle); // player name: site default white (live name,
                                               // no pool lookup here)
            if (rows == 0)
            {
                GUILayout.Label("no times yet", labelStyle);
            }
            else
            {
                for (int i = times.Count - 1; i >= 0; i--) // newest round first
                {
                    string t = times[i].Time;
                    if (t != null) t = t.Replace(',', '.'); // logged with comma decimals
                    Row("Round " + times[i].Round, t);
                }
            }
            GUILayout.EndArea();
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
            string rankStr = s.Rank > 0 ? ("  #" + s.Rank) : "";
            if (s.Elo > 0) RowColored("Weighted ELO", F1(s.Elo) + rankStr, TierColor(s.Elo));
            else Row("Weighted ELO", "-");
            if (s.Peak > 0) RowColored("Peak ELO", F1(s.Peak), TierColor(s.Peak));
            else Row("Peak ELO", "-");
            // wins/podiums/cups/best from the selected comp.
            GUILayout.Label(CompLabel(selectedComp) + " record", centerStyle);
            CompStat c = CompFor(s, selectedComp);
            Row("Wins", c != null ? c.Wins.ToString() : "-");
            Row("Podiums", c != null ? c.Podiums.ToString() : "-");
            Row("Cups", c != null ? c.Cups.ToString() : "-");
            Row("Best finish", (c != null && c.Best > 0) ? ("#" + c.Best) : "-");
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
            string lapA = FastestInCup(a.Name);
            string lapB = FastestInCup(b.Name);
            string dispA = FmtLap(lapA);
            string dispB = FmtLap(lapB);
            float fa = ParseTime(lapA);
            float fb = ParseTime(lapB);
            if (fa >= 0 && fb >= 0 && fa != fb)
            {
                string gap = FmtGap(Math.Abs(fa - fb));
                if (fa > fb) dispA = dispA + "  " + gap; else dispB = dispB + "  " + gap;
            }
            CompRow(dispA, "fastest in cup", dispB, BetterTime(lapA, lapB));

            // mutual record (more wins better) from the chosen h2h source; center shows total shared
            int w1, w2;
            MutualRecord(a, b, selectedComp, out w1, out w2);
            CompRow(w1.ToString(), "mutual (" + (w1 + w2) + ", " + CompLabel(selectedComp) + ")", w2.ToString(),
                w1 > w2 ? 1 : (w2 > w1 ? 2 : 0));

            // "-" for players with no pool data (mirrors the Stats card)
            CompRow(a.Peak > 0 ? F1(a.Peak) : "-", "peak elo",
                    b.Peak > 0 ? F1(b.Peak) : "-", Better(a.Peak, b.Peak, true));
            CompRow(a.Elo > 0 ? F1(a.Elo) : "-", "current elo",
                    b.Elo > 0 ? F1(b.Elo) : "-", Better(a.Elo, b.Elo, true));
            // wins/podiums/pb from the selected comp
            CompStat ca = CompFor(a, selectedComp);
            CompStat cb = CompFor(b, selectedComp);
            int aw = ca != null ? ca.Wins : 0, bw = cb != null ? cb.Wins : 0;
            int ap = ca != null ? ca.Podiums : 0, bp = cb != null ? cb.Podiums : 0;
            int ab = ca != null ? ca.Best : 0, bb = cb != null ? cb.Best : 0;
            CompRow(aw.ToString(), CompLabel(selectedComp) + " wins", bw.ToString(), Better(aw, bw, true));
            CompRow(ap.ToString(), "podiums", bp.ToString(), Better(ap, bp, true));
            CompRow(Pb(ab), "pb", Pb(bb), BetterPb(ab, bb));

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

            valueStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) valueStyle.font = uiFont;
            valueStyle.fontSize = Sci(19);
            valueStyle.fontStyle = FontStyle.Bold;
            valueStyle.normal.textColor = Color.white;
            valueStyle.alignment = TextAnchor.MiddleRight;

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
            }
            catch { }
            try { FreezeMouseLook(false); } catch { } // restore mouse sensitivity if we zeroed it
            try { if (cursorSaved) { Cursor.lockState = prevLock; Cursor.visible = prevCursorVisible; } }
            catch { }
        }
    }
}
