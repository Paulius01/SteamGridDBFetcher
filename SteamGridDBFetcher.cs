// SteamGridDB Fetcher - native Windows app (WinForms, .NET Framework 4.8)
//
// Applies SteamGridDB artwork (Cover, Wide Cover, Background, Logo) to the
// non-Steam games in your Steam library.
//
// Safety:
//   * Reads shortcuts.vdf READ-ONLY to find your games.
//   * Only ever writes image files into <Steam>\userdata\<you>\config\grid\ -
//     the same files Steam's own "Change" artwork button creates.
//   * Never modifies shortcuts.vdf, game files, the Steam client or any
//     process. The images are only ever loaded by the Steam UI, never by a
//     game, so anti-cheat is never involved.
//   * Replaced artwork is backed up to .\backups\<timestamp>\ first.
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
        public string Key, Endpoint, Query, Suffix, Label;
        public int W, H;   // thumbnail box
    }

    static class Cfg
    {
        public static readonly AType[] Types = new AType[]
        {
            // nsfw/humor/epilepsy=any: show everything, the API hides flagged
            // assets by default
            new AType { Key = "cover",      Endpoint = "grids",  Query = "?dimensions=600x900&types=static,animated&nsfw=any&humor=any&epilepsy=any",         Suffix = "p",     Label = "Cover (600x900)",      W = 220, H = 330 },
            new AType { Key = "wide",       Endpoint = "grids",  Query = "?dimensions=920x430,460x215&types=static,animated&nsfw=any&humor=any&epilepsy=any", Suffix = "",      Label = "Wide Cover (920x430)", W = 430, H = 201 },
            new AType { Key = "background", Endpoint = "heroes", Query = "?types=static,animated&nsfw=any&humor=any&epilepsy=any",                            Suffix = "_hero", Label = "Background (hero)",    W = 480, H = 155 },
            new AType { Key = "logo",       Endpoint = "logos",  Query = "?types=static,animated&nsfw=any&humor=any&epilepsy=any",                            Suffix = "_logo", Label = "Logo",                 W = 300, H = 150 },
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
            string spaced = Regex.Replace(name, @"[_\-\.]+", " ");
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
                object id, name;
                if (g.TryGetValue("id", out id) && g.TryGetValue("name", out name))
                    list.Add(new SgdbGame { Id = Convert.ToInt32(id), Name = Convert.ToString(name) });
            }
            return list;
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
        // The default artwork files Steam itself uses for every store game.
        static readonly Dictionary<string, string[]> Files = new Dictionary<string, string[]>
        {
            { "cover",      new string[] { "library_600x900_2x.jpg", "library_600x900.jpg" } },
            { "wide",       new string[] { "header.jpg" } },
            { "background", new string[] { "library_hero_2x.jpg", "library_hero.jpg" } },
            { "logo",       new string[] { "logo_2x.png", "logo.png" } },
        };

        // Valve serves official art from two CDN layouts; newer titles often
        // exist only on the second one.
        public static readonly string[] CdnBases = new string[]
        {
            "https://cdn.cloudflare.steamstatic.com/steam/apps/",
            "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/",
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

        // Apply the official Steam default for one asset type. False if unavailable.
        public static async Task<bool> Apply(string gdir, uint appid, AType t, int steamId, string stamp)
        {
            foreach (string f in Files[t.Key])
                foreach (string cdn in CdnBases)
                {
                    try
                    {
                        await Artwork.Apply(gdir, appid, t, cdn + steamId + "/" + f, stamp);
                        return true;
                    }
                    catch (Exception) { }
                }
            return false;
        }
    }

    // ------------------------------------------------------- artwork writing

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
        public static async Task<string> Apply(string gdir, uint appid, AType t, string url, string stamp)
        {
            byte[] data = await Sgdb.Download(url);
            string target = Path.Combine(gdir, appid + t.Suffix + ExtFromUrl(url));

            string bdir = Path.Combine(Cfg.BackupRoot, stamp);
            foreach (string ext in Cfg.ImageExts)
            {
                string old = Path.Combine(gdir, appid + t.Suffix + ext);
                if (File.Exists(old))
                {
                    Directory.CreateDirectory(bdir);
                    string bak = Path.Combine(bdir, Path.GetFileName(old));
                    if (!File.Exists(bak)) File.Copy(old, bak);
                    File.Delete(old);
                }
            }
            File.WriteAllBytes(target, data);
            return Path.GetFileName(target);
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
            // hide bars whenever layout/paint/scroll would show them (SB_BOTH=3)
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

    class GameTile
    {
        public Panel Root;
        public PictureBox Pic;
        public Label Name;
        public Label Status;
    }

    class MainForm : Form
    {
        // slate neutral ramp (one step per elevation) + Steam-blue accent
        static readonly Color BG = ColorTranslator.FromHtml("#0F1522");      // window
        static readonly Color PANEL = ColorTranslator.FromHtml("#1B2434");   // bars, cards
        static readonly Color PANEL2 = ColorTranslator.FromHtml("#28344A");  // hover / raised
        static readonly Color FIELD = ColorTranslator.FromHtml("#0B111C");   // inputs, status bar
        static readonly Color BORDER = ColorTranslator.FromHtml("#2E3A50");  // 1px separators
        static readonly Color TX = ColorTranslator.FromHtml("#E2E8F0");
        static readonly Color DIM = ColorTranslator.FromHtml("#94A3B8");
        static readonly Color ACC = ColorTranslator.FromHtml("#66C0F4");     // interactive/selected only
        static readonly Color OKC = ColorTranslator.FromHtml("#5FCB71");
        static readonly Color ERRC = ColorTranslator.FromHtml("#F07878");
        static readonly Color WARN = ColorTranslator.FromHtml("#E8B44C");

        const int CoverW = 220, CoverH = 330;

        Dictionary<string, object> cfg;
        Sgdb api;
        string steamPath, userId, gridDir, stamp;
        List<Shortcut> shortcuts;

        // library view
        Panel libraryView;
        FlowLayoutPanel libraryFlow;
        Label profileLabel;
        Button autoAllBtn, refreshBtn, filterBtn;
        TextBox filterBox;
        CheckBox steamBox;
        List<Shortcut> steamGames = new List<Shortcut>();
        int libGen;   // invalidates in-flight Steam cover downloads on rebuild
        readonly Dictionary<uint, GameTile> gameTiles = new Dictionary<uint, GameTile>();
        readonly Dictionary<uint, Image> coverImages = new Dictionary<uint, Image>();
        readonly Dictionary<uint, Image> placeholders = new Dictionary<uint, Image>();
        readonly Dictionary<uint, Image> steamCoverCache = new Dictionary<uint, Image>();

        IEnumerable<Shortcut> DisplayGames
        {
            get { return steamGames.Count > 0 ? shortcuts.Concat(steamGames) : (IEnumerable<Shortcut>)shortcuts; }
        }

        // picker view
        Panel pickerView;
        Panel pickerHeader;
        Button backBtn, searchBtn, applyBtn, autoBtn;
        TextBox searchBox;
        ComboBox matchCombo;
        Label selLabel, gameTitle;
        Panel contentPanel;
        FlowLayoutPanel sectionsFlow;
        CheckBox cbStatic, cbAnimated, cbHumor, cbAdult, cbEpilepsy, cbUntagged;
        bool suppressFilter;
        readonly Dictionary<Panel, SgdbAsset> tileAssets = new Dictionary<Panel, SgdbAsset>();

        Label statusLabel;

        readonly Dictionary<string, FlowLayoutPanel> flows = new Dictionary<string, FlowLayoutPanel>();
        readonly Dictionary<string, Label> countLabels = new Dictionary<string, Label>();
        readonly Dictionary<string, int> pageByType = new Dictionary<string, int>();
        readonly Dictionary<string, int> totalByType = new Dictionary<string, int>();
        readonly Dictionary<string, int> shownByType = new Dictionary<string, int>();
        readonly Dictionary<string, List<Panel>> tiles = new Dictionary<string, List<Panel>>();
        readonly Dictionary<string, string> sel = new Dictionary<string, string>();
        readonly Dictionary<string, Label> selBadges = new Dictionary<string, Label>();
        readonly HashSet<uint> applied = new HashSet<uint>();
        readonly List<SgdbGame> matches = new List<SgdbGame>();
        readonly SemaphoreSlim thumbSem = new SemaphoreSlim(6);

        Shortcut currentShortcut;
        int gen;                 // invalidates in-flight loads
        int currentSgdbId = -1;
        bool busy, suppressMatch, suppressSteamBox;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public MainForm()
        {
            Text = "SteamGridDB Fetcher";
            BackColor = BG;
            ForeColor = TX;
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;   // scale all fixed sizes with display DPI
            ClientSize = new Size(1280, 840);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            BuildUi();
            Shown += OnShownAsync;
            FormClosing += SaveWindowState;
        }

        // remember window size between runs (saved in config.json)
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); }  // dark title bar
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

                suppressSteamBox = true;
                steamBox.Checked = Cfg.Int(cfg, "show_steam", 0) == 1;
                suppressSteamBox = false;
                LoadFilters();
                ReloadLibrary();
                UpdateButtons();   // enables Auto-apply ALL right away
                SetStatus("Ready. Click a game to pick its artwork.", DIM);
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
                f.BackColor = BG; f.ForeColor = TX; f.Font = Font;
                f.AutoScaleDimensions = new SizeF(96f, 96f);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.ClientSize = new Size(460, 150);
                f.StartPosition = FormStartPosition.CenterParent;

                var lbl = new Label
                {
                    Text = "Paste your SteamGridDB API key.\nGet one free at:  steamgriddb.com -> Profile -> Preferences -> API",
                    Location = new Point(14, 12), Size = new Size(430, 40), ForeColor = TX
                };
                var box = new TextBox
                {
                    Location = new Point(14, 60), Size = new Size(430, 26),
                    BackColor = FIELD, ForeColor = TX, BorderStyle = BorderStyle.FixedSingle
                };
                var ok = MakeButton("OK", true);
                ok.Location = new Point(254, 104); ok.Size = new Size(90, 30); ok.AutoSize = false;
                ok.DialogResult = DialogResult.OK;
                var cancel = MakeButton("Cancel", false);
                cancel.Location = new Point(354, 104); cancel.Size = new Size(90, 30); cancel.AutoSize = false;
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(lbl); f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        // ---------------------------------------------------------------- ui

        Button MakeButton(string text, bool accent)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? ACC : PANEL2,
                ForeColor = accent ? ColorTranslator.FromHtml("#06121C") : TX,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 7, 14, 7)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = accent ? ColorTranslator.FromHtml("#8AD0F8") : ColorTranslator.FromHtml("#324058");
            b.FlatAppearance.MouseDownBackColor = accent ? ColorTranslator.FromHtml("#4FAEE8") : ColorTranslator.FromHtml("#1F2A3D");
            return b;
        }

        void BuildUi()
        {
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 37, BackColor = FIELD };
            statusLabel = new Label
            {
                Dock = DockStyle.Fill, BackColor = FIELD, ForeColor = DIM,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0),
                Text = "Starting..."
            };
            statusBar.Controls.Add(statusLabel);
            statusBar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = BORDER });
            Controls.Add(statusBar);

            // ------------------------------------------------- library view
            libraryView = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            Controls.Add(libraryView);
            libraryView.BringToFront();

            var topBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = PANEL };
            libraryView.Controls.Add(topBar);

            var title = new Label
            {
                Text = "SteamGridDB Fetcher", ForeColor = TX, BackColor = PANEL,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Location = new Point(16, 10), AutoSize = true
            };
            topBar.Controls.Add(title);

            profileLabel = new Label
            {
                Text = "", ForeColor = DIM, BackColor = PANEL,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(16, 38), AutoSize = true
            };
            topBar.Controls.Add(profileLabel);

            var tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, BackColor = PANEL,
                Padding = new Padding(0, 15, 16, 0)
            };
            topBar.Controls.Add(tools);
            topBar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = BORDER });

            steamBox = new CheckBox
            {
                Text = "Show Steam games", ForeColor = TX, BackColor = PANEL,
                AutoSize = true, Margin = new Padding(0, 8, 12, 0), Cursor = Cursors.Hand
            };
            steamBox.CheckedChanged += delegate
            {
                if (suppressSteamBox) return;
                cfg["show_steam"] = steamBox.Checked ? 1 : 0;
                Cfg.Save(cfg);
                RefreshLibrary();
            };
            tools.Controls.Add(steamBox);

            filterBox = new TextBox
            {
                Width = 210, BackColor = FIELD, ForeColor = TX,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f),
                Margin = new Padding(0, 5, 8, 0)
            };
            filterBox.TextChanged += delegate { ApplyFilter(); };
            filterBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFilter(); }
            };
            tools.Controls.Add(filterBox);

            filterBtn = MakeButton("Search", false);
            filterBtn.Margin = new Padding(0, 0, 8, 0);
            filterBtn.Click += delegate { ApplyFilter(); };
            tools.Controls.Add(filterBtn);

            refreshBtn = MakeButton("Refresh", false);
            refreshBtn.Margin = new Padding(0, 0, 8, 0);
            refreshBtn.Click += delegate { RefreshLibrary(); };
            tools.Controls.Add(refreshBtn);

            autoAllBtn = MakeButton("Auto-apply ALL games", false);
            autoAllBtn.Margin = new Padding(0);
            autoAllBtn.Click += delegate { AutoAll(); };
            tools.Controls.Add(autoAllBtn);

            libraryFlow = new BareFlowPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = BG,
                Padding = new Padding(16, 12, 16, 12)
            };
            libraryView.Controls.Add(libraryFlow);
            libraryFlow.BringToFront();

            // -------------------------------------------------- picker view
            pickerView = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };
            Controls.Add(pickerView);
            pickerView.BringToFront();

            // header: every picker control lives in one auto-sized docked
            // stack, so rows grow with content and nothing can overlap
            pickerHeader = new Panel
            {
                Dock = DockStyle.Top, BackColor = PANEL,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            pickerView.Controls.Add(pickerHeader);

            // border first: added earlier = docked later = below the stack
            pickerHeader.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = BORDER });

            var headerStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = PANEL, ColumnCount = 1, Padding = new Padding(16, 10, 16, 12)
            };
            headerStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pickerHeader.Controls.Add(headerStack);

            var titleRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = PANEL, Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            headerStack.Controls.Add(titleRow);

            backBtn = MakeButton("<   Library", false);
            backBtn.Margin = new Padding(0, 0, 12, 0);
            backBtn.Click += delegate { BackToLibrary(); };
            titleRow.Controls.Add(backBtn);

            gameTitle = new Label
            {
                Text = "", ForeColor = TX, BackColor = PANEL,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                AutoSize = true, Margin = new Padding(0, 5, 0, 0)
            };
            titleRow.Controls.Add(gameTitle);

            var searchRow = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, BackColor = PANEL, ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 0)
            };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerStack.Controls.Add(searchRow);

            searchBox = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                BackColor = FIELD, ForeColor = TX, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f), Margin = new Padding(0, 0, 8, 0)
            };
            searchBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); }
            };
            searchRow.Controls.Add(searchBox, 0, 0);

            searchBtn = MakeButton("Search", true);
            searchBtn.Margin = new Padding(0);
            searchBtn.Click += delegate { DoSearch(); };
            searchRow.Controls.Add(searchBtn, 1, 0);

            var matchRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, BackColor = PANEL, Dock = DockStyle.Fill,
                Margin = new Padding(0, 8, 0, 0)
            };
            headerStack.Controls.Add(matchRow);

            var matchLbl = new Label
            {
                Text = "Match:", ForeColor = DIM, BackColor = PANEL,
                AutoSize = true, Margin = new Padding(0, 6, 8, 0)
            };
            matchRow.Controls.Add(matchLbl);

            matchCombo = new ComboBox
            {
                Size = new Size(420, 26),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = PANEL2, ForeColor = TX, Margin = new Padding(0)
            };
            matchCombo.SelectedIndexChanged += delegate
            {
                if (suppressMatch || busy || matchCombo.SelectedIndex < 0) return;
                if (matchCombo.SelectedIndex < matches.Count)
                    LoadAssets(matches[matchCombo.SelectedIndex].Id, matches[matchCombo.SelectedIndex].Name);
            };
            matchRow.Controls.Add(matchCombo);

            // type/tag filters, mirroring the SteamGridDB site (all on = show everything)
            var filterRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = PANEL, Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0)
            };
            headerStack.Controls.Add(filterRow);

            filterRow.Controls.Add(FilterHeading("Types", 0));
            cbStatic = MakeCheck("Static");
            cbAnimated = MakeCheck("Animated");
            filterRow.Controls.Add(cbStatic);
            filterRow.Controls.Add(cbAnimated);
            filterRow.Controls.Add(FilterHeading("Tags", 16));
            cbHumor = MakeCheck("Humor");
            cbAdult = MakeCheck("Adult Content");
            cbEpilepsy = MakeCheck("Epilepsy");
            cbUntagged = MakeCheck("Untagged");
            filterRow.Controls.Add(cbHumor);
            filterRow.Controls.Add(cbAdult);
            filterRow.Controls.Add(cbEpilepsy);
            filterRow.Controls.Add(cbUntagged);

            var allBtn = MakeButton("All", false);
            allBtn.Margin = new Padding(16, 0, 0, 0);
            allBtn.Click += delegate
            {
                suppressFilter = true;
                foreach (CheckBox c in AllFilterBoxes()) c.Checked = true;
                suppressFilter = false;
                SaveFilters();
                ApplyAssetFilter();
            };
            filterRow.Controls.Add(allBtn);
            foreach (CheckBox c in AllFilterBoxes())
                c.CheckedChanged += delegate
                {
                    if (suppressFilter) return;
                    SaveFilters();
                    ApplyAssetFilter();
                };

            var actionRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = PANEL, Dock = DockStyle.Fill,
                Margin = new Padding(0, 12, 0, 0)
            };
            headerStack.Controls.Add(actionRow);

            applyBtn = MakeButton("Apply selected", true);
            applyBtn.Margin = new Padding(0, 0, 8, 0);
            applyBtn.Click += delegate { ApplySelected(); };
            actionRow.Controls.Add(applyBtn);

            autoBtn = MakeButton("Auto (top picks)", false);
            autoBtn.Margin = new Padding(0, 0, 12, 0);
            autoBtn.Click += delegate { AutoOne(); };
            actionRow.Controls.Add(autoBtn);

            selLabel = new Label
            {
                Text = "", ForeColor = DIM, BackColor = PANEL,
                AutoSize = true, Margin = new Padding(0, 8, 0, 0)
            };
            actionRow.Controls.Add(selLabel);

            contentPanel = new BarePanel
            {
                Dock = DockStyle.Fill, BackColor = BG, AutoScroll = true
            };
            pickerView.Controls.Add(contentPanel);
            contentPanel.BringToFront();

            sectionsFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BG, Location = new Point(16, 4)
            };
            contentPanel.Controls.Add(sectionsFlow);
            contentPanel.Resize += delegate { UpdateFlowWidths(); };

            var animTimer = new System.Windows.Forms.Timer { Interval = 30 };
            animTimer.Tick += delegate { AnimTick(); };
            animTimer.Start();

            UpdateButtons();
        }

        Label FilterHeading(string text, int leftGap)
        {
            return new Label
            {
                Text = text, ForeColor = DIM, BackColor = PANEL, AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Margin = new Padding(leftGap, 7, 10, 0)
            };
        }

        CheckBox MakeCheck(string text)
        {
            return new CheckBox
            {
                Text = text, Checked = true, AutoSize = true,
                ForeColor = TX, BackColor = PANEL, Cursor = Cursors.Hand,
                Margin = new Padding(0, 4, 12, 0)
            };
        }

        CheckBox[] AllFilterBoxes()
        {
            return new CheckBox[] { cbStatic, cbAnimated, cbHumor, cbAdult, cbEpilepsy, cbUntagged };
        }

        // the type/tag filters are global and persist between runs (config.json)
        static readonly string[] FilterKeys = new string[]
            { "f_static", "f_animated", "f_humor", "f_adult", "f_epilepsy", "f_untagged" };

        void LoadFilters()
        {
            suppressFilter = true;
            CheckBox[] boxes = AllFilterBoxes();
            for (int i = 0; i < boxes.Length; i++)
                boxes[i].Checked = Cfg.Int(cfg, FilterKeys[i], 1) == 1;
            suppressFilter = false;
        }

        void SaveFilters()
        {
            if (cfg == null) return;
            CheckBox[] boxes = AllFilterBoxes();
            for (int i = 0; i < boxes.Length; i++)
                cfg[FilterKeys[i]] = boxes[i].Checked ? 1 : 0;
            Cfg.Save(cfg);
        }

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

        // Show/hide asset tiles per the type/tag filters (current, Steam
        // default and Load-more tiles always stay visible).
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
                // if the selected pick just got hidden, fall back to "current"
                string selUrl;
                if (sel.TryGetValue(kv.Key, out selUrl))
                {
                    Panel selTile = kv.Value.FirstOrDefault(
                        p => !p.IsDisposed && (p.Tag as string) == selUrl);
                    if (selTile != null && !selTile.Visible && kv.Value.Count > 0)
                        SelectTile(kv.Key, kv.Value[0], null);
                }
            }
        }

        void UpdateFlowWidths()
        {
            int w = Math.Max(300, contentPanel.ClientSize.Width - 48);
            foreach (var f in flows.Values) f.MaximumSize = new Size(w, 0);
        }

        void SetStatus(string text, Color c)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = c;
        }

        void UpdateButtons()
        {
            applyBtn.Enabled = !busy && sel.Count > 0;
            autoBtn.Enabled = !busy && currentSgdbId > 0;
            autoAllBtn.Enabled = !busy && shortcuts != null;
            refreshBtn.Enabled = !busy && shortcuts != null;
            searchBtn.Enabled = !busy;
            backBtn.Enabled = !busy;
            matchCombo.Enabled = !busy;
            selLabel.Text = sel.Count > 0 ? sel.Count + " change(s) selected" : "";
        }

        // ------------------------------------------------- library (grid) view

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

        // Fetch a Steam-store game's own library cover from the CDN in the
        // background (session-cached), so store games show their real art.
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

                    // Steam's local library cache first: it's exactly the art
                    // Steam itself shows, works offline, and covers titles the
                    // public CDNs are missing.
                    string cache = Path.Combine(steamPath, "appcache", "librarycache");
                    var local = new List<string>
                    {
                        Path.Combine(cache, appid + "_library_600x900.jpg"),
                        Path.Combine(cache, appid.ToString(), "library_600x900.jpg"),
                    };
                    try
                    {
                        string sub = Path.Combine(cache, appid.ToString());
                        if (Directory.Exists(sub))
                            local.AddRange(Directory.GetFiles(sub, "library_600x900*"));
                    }
                    catch (Exception) { }
                    foreach (string lc in local)
                        if (File.Exists(lc))
                        {
                            try { data = File.ReadAllBytes(lc); break; }
                            catch (Exception) { }
                        }

                    // Newer Steam cache layout: librarycache/{appid}/{hash}/
                    // subfolders, each holding a properly named image. Search
                    // recursively; some titles (demos) only have the store
                    // capsule instead of a library capsule.
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
                        Shortcut cur = DisplayGames.FirstOrDefault(x => x.AppId == appid);
                        if (cur != null) UpdateTile(cur);
                    });
                }
                catch (Exception) { bmp.Dispose(); }   // window closed
            });
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
                    ColorTranslator.FromHtml("#3E4E6B"), ColorTranslator.FromHtml("#1B2434"), 65f))
                    g.FillRectangle(lg, 0, 0, CoverW, CoverH);
                TextRenderer.DrawText(g, sc.Name, new Font("Segoe UI", 10f),
                    new Rectangle(10, 10, CoverW - 20, CoverH - 20),
                    ColorTranslator.FromHtml("#c7d0da"),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak);
            }
            placeholders[sc.AppId] = bmp;
            return bmp;
        }

        // Re-reads shortcuts.vdf (and installed Steam games when enabled)
        // from disk and rebuilds the whole grid.
        void ReloadLibrary()
        {
            libGen++;
            shortcuts = Steam.LoadShortcuts(steamPath, Cfg.Str(cfg, "user_id"), out userId);
            steamGames = steamBox.Checked ? Steam.LoadSteamGames(steamPath) : new List<Shortcut>();
            gridDir = Artwork.GridDir(steamPath, userId);

            var old = libraryFlow.Controls.Cast<Control>().ToList();
            libraryFlow.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            gameTiles.Clear();
            foreach (Image img in coverImages.Values) img.Dispose();
            coverImages.Clear();
            foreach (Image img in placeholders.Values) img.Dispose();
            placeholders.Clear();

            foreach (Shortcut sc in DisplayGames)
            {
                LoadCoverImage(sc.AppId);
                if (sc.IsSteam && !coverImages.ContainsKey(sc.AppId))
                    QueueSteamCover(sc);   // show Steam's own cover for store games
            }
            BuildLibrary();
            UpdateProfileLabel();
            ApplyFilter();
        }

        void RefreshLibrary()
        {
            if (busy) return;
            try
            {
                ReloadLibrary();
                SetStatus("Refreshed - " + shortcuts.Count + " games. (If a just-added " +
                          "shortcut is missing, restart Steam: it can hold shortcuts.vdf " +
                          "in memory until it exits.)", OKC);
            }
            catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message, ERRC); }
        }

        void ApplyFilter()
        {
            if (shortcuts == null || filterBox == null) return;
            string f = filterBox.Text.Trim();
            foreach (Shortcut sc in DisplayGames)
            {
                GameTile t;
                if (gameTiles.TryGetValue(sc.AppId, out t))
                    t.Root.Visible = f.Length == 0 ||
                        sc.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        void BuildLibrary()
        {
            libraryFlow.SuspendLayout();
            foreach (Shortcut sc in DisplayGames)
            {
                var tile = new GameTile();
                tile.Root = new Panel
                {
                    Size = new Size(CoverW + 8, CoverH + 48), BackColor = PANEL,
                    Margin = new Padding(8), Cursor = Cursors.Hand
                };
                tile.Pic = new PictureBox
                {
                    Location = new Point(4, 4), Size = new Size(CoverW, CoverH),
                    SizeMode = PictureBoxSizeMode.StretchImage, BackColor = PANEL,
                    Cursor = Cursors.Hand
                };
                tile.Name = new Label
                {
                    Location = new Point(4, CoverH + 10), Size = new Size(CoverW, 17),
                    ForeColor = TX, BackColor = PANEL, AutoEllipsis = true, Text = sc.Name,
                    Cursor = Cursors.Hand
                };
                tile.Status = new Label
                {
                    Location = new Point(4, CoverH + 28), Size = new Size(CoverW, 16),
                    ForeColor = DIM, BackColor = PANEL, Font = new Font("Segoe UI", 8.25f),
                    Cursor = Cursors.Hand
                };
                tile.Root.Controls.Add(tile.Pic);
                tile.Root.Controls.Add(tile.Name);
                tile.Root.Controls.Add(tile.Status);

                Shortcut captured = sc;
                EventHandler click = delegate { if (!busy) OpenPicker(captured); };
                tile.Root.Click += click; tile.Pic.Click += click;
                tile.Name.Click += click; tile.Status.Click += click;

                GameTile t = tile;
                EventHandler enter = delegate { t.Root.BackColor = PANEL2; t.Name.BackColor = PANEL2; t.Status.BackColor = PANEL2; };
                EventHandler leave = delegate { t.Root.BackColor = PANEL; t.Name.BackColor = PANEL; t.Status.BackColor = PANEL; };
                foreach (Control c in new Control[] { tile.Root, tile.Pic, tile.Name, tile.Status })
                {
                    c.MouseEnter += enter;
                    c.MouseLeave += leave;
                }

                gameTiles[sc.AppId] = tile;
                libraryFlow.Controls.Add(tile.Root);
                UpdateTile(sc);
            }
            libraryFlow.ResumeLayout();
        }

        void UpdateTile(Shortcut sc)
        {
            GameTile t;
            if (!gameTiles.TryGetValue(sc.AppId, out t)) return;
            Image cover;
            if (!coverImages.TryGetValue(sc.AppId, out cover) && sc.IsSteam)
                steamCoverCache.TryGetValue(sc.AppId, out cover);
            t.Pic.Image = cover != null ? cover : GetPlaceholder(sc);
            bool isApplied = applied.Contains(sc.AppId);
            t.Name.ForeColor = isApplied ? OKC : TX;
            if (isApplied) { t.Status.Text = "updated"; t.Status.ForeColor = OKC; }
            else if (sc.IsSteam) { t.Status.Text = "Steam"; t.Status.ForeColor = DIM; }
            else if (cover == null) { t.Status.Text = "missing artwork"; t.Status.ForeColor = WARN; }
            else { t.Status.Text = ""; }
        }

        void UpdateProfileLabel()
        {
            if (shortcuts == null) return;
            int missing = shortcuts.Count(sc => !coverImages.ContainsKey(sc.AppId));
            profileLabel.Text = "Profile " + userId + "   -   " + shortcuts.Count + " non-Steam games"
                + (steamGames.Count > 0 ? " + " + steamGames.Count + " Steam games" : "")
                + (missing > 0 ? "   -   " + missing + " without cover art" : "   -   all covered");
        }

        void RefreshGame(uint appid)
        {
            LoadCoverImage(appid);
            Shortcut sc = DisplayGames.FirstOrDefault(x => x.AppId == appid);
            if (sc != null) UpdateTile(sc);
            UpdateProfileLabel();
        }

        // ------------------------------------------------- view switching

        void OpenPicker(Shortcut sc)
        {
            currentShortcut = sc;
            gameTitle.Text = sc.Name;
            libraryView.Visible = false;
            pickerView.Visible = true;
            // layout is deferred while the view is hidden; without this the
            // auto-sized header keeps a stale height and the content docks
            // below it, leaving a dead gap until the next layout event
            pickerHeader.PerformLayout();
            pickerView.PerformLayout();
            searchBox.Text = sc.Name;
            DoSearch();
        }

        void BackToLibrary()
        {
            if (busy) return;
            gen++;   // cancel any in-flight loads
            currentSgdbId = -1;
            sel.Clear();
            ClearSections();
            pickerView.Visible = false;
            libraryView.Visible = true;
            UpdateButtons();
            SetStatus("Ready. Click a game to pick its artwork.", DIM);
        }

        // -------------------------------------------- existing-artwork helpers

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

        // ------------------------------------------------------ picker: search

        void ClearSections()
        {
            flows.Clear(); countLabels.Clear(); tiles.Clear(); tileAssets.Clear();
            selBadges.Clear();   // badges are children of the disposed tiles
            var old = sectionsFlow.Controls.Cast<Control>().ToList();
            sectionsFlow.Controls.Clear();
            foreach (Control c in old) c.Dispose();
        }

        void ShowPlaceholder(string text)
        {
            ClearSections();
            sectionsFlow.Controls.Add(new Label
            {
                Text = text, ForeColor = DIM, BackColor = BG, AutoSize = true,
                Margin = new Padding(6, 30, 0, 0), Font = new Font("Segoe UI", 10f)
            });
        }

        async void DoSearch()
        {
            string term = searchBox.Text.Trim();
            if (term.Length == 0 || busy || api == null) return;
            gen++;
            int g = gen;
            sel.Clear();
            currentSgdbId = -1;
            UpdateButtons();
            ShowPlaceholder("Searching SteamGridDB...");
            SetStatus("Searching \"" + term + "\"...", DIM);
            List<SgdbGame> results;
            string usedTerm;
            try
            {
                var smart = await api.SearchSmart(term);
                usedTerm = smart.Key;
                results = smart.Value;
            }
            catch (Exception ex) { SetStatus("Search failed: " + ex.Message, ERRC); return; }
            if (g != gen) return;

            if (results.Count > 0 && usedTerm != term)
            {
                searchBox.Text = usedTerm;   // show the term that actually matched
                term = usedTerm;
            }

            matches.Clear();
            matches.AddRange(results);
            suppressMatch = true;
            matchCombo.Items.Clear();
            foreach (SgdbGame m in matches) matchCombo.Items.Add(m.Name);
            suppressMatch = false;

            if (matches.Count == 0)
            {
                ShowPlaceholder("No SteamGridDB match. Try a different search term.");
                SetStatus("No results for \"" + term + "\".", ERRC);
                return;
            }
            suppressMatch = true;
            matchCombo.SelectedIndex = 0;
            suppressMatch = false;
            SetStatus(matches.Count + " match(es).", OKC);
            LoadAssets(matches[0].Id, matches[0].Name);
        }

        async void LoadAssets(int gameId, string gameName)
        {
            gen++;
            int g = gen;
            currentSgdbId = gameId;
            sel.Clear();
            UpdateButtons();

            Shortcut game = currentShortcut;
            var hasExisting = new Dictionary<string, bool>();
            ClearSections();
            foreach (AType t in Cfg.Types)
            {
                sectionsFlow.Controls.Add(new Label
                {
                    Text = t.Label, ForeColor = TX, BackColor = BG, AutoSize = true,
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    Margin = new Padding(4, 16, 0, 0)
                });
                var cnt = new Label
                {
                    Text = "loading...", ForeColor = DIM, BackColor = BG, AutoSize = true,
                    Font = new Font("Segoe UI", 8.25f), Margin = new Padding(5, 2, 0, 2)
                };
                countLabels[t.Key] = cnt;
                sectionsFlow.Controls.Add(cnt);

                var flow = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = BG, Margin = new Padding(0)
                };
                flows[t.Key] = flow;
                tiles[t.Key] = new List<Panel>();
                sectionsFlow.Controls.Add(flow);
                string existing = game != null ? FindExisting(game.AppId, t.Suffix) : null;
                hasExisting[t.Key] = existing != null;
                AddCurrentTile(t, flow, existing);
            }
            UpdateFlowWidths();
            SetStatus("Loading assets for " + gameName + "...", DIM);

            // offer the game's original Steam assets as picks, like auto mode uses
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
                try { pg = await api.AssetsPaged(gameId, t, 0); }
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
                    : pg.Total + " available - click to pick";

                bool preselected = false;
                for (int i = 0; i < pg.Assets.Count; i++)
                {
                    Panel tile = AddAssetTile(t, flows[t.Key], pg.Assets[i]);
                    // default pick: keep existing art if there is any, else the
                    // top result that passes the current filters
                    if (!preselected && !hasExisting[t.Key] && ShouldShow(pg.Assets[i]))
                    {
                        SelectTile(t.Key, tile, pg.Assets[i].Url);
                        preselected = true;
                    }
                    // by type, not index: the selection badge sits at index 0
                    LoadThumb(tile.Controls.OfType<PictureBox>().First(), pg.Assets[i], g);
                }
                if (shownByType[t.Key] < pg.Total)
                    AddLoadMoreTile(t, g);
            }
            if (g == gen)
                SetStatus("Assets loaded. Click thumbnails to change picks, then Apply selected.", OKC);
        }

        // First tile of every row: what the game has right now. Clicking it
        // means "keep as is". Shows the existing image, or "none" if missing.
        void AddCurrentTile(AType t, FlowLayoutPanel flow, string existingPath)
        {
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = PANEL,
                Margin = new Padding(4), Tag = null
            };
            var caption = new Label
            {
                Text = existingPath != null ? "current" : "none",
                ForeColor = existingPath != null ? DIM : WARN,
                BackColor = FIELD, Font = new Font("Segoe UI", 8.25f),
                Location = new Point(5, 5 + t.H - 18), Size = new Size(t.W, 18),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            string key = t.Key;
            EventHandler h = delegate { SelectTile(key, p, null); };

            if (existingPath != null)
            {
                var pb = new PictureBox
                {
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - 18),
                    SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD, Cursor = Cursors.Hand
                };
                try
                {
                    byte[] bytes = File.ReadAllBytes(existingPath);
                    pb.Image = Image.FromStream(new MemoryStream(bytes));
                }
                catch (Exception) { }
                p.Controls.Add(pb);
                pb.Click += h;
            }
            else
            {
                var empty = new Label
                {
                    Text = "keep\nempty", ForeColor = DIM, BackColor = FIELD,
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - 18),
                    TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 8.25f)
                };
                p.Controls.Add(empty);
                empty.Click += h;
            }
            p.Controls.Add(caption);
            caption.Click += h;
            p.Click += h;
            flow.Controls.Add(p);
            tiles[key].Add(p);
            // keeping what's there is the default until the user picks something
            SelectTile(key, p, null);
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
                Size = new Size(t.W + 10, t.H + 10), BackColor = PANEL,
                Margin = new Padding(4), Tag = selUrl
            };
            var pb = new PictureBox
            {
                Location = new Point(5, 5), Size = new Size(t.W, t.H - 18),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD,
                Cursor = Cursors.Hand, Image = img
            };
            var caption = new Label
            {
                Text = "Steam default", ForeColor = OKC, BackColor = FIELD,
                Font = new Font("Segoe UI", 8.25f),
                Location = new Point(5, 5 + t.H - 18), Size = new Size(t.W, 18),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            p.Controls.Add(pb);
            p.Controls.Add(caption);
            string key = t.Key;
            EventHandler h = delegate { SelectTile(key, p, selUrl); };
            p.Click += h; pb.Click += h; caption.Click += h;
            flow.Controls.Add(p);
            flow.Controls.SetChildIndex(p, 1);   // right after the "current" tile
            tiles[key].Add(p);
        }

        Panel AddAssetTile(AType t, FlowLayoutPanel flow, SgdbAsset a)
        {
            string url = a.Url;
            bool animated = a.Animated;
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = PANEL,
                Margin = new Padding(4), Tag = url
            };
            var pb = new PictureBox
            {
                Location = new Point(5, 5), Size = new Size(t.W, animated ? t.H - 18 : t.H),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD, Cursor = Cursors.Hand
            };
            p.Controls.Add(pb);
            string key = t.Key;
            EventHandler h = delegate { SelectTile(key, p, url); };
            p.Click += h; pb.Click += h;
            if (animated)
            {
                var cap = new Label
                {
                    Text = "animated", ForeColor = DIM, BackColor = FIELD,
                    Font = new Font("Segoe UI", 8.25f),
                    Location = new Point(5, 5 + t.H - 18), Size = new Size(t.W, 18),
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
                Size = new Size(t.W + 10, t.H + 10), BackColor = PANEL2,
                Margin = new Padding(4), Cursor = Cursors.Hand
            };
            var lbl = new Label
            {
                Text = "Load more\n(" + remaining + " left)", ForeColor = TX, BackColor = PANEL2,
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
                LoadThumb(tp.Controls.OfType<PictureBox>().First(), a, g);
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
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                    using (var ms = new MemoryStream())
                    {
                        enc.Save(ms);
                        clip.Frames.Add(Image.FromStream(new MemoryStream(ms.ToArray())));
                    }
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
                    return;
                }
                // fall through: static single-frame fallback below
            }
            try { pb.Image = Image.FromStream(new MemoryStream(data)); }
            catch (Exception)
            {
                // GDI+ can't decode WebP; try WIC for a single frame,
                // else show a labeled placeholder.
                Image wic = WicDecode(data);
                pb.Image = wic != null ? wic : TextThumb(pb.Width, pb.Height);
            }
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

        void SelectTile(string typeKey, Panel tile, string url)
        {
            List<Panel> list;
            if (!tiles.TryGetValue(typeKey, out list)) return;
            foreach (Panel p in list)
                if (!p.IsDisposed) p.BackColor = PANEL;
            tile.BackColor = ACC;

            // check badge so the pick isn't indicated by the ring color alone
            Label badge;
            if (selBadges.TryGetValue(typeKey, out badge))
            {
                if (!badge.IsDisposed)
                {
                    if (badge.Parent != null) badge.Parent.Controls.Remove(badge);
                    badge.Dispose();
                }
                selBadges.Remove(typeKey);
            }
            badge = new Label
            {
                Text = "✓", BackColor = ACC,
                ForeColor = ColorTranslator.FromHtml("#06121C"),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(22, 22), TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(tile.Width - 27, 5)
            };
            tile.Controls.Add(badge);
            badge.BringToFront();
            selBadges[typeKey] = badge;

            if (url != null) sel[typeKey] = url;
            else sel.Remove(typeKey);
            UpdateButtons();
        }

        // ------------------------------------------------------ apply flows

        void MarkApplied(uint appid)
        {
            applied.Add(appid);
            RefreshGame(appid);
        }

        async void ApplySelected()
        {
            Shortcut game = currentShortcut;
            if (busy || game == null || sel.Count == 0) return;
            busy = true;
            UpdateButtons();
            SetStatus("Applying to " + game.Name + "...", DIM);
            try
            {
                var written = new List<string>();
                foreach (var kv in sel.ToList())
                {
                    AType t = Cfg.Types.First(x => x.Key == kv.Key);
                    if (kv.Value.StartsWith("official:"))
                    {
                        int sid = int.Parse(kv.Value.Substring("official:".Length));
                        if (await SteamStore.Apply(gridDir, game.AppId, t, sid, stamp))
                            written.Add(t.Key + " (Steam default)");
                    }
                    else
                        written.Add(await Artwork.Apply(gridDir, game.AppId, t, kv.Value, stamp));
                }
                MarkApplied(game.AppId);
                SetStatus("Applied: " + string.Join(", ", written) +
                          "   (press F5 in your Steam library to see it)", OKC);
            }
            catch (Exception ex) { SetStatus("Apply failed: " + ex.Message, ERRC); }
            finally { busy = false; UpdateButtons(); }
        }

        async void AutoOne()
        {
            Shortcut game = currentShortcut;
            if (busy || game == null || currentSgdbId <= 0) return;
            busy = true;
            UpdateButtons();
            SetStatus("Auto-applying top picks for " + game.Name + "...", DIM);
            try
            {
                var written = new List<string>();
                foreach (AType t in Cfg.Types)
                {
                    var assets = await api.Assets(currentSgdbId, t);
                    if (assets.Count > 0)
                        written.Add(await Artwork.Apply(gridDir, game.AppId, t, assets[0].Url, stamp));
                }
                MarkApplied(game.AppId);
                SetStatus(written.Count == 0
                    ? "Nothing available on SteamGridDB for this match."
                    : "Applied: " + string.Join(", ", written) +
                      "   (press F5 in your Steam library to see it)", OKC);
            }
            catch (Exception ex) { SetStatus("Auto failed: " + ex.Message, ERRC); }
            finally { busy = false; UpdateButtons(); }
        }

        // Fill ONLY missing asset slots, preferring the official Steam default
        // artwork; SteamGridDB top results are the fallback for games that are
        // not on the Steam store. Existing artwork is never replaced.
        async void AutoAll()
        {
            if (busy || api == null || shortcuts == null) return;
            // only what the list is actually showing right now: the Steam
            // toggle and the search filter both narrow the scope
            var targets = DisplayGames.Where(sc =>
            {
                GameTile t;
                return gameTiles.TryGetValue(sc.AppId, out t) && t.Root.Visible;
            }).ToList();
            if (targets.Count == 0) return;

            DialogResult r = MessageBox.Show(this,
                "Fill in missing artwork for the " + targets.Count + " game(s) currently " +
                "shown in the list?\n\n" +
                "Empty slots get the official Steam default art (SteamGridDB top result " +
                "if the game isn't on Steam).\n\nArtwork you already have is never touched; " +
                "games hidden by the search box or the Steam toggle are not touched either.",
                "Auto-apply all", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            busy = true;
            UpdateButtons();
            int updated = 0, complete = 0, notFound = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Shortcut sc = targets[i];
                    var missing = Cfg.Types.Where(t => FindExisting(sc.AppId, t.Suffix) == null).ToList();
                    if (missing.Count == 0) { complete++; continue; }

                    SetStatus("[" + (i + 1) + "/" + targets.Count + "] " + sc.Name +
                              "  (" + missing.Count + " empty slot(s))...", DIM);
                    bool wrote = false;

                    List<SgdbGame> res = null;
                    try { res = (await api.SearchSmart(sc.Name)).Value; }
                    catch (Exception) { }
                    if (res != null && res.Count > 0)
                    {
                        // 1) the game's original Steam assets, as linked by SteamGridDB
                        int steamId = await api.SteamAppId(res[0].Id);
                        if (steamId > 0)
                        {
                            foreach (AType t in missing.ToList())
                            {
                                if (await SteamStore.Apply(gridDir, sc.AppId, t, steamId, stamp))
                                {
                                    missing.Remove(t);
                                    wrote = true;
                                }
                            }
                        }

                        // 2) SteamGridDB top results for whatever is still empty
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

                    if (wrote) { updated++; MarkApplied(sc.AppId); }
                    else notFound++;
                }
                SetStatus("Done: " + updated + " game(s) filled in, " + complete +
                          " already complete, " + notFound + " with nothing found. " +
                          "Press F5 in your Steam library to see the artwork.", OKC);
            }
            finally { busy = false; UpdateButtons(); }
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.AddMessageFilter(new WheelRedirector());
            Application.Run(new MainForm());
        }
    }
}
