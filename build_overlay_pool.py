"""Build overlay_pool.json for the Lobby Overlay mod (multi-comp).

Keyed by steam_id. Per comp: wins / best finish / podiums / cups + per-event finish
positions (for head-to-head). ELO/peak/rank are COTD-weighted only (the fixed skill
benchmark). Cross-comp is NOT stored here -- the mod aggregates it from the per-comp data.

Each comp is read from its OWN native ranking output (matches the public sites, includes
troll/roulette cups). Cross-comp's allcompdata.json is deliberately NOT used (it drops
troll cups, so its counts differ from the sites).
"""
import json
import os
import re
import datetime

BASE = r"C:\Users\rafa\Desktop\Claude"
COTD_DIR = os.path.join(BASE, "zeepkist cotd elo")
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(SCRIPT_DIR, "overlay_pool.json")

# Petite display-name -> canonical (copied from petite cup stats/petite_ranking.py).
PETITE_ALIASES = {
    'Pandamane': 'PandaMane', 'Pandamane ': 'PandaMane', 'JakeAdjecent': 'JakeAdjacent',
    'JakeAdjecent ': 'JakeAdjacent', 'Radsabsrad': 'RadAbsRad', 'Streben': 'Sterben',
    'Mega Knight (Sterben)': 'Sterben', 'Bowler (Sterben)': 'Sterben', 'AndeMe17': 'AndMe',
    'Mackcheesy': 'MackCheesy', 'An Actual G00se': 'An Actual g00se', 'AndMe93': 'brrryy',
    'JustMaki': 'justMaki', 'QuickRacer10': 'Quickracer10', 'Redal': 'redal',
    '[GECK]R0nanC': 'R0nanC', '[CCC]Shinikage221': 'Shinikage221', '[Fae]Kyn': 'Kyn',
    'Brrry': 'brrryy', 'Gilgool': 'gilgool', 'BB_Benji': 'BB_Benji', 'Ping': 'ping',
    'RoundNZT': 'RoundNzt', 'Clowney': 'Clowny', 'Brrryy': 'brrryy', 'Magical': 'Magical',
    'Redstoney': 'Redstony', 'r-tube': 'rtyyyyb', 'rtube': 'rtyyyyb', 'Mu': 'Mμ',
    'Lkat': 'LKat', "Jake Replacement's Replacement": 'LKat', 'null/plexus': 'null/plexus',
    'Zoman': 'ZOMAN', 'redal': 'redal', 'A2 Zecklord': 'A2 Zecklord', 'OLR94': 'SGR',
    'ShyGirlyRaccoon': 'SGR',
}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


# ---- COTD custom name colours (cup winners; scraped from the site's customColors map so
# newly granted colours flow into the mod on every pool rebuild) ----
def load_custom_colors():
    try:
        with open(os.path.join(COTD_DIR, "index.html"), encoding="utf-8") as f:
            html = f.read()
        m = re.search(r"const customColors = \{(.*?)\};", html, re.S)
        if not m:
            print("  customColors block not found in index.html")
            return {}
        return dict(re.findall(r'"([^"]+)"\s*:\s*"(#[0-9a-fA-F]{3,8})"', m.group(1)))
    except Exception as e:
        print("  customColors skipped:", e)
        return {}


def strip_tag(s):
    return re.sub(r'^\[.*?\]\s*', '', s or '').strip()


# ---- name -> steam_id resolver, from COTD players.json (+ aliases) ----
_players = load(os.path.join(COTD_DIR, "players.json"))
_name_sid = {}
for canon, info in _players.items():
    sid = info.get("steam_id")
    if not sid:
        continue
    for variant in [canon, strip_tag(canon)] + list(info.get("aliases", [])):
        if variant:
            _name_sid[variant.lower()] = sid
            _name_sid[strip_tag(variant).lower()] = sid


def resolve(name, alias_map=None):
    if not name:
        return None
    cands = [name]
    if alias_map and name in alias_map:
        cands.append(alias_map[name])
    for c in list(cands):
        cands.append(strip_tag(c))
    for c in cands:
        if not c:
            continue
        if c.lower() in _name_sid:
            return _name_sid[c.lower()]
    return None


NAMES = {}          # sid -> display name (COTD canonical wins)
ELO = {}            # sid -> {elo, peak, rank}
POOL = {}           # sid -> {comp -> {wins,best,podiums,cups,hist}}


def best_from_hist(hist):
    vals = [p for p in hist.values() if p]
    return min(vals) if vals else 0


def derive(hist):
    """wins/best/podiums/cups straight from per-event finish positions (ground truth)."""
    wins = sum(1 for v in hist.values() if v == 1)
    pods = sum(1 for v in hist.values() if v and v <= 3)
    return wins, best_from_hist(hist), pods, len(hist)


def add_comp(comp, sid, hist, name=None, stats=None):
    if not sid:
        return
    if stats is None:
        stats = derive(hist)            # position-based comps: derive (native fields unreliable)
    w, b, p, c = stats
    POOL.setdefault(sid, {})[comp] = {
        "wins": w, "best": b, "podiums": p, "cups": c, "hist": hist,
    }
    if name:
        NAMES.setdefault(sid, name)


# ---- COTD qualification (mirrors index.html isQualified, weighted/ELO mode) ----
# Qualified if: 6+ cups, OR any lifetime podium (g+s+z>0), OR 4+ cups with at least one
# in the last 20 events. Rank is the position among QUALIFIED players only (by active
# rating) -- the site never ranks one-cup players, so total position is far too high.
def _last20_cups(weighted):
    cs = set()
    for p in weighted:
        for h in p.get("h", []):
            if h.get("c") is not None:
                cs.add(h["c"])
    return set(sorted(cs, reverse=True)[:20])


def _is_qualified(p, last20):
    cups = p.get("c", 0)
    podiums = p.get("g", 0) + p.get("s", 0) + p.get("z", 0)
    if cups >= 6:
        return True
    if podiums > 0:
        return True
    return cups >= 4 and any(h.get("c") in last20 for h in p.get("h", []))


# ---- COTD (authoritative; also supplies ELO/peak/rank) ----
def load_cotd():
    weighted = load(os.path.join(COTD_DIR, "alldata.json"))["weighted"]
    last20 = _last20_cups(weighted)
    qualified = [p for p in weighted if _is_qualified(p, last20)]
    qualified.sort(key=lambda p: p.get("a", 0), reverse=True)
    rank_of = {p["n"]: i + 1 for i, p in enumerate(qualified)}  # unqualified -> absent -> rank 0
    n = 0
    for p in weighted:
        sid = resolve(p["n"])
        if not sid:
            continue
        hist = {str(h["c"]): h["p"] for h in p.get("h", []) if h.get("p") is not None}
        add_comp("cotd", sid, hist, p["n"], stats=(
            p.get("w", 0), p.get("b", 0), p.get("g", 0) + p.get("s", 0) + p.get("z", 0), p.get("c", 0)))
        ELO[sid] = {"elo": round(p.get("a", 0), 1), "peak": round(p.get("p", 0), 1),
                    "rank": rank_of.get(p["n"], 0)}
        NAMES[sid] = p["n"]  # COTD name always wins
        n += 1
    return n


# ---- Eggy (own steam_ids.json, COTD convention) ----
def load_eggy():
    try:
        eggy_sids = load(os.path.join(BASE, "eggy cup", "steam_ids.json"))
        data = load(os.path.join(BASE, "eggy cup", "alldata.json"))
    except Exception as e:
        print("  eggy skipped:", e); return 0
    arr = data.get("weighted") or data.get("glicko") or []
    n = 0
    for p in arr:
        nm = p["n"]
        sid = eggy_sids.get(nm) or eggy_sids.get(strip_tag(nm)) or resolve(nm)
        if not sid:
            continue
        hist = {str(h["c"]): h["p"] for h in p.get("h", []) if h.get("p") is not None}
        add_comp("eggy", sid, hist, nm, stats=(
            p.get("w", 0), p.get("b", 0), p.get("g", 0) + p.get("s", 0) + p.get("z", 0), p.get("c", 0)))
        n += 1
    return n


# ---- Petite (PCDJ trailing; derive from per-round history) ----
def load_petite():
    try:
        data = load(os.path.join(BASE, "petite cup stats", "petite_rankings.json"))
    except Exception as e:
        print("  petite skipped:", e); return 0
    rankings = data.get("PCDJ Ranking", {}).get("rankings", [])
    n = 0
    for p in rankings:
        sid = resolve(p["name"], PETITE_ALIASES)
        if not sid:
            continue
        hist = {h["r"]: h["pos"] for h in p.get("history", []) if h.get("pos") is not None}
        add_comp("pcdj", sid, hist, p["name"])  # derive
        n += 1
    return n


# ---- Qube (steam_id keyed) ----
def load_qube():
    try:
        data = load(os.path.join(BASE, "qube", "qube.json"))
    except Exception as e:
        print("  qube skipped:", e); return 0
    n = 0
    for p in data.get("players", []):
        sid = p.get("key")
        if not sid:
            continue
        hist = {str(h["e"]): h["p"] for h in p.get("history", []) if h.get("p") is not None}
        add_comp("qube", sid, hist, p.get("name"))  # derive
        n += 1
    return n


# ---- TyO (steam_id keyed) ----
def load_tyo():
    try:
        data = load(os.path.join(BASE, "TyO", "tyo.json"))
    except Exception as e:
        print("  tyo skipped:", e); return 0
    players = data.get("players", data) if isinstance(data, dict) else data
    n = 0
    for p in players:
        sid = p.get("steamid") or p.get("steamId")
        if not sid:
            continue
        hist = {str(h["event"]): h["placement"] for h in p.get("history", [])
                if h.get("placement") is not None}
        add_comp("tyo", sid, hist, p.get("name"))  # derive (native cups_won/podiums are tag-format)
        n += 1
    return n


# ---- Kerki (name keyed; coarse history via result codes) ----
def load_kerki():
    try:
        data = load(os.path.join(BASE, "kerki", "kerki.json"))
    except Exception as e:
        print("  kerki skipped:", e); return 0
    codes = {"w1": 1, "w2": 2, "w3": 3, "w4": 4, "w5": 5, "f": 6}
    n = 0
    for p in data.get("players", []):
        sid = resolve(p["name"])
        if not sid:
            continue
        hist = {}
        for h in p.get("history", []):
            pos = codes.get(h.get("result"))
            if pos:
                hist[str(h["k"])] = pos
        pods = p.get("w1", 0) + p.get("w2", 0) + p.get("w3", 0)
        add_comp("kerki", sid, hist, p["name"],
                 stats=(p.get("wins", 0), p.get("best", 0), pods, p.get("apps", 0)))
        n += 1
    return n


# ---- ZSL (steam_id keyed; history via raw round-results join on userId) ----
def load_zsl():
    try:
        hy = load(os.path.join(BASE, "zeepkist zsl analysis", "hybrid.json"))
        raw = load(os.path.join(BASE, "zeepkist zsl analysis", "raw", "zsl_round_results.json"))
    except Exception as e:
        print("  zsl skipped:", e); return 0
    players = hy.get("players", hy) if isinstance(hy, dict) else hy
    uid_sid = {p["userId"]: p["steamId"] for p in players if p.get("steamId")}
    hist_by_sid = {}
    for r in raw:
        sid = uid_sid.get(r.get("userId"))
        if sid and r.get("position") is not None:
            hist_by_sid.setdefault(sid, {})[str(r["roundId"])] = r["position"]
    n = 0
    for p in players:
        sid = p.get("steamId")
        if not sid:
            continue
        pods = sum(p.get("pods", []) or [])
        add_comp("zsl", sid, hist_by_sid.get(sid, {}), p.get("steamName"),
                 stats=(p.get("wins", 0), p.get("best", 0), pods, p.get("gp", 0)))
        n += 1
    return n


def main():
    counts = {}
    counts["cotd"] = load_cotd()
    counts["pcdj"] = load_petite()
    counts["eggy"] = load_eggy()
    counts["qube"] = load_qube()
    counts["tyo"] = load_tyo()
    counts["kerki"] = load_kerki()
    counts["zsl"] = load_zsl()

    colors = load_custom_colors()
    colored = 0
    players = {}
    for sid, comps in POOL.items():
        rec = {"name": NAMES.get(sid, "?")}
        e = ELO.get(sid)
        rec["elo"] = e["elo"] if e else 0
        rec["peak"] = e["peak"] if e else 0
        rec["rank"] = e["rank"] if e else 0
        col = colors.get(rec["name"])
        if col:
            rec["col"] = col
            colored += 1
        rec["comps"] = comps
        players[sid] = rec
    print("  custom colours matched:", colored, "of", len(colors))

    out = {
        "version": datetime.date.today().isoformat(),
        "comps": ["cotd", "pcdj", "eggy", "qube", "tyo", "kerki", "zsl"],
        "count": len(players),
        "players_by_steam_id": players,
    }
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))

    print("overlay_pool.json written:", OUT)
    print("  players resolved per comp:", counts)
    print("  unique players:", len(players))
    mk = players.get("76561198050792757")
    if mk:
        print("  justMaki ELO=%s peak=%s rank=%s" % (mk["elo"], mk["peak"], mk["rank"]))
        for c, s in sorted(mk["comps"].items()):
            print("    %-6s wins=%d best=%d podiums=%d cups=%d (hist=%d)"
                  % (c, s["wins"], s["best"], s["podiums"], s["cups"], len(s["hist"])))


if __name__ == "__main__":
    main()
