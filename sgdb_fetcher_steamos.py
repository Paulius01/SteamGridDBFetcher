#!/usr/bin/env python3
"""
SteamGridDB Fetcher - SteamOS / Steam Deck edition.

Works on the Steam Deck and on standalone SteamOS installs (desktop mode),
and on any Linux with Steam. Uses only the Python 3 standard library that
ships with SteamOS - nothing to install. The UI runs in your browser on
127.0.0.1 (local machine only, not reachable from the network).

Safety (same guarantees as the Windows version):
  * Reads shortcuts.vdf READ-ONLY to find your non-Steam games.
  * Only ever writes image files into <Steam>/userdata/<you>/config/grid/ -
    the same files Steam's own "Change" artwork button creates.
  * Never modifies shortcuts.vdf, game files, the Steam client or any
    process; anti-cheat is never involved.
  * Replaced artwork is backed up to ./backups/<timestamp>/ first.

Usage (Desktop Mode on the Deck / SteamOS):
    python3 sgdb_fetcher_steamos.py            # opens the UI in your browser
    python3 sgdb_fetcher_steamos.py --auto     # no UI: fill missing artwork
    python3 sgdb_fetcher_steamos.py --user <steamid> --port 8787 --no-browser
"""

import argparse
import json
import os
import re
import shutil
import struct
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
import zlib
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
CONFIG_PATH = SCRIPT_DIR / "config_steamos.json"
BACKUP_DIR = SCRIPT_DIR / "backups"

SGDB_API = "https://www.steamgriddb.com/api/v2"
# Valve serves official art from two CDN layouts; newer titles often exist
# only on the second one.
CDN_BASES = (
    "https://cdn.cloudflare.steamstatic.com/steam/apps/",
    "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/",
)

# key, endpoint, query params, grid-file suffix, label
TYPES = [
    ("cover",      "grids",  {"dimensions": "600x900", "types": "static,animated"},         "p",     "Cover (600x900)"),
    ("wide",       "grids",  {"dimensions": "920x430,460x215", "types": "static,animated"}, "",      "Wide Cover (920x430)"),
    ("background", "heroes", {"types": "static,animated"},                                  "_hero", "Background (hero)"),
    ("logo",       "logos",  {"types": "static,animated"},                                  "_logo", "Logo"),
]
TYPE_BY_KEY = {t[0]: t for t in TYPES}

# Official Steam default files per type, best quality first (same files the
# SteamGridDB site's "Original Steam Assets" panel links to).
STEAM_FILES = {
    "cover":      ["library_600x900_2x.jpg", "library_600x900.jpg"],
    "wide":       ["header.jpg"],
    "background": ["library_hero_2x.jpg", "library_hero.jpg"],
    "logo":       ["logo_2x.png", "logo.png"],
}

IMAGE_EXTS = (".png", ".jpg", ".jpeg", ".webp")


# ---------------------------------------------------------------- config

def load_config():
    try:
        if CONFIG_PATH.exists():
            return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        pass
    return {}


def save_config(cfg):
    CONFIG_PATH.write_text(json.dumps(cfg, indent=2), encoding="utf-8")


# ------------------------------------------------ Steam (read-only side)

def steam_roots():
    """Candidate Steam install locations for SteamOS / Steam Deck / Linux."""
    home = Path.home()
    cands = [
        home / ".local/share/Steam",                                  # native (Deck + SteamOS default)
        home / ".steam/steam",                                        # classic symlink
        home / ".var/app/com.valvesoftware.Steam/data/Steam",         # flatpak Steam
        home / ".var/app/com.valvesoftware.Steam/.local/share/Steam", # flatpak (older layout)
    ]
    if os.name == "nt":  # allows testing the script on Windows too
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
                cands.insert(0, Path(winreg.QueryValueEx(k, "SteamPath")[0]))
        except OSError:
            pass
    return cands


def find_steam(cfg):
    p = cfg.get("steam_path")
    if p and (Path(p) / "userdata").exists():
        return Path(p)
    for c in steam_roots():
        try:
            if (c / "userdata").exists():
                return c.resolve()
        except OSError:
            continue
    sys.exit("Could not locate Steam. Set \"steam_path\" in " + str(CONFIG_PATH))


def parse_binary_vdf(data):
    pos = 0

    def read_cstring():
        nonlocal pos
        end = data.index(b"\x00", pos)
        s = data[pos:end].decode("utf-8", errors="replace")
        pos = end + 1
        return s

    def read_map():
        nonlocal pos
        obj = {}
        while True:
            t = data[pos]
            pos += 1
            if t == 0x08:
                return obj
            name = read_cstring()
            if t == 0x00:
                obj[name] = read_map()
            elif t == 0x01:
                obj[name] = read_cstring()
            elif t == 0x02:
                obj[name] = struct.unpack_from("<I", data, pos)[0]
                pos += 4
            elif t == 0x07:
                obj[name] = struct.unpack_from("<Q", data, pos)[0]
                pos += 8
            else:
                raise ValueError("unknown VDF field type 0x%02x" % t)

    if data[pos] != 0x00:
        raise ValueError("not a binary VDF file")
    pos += 1
    read_cstring()
    return read_map()


def shortcut_appid(e):
    appid = e.get("appid", 0)
    if appid:
        return appid & 0xFFFFFFFF
    exe = e.get("exe", "")
    name = e.get("appname", "")
    return (zlib.crc32((exe + name).encode("utf-8")) | 0x80000000) & 0xFFFFFFFF


def load_shortcuts(steam_path, user_override):
    userdata = steam_path / "userdata"
    candidates = []
    for d in sorted(userdata.iterdir()):
        vdf = d / "config" / "shortcuts.vdf"
        if not vdf.is_file():
            continue
        try:
            root = parse_binary_vdf(vdf.read_bytes())
        except (ValueError, IndexError):
            continue
        entries = []
        for _, raw in sorted(root.items(), key=lambda kv: int(kv[0]) if kv[0].isdigit() else 0):
            if not isinstance(raw, dict):
                continue
            e = {k.lower(): v for k, v in raw.items()}
            name = str(e.get("appname", "")).strip()
            if not name:
                continue
            entries.append({"appid": shortcut_appid(e), "name": name})
        candidates.append((d.name, entries))

    if not candidates:
        sys.exit("No non-Steam shortcuts found in any Steam profile.")
    if user_override:
        for uid, entries in candidates:
            if uid == str(user_override):
                return uid, entries
        sys.exit("Steam profile %s has no shortcuts.vdf. Profiles: %s"
                 % (user_override, ", ".join(u for u, _ in candidates)))
    return max(candidates, key=lambda c: len(c[1]))


# ------------------------------------------------------ SteamGridDB API

class Sgdb:
    def __init__(self, api_key):
        self.key = api_key
        self._cache = {}
        self._lock = threading.Lock()

    def _raw(self, path):
        url = SGDB_API + path
        with self._lock:
            if url in self._cache:
                return self._cache[url]
        req = urllib.request.Request(url, headers={
            "Authorization": "Bearer " + self.key,
            "User-Agent": "SteamGridDBFetcher/1.0",
        })
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                body = json.loads(resp.read().decode("utf-8"))
            data = body.get("data") if body.get("success") else None
        except urllib.error.HTTPError as e:
            if e.code == 404:
                data = None
            elif e.code == 401:
                raise RuntimeError("SteamGridDB rejected the API key (401).")
            else:
                raise
        with self._lock:
            self._cache[url] = data
        return data

    def _list(self, path):
        d = self._raw(path)
        return d if isinstance(d, list) else []

    def search(self, term):
        return [{"id": g["id"], "name": g["name"]}
                for g in self._list("/search/autocomplete/" + urllib.parse.quote(term, safe=""))]

    @staticmethod
    def alt_terms(name):
        """Smarter fallback terms: separators->spaces, CamelCase split,
        letter/digit split, stripped noise suffixes (Ver1, Steam, ...)."""
        alts = []

        def add(s):
            s = re.sub(r"\s+", " ", s).strip()
            if len(s) > 1 and s.lower() != name.lower() \
                    and s.lower() not in [a.lower() for a in alts]:
                alts.append(s)

        spaced = re.sub(r"[_\-.]+", " ", name)
        add(spaced)
        camel = re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", spaced)
        add(camel)
        digits = re.sub(r"(?<=[A-Za-z])(?=\d)", " ", camel)
        add(digits)
        noise = re.sub(r"\s+(steam|pc|en|eng|jp)$", "", digits, flags=re.I)
        noise = re.sub(r"\s+v(er)?\.?\s*\d+$", "", noise, flags=re.I)
        add(noise)
        return alts

    def search_smart(self, term):
        res = self.search(term)
        if res:
            return term, res
        for alt in self.alt_terms(term):
            res = self.search(alt)
            if res:
                return alt, res
        return term, []

    def assets(self, game_id, type_key):
        _, endpoint, params, _, _ = TYPE_BY_KEY[type_key]
        q = ("?" + urllib.parse.urlencode(params)) if params else ""
        out = []
        for a in self._list("/%s/game/%d%s" % (endpoint, game_id, q)):
            thumb = a.get("thumb") or a["url"]
            out.append({"url": a["url"], "thumb": thumb,
                        "animated": str(thumb).endswith(".webm")})
        return out

    def official_logos(self, game_id):
        try:
            return [{"url": a["url"], "thumb": a.get("thumb") or a["url"]}
                    for a in self._list("/logos/game/%d?styles=official" % game_id)]
        except Exception:
            return []

    def steam_appid(self, game_id):
        """The Steam appid SteamGridDB has linked to this game (powers the
        site's "Original Steam Assets" panel); 0 if none."""
        try:
            d = self._raw("/games/id/%d?platformdata=steam" % game_id)
            arr = (d or {}).get("external_platform_data", {}).get("steam", [])
            if arr:
                return int(arr[0]["id"])
        except Exception:
            pass
        return 0


def download(url):
    req = urllib.request.Request(url, headers={"User-Agent": "SteamGridDBFetcher/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return resp.read()


# ------------------------------------------------------ artwork writing

def grid_dir(steam_path, user_id):
    d = steam_path / "userdata" / str(user_id) / "config" / "grid"
    d.mkdir(parents=True, exist_ok=True)
    return d


def ext_from_url(url):
    ext = os.path.splitext(urllib.parse.urlparse(url).path)[1].lower()
    return ext if ext in IMAGE_EXTS else ".png"


def existing_art(gdir, appid, type_key):
    suffix = TYPE_BY_KEY[type_key][3]
    for ext in IMAGE_EXTS:
        p = gdir / ("%d%s%s" % (appid, suffix, ext))
        if p.exists():
            return p
    return None


def apply_asset(gdir, appid, type_key, url, stamp):
    suffix = TYPE_BY_KEY[type_key][3]
    data = download(url)
    target = gdir / ("%d%s%s" % (appid, suffix, ext_from_url(url)))
    bdir = BACKUP_DIR / stamp
    for ext in IMAGE_EXTS:
        old = gdir / ("%d%s%s" % (appid, suffix, ext))
        if old.exists():
            bdir.mkdir(parents=True, exist_ok=True)
            bak = bdir / old.name
            if not bak.exists():
                shutil.copy2(old, bak)
            old.unlink()
    target.write_bytes(data)
    return target.name


def apply_official(gdir, appid, type_key, steam_id, stamp):
    """Apply the official Steam default (best quality variant that exists)."""
    for f in STEAM_FILES[type_key]:
        for cdn in CDN_BASES:
            try:
                return apply_asset(gdir, appid, type_key, cdn + "%d/%s" % (steam_id, f), stamp)
            except Exception:
                continue
    return None


def fill_missing(client, gdir, sc, stamp, log):
    """Fill ONLY empty slots: official Steam defaults first, SteamGridDB top
    results as fallback. Never replaces existing artwork.
    Returns 'complete', 'updated' or 'none'."""
    missing = [k for k, _, _, _, _ in TYPES if not existing_art(gdir, sc["appid"], k)]
    if not missing:
        return "complete"
    term, results = client.search_smart(sc["name"])
    if not results:
        log("  no SteamGridDB match")
        return "none"
    gid = results[0]["id"]
    wrote = False

    steam_id = client.steam_appid(gid)
    if steam_id:
        for k in list(missing):
            fname = apply_official(gdir, sc["appid"], k, steam_id, stamp)
            if fname:
                log("  %s: %s (Steam default)" % (k, fname))
                missing.remove(k)
                wrote = True

    for k in missing:
        try:
            assets = client.official_logos(gid) if k == "logo" else []
            if not assets:
                assets = client.assets(gid, k)
            if assets:
                fname = apply_asset(gdir, sc["appid"], k, assets[0]["url"], stamp)
                log("  %s: %s" % (k, fname))
                wrote = True
            else:
                log("  %s: nothing available" % k)
        except Exception as e:
            log("  %s: FAILED (%s)" % (k, e))
        time.sleep(0.1)
    return "updated" if wrote else "none"


# ---------------------------------------------------------- app context

class App:
    def __init__(self):
        self.cfg = load_config()
        self.client = None
        key = os.environ.get("SGDB_API_KEY") or self.cfg.get("api_key")
        if key:
            self.client = Sgdb(key.strip())
        self.steam_path = None
        self.user_id = None
        self.shortcuts = []
        self.gdir = None
        self.stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        self.lock = threading.Lock()
        self.progress = {"running": False, "text": "", "done": False,
                         "updated": 0, "complete": 0, "none": 0, "applied": []}

    def set_key(self, key):
        self.cfg["api_key"] = key.strip()
        save_config(self.cfg)
        self.client = Sgdb(key.strip())


APP = App()


# ---------------------------------------------------------------- web UI

PAGE = r"""<!DOCTYPE html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>SteamGridDB Fetcher</title>
<style>
  :root { --bg:#171a21; --panel:#1f2430; --panel2:#2a3140; --field:#0e1117;
          --tx:#dbe2ec; --dim:#8f98a0; --acc:#66c0f4; --ok:#7bc94e; --warn:#d9a04a; --err:#e06c6c; }
  * { box-sizing:border-box; scrollbar-width:none; }
  *::-webkit-scrollbar { display:none; }
  body { margin:0; background:var(--bg); color:var(--tx);
         font:14px/1.4 "Segoe UI","Noto Sans",sans-serif; }
  header { background:var(--panel); padding:12px 20px; display:flex; align-items:center; gap:16px;
           position:sticky; top:0; z-index:5; }
  header h1 { font-size:17px; color:var(--acc); margin:0; }
  header .sub { color:var(--dim); font-size:12px; }
  header .spacer { flex:1; }
  button { background:linear-gradient(90deg,#47bfff,#1a9fff); border:0; color:#04121f;
           padding:9px 18px; border-radius:4px; cursor:pointer; font-weight:700; font-size:13px; }
  button.gray { background:var(--panel2); color:var(--tx); }
  button:disabled { opacity:.4; cursor:default; }
  input[type=text] { background:var(--field); border:1px solid #3a4356; color:var(--tx);
                     padding:9px 12px; border-radius:4px; font-size:14px; }
  select { background:var(--field); color:var(--tx); border:1px solid #3a4356;
           padding:8px; border-radius:4px; }
  #grid { display:flex; flex-wrap:wrap; gap:14px; padding:16px 20px 70px; }
  .tile { width:160px; cursor:pointer; }
  .tile .art { width:160px; height:240px; border-radius:4px; overflow:hidden; background:var(--panel);
               display:flex; align-items:center; justify-content:center; text-align:center; }
  .tile .art img { width:100%; height:100%; object-fit:cover; display:block; }
  .tile .ph { width:100%; height:100%; display:flex; align-items:center; justify-content:center;
              padding:10px; background:linear-gradient(150deg,#4d5b6d,#20262f); color:#c7d0da; }
  .tile:hover .art { outline:2px solid var(--acc); }
  .tile .nm { margin-top:6px; font-size:13px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .tile .st { font-size:11px; color:var(--warn); min-height:14px; }
  .tile .st.ok { color:var(--ok); }
  .tile.done .nm { color:var(--ok); }
  #picker { display:none; padding:16px 20px 70px; }
  #picker .bar { display:flex; gap:10px; align-items:center; flex-wrap:wrap; margin-bottom:10px; }
  #picker h2 { color:var(--acc); margin:0 0 12px; }
  #search { flex:1; min-width:220px; }
  h3 { margin:22px 0 2px; font-size:15px; }
  .cnt { color:var(--dim); font-size:11px; margin-bottom:6px; }
  .row { display:flex; flex-wrap:wrap; gap:10px; }
  .pick { border:3px solid var(--panel); border-radius:5px; background:var(--panel);
          cursor:pointer; position:relative; overflow:hidden; }
  .pick.sel { border-color:var(--acc); box-shadow:0 0 10px #66c0f466; }
  .pick img, .pick video { display:block; background:var(--field); object-fit:cover; }
  .pick .cap { position:absolute; left:0; right:0; bottom:0; background:#0e1117d9; font-size:10px;
               text-align:center; padding:2px 0; }
  .cap.cur { color:var(--acc); } .cap.off { color:var(--ok); } .cap.none { color:var(--warn); }
  .pick .empty { display:flex; align-items:center; justify-content:center; color:var(--dim);
                 background:var(--field); font-size:12px; }
  .c-cover img, .c-cover video, .c-cover .empty { width:160px; height:240px; }
  .c-wide img, .c-wide video, .c-wide .empty { width:320px; height:150px; }
  .c-background img, .c-background video, .c-background .empty { width:360px; height:116px; }
  .c-logo img, .c-logo video, .c-logo .empty { width:220px; height:110px; object-fit:contain; }
  #status { position:fixed; left:0; right:0; bottom:0; background:var(--field); color:var(--dim);
            padding:9px 20px; font-size:12px; border-top:1px solid #000a; }
  #status.ok { color:var(--ok); } #status.err { color:var(--err); }
  #keyform { padding:40px 20px; max-width:640px; }
</style></head><body>
<header>
  <h1>SteamGridDB Fetcher</h1><div class="sub" id="profile"></div>
  <div class="spacer"></div>
  <button class="gray" id="autoall">Auto-apply ALL games</button>
</header>
<div id="keyform" style="display:none">
  <p>Paste your free SteamGridDB API key (steamgriddb.com &rarr; Profile &rarr; Preferences &rarr; API):</p>
  <input type="text" id="keyinput" size="42"> <button onclick="saveKey()">Save</button>
</div>
<div id="grid"></div>
<div id="picker">
  <button class="gray" onclick="backToLib()">&lt; Library</button>
  <h2 id="pname" style="margin-top:12px"></h2>
  <div class="bar">
    <input type="text" id="search">
    <button onclick="doSearch()">Search</button>
    <select id="matches" onchange="loadAssets(+this.value)"></select>
  </div>
  <div class="bar">
    <button id="applyBtn" onclick="applyPicks()" disabled>Apply selected</button>
    <button class="gray" onclick="autoOne()">Auto (top picks)</button>
    <span class="sub" id="selinfo"></span>
  </div>
  <div id="sections"></div>
</div>
<div id="status">Loading...</div>
<script>
const TYPES = [["cover","Cover (600x900)"],["wide","Wide Cover (920x430)"],
               ["background","Background (hero)"],["logo","Logo"]];
let GAMES = [], cur = null, sgdbId = 0, steamId = 0, picks = {}, gen = 0;

const $ = s => document.querySelector(s);
const esc = s => s.replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const status = (m, cls) => { const el = $('#status'); el.textContent = m; el.className = cls || ''; };
async function api(p, opts) {
  const r = await fetch(p, opts);
  if (!r.ok) throw new Error(await r.text());
  return r.json();
}

async function init() {
  const d = await api('/api/games');
  if (d.need_key) { $('#keyform').style.display = 'block'; status('API key needed.'); return; }
  $('#keyform').style.display = 'none';
  GAMES = d.games;
  $('#profile').textContent = 'Profile ' + d.user + ' - ' + GAMES.length + ' games - ' +
    GAMES.filter(g => !g.has.cover).length + ' without cover art';
  renderLib();
  status('Ready. Click a game to pick its artwork.');
}

async function saveKey() {
  const k = $('#keyinput').value.trim();
  if (!k) return;
  await api('/api/key', {method:'POST', headers:{'Content-Type':'application/json'},
                         body: JSON.stringify({key:k})});
  init();
}

function renderLib() {
  $('#grid').innerHTML = GAMES.map((g,i) => `
    <div class="tile ${g.applied?'done':''}" onclick="openGame(${i})">
      <div class="art">${g.has.cover
        ? `<img src="/grid/cover/${g.appid}?t=${g.bust||0}">`
        : `<div class="ph">${esc(g.name)}</div>`}</div>
      <div class="nm">${esc(g.name)}</div>
      <div class="st ${g.applied?'ok':''}">${g.applied?'updated':(g.has.cover?'':'missing artwork')}</div>
    </div>`).join('');
}

function backToLib() {
  gen++;
  $('#picker').style.display = 'none';
  $('#grid').style.display = 'flex';
  renderLib();
  status('Ready. Click a game to pick its artwork.');
}

function openGame(i) {
  cur = GAMES[i];
  $('#grid').style.display = 'none';
  $('#picker').style.display = 'block';
  $('#pname').textContent = cur.name;
  $('#search').value = cur.name;
  $('#search').onkeydown = e => { if (e.key === 'Enter') doSearch(); };
  doSearch();
}

async function doSearch() {
  const term = $('#search').value.trim();
  if (!term) return;
  gen++; const g = gen;
  picks = {}; sgdbId = 0; steamId = 0; updateApply();
  $('#sections').innerHTML = '<p class="sub">Searching SteamGridDB...</p>';
  status('Searching "' + term + '"...');
  let d;
  try { d = await api('/api/search?q=' + encodeURIComponent(term)); }
  catch (e) { status('Search failed: ' + e.message, 'err'); return; }
  if (g !== gen) return;
  if (d.term !== term) $('#search').value = d.term;
  const sel = $('#matches');
  sel.innerHTML = d.results.map(r => `<option value="${r.id}">${esc(r.name)}</option>`).join('');
  if (!d.results.length) {
    $('#sections').innerHTML = '<p class="sub">No SteamGridDB match. Try a different term.</p>';
    status('No results.', 'err'); return;
  }
  status(d.results.length + ' match(es).', 'ok');
  loadAssets(d.results[0].id);
}

async function loadAssets(gameId) {
  gen++; const g = gen;
  sgdbId = gameId; picks = {}; updateApply();
  let html = '';
  for (const [k, label] of TYPES) {
    html += `<h3>${label}</h3><div class="cnt" id="cnt-${k}">loading...</div><div class="row" id="row-${k}"></div>`;
  }
  $('#sections').innerHTML = html;

  // current + official tiles
  let ex = {};
  try { ex = await api('/api/existing?appid=' + cur.appid); } catch (e) {}
  try { steamId = (await api('/api/steamid?game=' + gameId)).steamid; } catch (e) { steamId = 0; }
  if (g !== gen) return;
  for (const [k] of TYPES) {
    const row = $('#row-' + k);
    const has = ex[k];
    row.insertAdjacentHTML('beforeend', `
      <div class="pick c-${k} sel" data-t="${k}" data-v="" onclick="choose(this)">
        ${has ? `<img src="/grid/${k}/${cur.appid}?t=${Date.now()}">`
              : `<div class="empty">keep<br>empty</div>`}
        <div class="cap ${has?'cur':'none'}">${has?'current':'none'}</div></div>`);
    if (steamId) {
      row.insertAdjacentHTML('beforeend', `
        <div class="pick c-${k}" data-t="${k}" data-v="official:${steamId}" onclick="choose(this)"
             id="off-${k}">
          <img src="${officialUrl(k, steamId, 0)}"
               onerror="offErr(this, '${k}', ${steamId})">
          <div class="cap off">Steam default</div></div>`);
    }
  }

  status('Loading assets...');
  for (const [k, label] of TYPES) {
    let assets = [];
    try { assets = await api(`/api/assets?game=${gameId}&type=${k}`); } catch (e) {}
    if (g !== gen) return;
    $('#cnt-' + k).textContent = assets.length ? assets.length + ' found - click to pick' : 'none available on SteamGridDB';
    const row = $('#row-' + k);
    for (const a of assets) {
      const media = a.animated
        ? `<video autoplay loop muted playsinline src="${esc(a.thumb)}"></video>`
        : `<img loading="lazy" src="${esc(a.thumb)}">`;
      row.insertAdjacentHTML('beforeend', `
        <div class="pick c-${k}" data-t="${k}" data-v="${esc(a.url)}" onclick="choose(this)">${media}</div>`);
    }
    // default: keep existing if present, else preselect the top result
    if (!(await api('/api/existing?appid=' + cur.appid))[k] && assets.length) {
      const first = row.querySelector(`.pick[data-v]:not([data-v=""]):not([data-v^="official"])`);
      if (first) choose(first);
    }
  }
  if (g === gen) status('Assets loaded. Click thumbnails to change picks, then Apply.', 'ok');
}

const CDN_BASES = ['https://cdn.cloudflare.steamstatic.com/steam/apps/',
                   'https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/'];
function officialUrl(k, sid, i) {
  const f = {cover:'library_600x900.jpg', wide:'header.jpg',
             background:'library_hero.jpg', logo:'logo.png'}[k];
  return CDN_BASES[i] + sid + '/' + f;
}
function offErr(img, k, sid) {
  if (!img.dataset.alt) { img.dataset.alt = '1'; img.src = officialUrl(k, sid, 1); }
  else { const p = img.closest('.pick'); if (p) p.remove(); }
}

function choose(el) {
  const t = el.dataset.t;
  document.querySelectorAll(`.pick[data-t="${t}"]`).forEach(p => p.classList.remove('sel'));
  el.classList.add('sel');
  if (el.dataset.v) picks[t] = el.dataset.v; else delete picks[t];
  updateApply();
}

function updateApply() {
  const n = Object.keys(picks).length;
  $('#applyBtn').disabled = n === 0;
  $('#selinfo').textContent = n ? n + ' change(s) selected' : '';
}

async function applyPicks() {
  status('Applying to ' + cur.name + '...');
  try {
    const r = await api('/api/apply', {method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({appid: cur.appid, picks})});
    cur.applied = true; cur.has.cover = true; cur.bust = Date.now();
    status('Applied: ' + r.written.join(', ') + '  (press F5 / restart Steam to see it)', 'ok');
  } catch (e) { status('Apply failed: ' + e.message, 'err'); }
}

async function autoOne() {
  if (!sgdbId) return;
  status('Auto-applying top picks...');
  try {
    const r = await api('/api/auto', {method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({appid: cur.appid, game: sgdbId})});
    cur.applied = true; cur.has.cover = true; cur.bust = Date.now();
    status('Applied: ' + r.written.join(', ') + '  (press F5 / restart Steam to see it)', 'ok');
  } catch (e) { status('Auto failed: ' + e.message, 'err'); }
}

$('#autoall').onclick = async () => {
  if (!confirm('Fill in missing artwork for all ' + GAMES.length + ' games?\n\n' +
      'Empty slots get the official Steam default art (SteamGridDB top result if the ' +
      'game is not on Steam). Artwork you already have is never touched.')) return;
  $('#autoall').disabled = true;
  try { await api('/api/autoall', {method:'POST'}); } catch (e) {}
  const timer = setInterval(async () => {
    let p;
    try { p = await api('/api/progress'); } catch (e) { return; }
    status(p.text, p.done ? 'ok' : '');
    if (p.done) {
      clearInterval(timer);
      $('#autoall').disabled = false;
      GAMES.forEach(g => { if (p.applied.includes(g.appid)) { g.applied = true; g.has.cover = true; g.bust = Date.now(); } });
      if ($('#picker').style.display !== 'block') renderLib();
    }
  }, 800);
};

init();
</script></body></html>
"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, body, ctype="application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj).encode("utf-8"))

    def _err(self, msg, code=500):
        self._send(code, str(msg).encode("utf-8"), "text/plain")

    def do_GET(self):
        try:
            url = urllib.parse.urlparse(self.path)
            q = urllib.parse.parse_qs(url.query)
            if url.path == "/":
                return self._send(200, PAGE.encode("utf-8"), "text/html; charset=utf-8")
            if url.path == "/api/games":
                if APP.client is None:
                    return self._json({"need_key": True})
                games = []
                for sc in APP.shortcuts:
                    games.append({
                        "appid": sc["appid"], "name": sc["name"], "applied": False,
                        "has": {k: existing_art(APP.gdir, sc["appid"], k) is not None
                                for k, _, _, _, _ in TYPES},
                    })
                return self._json({"user": APP.user_id, "games": games})
            if url.path == "/api/search":
                term, results = APP.client.search_smart(q["q"][0])
                return self._json({"term": term, "results": results})
            if url.path == "/api/assets":
                return self._json(APP.client.assets(int(q["game"][0]), q["type"][0]))
            if url.path == "/api/steamid":
                return self._json({"steamid": APP.client.steam_appid(int(q["game"][0]))})
            if url.path == "/api/existing":
                appid = int(q["appid"][0])
                return self._json({k: existing_art(APP.gdir, appid, k) is not None
                                   for k, _, _, _, _ in TYPES})
            if url.path == "/api/progress":
                with APP.lock:
                    return self._json(dict(APP.progress))
            m = re.match(r"^/grid/(cover|wide|background|logo)/(\d+)$", url.path)
            if m:
                p = existing_art(APP.gdir, int(m.group(2)), m.group(1))
                if not p:
                    return self._err("not found", 404)
                ctype = {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg",
                         "webp": "image/webp"}.get(p.suffix.lstrip("."), "application/octet-stream")
                return self._send(200, p.read_bytes(), ctype)
            self._err("not found", 404)
        except Exception as e:
            self._err("%s: %s" % (type(e).__name__, e))

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", 0))
            body = json.loads(self.rfile.read(length) or b"{}")
            if self.path == "/api/key":
                APP.set_key(body["key"])
                return self._json({"ok": True})
            if self.path == "/api/apply":
                appid = int(body["appid"])
                written = []
                for k, v in body.get("picks", {}).items():
                    if k not in TYPE_BY_KEY or not v:
                        continue
                    if v.startswith("official:"):
                        fname = apply_official(APP.gdir, appid, k, int(v.split(":")[1]), APP.stamp)
                        if fname:
                            written.append(k + " (Steam default)")
                    else:
                        written.append(apply_asset(APP.gdir, appid, k, v, APP.stamp))
                return self._json({"written": written})
            if self.path == "/api/auto":
                appid = int(body["appid"])
                gid = int(body["game"])
                written = []
                for k, _, _, _, _ in TYPES:
                    assets = APP.client.assets(gid, k)
                    if assets:
                        written.append(apply_asset(APP.gdir, appid, k, assets[0]["url"], APP.stamp))
                return self._json({"written": written})
            if self.path == "/api/autoall":
                with APP.lock:
                    if APP.progress["running"]:
                        return self._json({"ok": False})
                    APP.progress = {"running": True, "text": "Starting...", "done": False,
                                    "updated": 0, "complete": 0, "none": 0, "applied": []}
                threading.Thread(target=auto_all_worker, daemon=True).start()
                return self._json({"ok": True})
            self._err("not found", 404)
        except Exception as e:
            self._err("%s: %s" % (type(e).__name__, e))


def auto_all_worker():
    updated = complete = none = 0
    applied = []
    total = len(APP.shortcuts)
    for i, sc in enumerate(APP.shortcuts, 1):
        with APP.lock:
            APP.progress["text"] = "[%d/%d] %s..." % (i, total, sc["name"])
        try:
            res = fill_missing(APP.client, APP.gdir, sc, APP.stamp, lambda m: None)
        except Exception:
            res = "none"
        if res == "updated":
            updated += 1
            applied.append(sc["appid"])
        elif res == "complete":
            complete += 1
        else:
            none += 1
    with APP.lock:
        APP.progress.update({
            "running": False, "done": True, "updated": updated, "complete": complete,
            "none": none, "applied": applied,
            "text": "Done: %d filled in, %d already complete, %d with nothing found. "
                    "Restart Steam (or press F5 in the library) to see the artwork."
                    % (updated, complete, none)})


# ------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser(description="Apply SteamGridDB artwork to non-Steam games (SteamOS edition).")
    ap.add_argument("--auto", action="store_true",
                    help="no UI: fill missing artwork for all games and exit")
    ap.add_argument("--user", help="Steam profile id (folder under userdata)")
    ap.add_argument("--port", type=int, default=8787)
    ap.add_argument("--no-browser", action="store_true", help="don't open the browser automatically")
    args = ap.parse_args()

    APP.steam_path = find_steam(APP.cfg)
    APP.user_id, APP.shortcuts = load_shortcuts(APP.steam_path, args.user or APP.cfg.get("user_id"))
    APP.gdir = grid_dir(APP.steam_path, APP.user_id)

    print("Steam:   %s" % APP.steam_path)
    print("Profile: %s - %d non-Steam game(s)" % (APP.user_id, len(APP.shortcuts)))

    if args.auto:
        if APP.client is None:
            print("\nGet a free API key: https://www.steamgriddb.com/profile/preferences/api")
            APP.set_key(input("Paste your API key: ").strip())
        print("Backups: %s\n" % (BACKUP_DIR / APP.stamp))
        updated = complete = none = 0
        for sc in APP.shortcuts:
            print("* " + sc["name"])
            res = fill_missing(APP.client, APP.gdir, sc, APP.stamp, lambda m: print(m))
            if res == "complete":
                print("  already complete")
                complete += 1
            elif res == "updated":
                updated += 1
            else:
                none += 1
        print("\nDone: %d filled in, %d already complete, %d with nothing found."
              % (updated, complete, none))
        print("Restart Steam to see the artwork.")
        return

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    url = "http://127.0.0.1:%d/" % args.port
    print("\nUI: %s  (local machine only)" % url)
    print("Press Ctrl+C to quit.")
    if not args.no_browser:
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
