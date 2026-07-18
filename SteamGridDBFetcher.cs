// SteamGridDB Fetcher - native Windows app (WinForms, .NET Framework 4.8)
//
// Applies SteamGridDB artwork (Cover, Wide Cover, Background, Logo) to the
// non-Steam games (and optionally Steam games) in your Steam library.
//
// UI model:
//   * Library: poster grid with scope segments (All / Missing art / Non-Steam
//     / Steam), search, per-game slot meters, batch "Fill missing art".
//   * Game workspace: left rail (identity, slot checklist, staged changes,
//     Apply/Undo) + asset browser with type/tag filters, Steam defaults,
//     community assets and animated previews. Picks are STAGED, then applied.
//
// Safety:
//   * Reads shortcuts.vdf / appmanifests READ-ONLY.
//   * Only ever writes image files into <Steam>\userdata\<you>\config\grid\ -
//     the same files Steam's own "Change" artwork button creates.
//   * Replaced artwork is backed up to .\backups\<timestamp>\ first; the last
//     apply can be undone from inside the app.
//
// Build (uses the C# compiler that ships with Windows):  build.bat

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamGridDBFetcher
{
    // ------------------------------------------------------------------ model

    class Shortcut
    {
        public uint AppId;
        public string Name;
        public bool IsSteam;   // installed Steam-store game (not a shortcut)
    }

    class SgdbGame
    {
        public int Id;
        public string Name;
        public int Year;      // release year, 0 when unknown

        public string Display
        {
            get { return Year > 0 ? Name + " (" + Year + ")" : Name; }
        }
    }

    class SgdbAsset
    {
        public string Url;
        public string Thumb;
        public string Mime;
        public bool Animated;
        public bool Nsfw, Humor, Epilepsy;
    }

    class AssetPage
    {
        public List<SgdbAsset> Assets = new List<SgdbAsset>();
        public int Total;
    }

    class AType
    {
        public string Key, Endpoint, Query, Suffix, Label, Short;
        public int W, H;   // thumbnail box
    }

    static class Cfg
    {
        public static readonly AType[] Types = new AType[]
        {
            // nsfw/humor/epilepsy=any: fetch everything, filtering is client-side
            new AType { Key = "cover",      Endpoint = "grids",  Query = "?dimensions=600x900&types=static,animated&nsfw=any&humor=any&epilepsy=any",         Suffix = "p",     Label = "Cover (600x900)",      Short = "Cover",      W = 220, H = 330 },
            new AType { Key = "wide",       Endpoint = "grids",  Query = "?dimensions=920x430,460x215&types=static,animated&nsfw=any&humor=any&epilepsy=any", Suffix = "",      Label = "Wide Cover (920x430)", Short = "Wide cover", W = 430, H = 201 },
            new AType { Key = "background", Endpoint = "heroes", Query = "?types=static,animated&nsfw=any&humor=any&epilepsy=any",                            Suffix = "_hero", Label = "Background (hero)",    Short = "Background", W = 480, H = 155 },
            new AType { Key = "logo",       Endpoint = "logos",  Query = "?types=static,animated&nsfw=any&humor=any&epilepsy=any",                            Suffix = "_logo", Label = "Logo",                 Short = "Logo",       W = 300, H = 150 },
        };

        public static readonly string[] ImageExts = new string[] { ".png", ".jpg", ".jpeg", ".webp" };

        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string ConfigPath { get { return Path.Combine(ExeDir, "config.json"); } }
        public static string BackupRoot { get { return Path.Combine(ExeDir, "backups"); } }

        public static Dictionary<string, object> Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var js = new JavaScriptSerializer();
                    var obj = js.DeserializeObject(File.ReadAllText(ConfigPath)) as Dictionary<string, object>;
                    if (obj != null) return obj;
                }
            }
            catch (Exception) { }
            return new Dictionary<string, object>();
        }

        public static void Save(Dictionary<string, object> cfg)
        {
            var js = new JavaScriptSerializer();
            File.WriteAllText(ConfigPath, js.Serialize(cfg));
        }

        public static string Str(Dictionary<string, object> cfg, string key)
        {
            object v;
            if (cfg.TryGetValue(key, out v) && v is string) return (string)v;
            return null;
        }

        public static int Int(Dictionary<string, object> cfg, string key, int def)
        {
            object v;
            if (cfg.TryGetValue(key, out v))
            {
                try { return Convert.ToInt32(v); }
                catch (Exception) { }
            }
            return def;
        }
    }

    // ------------------------------------------------- Steam (read-only side)

    static class Steam
    {
        public static string FindPath(Dictionary<string, object> cfg)
        {
            string p = Cfg.Str(cfg, "steam_path");
            if (p != null && Directory.Exists(p)) return p;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        string v = k.GetValue("SteamPath") as string;
                        if (v != null && Directory.Exists(v)) return v;
                    }
                }
            }
            catch (Exception) { }
            if (Directory.Exists(@"C:\Program Files (x86)\Steam")) return @"C:\Program Files (x86)\Steam";
            if (Directory.Exists(@"C:\Program Files\Steam")) return @"C:\Program Files\Steam";
            throw new Exception("Could not locate Steam. Set \"steam_path\" in config.json.");
        }

        // Minimal parser for Steam's binary VDF format (shortcuts.vdf).
        static string ReadCString(byte[] d, ref int pos)
        {
            int end = Array.IndexOf(d, (byte)0, pos);
            if (end < 0) throw new Exception("corrupt vdf");
            string s = Encoding.UTF8.GetString(d, pos, end - pos);
            pos = end + 1;
            return s;
        }

        static Dictionary<string, object> ReadMap(byte[] d, ref int pos)
        {
            var m = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                byte t = d[pos++];
                if (t == 0x08) return m;
                string name = ReadCString(d, ref pos);
                if (t == 0x00) m[name] = ReadMap(d, ref pos);
                else if (t == 0x01) m[name] = ReadCString(d, ref pos);
                else if (t == 0x02) { m[name] = BitConverter.ToUInt32(d, pos); pos += 4; }
                else if (t == 0x07) { m[name] = BitConverter.ToUInt64(d, pos); pos += 8; }
                else throw new Exception("Unknown VDF field type 0x" + t.ToString("x2"));
            }
        }

        public static Dictionary<string, object> ParseVdf(byte[] d)
        {
            int pos = 0;
            if (d.Length < 2 || d[pos++] != 0x00) throw new Exception("Not a binary VDF file");
            ReadCString(d, ref pos);
            return ReadMap(d, ref pos);
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static uint ShortcutAppId(Dictionary<string, object> e)
        {
            object v;
            if (e.TryGetValue("appid", out v) && v is uint && (uint)v != 0) return (uint)v;
            string exe = e.TryGetValue("exe", out v) && v is string ? (string)v : "";
            string name = e.TryGetValue("appname", out v) && v is string ? (string)v : "";
            return Crc32(Encoding.UTF8.GetBytes(exe + name)) | 0x80000000u;
        }

        public static List<Shortcut> LoadShortcuts(string steamPath, string userOverride, out string userId)
        {
            var candidates = new List<KeyValuePair<string, List<Shortcut>>>();
            string userdata = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdata))
            {
                foreach (string dir in Directory.GetDirectories(userdata))
                {
                    string vdf = Path.Combine(dir, "config", "shortcuts.vdf");
                    if (!File.Exists(vdf)) continue;
                    Dictionary<string, object> root;
                    try { root = ParseVdf(File.ReadAllBytes(vdf)); }
                    catch (Exception) { continue; }

                    var list = new List<Shortcut>();
                    foreach (var kv in root.OrderBy(SortKey))
                    {
                        var e = kv.Value as Dictionary<string, object>;
                        if (e == null) continue;
                        object nv;
                        string name = e.TryGetValue("appname", out nv) && nv is string ? ((string)nv).Trim() : "";
                        if (name.Length == 0) continue;
                        list.Add(new Shortcut { AppId = ShortcutAppId(e), Name = name });
                    }
                    candidates.Add(new KeyValuePair<string, List<Shortcut>>(Path.GetFileName(dir), list));
                }
            }

            if (candidates.Count == 0)
                throw new Exception("No non-Steam shortcuts found in any Steam profile.");

            if (!string.IsNullOrEmpty(userOverride))
            {
                foreach (var c in candidates)
                    if (c.Key == userOverride) { userId = c.Key; return c.Value; }
                throw new Exception("Steam profile " + userOverride + " has no shortcuts.vdf.");
            }

            var best = candidates.OrderByDescending(c => c.Value.Count).First();
            userId = best.Key;
            return best.Value;
        }

        static int SortKey(KeyValuePair<string, object> kv)
        {
            int n;
            return int.TryParse(kv.Key, out n) ? n : 0;
        }

        // Installed Steam-store games, from appmanifest_*.acf across all
        // Steam library folders. Read-only.
        public static List<Shortcut> LoadSteamGames(string steamPath)
        {
            var games = new List<Shortcut>();
            var seen = new HashSet<uint>();
            var libs = new List<string> { steamPath };
            try
            {
                string lf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(lf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(lf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception) { }

            foreach (string lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string sa = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(sa)) continue;
                string[] acfs;
                try { acfs = Directory.GetFiles(sa, "appmanifest_*.acf"); }
                catch (Exception) { continue; }
                foreach (string acf in acfs)
                {
                    try
                    {
                        string txt = File.ReadAllText(acf);
                        Match ma = Regex.Match(txt, "\"appid\"\\s+\"(\\d+)\"");
                        Match mn = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"");
                        if (!ma.Success || !mn.Success) continue;
                        uint id = uint.Parse(ma.Groups[1].Value);
                        string name = mn.Groups[1].Value;
                        if (id == 228980 || name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase))
                            continue;   // runtime/redist entries, not games
                        if (seen.Add(id))
                            games.Add(new Shortcut { AppId = id, Name = name, IsSteam = true });
                    }
                    catch (Exception) { }
                }
            }
            games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return games;
        }
    }

    // ------------------------------------------------------- SteamGridDB API

    class Sgdb
    {
        const string Base = "https://www.steamgriddb.com/api/v2";
        readonly string key;
        readonly Dictionary<string, object> cache = new Dictionary<string, object>();
        readonly object cacheLock = new object();
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Sgdb(string apiKey) { key = apiKey; }

        // Returns the whole (successful) response body as a dictionary,
        // null on 404 / failure. Cached per URL.
        async Task<Dictionary<string, object>> GetJson(string url)
        {
            lock (cacheLock)
            {
                object hit;
                if (cache.TryGetValue(url, out hit)) return (Dictionary<string, object>)hit;
            }
            string body;
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers["Authorization"] = "Bearer " + key;
                    wc.Headers["User-Agent"] = "SteamGridDBFetcher/1.0";
                    wc.Encoding = Encoding.UTF8;
                    body = await wc.DownloadStringTaskAsync(url);
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && (int)resp.StatusCode == 404) body = null;
                else if (resp != null && (int)resp.StatusCode == 401)
                    throw new Exception("SteamGridDB rejected the API key (401). Fix it in config.json.");
                else throw;
            }
            Dictionary<string, object> root = null;
            if (body != null)
            {
                root = Js.DeserializeObject(body) as Dictionary<string, object>;
                object ok;
                if (root != null && root.TryGetValue("success", out ok) && ok is bool && !(bool)ok)
                    root = null;
            }
            lock (cacheLock) { cache[url] = root; }
            return root;
        }

        // The "data" value of a response: object[] for lists, Dictionary for
        // single objects, null on 404.
        async Task<object> GetRaw(string url)
        {
            var root = await GetJson(url);
            object d;
            return root != null && root.TryGetValue("data", out d) ? d : null;
        }

        async Task<object[]> GetData(string url)
        {
            return (await GetRaw(url)) as object[] ?? new object[0];
        }

        // The Steam store appid SteamGridDB has linked to this game (this is
        // what powers the site's "Original Steam Assets" panel); 0 if none.
        public async Task<int> SteamAppId(int gameId)
        {
            try
            {
                var d = await GetRaw(Base + "/games/id/" + gameId + "?platformdata=steam")
                        as Dictionary<string, object>;
                if (d == null) return 0;
                object epd;
                if (!d.TryGetValue("external_platform_data", out epd)) return 0;
                var ep = epd as Dictionary<string, object>;
                object steamArr;
                if (ep == null || !ep.TryGetValue("steam", out steamArr)) return 0;
                var arr = steamArr as object[];
                if (arr == null || arr.Length == 0) return 0;
                var first = arr[0] as Dictionary<string, object>;
                object idv;
                if (first != null && first.TryGetValue("id", out idv))
                    return Convert.ToInt32(idv);
            }
            catch (Exception) { }
            return 0;
        }

        static bool Flag(Dictionary<string, object> a, string key)
        {
            object v;
            return a.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static List<SgdbAsset> ParseAssets(object[] data)
        {
            var list = new List<SgdbAsset>();
            foreach (object o in data)
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                object url, thumb, mime;
                if (!a.TryGetValue("url", out url)) continue;
                a.TryGetValue("thumb", out thumb);
                a.TryGetValue("mime", out mime);
                string thumbStr = thumb != null ? Convert.ToString(thumb) : Convert.ToString(url);
                string mimeStr = mime != null ? Convert.ToString(mime) : null;
                list.Add(new SgdbAsset
                {
                    Url = Convert.ToString(url),
                    Thumb = thumbStr,
                    Mime = mimeStr,
                    // SGDB gives animated assets a .webm video as "thumb"
                    Animated = thumbStr.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                        || (mimeStr != null &&
                            (mimeStr.IndexOf("apng", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             mimeStr.IndexOf("gif", StringComparison.OrdinalIgnoreCase) >= 0)),
                    Nsfw = Flag(a, "nsfw"),
                    Humor = Flag(a, "humor"),
                    Epilepsy = Flag(a, "epilepsy")
                });
            }
            return list;
        }

        // Alternative search terms for shortcut names that don't match as-is:
        // separators to spaces, CamelCase split, letter/digit split, and
        // stripped noise suffixes ("Ver1", "Steam", ...).
        public static List<string> AltTerms(string name)
        {
            var alts = new List<string>();
            Action<string> add = delegate(string s)
            {
                s = Regex.Replace(s, @"\s+", " ").Trim();
                if (s.Length > 1 &&
                    !string.Equals(s, name, StringComparison.OrdinalIgnoreCase) &&
                    !alts.Contains(s, StringComparer.OrdinalIgnoreCase))
                    alts.Add(s);
            };
            string spaced = Regex.Replace(name, @"[_\-.]+", " ");
            add(spaced);
            string camel = Regex.Replace(spaced, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            add(camel);
            string digits = Regex.Replace(camel, @"(?<=[A-Za-z])(?=\d)", " ");
            add(digits);
            string noise = Regex.Replace(digits, @"\s+(steam|pc|en|eng|jp)$", "", RegexOptions.IgnoreCase);
            noise = Regex.Replace(noise, @"\s+v(er)?\.?\s*\d+$", "", RegexOptions.IgnoreCase);
            add(noise);
            return alts;
        }

        // Search, retrying with smarter variants of the term when nothing is
        // found. Returns the term that worked together with its results.
        public async Task<KeyValuePair<string, List<SgdbGame>>> SearchSmart(string term)
        {
            var res = await Search(term);
            if (res.Count > 0) return new KeyValuePair<string, List<SgdbGame>>(term, res);
            foreach (string alt in AltTerms(term))
            {
                res = await Search(alt);
                if (res.Count > 0) return new KeyValuePair<string, List<SgdbGame>>(alt, res);
            }
            return new KeyValuePair<string, List<SgdbGame>>(term, new List<SgdbGame>());
        }

        public async Task<List<SgdbGame>> Search(string term)
        {
            var data = await GetData(Base + "/search/autocomplete/" + Uri.EscapeDataString(term));
            var list = new List<SgdbGame>();
            foreach (object o in data)
            {
                var g = o as Dictionary<string, object>;
                if (g == null) continue;
                object id, name, rd;
                if (g.TryGetValue("id", out id) && g.TryGetValue("name", out name))
                {
                    var sg = new SgdbGame { Id = Convert.ToInt32(id), Name = Convert.ToString(name) };
                    if (g.TryGetValue("release_date", out rd) && rd != null)
                    {
                        try
                        {
                            long secs = Convert.ToInt64(rd);
                            if (secs > 0)
                                sg.Year = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                          .AddSeconds(secs).Year;
                        }
                        catch { }
                    }
                    list.Add(sg);
                }
            }
            return list;
        }

        // One page (50) of assets plus the server's total count.
        public async Task<AssetPage> AssetsPaged(int gameId, AType t, int page)
        {
            var root = await GetJson(Base + "/" + t.Endpoint + "/game/" + gameId + t.Query
                                     + "&page=" + page);
            var res = new AssetPage();
            if (root != null)
            {
                object d, tot;
                if (root.TryGetValue("data", out d) && d is object[])
                    res.Assets = ParseAssets((object[])d);
                res.Total = root.TryGetValue("total", out tot) ? Convert.ToInt32(tot) : res.Assets.Count;
            }
            return res;
        }

        public async Task<List<SgdbAsset>> Assets(int gameId, AType t)
        {
            return (await AssetsPaged(gameId, t, 0)).Assets;
        }

        // Official (Steam-mirrored) logos; the API only supports styles=official
        // for logos and icons, not for grids or heroes.
        public async Task<List<SgdbAsset>> OfficialLogos(int gameId)
        {
            try
            {
                return ParseAssets(await GetData(
                    Base + "/logos/game/" + gameId + "?styles=official&nsfw=any&humor=any"));
            }
            catch (Exception) { return new List<SgdbAsset>(); }
        }

        public static async Task<byte[]> Download(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "SteamGridDBFetcher/1.0";
                return await wc.DownloadDataTaskAsync(url);
            }
        }
    }

    // ---------------------------------------- official Steam default artwork
    // The steam appid comes from SteamGridDB's platform data (the same source
    // the site's "Original Steam Assets" panel uses); these are the files it
    // links to.

    static class SteamStore
    {
        // Valve serves official art from two CDN layouts; newer titles often
        // exist only on the second one.
        public static readonly string[] CdnBases = new string[]
        {
            "https://cdn.cloudflare.steamstatic.com/steam/apps/",
            "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/",
        };

        // The default artwork files Steam itself uses for every store game.
        static readonly Dictionary<string, string[]> Files = new Dictionary<string, string[]>
        {
            { "cover",      new string[] { "library_600x900_2x.jpg", "library_600x900.jpg" } },
            { "wide",       new string[] { "header.jpg" } },
            { "background", new string[] { "library_hero_2x.jpg", "library_hero.jpg" } },
            { "logo",       new string[] { "logo_2x.png", "logo.png" } },
        };

        // Preview candidates (always-present non-2x variant, on each CDN).
        public static List<string> PreviewUrls(int steamId, string key)
        {
            string[] f = Files[key];
            string file = f[f.Length - 1];
            var list = new List<string>();
            foreach (string cdn in CdnBases) list.Add(cdn + steamId + "/" + file);
            return list;
        }

        // Apply the official Steam default for one asset type. Null if unavailable.
        public static async Task<ApplyResult> Apply(string gdir, uint appid, AType t, int steamId, string stamp)
        {
            foreach (string f in Files[t.Key])
                foreach (string cdn in CdnBases)
                {
                    try { return await Artwork.Apply(gdir, appid, t, cdn + steamId + "/" + f, stamp); }
                    catch (Exception) { }
                }
            return null;
        }
    }

    // ------------------------------------------------------- artwork writing

    class ApplyResult
    {
        public string Name;                                       // written file name
        public string NewPath;                                    // full path written
        public List<string> BackupPaths = new List<string>();     // replaced files, backed up
    }

    static class Artwork
    {
        public static string GridDir(string steamPath, string userId)
        {
            string d = Path.Combine(steamPath, "userdata", userId, "config", "grid");
            Directory.CreateDirectory(d);
            return d;
        }

        static string ExtFromUrl(string url)
        {
            try
            {
                string ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
                if (Cfg.ImageExts.Contains(ext)) return ext;
            }
            catch (Exception) { }
            return ".png";
        }

        // Download one asset and write it as the correct grid file.
        // Any replaced files are backed up to backups\<stamp>\ first.
        public static async Task<ApplyResult> Apply(string gdir, uint appid, AType t, string url, string stamp)
        {
            byte[] data = await Sgdb.Download(url);
            string target = Path.Combine(gdir, appid + t.Suffix + ExtFromUrl(url));
            var res = new ApplyResult { NewPath = target, Name = Path.GetFileName(target) };

            string bdir = Path.Combine(Cfg.BackupRoot, stamp);
            foreach (string ext in Cfg.ImageExts)
            {
                string old = Path.Combine(gdir, appid + t.Suffix + ext);
                if (File.Exists(old))
                {
                    Directory.CreateDirectory(bdir);
                    string bak = Path.Combine(bdir, Path.GetFileName(old));
                    if (!File.Exists(bak)) File.Copy(old, bak);
                    res.BackupPaths.Add(bak);
                    File.Delete(old);
                }
            }
            File.WriteAllBytes(target, data);
            return res;
        }
    }

    // --------------------------------------------------------------- main UI

    // Scrollable containers with the scrollbars hidden; mouse-wheel scrolling
    // still works through the WheelRedirector message filter. WS_EX_COMPOSITED
    // makes Windows paint the container and all child tiles into one buffer
    // per frame, eliminating tearing/flicker while scrolling.
    class BareFlowPanel : FlowLayoutPanel
    {
        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        public BareFlowPanel()
        {
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (IsHandleCreated &&
                (m.Msg == 0x05 || m.Msg == 0x0F || m.Msg == 0x83 || m.Msg == 0x85 ||
                 m.Msg == 0x114 || m.Msg == 0x115 || m.Msg == 0x20A))
                ShowScrollBar(Handle, 3, false);
            base.WndProc(ref m);
        }
    }

    class BarePanel : Panel
    {
        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        public BarePanel()
        {
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (IsHandleCreated &&
                (m.Msg == 0x05 || m.Msg == 0x0F || m.Msg == 0x83 || m.Msg == 0x85 ||
                 m.Msg == 0x114 || m.Msg == 0x115 || m.Msg == 0x20A))
                ShowScrollBar(Handle, 3, false);
            base.WndProc(ref m);
        }
    }

    // One owner-drawn control per library card (cover + name + slot meter +
    // status) instead of five child controls - far fewer native windows, so
    // creation and composited scrolling are much faster.
    class GameCard : Control
    {
        public Shortcut Game;
        public Image Cover;          // owned by MainForm caches, not by us
        public Image Placeholder;
        public string StatusText = "";
        public Color StatusColor;
        public Color NameColor;
        public bool[] Slots;         // null = all filled (Steam defaults)
        public Color BgNormal, BgHover, MeterOn, MeterOff;
        bool hover;

        static readonly Font NameFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        static readonly Font StatusFont = new Font("Segoe UI", 7.6f);

        // Vertical space needed below the top pad + cover for the name row and
        // the meter/status row, derived from the fonts so text never clips.
        public static int ExtraH
        {
            get { return 6 + NameFont.Height + 2 + 6 + Math.Max(6, StatusFont.Height - 2) + 10; }
        }

        public GameCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(hover ? BgHover : BgNormal);
            int pad = 4;
            int cw = Width - pad * 2;
            int ch = cw * 3 / 2;
            Image img = Cover != null ? Cover : Placeholder;
            if (img != null)
            {
                try { g.DrawImage(img, pad, pad, cw, ch); }
                catch (Exception) { }
            }
            int y = pad + ch + 6;
            int nameH = NameFont.Height + 2;
            TextRenderer.DrawText(g, Game != null ? Game.Name : "", NameFont,
                new Rectangle(pad, y, cw, nameH), NameColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int my = y + nameH + 6;
            for (int i = 0; i < 4; i++)
            {
                bool on = Slots == null || (i < Slots.Length && Slots[i]);
                using (var br = new SolidBrush(on ? MeterOn : MeterOff))
                    g.FillRectangle(br, pad + i * 23, my + 1, 19, 4);
            }
            TextRenderer.DrawText(g, StatusText, StatusFont,
                new Rectangle(pad + 96, my - 5, cw - 96, StatusFont.Height + 3), StatusColor,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }
    }

    class Pick
    {
        public string Value;        // asset url, or "official:<steamid>"
        public string SourceLabel;  // "Steam default" / "community" / ...
        public Panel Tile;
    }

    class MainForm : Form
    {
        // ---- palette (prototype "modern dark" tokens)
        static readonly Color BG0 = ColorTranslator.FromHtml("#0b0d12");
        static readonly Color BG1 = ColorTranslator.FromHtml("#10131b");
        static readonly Color BG2 = ColorTranslator.FromHtml("#161a25");
        static readonly Color BG3 = ColorTranslator.FromHtml("#1d2230");
        static readonly Color FIELD = ColorTranslator.FromHtml("#0b0d12");
        static readonly Color TX = ColorTranslator.FromHtml("#e8ecf4");
        static readonly Color DIM = ColorTranslator.FromHtml("#8b93a5");
        static readonly Color ACC = ColorTranslator.FromHtml("#4da3ff");
        static readonly Color OKC = ColorTranslator.FromHtml("#3fb950");
        static readonly Color WARN = ColorTranslator.FromHtml("#d4a24e");
        static readonly Color ERRC = ColorTranslator.FromHtml("#e5534b");
        static readonly Color NSFWB = ColorTranslator.FromHtml("#c0392f");   // adult-content tile border
        static readonly Color METER_OFF = ColorTranslator.FromHtml("#2a3040");

        const int CoverW = 220, CoverH = 330;

        Dictionary<string, object> cfg;
        Sgdb api;
        string steamPath, userId, gridDir, stamp;
        List<Shortcut> shortcuts;
        List<Shortcut> steamGames = new List<Shortcut>();

        IEnumerable<Shortcut> AllGames
        {
            get { return steamGames.Count > 0 ? shortcuts.Concat(steamGames) : (IEnumerable<Shortcut>)shortcuts; }
        }

        // per-game slot state (cover/wide/background/logo custom files present)
        readonly Dictionary<uint, bool[]> slotState = new Dictionary<uint, bool[]>();
        readonly HashSet<uint> applied = new HashSet<uint>();

        // ---- library view
        Panel libraryView;
        FlowLayoutPanel libraryFlow;
        Panel scopeSeg;
        readonly List<Button> scopeButtons = new List<Button>();
        readonly string[] scopeKeys = new string[] { "all", "missing", "nonsteam", "steam" };
        readonly string[] scopeLabels = new string[] { "All", "Missing art", "Non-Steam", "Steam" };
        string scope = "all";
        TextBox libSearch;
        Button refreshBtn, fillBtn;
        Panel batchStrip;
        Label batchLabel, batchTxt;
        Panel batchBarOuter, batchBarInner;
        System.Windows.Forms.Timer stripHideTimer;
        readonly Dictionary<uint, GameCard> gameTiles = new Dictionary<uint, GameCard>();
        readonly Dictionary<uint, Image> coverImages = new Dictionary<uint, Image>();
        readonly Dictionary<uint, Image> placeholders = new Dictionary<uint, Image>();
        readonly Dictionary<uint, Image> steamCoverCache = new Dictionary<uint, Image>();
        int libGen;

        // ---- detail view
        Panel detailView;
        Button backBtn, applyBtn, autoFillBtn;
        PictureBox heroPb;
        Image heroOwned;                 // hero image loaded from disk (we own it)
        Label dName, stagedLabel, railStatus;
        LinkLabel undoLink;
        ComboBox matchCombo;
        TextBox dSearch;
        readonly Dictionary<string, Label> chkDot = new Dictionary<string, Label>();
        readonly Dictionary<string, Label> chkStatus = new Dictionary<string, Label>();
        Panel contentPanel;
        FlowLayoutPanel sectionsFlow;
        CheckBox cbStatic, cbAnimated, cbHumor, cbAdult, cbEpilepsy, cbUntagged;
        bool suppressFilter;

        // picker state
        readonly Dictionary<string, FlowLayoutPanel> flows = new Dictionary<string, FlowLayoutPanel>();
        readonly Dictionary<string, Label> countLabels = new Dictionary<string, Label>();
        readonly Dictionary<string, int> pageByType = new Dictionary<string, int>();
        readonly Dictionary<string, int> totalByType = new Dictionary<string, int>();
        readonly Dictionary<string, int> shownByType = new Dictionary<string, int>();
        readonly Dictionary<string, List<Panel>> tiles = new Dictionary<string, List<Panel>>();
        readonly Dictionary<Panel, SgdbAsset> tileAssets = new Dictionary<Panel, SgdbAsset>();
        readonly Dictionary<string, Panel> currentTiles = new Dictionary<string, Panel>();
        readonly Dictionary<string, Panel> officialTiles = new Dictionary<string, Panel>();
        readonly Dictionary<string, Pick> staged = new Dictionary<string, Pick>();
        readonly List<SgdbGame> matches = new List<SgdbGame>();
        readonly SemaphoreSlim thumbSem = new SemaphoreSlim(6);
        List<UndoItem> lastUndo;
        uint lastUndoApp;

        class UndoItem
        {
            public AType Type;
            public List<string> BackupPaths;
        }

        Shortcut currentShortcut;
        int gen;
        int currentSgdbId = -1;
        bool busy, suppressMatch;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public MainForm()
        {
            Text = "SteamGridDB Fetcher";
            BackColor = BG0;
            ForeColor = TX;
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1320, 860);
            MinimumSize = new Size(1060, 660);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            BuildUi();
            Shown += OnShownAsync;
            FormClosing += SaveWindowState;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); }  // dark title bar
            catch (Exception) { }
        }

        // ------------------------------------------------- window persistence

        void RestoreWindowState()
        {
            int ww = Cfg.Int(cfg, "win_w", 0), wh = Cfg.Int(cfg, "win_h", 0);
            if (ww >= 800 && wh >= 500)
            {
                var screen = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(ww, screen.Width), Math.Min(wh, screen.Height));
                Location = new Point(
                    screen.Left + (screen.Width - Width) / 2,
                    screen.Top + (screen.Height - Height) / 2);
            }
            if (Cfg.Int(cfg, "win_max", 0) == 1) WindowState = FormWindowState.Maximized;
        }

        void SaveWindowState(object s, FormClosingEventArgs e)
        {
            try
            {
                if (cfg == null) cfg = Cfg.Load();
                Size sz = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
                cfg["win_w"] = sz.Width;
                cfg["win_h"] = sz.Height;
                cfg["win_max"] = WindowState == FormWindowState.Maximized ? 1 : 0;
                Cfg.Save(cfg);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------- setup

        void OnShownAsync(object s, EventArgs e)
        {
            try
            {
                cfg = Cfg.Load();
                RestoreWindowState();
                string key = Environment.GetEnvironmentVariable("SGDB_API_KEY");
                if (string.IsNullOrEmpty(key)) key = Cfg.Str(cfg, "api_key");
                if (string.IsNullOrEmpty(key))
                {
                    key = PromptForKey();
                    if (string.IsNullOrEmpty(key)) { Close(); return; }
                    cfg["api_key"] = key.Trim();
                    Cfg.Save(cfg);
                }
                api = new Sgdb(key.Trim());

                steamPath = Steam.FindPath(cfg);
                stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                // restore persisted scope + filters
                scope = Cfg.Str(cfg, "scope") ?? "all";
                if (!scopeKeys.Contains(scope)) scope = "all";
                suppressFilter = true;
                cbStatic.Checked = Cfg.Int(cfg, "f_static", 1) == 1;
                cbAnimated.Checked = Cfg.Int(cfg, "f_animated", 1) == 1;
                cbHumor.Checked = Cfg.Int(cfg, "f_humor", 1) == 1;
                cbAdult.Checked = Cfg.Int(cfg, "f_adult", 1) == 1;
                cbEpilepsy.Checked = Cfg.Int(cfg, "f_epilepsy", 1) == 1;
                cbUntagged.Checked = Cfg.Int(cfg, "f_untagged", 1) == 1;
                suppressFilter = false;

                ReloadLibrary();
                UpdateButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "SteamGridDB Fetcher",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        string PromptForKey()
        {
            using (var f = new Form())
            {
                f.Text = "SteamGridDB API key";
                f.BackColor = BG0; f.ForeColor = TX; f.Font = Font;
                f.AutoScaleDimensions = new SizeF(96f, 96f);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.ClientSize = new Size(460, 150);
                f.StartPosition = FormStartPosition.CenterParent;

                var lbl = new Label
                {
                    Text = "Paste your SteamGridDB API key.\nGet one free at:  steamgriddb.com -> Profile -> Preferences -> API",
                    Location = new Point(14, 12), AutoSize = true,
                    MaximumSize = new Size(430, 0), ForeColor = TX
                };
                var box = new TextBox
                {
                    Location = new Point(14, 12 + lbl.PreferredHeight + 10), Width = 430,
                    BackColor = FIELD, ForeColor = TX, BorderStyle = BorderStyle.FixedSingle
                };
                var ok = MakeButton("OK", true);
                ok.AutoSize = false;
                var cancel = MakeButton("Cancel", false);
                cancel.AutoSize = false;
                int bh = Math.Max(32, Math.Max(ok.GetPreferredSize(Size.Empty).Height,
                                               cancel.GetPreferredSize(Size.Empty).Height));
                int by = box.Bottom + 16;
                f.ClientSize = new Size(460, by + bh + 12);
                ok.Size = new Size(90, bh); ok.Location = new Point(254, by);
                ok.DialogResult = DialogResult.OK;
                cancel.Size = new Size(90, bh); cancel.Location = new Point(354, by);
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(lbl); f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        // ------------------------------------------------------- ui helpers

        Button MakeButton(string text, bool accent)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? ACC : BG3,
                ForeColor = accent ? ColorTranslator.FromHtml("#06121f") : TX,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 7, 14, 7)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = accent ? ColorTranslator.FromHtml("#6cb4ff") : ColorTranslator.FromHtml("#242b3d");
            return b;
        }

        CheckBox MakeCheck(string text)
        {
            return new CheckBox
            {
                Text = text, Checked = true, AutoSize = true,
                ForeColor = TX, BackColor = BG1, Cursor = Cursors.Hand,
                Margin = new Padding(0, 5, 14, 0)
            };
        }

        // Shared caption font + strip height for asset tiles, sized from the
        // font so the text never clips regardless of DPI.
        static readonly Font CapFont = new Font("Segoe UI", 7.5f);
        static readonly int CapH = Math.Max(16, CapFont.Height + 4);

        Label RailHeading(string text)
        {
            return new Label
            {
                Text = text.ToUpperInvariant(), ForeColor = DIM, BackColor = BG1,
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold), AutoSize = true,
                Margin = new Padding(0, 10, 0, 4)
            };
        }

        Label FilterHeading(string text, int leftGap)
        {
            return new Label
            {
                Text = text.ToUpperInvariant(), ForeColor = DIM, BackColor = BG1, AutoSize = true,
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                Margin = new Padding(leftGap, 9, 10, 0)
            };
        }

        // ---------------------------------------------------------- build ui

        void BuildUi()
        {
            // ============================================= LIBRARY VIEW
            libraryView = new Panel { Dock = DockStyle.Fill, BackColor = BG0 };
            Controls.Add(libraryView);
            libraryView.BringToFront();

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = BG1 };
            libraryView.Controls.Add(toolbar);

            var brand = new Label
            {
                Text = "SteamGridDB Fetcher", ForeColor = TX, BackColor = BG1,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(18, 20), AutoSize = true
            };
            toolbar.Controls.Add(brand);

            scopeSeg = new Panel { Location = new Point(240, 15), Height = 36, BackColor = BG0, Width = 480 };
            toolbar.Controls.Add(scopeSeg);
            for (int i = 0; i < scopeKeys.Length; i++)
            {
                string key = scopeKeys[i];
                var b = new Button
                {
                    Text = scopeLabels[i], FlatStyle = FlatStyle.Flat, ForeColor = DIM,
                    BackColor = BG0, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(10, 6, 10, 6), Cursor = Cursors.Hand,
                    Location = new Point(3, 3), Tag = key
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = BG2;
                b.Click += delegate
                {
                    if (busy) return;
                    scope = key;
                    cfg["scope"] = scope;
                    Cfg.Save(cfg);
                    RenderScopeSeg();
                    ApplyLibraryFilter();
                };
                scopeSeg.Controls.Add(b);
                scopeButtons.Add(b);
            }

            var tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, BackColor = BG1,
                Padding = new Padding(0, 15, 14, 0)
            };
            toolbar.Controls.Add(tools);

            libSearch = new TextBox
            {
                Width = 220, BackColor = FIELD, ForeColor = TX,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f),
                Margin = new Padding(0, 5, 10, 0)
            };
            libSearch.TextChanged += delegate { ApplyLibraryFilter(); };
            tools.Controls.Add(libSearch);

            refreshBtn = MakeButton("Refresh", false);
            refreshBtn.Margin = new Padding(0, 0, 10, 0);
            refreshBtn.Click += delegate { RefreshLibrary(); };
            tools.Controls.Add(refreshBtn);

            fillBtn = MakeButton("Fill missing art", true);
            fillBtn.Margin = new Padding(0);
            fillBtn.Click += delegate { FillMissing(); };
            tools.Controls.Add(fillBtn);

            batchStrip = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = BG1, Visible = false };
            libraryView.Controls.Add(batchStrip);
            batchStrip.BringToFront();
            batchLabel = new Label
            {
                Text = "", ForeColor = TX, BackColor = BG1, AutoSize = false,
                Location = new Point(18, 10), Size = new Size(560, Math.Max(20, Font.Height + 4)),
                AutoEllipsis = true
            };
            batchStrip.Controls.Add(batchLabel);
            batchTxt = new Label
            {
                Text = "", ForeColor = DIM, BackColor = BG1, AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(batchStrip.Width - 100, 12)
            };
            batchStrip.Controls.Add(batchTxt);
            batchBarOuter = new Panel
            {
                Location = new Point(600, 16), Height = 7, BackColor = BG3,
                Width = Math.Max(120, batchStrip.Width - 720),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            batchStrip.Controls.Add(batchBarOuter);
            batchBarInner = new Panel { Location = new Point(0, 0), Height = 7, Width = 0, BackColor = ACC };
            batchBarOuter.Controls.Add(batchBarInner);

            stripHideTimer = new System.Windows.Forms.Timer { Interval = 3500 };
            stripHideTimer.Tick += delegate { stripHideTimer.Stop(); batchStrip.Visible = false; };

            libraryFlow = new BareFlowPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = BG0,
                Padding = new Padding(14, 12, 14, 12)
            };
            libraryView.Controls.Add(libraryFlow);
            libraryFlow.BringToFront();

            // ============================================= DETAIL VIEW
            detailView = new Panel { Dock = DockStyle.Fill, BackColor = BG0, Visible = false };
            Controls.Add(detailView);
            detailView.BringToFront();

            var rail = new BarePanel
            {
                Dock = DockStyle.Left, Width = 306, BackColor = BG1, AutoScroll = true
            };
            detailView.Controls.Add(rail);

            var railFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BG1, Location = new Point(16, 14)
            };
            rail.Controls.Add(railFlow);

            backBtn = MakeButton("<   Library", false);
            backBtn.Margin = new Padding(0, 0, 0, 14);
            backBtn.Click += delegate { BackToLibrary(); };
            railFlow.Controls.Add(backBtn);

            var heroWrap = new Panel { Size = new Size(272, 280), BackColor = BG1, Margin = new Padding(0, 0, 0, 8) };
            heroPb = new PictureBox
            {
                Location = new Point(44, 0), Size = new Size(184, 276),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = BG2
            };
            heroWrap.Controls.Add(heroPb);
            railFlow.Controls.Add(heroWrap);

            dName = new Label
            {
                Text = "", ForeColor = TX, BackColor = BG1,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                AutoSize = true, MaximumSize = new Size(272, 0),
                Margin = new Padding(0, 0, 0, 6)
            };
            railFlow.Controls.Add(dName);

            railFlow.Controls.Add(RailHeading("SteamGridDB match"));
            matchCombo = new ComboBox
            {
                Width = 272, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = BG2, ForeColor = TX, Margin = new Padding(0)
            };
            matchCombo.SelectedIndexChanged += delegate
            {
                if (suppressMatch || busy || matchCombo.SelectedIndex < 0) return;
                if (matchCombo.SelectedIndex < matches.Count)
                    LoadAssets(matches[matchCombo.SelectedIndex].Id, matches[matchCombo.SelectedIndex].Name);
            };
            railFlow.Controls.Add(matchCombo);

            railFlow.Controls.Add(RailHeading("Search override"));
            dSearch = new TextBox
            {
                Width = 272, BackColor = FIELD, ForeColor = TX,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f),
                Margin = new Padding(0)
            };
            dSearch.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); }
            };
            railFlow.Controls.Add(dSearch);

            railFlow.Controls.Add(RailHeading("Artwork slots"));
            int rowH = Math.Max(32, Font.Height + 16);
            foreach (AType t in Cfg.Types)
            {
                var row = new Panel { Size = new Size(272, rowH), BackColor = BG2, Margin = new Padding(0, 0, 0, 5) };
                var dot = new Label
                {
                    Text = "●", AutoSize = false, BackColor = BG2, ForeColor = DIM,
                    Font = new Font("Segoe UI", 8f), Size = new Size(20, rowH),
                    Location = new Point(10, 0), TextAlign = ContentAlignment.MiddleLeft
                };
                var nm = new Label
                {
                    Text = t.Short, AutoSize = false, BackColor = BG2, ForeColor = TX,
                    Size = new Size(120, rowH), Location = new Point(30, 0),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var st = new Label
                {
                    Text = "", AutoSize = false, BackColor = BG2, ForeColor = DIM,
                    Size = new Size(110, rowH), Location = new Point(152, 0),
                    TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Segoe UI", 8.2f, FontStyle.Bold)
                };
                row.Controls.Add(dot); row.Controls.Add(nm); row.Controls.Add(st);
                chkDot[t.Key] = dot;
                chkStatus[t.Key] = st;
                railFlow.Controls.Add(row);
            }

            railFlow.Controls.Add(RailHeading("Staged changes"));
            stagedLabel = new Label
            {
                Text = "", ForeColor = DIM, BackColor = BG2, AutoSize = true,
                MaximumSize = new Size(272, 0), MinimumSize = new Size(272, 40),
                Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 10)
            };
            railFlow.Controls.Add(stagedLabel);

            applyBtn = MakeButton("Apply changes", true);
            applyBtn.AutoSize = false;
            applyBtn.Size = new Size(272, Math.Max(38, applyBtn.GetPreferredSize(Size.Empty).Height));
            applyBtn.Margin = new Padding(0, 0, 0, 8);
            applyBtn.Click += delegate { ApplyStaged(); };
            railFlow.Controls.Add(applyBtn);

            autoFillBtn = MakeButton("Auto-fill empty slots", false);
            autoFillBtn.AutoSize = false;
            autoFillBtn.Size = new Size(272, Math.Max(34, autoFillBtn.GetPreferredSize(Size.Empty).Height));
            autoFillBtn.Margin = new Padding(0, 0, 0, 8);
            autoFillBtn.Click += delegate { AutoFillEmpty(); };
            railFlow.Controls.Add(autoFillBtn);

            railStatus = new Label
            {
                Text = "", ForeColor = DIM, BackColor = BG1, AutoSize = true,
                MaximumSize = new Size(272, 0), Margin = new Padding(0, 0, 0, 2),
                Font = new Font("Segoe UI", 8.6f)
            };
            railFlow.Controls.Add(railStatus);

            undoLink = new LinkLabel
            {
                Text = "Undo last apply", AutoSize = true, Visible = false,
                LinkColor = ACC, ActiveLinkColor = TX, BackColor = BG1,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
                Margin = new Padding(0, 2, 0, 16)
            };
            undoLink.LinkClicked += delegate { UndoLast(); };
            railFlow.Controls.Add(undoLink);

            // workarea (filters + sections)
            var work = new Panel { Dock = DockStyle.Fill, BackColor = BG0 };
            detailView.Controls.Add(work);
            work.BringToFront();

            var filterbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = BG1 };
            work.Controls.Add(filterbar);
            var filterRow = new FlowLayoutPanel
            {
                Location = new Point(12, 8), AutoSize = true, WrapContents = false,
                BackColor = BG1, FlowDirection = FlowDirection.LeftToRight
            };
            filterbar.Controls.Add(filterRow);

            filterRow.Controls.Add(FilterHeading("Type", 4));
            cbStatic = MakeCheck("Static");
            cbAnimated = MakeCheck("Animated");
            filterRow.Controls.Add(cbStatic);
            filterRow.Controls.Add(cbAnimated);
            filterRow.Controls.Add(FilterHeading("Tags", 18));
            cbHumor = MakeCheck("Humor");
            cbAdult = MakeCheck("Adult");
            cbEpilepsy = MakeCheck("Epilepsy");
            cbUntagged = MakeCheck("Untagged");
            filterRow.Controls.Add(cbHumor);
            filterRow.Controls.Add(cbAdult);
            filterRow.Controls.Add(cbEpilepsy);
            filterRow.Controls.Add(cbUntagged);

            var allBtn = MakeButton("Reset", false);
            allBtn.Margin = new Padding(14, 0, 0, 0);
            allBtn.Click += delegate
            {
                suppressFilter = true;
                foreach (CheckBox c in AllFilterBoxes()) c.Checked = true;
                suppressFilter = false;
                SaveFilters();
                ApplyAssetFilter();
            };
            filterRow.Controls.Add(allBtn);
            filterbar.Height = Math.Max(46, filterRow.PreferredSize.Height + 16);
            foreach (CheckBox c in AllFilterBoxes())
                c.CheckedChanged += delegate
                {
                    if (suppressFilter) return;
                    SaveFilters();
                    ApplyAssetFilter();
                };

            contentPanel = new BarePanel
            {
                Dock = DockStyle.Fill, BackColor = BG0, AutoScroll = true, Padding = new Padding(6)
            };
            work.Controls.Add(contentPanel);
            contentPanel.BringToFront();

            sectionsFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BG0, Location = new Point(10, 4)
            };
            contentPanel.Controls.Add(sectionsFlow);
            contentPanel.Resize += delegate { UpdateFlowWidths(); };

            var animTimer = new System.Windows.Forms.Timer { Interval = 30 };
            animTimer.Tick += delegate { AnimTick(); };
            animTimer.Start();
        }

        CheckBox[] AllFilterBoxes()
        {
            return new CheckBox[] { cbStatic, cbAnimated, cbHumor, cbAdult, cbEpilepsy, cbUntagged };
        }

        void SaveFilters()
        {
            cfg["f_static"] = cbStatic.Checked ? 1 : 0;
            cfg["f_animated"] = cbAnimated.Checked ? 1 : 0;
            cfg["f_humor"] = cbHumor.Checked ? 1 : 0;
            cfg["f_adult"] = cbAdult.Checked ? 1 : 0;
            cfg["f_epilepsy"] = cbEpilepsy.Checked ? 1 : 0;
            cfg["f_untagged"] = cbUntagged.Checked ? 1 : 0;
            Cfg.Save(cfg);
        }

        void UpdateFlowWidths()
        {
            int w = Math.Max(300, contentPanel.ClientSize.Width - 34);
            foreach (var f in flows.Values) f.MaximumSize = new Size(w, 0);
        }

        void UpdateButtons()
        {
            bool loaded = shortcuts != null;
            fillBtn.Enabled = !busy && loaded;
            refreshBtn.Enabled = !busy && loaded;
            applyBtn.Enabled = !busy && staged.Count > 0;
            applyBtn.Text = staged.Count > 0
                ? "Apply " + staged.Count + " change" + (staged.Count > 1 ? "s" : "")
                : "Apply changes";
            autoFillBtn.Enabled = !busy && currentShortcut != null;
            backBtn.Enabled = !busy;
            matchCombo.Enabled = !busy;
        }

        // ------------------------------------------------- slot state helpers

        string FindExisting(uint appid, string suffix)
        {
            if (gridDir == null) return null;
            foreach (string ext in Cfg.ImageExts)
            {
                string p = Path.Combine(gridDir, appid + suffix + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        bool[] ComputeSlots(uint appid)
        {
            var b = new bool[Cfg.Types.Length];
            for (int i = 0; i < Cfg.Types.Length; i++)
                b[i] = FindExisting(appid, Cfg.Types[i].Suffix) != null;
            return b;
        }

        bool[] GetSlots(uint appid)
        {
            bool[] b;
            if (!slotState.TryGetValue(appid, out b))
            {
                b = ComputeSlots(appid);
                slotState[appid] = b;
            }
            return b;
        }

        int MissingCount(Shortcut g)
        {
            if (g.IsSteam) return 0;   // store games always have official art
            return GetSlots(g.AppId).Count(v => !v);
        }

        // -------------------------------------------------- library rendering

        void ReloadLibrary()
        {
            libGen++;
            shortcuts = Steam.LoadShortcuts(steamPath, Cfg.Str(cfg, "user_id"), out userId);
            steamGames = Steam.LoadSteamGames(steamPath);
            gridDir = Artwork.GridDir(steamPath, userId);

            slotState.Clear();
            var old = libraryFlow.Controls.Cast<Control>().ToList();
            libraryFlow.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            gameTiles.Clear();
            foreach (Image img in coverImages.Values) img.Dispose();
            coverImages.Clear();
            foreach (Image img in placeholders.Values) img.Dispose();
            placeholders.Clear();

            foreach (Shortcut sc in AllGames)
            {
                GetSlots(sc.AppId);
                if (sc.IsSteam) QueueSteamCover(sc);
            }
            BuildLibrary();
            RenderScopeSeg();
            ApplyLibraryFilter();

            // decode custom covers off the UI thread; cards pop in as ready
            int g = libGen;
            var snapshot = AllGames.ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (Shortcut sc in snapshot)
                {
                    if (g != libGen) return;
                    string p = FindExisting(sc.AppId, "p");
                    if (p == null) continue;
                    Bitmap bmp = null;
                    try
                    {
                        using (var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(p))))
                            bmp = ScaleCover(src);
                    }
                    catch (Exception) { continue; }
                    Shortcut cur = sc;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (g != libGen) { bmp.Dispose(); return; }
                            Image prev;
                            if (coverImages.TryGetValue(cur.AppId, out prev) && prev != null) prev.Dispose();
                            coverImages[cur.AppId] = bmp;
                            UpdateTile(cur);
                        });
                    }
                    catch (Exception) { bmp.Dispose(); return; }   // window closed
                }
            });
        }

        void RefreshLibrary()
        {
            if (busy) return;
            try
            {
                ReloadLibrary();
                ShowStrip("Refreshed - " + shortcuts.Count + " shortcuts, " + steamGames.Count +
                          " Steam games. (Just-added shortcuts may need a Steam restart to appear.)", true);
            }
            catch (Exception ex) { ShowStrip("Refresh failed: " + ex.Message, true); }
        }

        void RenderScopeSeg()
        {
            if (shortcuts == null) return;
            var counts = new int[4];
            counts[0] = shortcuts.Count + steamGames.Count;
            counts[1] = shortcuts.Count(g => MissingCount(g) > 0);
            counts[2] = shortcuts.Count;
            counts[3] = steamGames.Count;
            int x = 3, h = 30;
            for (int i = 0; i < scopeButtons.Count; i++)
            {
                Button b = scopeButtons[i];
                b.Text = scopeLabels[i] + "   " + counts[i];
                bool on = (string)b.Tag == scope;
                b.BackColor = on ? BG3 : BG0;
                b.ForeColor = on ? TX : DIM;
                b.Location = new Point(x, 3);
                x += b.Width + 2;
                if (b.Height > h) h = b.Height;
            }
            scopeSeg.Width = x + 3;
            scopeSeg.Height = h + 6;
            if (scopeSeg.Parent != null)
                scopeSeg.Top = Math.Max(4, (scopeSeg.Parent.Height - scopeSeg.Height) / 2);
        }

        bool ScopeMatch(Shortcut g)
        {
            if (scope == "missing") return !g.IsSteam && MissingCount(g) > 0;
            if (scope == "nonsteam") return !g.IsSteam;
            if (scope == "steam") return g.IsSteam;
            return true;
        }

        IEnumerable<Shortcut> VisibleGames()
        {
            string q = libSearch.Text.Trim();
            return AllGames.Where(g => ScopeMatch(g) &&
                (q.Length == 0 || g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        void ApplyLibraryFilter()
        {
            if (shortcuts == null) return;
            string q = libSearch.Text.Trim();
            libraryFlow.SuspendLayout();
            foreach (Shortcut g in AllGames)
            {
                GameCard t;
                if (gameTiles.TryGetValue(g.AppId, out t))
                    t.Visible = ScopeMatch(g) &&
                        (q.Length == 0 || g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            libraryFlow.ResumeLayout();
        }

        void BuildLibrary()
        {
            libraryFlow.SuspendLayout();
            foreach (Shortcut sc in AllGames)
            {
                var card = new GameCard
                {
                    Game = sc,
                    Size = new Size(CoverW + 8, CoverH + 4 + GameCard.ExtraH),
                    Margin = new Padding(7),
                    BgNormal = BG0, BgHover = BG2, MeterOn = OKC, MeterOff = METER_OFF
                };
                Shortcut captured = sc;
                card.Click += delegate { if (!busy) OpenDetail(captured); };
                gameTiles[sc.AppId] = card;
                libraryFlow.Controls.Add(card);
                UpdateTile(sc);
            }
            libraryFlow.ResumeLayout();
        }

        void UpdateTile(Shortcut sc)
        {
            GameCard t;
            if (!gameTiles.TryGetValue(sc.AppId, out t)) return;
            Image cover;
            if (!coverImages.TryGetValue(sc.AppId, out cover) && sc.IsSteam)
                steamCoverCache.TryGetValue(sc.AppId, out cover);
            t.Cover = cover;
            t.Placeholder = cover == null ? GetPlaceholder(sc) : null;
            t.Slots = sc.IsSteam ? null : GetSlots(sc.AppId);
            bool isApplied = applied.Contains(sc.AppId);
            t.NameColor = isApplied ? OKC : TX;
            int missing = MissingCount(sc);
            if (isApplied) { t.StatusText = "updated"; t.StatusColor = OKC; }
            else if (sc.IsSteam) { t.StatusText = "Steam"; t.StatusColor = DIM; }
            else if (missing == 4) { t.StatusText = "no artwork"; t.StatusColor = WARN; }
            else if (missing > 0) { t.StatusText = missing + (missing > 1 ? " slots empty" : " slot empty"); t.StatusColor = WARN; }
            else { t.StatusText = "complete"; t.StatusColor = DIM; }
            t.Invalidate();
        }

        void RefreshGame(uint appid)
        {
            slotState[appid] = ComputeSlots(appid);
            LoadCoverImage(appid);
            Shortcut sc = AllGames.FirstOrDefault(x => x.AppId == appid);
            if (sc != null) UpdateTile(sc);
            RenderScopeSeg();
        }

        void ShowStrip(string text, bool autoHide)
        {
            batchLabel.Text = text;
            batchTxt.Text = "";
            batchBarInner.Width = 0;
            batchStrip.Visible = true;
            stripHideTimer.Stop();
            if (autoHide) stripHideTimer.Start();
        }

        // ----------------------------------------------------- cover images

        static Bitmap ScaleCover(Image src)
        {
            var bmp = new Bitmap(CoverW, CoverH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float scale = Math.Max((float)CoverW / src.Width, (float)CoverH / src.Height);
                float sw = src.Width * scale, sh = src.Height * scale;
                g.DrawImage(src, (CoverW - sw) / 2f, (CoverH - sh) / 2f, sw, sh);
            }
            return bmp;
        }

        void LoadCoverImage(uint appid)
        {
            Image old;
            if (coverImages.TryGetValue(appid, out old) && old != null) old.Dispose();
            coverImages.Remove(appid);
            string p = FindExisting(appid, "p");
            if (p == null) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(p);   // read bytes so the file isn't locked
                using (var src = Image.FromStream(new MemoryStream(bytes)))
                    coverImages[appid] = ScaleCover(src);
            }
            catch (Exception) { }
        }

        Image GetPlaceholder(Shortcut sc)
        {
            Image ph;
            if (placeholders.TryGetValue(sc.AppId, out ph)) return ph;
            var bmp = new Bitmap(CoverW, CoverH);
            using (var g = Graphics.FromImage(bmp))
            {
                using (var lg = new LinearGradientBrush(
                    new Rectangle(0, 0, CoverW, CoverH),
                    ColorTranslator.FromHtml("#2b3446"), ColorTranslator.FromHtml("#151a26"), 65f))
                    g.FillRectangle(lg, 0, 0, CoverW, CoverH);
                TextRenderer.DrawText(g, sc.Name, new Font("Segoe UI", 11f, FontStyle.Bold),
                    new Rectangle(14, 14, CoverW - 28, CoverH - 28),
                    ColorTranslator.FromHtml("#aeb6c6"),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak);
            }
            placeholders[sc.AppId] = bmp;
            return bmp;
        }

        // Fetch a Steam-store game's own library cover: local Steam cache
        // first (exactly what Steam shows, offline), then both CDNs.
        void QueueSteamCover(Shortcut sc)
        {
            if (steamCoverCache.ContainsKey(sc.AppId)) return;
            int g = libGen;
            uint appid = sc.AppId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap bmp = null;
                try
                {
                    byte[] data = null;
                    string cache = Path.Combine(steamPath, "appcache", "librarycache");
                    var local = new List<string>
                    {
                        Path.Combine(cache, appid + "_library_600x900.jpg"),
                        Path.Combine(cache, appid.ToString(), "library_600x900.jpg"),
                    };
                    foreach (string lc in local)
                        if (File.Exists(lc))
                        {
                            try { data = File.ReadAllBytes(lc); break; }
                            catch (Exception) { }
                        }

                    // hash-subfolder layout: {appid}/{hash}/library_600x900.jpg
                    if (data == null)
                    {
                        try
                        {
                            string sub = Path.Combine(cache, appid.ToString());
                            if (Directory.Exists(sub))
                                foreach (string pattern in new string[]
                                         { "library_600x900*", "library_capsule.*", "capsule*" })
                                {
                                    string[] found = Directory.GetFiles(sub, pattern,
                                                                        SearchOption.AllDirectories);
                                    if (found.Length > 0)
                                    {
                                        data = File.ReadAllBytes(found[0]);
                                        break;
                                    }
                                }
                        }
                        catch (Exception) { }
                    }

                    if (data == null)
                        foreach (string cdn in SteamStore.CdnBases)
                        {
                            try
                            {
                                using (var wc = new WebClient())
                                {
                                    wc.Headers["User-Agent"] = "SteamGridDBFetcher/1.0";
                                    data = wc.DownloadData(cdn + appid + "/library_600x900.jpg");
                                }
                                break;
                            }
                            catch (Exception) { }
                        }

                    if (data == null) return;
                    using (var src = Image.FromStream(new MemoryStream(data)))
                        bmp = ScaleCover(src);
                }
                catch (Exception) { return; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (steamCoverCache.ContainsKey(appid)) { bmp.Dispose(); return; }
                        steamCoverCache[appid] = bmp;
                        if (g != libGen) return;
                        Shortcut cur = AllGames.FirstOrDefault(x => x.AppId == appid);
                        if (cur != null) UpdateTile(cur);
                    });
                }
                catch (Exception) { bmp.Dispose(); }   // window closed
            });
        }

        // ------------------------------------------------------ batch fill

        // Fill ONLY missing asset slots for the games currently visible in the
        // library. Official Steam defaults first, SteamGridDB top results as
        // fallback; existing artwork is never replaced.
        async void FillMissing()
        {
            if (busy || api == null || shortcuts == null) return;
            var targets = VisibleGames().Where(x => MissingCount(x) > 0).ToList();
            if (targets.Count == 0)
            {
                ShowStrip("Nothing to fill - every game in the current view has complete artwork.", true);
                return;
            }
            DialogResult r = MessageBox.Show(this,
                targets.Count + " of the games currently shown have empty slots.\n\n" +
                "Empty slots get the official Steam default art (SteamGridDB top result if the " +
                "game isn't on Steam). Artwork you already have is never touched; games hidden " +
                "by the search box or scope are not touched either.",
                "Fill missing artwork", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            busy = true;
            UpdateButtons();
            batchStrip.Visible = true;
            stripHideTimer.Stop();
            int updated = 0, none = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Shortcut sc = targets[i];
                    batchLabel.Text = "Filling " + sc.Name + "...";
                    batchTxt.Text = (i + 1) + " / " + targets.Count;
                    batchBarInner.Width = (int)((long)batchBarOuter.Width * (i + 1) / targets.Count);

                    var missing = Cfg.Types.Where(t => FindExisting(sc.AppId, t.Suffix) == null).ToList();
                    bool wrote = false;

                    List<SgdbGame> res = null;
                    try { res = (await api.SearchSmart(sc.Name)).Value; }
                    catch (Exception) { }
                    if (res != null && res.Count > 0)
                    {
                        int steamId = await api.SteamAppId(res[0].Id);
                        if (steamId > 0)
                        {
                            foreach (AType t in missing.ToList())
                            {
                                if (await SteamStore.Apply(gridDir, sc.AppId, t, steamId, stamp) != null)
                                {
                                    missing.Remove(t);
                                    wrote = true;
                                }
                            }
                        }
                        foreach (AType t in missing)
                        {
                            try
                            {
                                var assets = t.Key == "logo"
                                    ? await api.OfficialLogos(res[0].Id)
                                    : new List<SgdbAsset>();
                                if (assets.Count == 0)
                                    assets = await api.Assets(res[0].Id, t);
                                if (assets.Count > 0)
                                {
                                    await Artwork.Apply(gridDir, sc.AppId, t, assets[0].Url, stamp);
                                    wrote = true;
                                }
                            }
                            catch (Exception) { }
                            await Task.Delay(100);   // be polite to the API/CDN
                        }
                    }

                    if (wrote) { updated++; applied.Add(sc.AppId); }
                    else none++;
                    RefreshGame(sc.AppId);
                }
                batchLabel.Text = "Done: " + updated + " filled in, " + none +
                                  " with nothing found. Press F5 in your Steam library to see the artwork.";
                batchTxt.Text = "";
                batchBarInner.Width = batchBarOuter.Width;
                stripHideTimer.Start();
                ApplyLibraryFilter();   // the "missing" scope may have shrunk
            }
            finally { busy = false; UpdateButtons(); }
        }

        // ------------------------------------------------- detail: open/back

        void OpenDetail(Shortcut sc)
        {
            currentShortcut = sc;
            staged.Clear();
            lastUndo = null;
            undoLink.Visible = false;
            dName.Text = sc.Name;
            dSearch.Text = sc.Name;
            SetRailStatus("", false);
            libraryView.Visible = false;
            detailView.Visible = true;
            UpdateRail();
            DoSearch();
        }

        void BackToLibrary()
        {
            if (busy) return;
            gen++;
            currentSgdbId = -1;
            currentShortcut = null;
            staged.Clear();
            ClearSections();
            detailView.Visible = false;
            libraryView.Visible = true;
            UpdateButtons();
        }

        void SetRailStatus(string text, bool ok)
        {
            railStatus.Text = text;
            railStatus.ForeColor = ok ? OKC : DIM;
        }

        void SetRailError(string text)
        {
            railStatus.Text = text;
            railStatus.ForeColor = ERRC;
        }

        // ------------------------------------------------- detail: rail state

        void UpdateRail()
        {
            if (currentShortcut == null) return;
            bool[] slots = GetSlots(currentShortcut.AppId);

            UpdateHero();

            for (int i = 0; i < Cfg.Types.Length; i++)
            {
                AType t = Cfg.Types[i];
                bool isStaged = staged.ContainsKey(t.Key);
                Color c = isStaged ? ACC : slots[i] ? OKC : currentShortcut.IsSteam ? DIM : WARN;
                string txt = isStaged ? "will change"
                    : slots[i] ? "set"
                    : currentShortcut.IsSteam ? "Steam default" : "empty";
                chkDot[t.Key].ForeColor = c;
                chkStatus[t.Key].ForeColor = c;
                chkStatus[t.Key].Text = txt;
            }

            if (staged.Count == 0)
            {
                stagedLabel.Text = "Nothing staged - pick assets on the right.";
                stagedLabel.ForeColor = DIM;
            }
            else
            {
                var sb = new StringBuilder();
                foreach (AType t in Cfg.Types)
                {
                    Pick pk;
                    if (staged.TryGetValue(t.Key, out pk))
                        sb.AppendLine(t.Short + "  →  " + pk.SourceLabel);
                }
                stagedLabel.Text = sb.ToString().TrimEnd();
                stagedLabel.ForeColor = TX;
            }
            UpdateButtons();
        }

        void UpdateHero()
        {
            if (currentShortcut == null) return;
            Pick pk;
            if (staged.TryGetValue("cover", out pk) && pk.Tile != null && !pk.Tile.IsDisposed
                && pk.Tile.Controls.Count > 0)
            {
                var tpb = pk.Tile.Controls[0] as PictureBox;
                if (tpb != null && tpb.Image != null)
                {
                    heroPb.Image = tpb.Image;
                    return;
                }
            }
            string p = FindExisting(currentShortcut.AppId, "p");
            Image img = null;
            if (p != null)
            {
                try { img = Image.FromStream(new MemoryStream(File.ReadAllBytes(p))); }
                catch (Exception) { }
            }
            if (img == null && currentShortcut.IsSteam)
            {
                Image sc;
                if (steamCoverCache.TryGetValue(currentShortcut.AppId, out sc))
                {
                    heroPb.Image = sc;
                    if (heroOwned != null) { heroOwned.Dispose(); heroOwned = null; }
                    return;
                }
            }
            if (img != null)
            {
                Image prev = heroOwned;
                heroOwned = img;
                heroPb.Image = img;
                if (prev != null) prev.Dispose();
            }
            else
            {
                heroPb.Image = GetPlaceholder(currentShortcut);
                if (heroOwned != null) { heroOwned.Dispose(); heroOwned = null; }
            }
        }

        // ------------------------------------------------------ picker: search

        void ClearSections()
        {
            flows.Clear(); countLabels.Clear(); tiles.Clear(); tileAssets.Clear();
            currentTiles.Clear(); officialTiles.Clear();
            var old = sectionsFlow.Controls.Cast<Control>().ToList();
            sectionsFlow.Controls.Clear();
            foreach (Control c in old) c.Dispose();
        }

        void ShowPlaceholder(string text)
        {
            ClearSections();
            sectionsFlow.Controls.Add(new Label
            {
                Text = text, ForeColor = DIM, BackColor = BG0, AutoSize = true,
                Margin = new Padding(6, 30, 0, 0), Font = new Font("Segoe UI", 10f)
            });
        }

        async void DoSearch()
        {
            string term = dSearch.Text.Trim();
            if (term.Length == 0 || busy || api == null || currentShortcut == null) return;
            gen++;
            int g = gen;
            staged.Clear();
            currentSgdbId = -1;
            UpdateRail();
            ShowPlaceholder("Searching SteamGridDB...");
            SetRailStatus("Searching \"" + term + "\"...", false);
            List<SgdbGame> results;
            string usedTerm;
            try
            {
                var smart = await api.SearchSmart(term);
                usedTerm = smart.Key;
                results = smart.Value;
            }
            catch (Exception ex) { SetRailError("Search failed: " + ex.Message); return; }
            if (g != gen) return;

            if (results.Count > 0 && usedTerm != term)
                dSearch.Text = usedTerm;   // show the term that actually matched

            matches.Clear();
            matches.AddRange(results);
            suppressMatch = true;
            matchCombo.Items.Clear();
            int ddw = matchCombo.Width;
            foreach (SgdbGame m in matches)
            {
                matchCombo.Items.Add(m.Display);
                ddw = Math.Max(ddw, TextRenderer.MeasureText(m.Display, matchCombo.Font).Width + 24);
            }
            matchCombo.DropDownWidth = Math.Min(ddw, 480);
            suppressMatch = false;

            if (matches.Count == 0)
            {
                ShowPlaceholder("No SteamGridDB match. Try a different search term.");
                SetRailError("No results for \"" + term + "\".");
                return;
            }
            suppressMatch = true;
            matchCombo.SelectedIndex = 0;
            suppressMatch = false;
            SetRailStatus(matches.Count + " match(es).", true);
            LoadAssets(matches[0].Id, matches[0].Name);
        }

        async void LoadAssets(int gameId, string gameName)
        {
            gen++;
            int g = gen;
            currentSgdbId = gameId;
            staged.Clear();
            UpdateRail();

            Shortcut game = currentShortcut;
            ClearSections();
            foreach (AType t in Cfg.Types)
            {
                sectionsFlow.Controls.Add(new Label
                {
                    Text = t.Label, ForeColor = TX, BackColor = BG0, AutoSize = true,
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    Margin = new Padding(4, 16, 0, 0)
                });
                var cnt = new Label
                {
                    Text = "loading...", ForeColor = DIM, BackColor = BG0, AutoSize = true,
                    Font = new Font("Segoe UI", 8f), Margin = new Padding(5, 2, 0, 2)
                };
                countLabels[t.Key] = cnt;
                sectionsFlow.Controls.Add(cnt);

                var flow = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = BG0, Margin = new Padding(0)
                };
                flows[t.Key] = flow;
                tiles[t.Key] = new List<Panel>();
                sectionsFlow.Controls.Add(flow);
                string existing = game != null ? FindExisting(game.AppId, t.Suffix) : null;
                AddCurrentTile(t, flow, existing, game);
            }
            UpdateFlowWidths();
            RefreshAllHighlights();
            SetRailStatus("Loading assets for " + gameName + "...", false);

            // fire every request up front so the network round-trips overlap
            var pageTasks = new Dictionary<string, Task<AssetPage>>();
            foreach (AType t in Cfg.Types)
                pageTasks[t.Key] = api.AssetsPaged(gameId, t, 0);

            // offer the game's original Steam assets as picks
            int steamId = 0;
            try { steamId = await api.SteamAppId(gameId); }
            catch (Exception) { }
            if (g != gen) return;
            if (steamId > 0)
                foreach (AType t in Cfg.Types)
                    AddOfficialTile(t, flows[t.Key], steamId, g);

            pageByType.Clear();
            totalByType.Clear();
            shownByType.Clear();
            foreach (AType t in Cfg.Types)
            {
                AssetPage pg;
                try { pg = await pageTasks[t.Key]; }
                catch (Exception ex)
                {
                    if (g == gen) countLabels[t.Key].Text = "failed to load (" + ex.Message + ")";
                    continue;
                }
                if (g != gen) return;

                pageByType[t.Key] = 0;
                totalByType[t.Key] = pg.Total;
                shownByType[t.Key] = pg.Assets.Count;
                countLabels[t.Key].Text = pg.Total == 0
                    ? "none available on SteamGridDB"
                    : pg.Total + " available - click to stage a change";

                for (int i = 0; i < pg.Assets.Count; i++)
                {
                    Panel tile = AddAssetTile(t, flows[t.Key], pg.Assets[i]);
                    LoadThumb((PictureBox)tile.Controls[0], pg.Assets[i], g);
                }
                if (shownByType[t.Key] < pg.Total)
                    AddLoadMoreTile(t, g);
            }
            if (g == gen)
                SetRailStatus("Assets loaded. Click tiles to stage changes, then Apply.", true);
        }

        // ------------------------------------------------------- picker tiles

        // First tile of every row: what the game has right now. Clicking it
        // means "keep as is" (unstage). For Steam-store games without custom
        // art, "current" is the game's official Steam default - show it.
        void AddCurrentTile(AType t, FlowLayoutPanel flow, string existingPath, Shortcut game)
        {
            bool steamDefault = existingPath == null && game != null && game.IsSteam;
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = BG2,
                Margin = new Padding(4), Tag = null
            };
            var caption = new Label
            {
                Text = existingPath != null ? "current"
                     : steamDefault ? "current · Steam default" : "none",
                ForeColor = existingPath != null || steamDefault ? ACC : WARN,
                BackColor = FIELD, Font = CapFont,
                Location = new Point(5, 5 + t.H - CapH), Size = new Size(t.W, CapH),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            string key = t.Key;
            EventHandler h = delegate { Unstage(key); };

            if (existingPath != null || steamDefault)
            {
                var pb = new PictureBox
                {
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - CapH),
                    SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD, Cursor = Cursors.Hand
                };
                if (existingPath != null)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(existingPath);
                        pb.Image = Image.FromStream(new MemoryStream(bytes));
                    }
                    catch (Exception) { }
                }
                else
                {
                    LoadSteamDefaultInto(pb, game.AppId, t.Key);
                }
                p.Controls.Add(pb);
                pb.Click += h;
            }
            else
            {
                var empty = new Label
                {
                    Text = "keep\nempty", ForeColor = DIM, BackColor = FIELD,
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - CapH),
                    TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 8f)
                };
                p.Controls.Add(empty);
                empty.Click += h;
            }
            p.Controls.Add(caption);
            caption.Click += h;
            p.Click += h;
            flow.Controls.Add(p);
            tiles[key].Add(p);
            currentTiles[key] = p;
        }

        // What Steam's local cache / CDN calls each default asset.
        static readonly Dictionary<string, string> CacheFlat = new Dictionary<string, string>
        {
            { "cover", "_library_600x900.jpg" }, { "wide", "_header.jpg" },
            { "background", "_library_hero.jpg" }, { "logo", "_logo.png" },
        };
        static readonly Dictionary<string, string[]> CachePatterns = new Dictionary<string, string[]>
        {
            { "cover",      new string[] { "library_600x900*", "library_capsule.*", "capsule*" } },
            { "wide",       new string[] { "library_header.*", "header.*" } },
            { "background", new string[] { "library_hero.*" } },
            { "logo",       new string[] { "logo.*" } },
        };

        // The official default asset a Steam-store game is currently showing:
        // Steam's local librarycache first (both layouts), then the CDNs.
        byte[] SteamDefaultBytes(uint appid, string typeKey)
        {
            try
            {
                string cache = Path.Combine(steamPath, "appcache", "librarycache");
                string flat = Path.Combine(cache, appid + CacheFlat[typeKey]);
                if (File.Exists(flat)) return File.ReadAllBytes(flat);
                string sub = Path.Combine(cache, appid.ToString());
                if (Directory.Exists(sub))
                    foreach (string pattern in CachePatterns[typeKey])
                    {
                        string[] found = Directory.GetFiles(sub, pattern, SearchOption.AllDirectories);
                        if (found.Length > 0) return File.ReadAllBytes(found[0]);
                    }
            }
            catch (Exception) { }
            foreach (string u in SteamStore.PreviewUrls((int)appid, typeKey))
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers["User-Agent"] = "SteamGridDBFetcher/1.0";
                        return wc.DownloadData(u);
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        async void LoadSteamDefaultInto(PictureBox pb, uint appid, string typeKey)
        {
            int g = gen;
            byte[] data = await Task.Run(() => SteamDefaultBytes(appid, typeKey));
            if (data == null || g != gen || pb.IsDisposed) return;
            try { pb.Image = Image.FromStream(new MemoryStream(data)); }
            catch (Exception) { }
        }

        // The game's original Steam asset, selectable like any other pick.
        // Only appears when the file actually exists on Steam's CDN.
        async void AddOfficialTile(AType t, FlowLayoutPanel flow, int steamId, int g)
        {
            byte[] data = null;
            foreach (string u in SteamStore.PreviewUrls(steamId, t.Key))
            {
                try { data = await Sgdb.Download(u); break; }
                catch (Exception) { }
            }
            if (data == null) return;   // no official asset of this type
            if (g != gen || flow.IsDisposed) return;
            Image img;
            try { img = Image.FromStream(new MemoryStream(data)); }
            catch (Exception) { return; }

            string selUrl = "official:" + steamId;
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = BG2,
                Margin = new Padding(4), Tag = selUrl
            };
            var pb = new PictureBox
            {
                Location = new Point(5, 5), Size = new Size(t.W, t.H - CapH),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD,
                Cursor = Cursors.Hand, Image = img
            };
            var caption = new Label
            {
                Text = "Steam default", ForeColor = OKC, BackColor = FIELD,
                Font = CapFont,
                Location = new Point(5, 5 + t.H - CapH), Size = new Size(t.W, CapH),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            p.Controls.Add(pb);
            p.Controls.Add(caption);
            string key = t.Key;
            EventHandler h = delegate { Stage(key, selUrl, "Steam default", p); };
            p.Click += h; pb.Click += h; caption.Click += h;
            flow.Controls.Add(p);
            flow.Controls.SetChildIndex(p, 1);   // right after the "current" tile
            tiles[key].Add(p);
            officialTiles[key] = p;
            UpdateHighlights(key);
        }

        Panel AddAssetTile(AType t, FlowLayoutPanel flow, SgdbAsset a)
        {
            string url = a.Url;
            var capBits = new List<string>();
            if (a.Animated) capBits.Add("animated");
            if (a.Nsfw) capBits.Add("adult");
            if (a.Humor) capBits.Add("humor");
            if (a.Epilepsy) capBits.Add("epilepsy");
            bool hasCap = capBits.Count > 0;

            var p = new Panel
            {
                // adult-tagged assets get a red border, like on the SGDB site
                Size = new Size(t.W + 10, t.H + 10), BackColor = a.Nsfw ? NSFWB : BG2,
                Margin = new Padding(4), Tag = url
            };
            var pb = new PictureBox
            {
                Location = new Point(5, 5), Size = new Size(t.W, hasCap ? t.H - CapH : t.H),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD, Cursor = Cursors.Hand
            };
            p.Controls.Add(pb);
            string key = t.Key;
            string label = a.Animated ? "community · animated" : "community";
            EventHandler h = delegate { Stage(key, url, label, p); };
            p.Click += h; pb.Click += h;
            if (hasCap)
            {
                var cap = new Label
                {
                    Text = string.Join(" · ", capBits), ForeColor = a.Animated ? ACC : WARN,
                    BackColor = FIELD, Font = CapFont,
                    Location = new Point(5, 5 + t.H - CapH), Size = new Size(t.W, CapH),
                    TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
                };
                p.Controls.Add(cap);
                cap.Click += h;
            }
            flow.Controls.Add(p);
            tiles[key].Add(p);
            tileAssets[p] = a;
            p.Visible = ShouldShow(a);
            return p;
        }

        // "Load more" tile at the end of a row when the server has more pages.
        void AddLoadMoreTile(AType t, int g)
        {
            int remaining = totalByType[t.Key] - shownByType[t.Key];
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = BG3,
                Margin = new Padding(4), Cursor = Cursors.Hand
            };
            var lbl = new Label
            {
                Text = "Load more\n(" + remaining + " left)", ForeColor = ACC, BackColor = BG3,
                Location = new Point(5, 5), Size = new Size(t.W, t.H),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            p.Controls.Add(lbl);
            EventHandler h = delegate { LoadMore(t, p, lbl, g); };
            p.Click += h; lbl.Click += h;
            flows[t.Key].Controls.Add(p);
        }

        async void LoadMore(AType t, Panel tile, Label lbl, int g)
        {
            if (g != gen || currentSgdbId <= 0) return;
            lbl.Text = "loading...";
            AssetPage pg;
            try { pg = await api.AssetsPaged(currentSgdbId, t, pageByType[t.Key] + 1); }
            catch (Exception) { lbl.Text = "failed -\nclick to retry"; return; }
            if (g != gen) return;
            pageByType[t.Key]++;
            var flow = flows[t.Key];
            flow.Controls.Remove(tile);
            tile.Dispose();
            foreach (SgdbAsset a in pg.Assets)
            {
                Panel tp = AddAssetTile(t, flow, a);
                LoadThumb((PictureBox)tp.Controls[0], a, g);
            }
            shownByType[t.Key] += pg.Assets.Count;
            if (pg.Assets.Count > 0 && shownByType[t.Key] < totalByType[t.Key])
                AddLoadMoreTile(t, g);
        }

        // ---------------------------------------------- animated previews

        class AnimClip
        {
            public readonly List<Image> Frames = new List<Image>();
            public readonly List<int> Delays = new List<int>();

            public void Dispose()
            {
                foreach (Image f in Frames) f.Dispose();
                Frames.Clear();
            }
        }

        class AnimEntry
        {
            public PictureBox Pb;
            public AnimClip Clip;
            public int Idx;
            public int NextAt;
        }

        readonly List<AnimEntry> anims = new List<AnimEntry>();

        void AnimTick()
        {
            int now = Environment.TickCount;
            for (int i = anims.Count - 1; i >= 0; i--)
            {
                AnimEntry a = anims[i];
                if (a.Pb.IsDisposed)
                {
                    anims.RemoveAt(i);
                    a.Clip.Dispose();
                    continue;
                }
                if (now - a.NextAt >= 0)
                {
                    a.Idx = (a.Idx + 1) % a.Clip.Frames.Count;
                    a.Pb.Image = a.Clip.Frames[a.Idx];
                    a.NextAt = now + a.Clip.Delays[a.Idx];
                }
            }
        }

        // Decode an animated webp/apng/gif into downscaled frames + delays
        // via WIC. Returns null when the format can't be decoded.
        static AnimClip DecodeClip(byte[] data, int maxW, int maxH)
        {
            try
            {
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new MemoryStream(data),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                int total = dec.Frames.Count;
                if (total == 0) return null;
                int step = Math.Max(1, (total + 23) / 24);   // keep <= 24 frames
                var clip = new AnimClip();
                for (int i = 0; i < total; i += step)
                {
                    var frame = dec.Frames[i];
                    System.Windows.Media.Imaging.BitmapSource src = frame;
                    double scale = Math.Min(1.0, Math.Min(
                        (double)maxW / frame.PixelWidth, (double)maxH / frame.PixelHeight));
                    if (scale < 1.0)
                        src = new System.Windows.Media.Imaging.TransformedBitmap(
                            frame, new System.Windows.Media.ScaleTransform(scale, scale));
                    clip.Frames.Add(WpfToBitmap(src));
                    int delay = 70;
                    try
                    {
                        var md = frame.Metadata as System.Windows.Media.Imaging.BitmapMetadata;
                        if (md != null)
                        {
                            object q = md.GetQuery("/ANMF/FrameDuration");
                            if (q != null) delay = Math.Max(20, Convert.ToInt32(q));
                        }
                    }
                    catch (Exception) { }
                    clip.Delays.Add(Math.Min(2000, delay * step));
                }
                return clip.Frames.Count > 0 ? clip : null;
            }
            catch (Exception) { return null; }
        }

        async void LoadThumb(PictureBox pb, SgdbAsset asset, int g)
        {
            // animated "thumbs" are webm videos, useless to an image decoder -
            // fetch the real asset file and animate it ourselves
            string url = asset.Animated ? asset.Url : asset.Thumb;
            byte[] data;
            await thumbSem.WaitAsync();
            try
            {
                if (g != gen) return;
                data = await Sgdb.Download(url);
            }
            catch (Exception) { return; }
            finally { thumbSem.Release(); }
            if (g != gen || pb.IsDisposed) return;

            if (asset.Animated)
            {
                int mw = Math.Max(64, pb.Width * 3 / 5);   // decode below display
                int mh = Math.Max(64, pb.Height * 3 / 5);  // size to save memory
                AnimClip clip = await Task.Run(() => DecodeClip(data, mw, mh));
                if (g != gen || pb.IsDisposed)
                {
                    if (clip != null) clip.Dispose();
                    return;
                }
                if (clip != null && clip.Frames.Count > 0)
                {
                    pb.Image = clip.Frames[0];
                    if (clip.Frames.Count > 1)
                        anims.Add(new AnimEntry
                        {
                            Pb = pb, Clip = clip,
                            NextAt = Environment.TickCount + clip.Delays[0]
                        });
                    else
                        clip.Frames.Clear();   // single frame: keep image, drop clip
                    MaybeUpdateHero(pb);
                    return;
                }
            }
            // decode off the UI thread so tiles never jank the window
            Image img = await Task.Run(delegate
            {
                try { return (Image)new Bitmap(new MemoryStream(data)); }
                catch (Exception) { return WicDecode(data); }
            });
            if (g != gen || pb.IsDisposed)
            {
                if (img != null) img.Dispose();
                return;
            }
            pb.Image = img != null ? img : TextThumb(pb.Width, pb.Height);
            MaybeUpdateHero(pb);
        }

        void MaybeUpdateHero(PictureBox pb)
        {
            Pick pk;
            if (staged.TryGetValue("cover", out pk) && pk.Tile != null && !pk.Tile.IsDisposed
                && pk.Tile.Controls.Count > 0 && ReferenceEquals(pk.Tile.Controls[0], pb))
                UpdateHero();
        }

        // Direct pixel copy from a WPF bitmap into a GDI+ bitmap - far cheaper
        // than the PNG encode/decode round trip per animation frame.
        static Bitmap WpfToBitmap(System.Windows.Media.Imaging.BitmapSource src)
        {
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var bmp = new Bitmap(conv.PixelWidth, conv.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                conv.CopyPixels(System.Windows.Int32Rect.Empty, bd.Scan0,
                                bd.Stride * bd.Height, bd.Stride);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        static Image WicDecode(byte[] data)
        {
            try
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new MemoryStream(data),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(frame);
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return Image.FromStream(new MemoryStream(ms.ToArray()));
                }
            }
            catch (Exception) { return null; }
        }

        static Image TextThumb(int w, int h)
        {
            var bmp = new Bitmap(Math.Max(16, w), Math.Max(16, h));
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(FIELD);
                TextRenderer.DrawText(g, "animated\n(no preview)", new Font("Segoe UI", 8f),
                    new Rectangle(0, 0, bmp.Width, bmp.Height), DIM,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            return bmp;
        }

        // -------------------------------------------------- filters & staging

        bool ShouldShow(SgdbAsset a)
        {
            bool typeOk = a.Animated ? cbAnimated.Checked : cbStatic.Checked;
            bool tagged = a.Nsfw || a.Humor || a.Epilepsy;
            bool tagOk = tagged
                ? (a.Nsfw && cbAdult.Checked) || (a.Humor && cbHumor.Checked) ||
                  (a.Epilepsy && cbEpilepsy.Checked)
                : cbUntagged.Checked;
            return typeOk && tagOk;
        }

        void ApplyAssetFilter()
        {
            foreach (var kv in tiles)
            {
                foreach (Panel p in kv.Value)
                {
                    SgdbAsset a;
                    if (!p.IsDisposed && tileAssets.TryGetValue(p, out a))
                        p.Visible = ShouldShow(a);
                }
            }
            // if a staged pick just got hidden, fall back to "current"
            foreach (string k in staged.Keys.ToList())
            {
                Pick pk = staged[k];
                if (pk.Tile != null && !pk.Tile.IsDisposed && !pk.Tile.Visible
                    && tileAssets.ContainsKey(pk.Tile))
                    Unstage(k);
            }
        }

        void Stage(string slotKey, string value, string label, Panel tile)
        {
            if (busy) return;
            staged[slotKey] = new Pick { Value = value, SourceLabel = label, Tile = tile };
            UpdateHighlights(slotKey);
            UpdateRail();
        }

        void Unstage(string slotKey)
        {
            if (busy) return;
            staged.Remove(slotKey);
            UpdateHighlights(slotKey);
            UpdateRail();
        }

        void UpdateHighlights(string slotKey)
        {
            List<Panel> list;
            if (!tiles.TryGetValue(slotKey, out list)) return;
            Pick pk;
            staged.TryGetValue(slotKey, out pk);
            Panel target = pk != null ? pk.Tile
                : currentTiles.ContainsKey(slotKey) ? currentTiles[slotKey] : null;
            foreach (Panel p in list)
                if (!p.IsDisposed)
                {
                    SgdbAsset a;
                    p.BackColor = tileAssets.TryGetValue(p, out a) && a.Nsfw ? NSFWB : BG2;
                }
            if (target != null && !target.IsDisposed) target.BackColor = ACC;
        }

        void RefreshAllHighlights()
        {
            foreach (AType t in Cfg.Types) UpdateHighlights(t.Key);
        }

        // ------------------------------------------------------ apply / undo

        async void ApplyStaged()
        {
            Shortcut game = currentShortcut;
            if (busy || game == null || staged.Count == 0) return;
            busy = true;
            UpdateButtons();
            SetRailStatus("Applying " + staged.Count + " change(s)...", false);
            var record = new List<UndoItem>();
            var written = new List<string>();
            try
            {
                foreach (var kv in staged.ToList())
                {
                    AType t = Cfg.Types.First(x => x.Key == kv.Key);
                    ApplyResult r;
                    if (kv.Value.Value.StartsWith("official:"))
                        r = await SteamStore.Apply(gridDir, game.AppId, t,
                                int.Parse(kv.Value.Value.Substring("official:".Length)), stamp);
                    else
                        r = await Artwork.Apply(gridDir, game.AppId, t, kv.Value.Value, stamp);
                    if (r != null)
                    {
                        record.Add(new UndoItem { Type = t, BackupPaths = r.BackupPaths });
                        written.Add(t.Short);
                    }
                }
                lastUndo = record;
                lastUndoApp = game.AppId;
                staged.Clear();
                applied.Add(game.AppId);
                RefreshGame(game.AppId);
                RefreshAllHighlights();
                UpdateRail();
                undoLink.Visible = record.Count > 0;
                SetRailStatus("Applied: " + string.Join(", ", written) +
                              ". Old files were backed up. Press F5 in your Steam library to see it.", true);
            }
            catch (Exception ex) { SetRailError("Apply failed: " + ex.Message); }
            finally { busy = false; UpdateButtons(); }
        }

        void UndoLast()
        {
            if (busy || lastUndo == null) return;
            try
            {
                foreach (UndoItem it in lastUndo)
                {
                    foreach (string ext in Cfg.ImageExts)
                    {
                        string f = Path.Combine(gridDir, lastUndoApp + it.Type.Suffix + ext);
                        if (File.Exists(f)) File.Delete(f);
                    }
                    foreach (string b in it.BackupPaths)
                        if (File.Exists(b))
                            File.Copy(b, Path.Combine(gridDir, Path.GetFileName(b)), true);
                }
                lastUndo = null;
                undoLink.Visible = false;
                applied.Remove(lastUndoApp);
                RefreshGame(lastUndoApp);
                UpdateRail();
                SetRailStatus("Restored the previous artwork from backup.", false);
            }
            catch (Exception ex) { SetRailError("Undo failed: " + ex.Message); }
        }

        // Stage picks for every empty slot: the Steam default when available,
        // otherwise the top visible community asset. Nothing is written until
        // Apply is pressed.
        void AutoFillEmpty()
        {
            if (busy || currentShortcut == null) return;
            bool[] slots = GetSlots(currentShortcut.AppId);
            int n = 0;
            for (int i = 0; i < Cfg.Types.Length; i++)
            {
                AType t = Cfg.Types[i];
                if (slots[i] || staged.ContainsKey(t.Key)) continue;
                Panel off;
                if (officialTiles.TryGetValue(t.Key, out off) && !off.IsDisposed)
                {
                    Stage(t.Key, (string)off.Tag, "Steam default", off);
                    n++;
                    continue;
                }
                List<Panel> list;
                if (!tiles.TryGetValue(t.Key, out list)) continue;
                Panel first = list.FirstOrDefault(p => !p.IsDisposed && p.Visible && tileAssets.ContainsKey(p));
                if (first != null)
                {
                    SgdbAsset a = tileAssets[first];
                    Stage(t.Key, a.Url, a.Animated ? "community · animated" : "community", first);
                    n++;
                }
            }
            SetRailStatus(n == 0
                ? "Nothing to fill - every slot has artwork or a staged pick."
                : "Staged " + n + " pick(s) for the empty slots - review and press Apply.", false);
        }
    }

    // ------------------------------------- mouse wheel -> control under cursor

    class WheelRedirector : IMessageFilter
    {
        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x20A) return false;  // WM_MOUSEWHEEL
            IntPtr h = WindowFromPoint(Control.MousePosition);
            if (h == IntPtr.Zero) return false;
            Control c = Control.FromChildHandle(h);
            while (c != null && !(c is ScrollableControl && ((ScrollableControl)c).AutoScroll))
                c = c.Parent;
            if (c == null || c is Form || c.Handle == m.HWnd) return false;
            SendMessage(c.Handle, 0x20A, m.WParam, m.LParam);
            return true;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // default is 2 connections per host, which serializes every
            // thumbnail/asset download behind two sockets
            ServicePointManager.DefaultConnectionLimit = 16;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.AddMessageFilter(new WheelRedirector());
            Application.Run(new MainForm());
        }
    }
}
