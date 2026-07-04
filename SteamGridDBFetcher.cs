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
            new AType { Key = "cover",      Endpoint = "grids",  Query = "?dimensions=600x900",         Suffix = "p",     Label = "Cover (600x900)",      W = 220, H = 330 },
            new AType { Key = "wide",       Endpoint = "grids",  Query = "?dimensions=920x430,460x215", Suffix = "",      Label = "Wide Cover (920x430)", W = 430, H = 201 },
            new AType { Key = "background", Endpoint = "heroes", Query = "",                            Suffix = "_hero", Label = "Background (hero)",    W = 480, H = 155 },
            new AType { Key = "logo",       Endpoint = "logos",  Query = "",                            Suffix = "_logo", Label = "Logo",                 W = 300, H = 150 },
        };

        public static readonly string[] ImageExts = new string[] { ".png", ".jpg", ".jpeg", ".webp" };
        public const int MaxThumbs = 21;

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

        // Returns the "data" value of an API response: object[] for lists,
        // Dictionary for single objects, null on 404.
        async Task<object> GetRaw(string url)
        {
            lock (cacheLock)
            {
                object hit;
                if (cache.TryGetValue(url, out hit)) return hit;
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
            object data = null;
            if (body != null)
            {
                var root = Js.DeserializeObject(body) as Dictionary<string, object>;
                object ok, d;
                if (root != null && root.TryGetValue("success", out ok) && ok is bool && (bool)ok
                    && root.TryGetValue("data", out d))
                    data = d;
            }
            lock (cacheLock) { cache[url] = data; }
            return data;
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

        static List<SgdbAsset> ParseAssets(object[] data)
        {
            var list = new List<SgdbAsset>();
            foreach (object o in data)
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                object url, thumb;
                if (!a.TryGetValue("url", out url)) continue;
                a.TryGetValue("thumb", out thumb);
                list.Add(new SgdbAsset
                {
                    Url = Convert.ToString(url),
                    Thumb = thumb != null ? Convert.ToString(thumb) : Convert.ToString(url)
                });
            }
            return list;
        }

        public async Task<List<SgdbAsset>> Assets(int gameId, AType t)
        {
            return ParseAssets(await GetData(Base + "/" + t.Endpoint + "/game/" + gameId + t.Query));
        }

        // Official (Steam-mirrored) logos; the API only supports styles=official
        // for logos and icons, not for grids or heroes.
        public async Task<List<SgdbAsset>> OfficialLogos(int gameId)
        {
            try { return ParseAssets(await GetData(Base + "/logos/game/" + gameId + "?styles=official")); }
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

        // Apply the official Steam default for one asset type. False if unavailable.
        public static async Task<bool> Apply(string gdir, uint appid, AType t, int steamId, string stamp)
        {
            foreach (string f in Files[t.Key])
            {
                string url = "https://cdn.cloudflare.steamstatic.com/steam/apps/" + steamId + "/" + f;
                try { await Artwork.Apply(gdir, appid, t, url, stamp); return true; }
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
    // still works through the WheelRedirector message filter.
    class BareFlowPanel : FlowLayoutPanel
    {
        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

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
        static readonly Color BG = ColorTranslator.FromHtml("#171a21");
        static readonly Color PANEL = ColorTranslator.FromHtml("#1f2430");
        static readonly Color PANEL2 = ColorTranslator.FromHtml("#2a3140");
        static readonly Color FIELD = ColorTranslator.FromHtml("#0e1117");
        static readonly Color TX = ColorTranslator.FromHtml("#dbe2ec");
        static readonly Color DIM = ColorTranslator.FromHtml("#8f98a0");
        static readonly Color ACC = ColorTranslator.FromHtml("#66c0f4");
        static readonly Color OKC = ColorTranslator.FromHtml("#7bc94e");
        static readonly Color ERRC = ColorTranslator.FromHtml("#e06c6c");
        static readonly Color WARN = ColorTranslator.FromHtml("#d9a04a");

        const int CoverW = 220, CoverH = 330;

        Dictionary<string, object> cfg;
        Sgdb api;
        string steamPath, userId, gridDir, stamp;
        List<Shortcut> shortcuts;

        // library view
        Panel libraryView;
        FlowLayoutPanel libraryFlow;
        Label profileLabel;
        Button autoAllBtn;
        readonly Dictionary<uint, GameTile> gameTiles = new Dictionary<uint, GameTile>();
        readonly Dictionary<uint, Image> coverImages = new Dictionary<uint, Image>();
        readonly Dictionary<uint, Image> placeholders = new Dictionary<uint, Image>();

        // picker view
        Panel pickerView;
        Button backBtn, searchBtn, applyBtn, autoBtn;
        TextBox searchBox;
        ComboBox matchCombo;
        Label selLabel, gameTitle;
        Panel contentPanel;
        FlowLayoutPanel sectionsFlow;

        Label statusLabel;

        readonly Dictionary<string, FlowLayoutPanel> flows = new Dictionary<string, FlowLayoutPanel>();
        readonly Dictionary<string, Label> countLabels = new Dictionary<string, Label>();
        readonly Dictionary<string, List<Panel>> tiles = new Dictionary<string, List<Panel>>();
        readonly Dictionary<string, string> sel = new Dictionary<string, string>();
        readonly HashSet<uint> applied = new HashSet<uint>();
        readonly List<SgdbGame> matches = new List<SgdbGame>();
        readonly SemaphoreSlim thumbSem = new SemaphoreSlim(6);

        Shortcut currentShortcut;
        int gen;                 // invalidates in-flight loads
        int currentSgdbId = -1;
        bool busy, suppressMatch;

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
                shortcuts = Steam.LoadShortcuts(steamPath, Cfg.Str(cfg, "user_id"), out userId);
                gridDir = Artwork.GridDir(steamPath, userId);
                stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                foreach (Shortcut sc in shortcuts) LoadCoverImage(sc.AppId);
                BuildLibrary();
                UpdateProfileLabel();
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
                ForeColor = accent ? ColorTranslator.FromHtml("#08131c") : TX,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 4, 10, 4)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = accent ? ColorTranslator.FromHtml("#8ad0f8") : PANEL;
            return b;
        }

        void BuildUi()
        {
            statusLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = 32, BackColor = FIELD, ForeColor = DIM,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
                Text = "Starting..."
            };
            Controls.Add(statusLabel);

            // ------------------------------------------------- library view
            libraryView = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            Controls.Add(libraryView);
            libraryView.BringToFront();

            var topBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = PANEL };
            libraryView.Controls.Add(topBar);

            var title = new Label
            {
                Text = "SteamGridDB Fetcher", ForeColor = ACC, BackColor = PANEL,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Location = new Point(18, 10), AutoSize = true
            };
            topBar.Controls.Add(title);

            profileLabel = new Label
            {
                Text = "", ForeColor = DIM, BackColor = PANEL,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(20, 38), AutoSize = true
            };
            topBar.Controls.Add(profileLabel);

            autoAllBtn = MakeButton("Auto-apply ALL games", false);
            autoAllBtn.AutoSize = false;
            autoAllBtn.Size = new Size(190, 34);
            autoAllBtn.Location = new Point(topBar.Width - 210, 15);
            autoAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            autoAllBtn.Click += delegate { AutoAll(); };
            topBar.Controls.Add(autoAllBtn);

            libraryFlow = new BareFlowPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = BG,
                Padding = new Padding(12, 10, 12, 10)
            };
            libraryView.Controls.Add(libraryFlow);
            libraryFlow.BringToFront();

            // -------------------------------------------------- picker view
            pickerView = new Panel { Dock = DockStyle.Fill, BackColor = BG, Visible = false };
            Controls.Add(pickerView);
            pickerView.BringToFront();

            backBtn = MakeButton("<   Library", false);
            backBtn.AutoSize = false;
            backBtn.Size = new Size(120, 34);
            backBtn.Location = new Point(16, 14);
            backBtn.Click += delegate { BackToLibrary(); };
            pickerView.Controls.Add(backBtn);

            gameTitle = new Label
            {
                Text = "", ForeColor = ACC, BackColor = BG,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(140, 18), AutoSize = true
            };
            pickerView.Controls.Add(gameTitle);

            searchBox = new TextBox
            {
                Location = new Point(16, 58), Size = new Size(pickerView.Width - 140, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = FIELD, ForeColor = TX, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f)
            };
            searchBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); }
            };
            pickerView.Controls.Add(searchBox);

            searchBtn = MakeButton("Search", true);
            searchBtn.AutoSize = false;
            searchBtn.Size = new Size(100, 32);
            searchBtn.Location = new Point(pickerView.Width - 118, 55);
            searchBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            searchBtn.Click += delegate { DoSearch(); };
            pickerView.Controls.Add(searchBtn);

            var matchLbl = new Label
            {
                Text = "Match:", ForeColor = DIM, BackColor = BG,
                Location = new Point(16, 98), AutoSize = true
            };
            pickerView.Controls.Add(matchLbl);

            matchCombo = new ComboBox
            {
                Location = new Point(70, 94), Size = new Size(420, 26),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = PANEL2, ForeColor = TX
            };
            matchCombo.SelectedIndexChanged += delegate
            {
                if (suppressMatch || busy || matchCombo.SelectedIndex < 0) return;
                if (matchCombo.SelectedIndex < matches.Count)
                    LoadAssets(matches[matchCombo.SelectedIndex].Id, matches[matchCombo.SelectedIndex].Name);
            };
            pickerView.Controls.Add(matchCombo);

            applyBtn = MakeButton("Apply selected", true);
            applyBtn.Location = new Point(16, 130);
            applyBtn.Click += delegate { ApplySelected(); };
            pickerView.Controls.Add(applyBtn);

            autoBtn = MakeButton("Auto (top picks)", false);
            autoBtn.Location = new Point(146, 130);
            autoBtn.Click += delegate { AutoOne(); };
            pickerView.Controls.Add(autoBtn);

            selLabel = new Label
            {
                Text = "", ForeColor = DIM, BackColor = BG,
                Location = new Point(300, 136), AutoSize = true
            };
            pickerView.Controls.Add(selLabel);

            contentPanel = new BarePanel
            {
                Location = new Point(16, 170),
                Size = new Size(pickerView.Width - 24, pickerView.Height - 178),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = BG, AutoScroll = true
            };
            pickerView.Controls.Add(contentPanel);

            sectionsFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BG, Location = new Point(0, 0)
            };
            contentPanel.Controls.Add(sectionsFlow);
            contentPanel.Resize += delegate { UpdateFlowWidths(); };

            UpdateButtons();
        }

        void UpdateFlowWidths()
        {
            int w = Math.Max(300, contentPanel.ClientSize.Width - 28);
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
            searchBtn.Enabled = !busy;
            backBtn.Enabled = !busy;
            matchCombo.Enabled = !busy;
            selLabel.Text = sel.Count > 0 ? sel.Count + " change(s) selected" : "";
        }

        // ------------------------------------------------- library (grid) view

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
                {
                    var bmp = new Bitmap(CoverW, CoverH);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        float scale = Math.Max((float)CoverW / src.Width, (float)CoverH / src.Height);
                        float sw = src.Width * scale, sh = src.Height * scale;
                        g.DrawImage(src, (CoverW - sw) / 2f, (CoverH - sh) / 2f, sw, sh);
                    }
                    coverImages[appid] = bmp;
                }
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
                    ColorTranslator.FromHtml("#4d5b6d"), ColorTranslator.FromHtml("#20262f"), 65f))
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

        void BuildLibrary()
        {
            libraryFlow.SuspendLayout();
            foreach (Shortcut sc in shortcuts)
            {
                var tile = new GameTile();
                tile.Root = new Panel
                {
                    Size = new Size(CoverW + 8, CoverH + 46), BackColor = BG,
                    Margin = new Padding(7), Cursor = Cursors.Hand
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
                    ForeColor = TX, BackColor = BG, AutoEllipsis = true, Text = sc.Name,
                    Cursor = Cursors.Hand
                };
                tile.Status = new Label
                {
                    Location = new Point(4, CoverH + 28), Size = new Size(CoverW, 14),
                    ForeColor = DIM, BackColor = BG, Font = new Font("Segoe UI", 7.5f),
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
                EventHandler leave = delegate { t.Root.BackColor = BG; t.Name.BackColor = BG; t.Status.BackColor = BG; };
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
            coverImages.TryGetValue(sc.AppId, out cover);
            t.Pic.Image = cover != null ? cover : GetPlaceholder(sc);
            bool isApplied = applied.Contains(sc.AppId);
            t.Name.ForeColor = isApplied ? OKC : TX;
            if (isApplied) { t.Status.Text = "updated"; t.Status.ForeColor = OKC; }
            else if (cover == null) { t.Status.Text = "missing artwork"; t.Status.ForeColor = WARN; }
            else { t.Status.Text = ""; }
        }

        void UpdateProfileLabel()
        {
            if (shortcuts == null) return;
            int missing = shortcuts.Count(sc => !coverImages.ContainsKey(sc.AppId));
            profileLabel.Text = "Profile " + userId + "   -   " + shortcuts.Count + " non-Steam games"
                + (missing > 0 ? "   -   " + missing + " without cover art" : "   -   all covered");
        }

        void RefreshGame(uint appid)
        {
            LoadCoverImage(appid);
            Shortcut sc = shortcuts.FirstOrDefault(x => x.AppId == appid);
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
            flows.Clear(); countLabels.Clear(); tiles.Clear();
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
            try { results = await api.Search(term); }
            catch (Exception ex) { SetStatus("Search failed: " + ex.Message, ERRC); return; }
            if (g != gen) return;

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
                    Font = new Font("Segoe UI", 8f), Margin = new Padding(5, 2, 0, 2)
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

            foreach (AType t in Cfg.Types)
            {
                List<SgdbAsset> assets;
                try { assets = await api.Assets(gameId, t); }
                catch (Exception ex)
                {
                    if (g == gen) countLabels[t.Key].Text = "failed to load (" + ex.Message + ")";
                    continue;
                }
                if (g != gen) return;

                int n = Math.Min(assets.Count, Cfg.MaxThumbs);
                countLabels[t.Key].Text = assets.Count == 0
                    ? "none available on SteamGridDB"
                    : assets.Count + " found - click to pick";

                for (int i = 0; i < n; i++)
                {
                    Panel tile = AddAssetTile(t, flows[t.Key], assets[i].Url);
                    // default pick: keep existing art if there is any, else top result
                    if (i == 0 && !hasExisting[t.Key]) SelectTile(t.Key, tile, assets[i].Url);
                    LoadThumb((PictureBox)tile.Controls[0], assets[i].Thumb, g);
                }
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
                ForeColor = existingPath != null ? ACC : WARN,
                BackColor = FIELD, Font = new Font("Segoe UI", 7.5f),
                Location = new Point(5, 5 + t.H - 16), Size = new Size(t.W, 16),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            string key = t.Key;
            EventHandler h = delegate { SelectTile(key, p, null); };

            if (existingPath != null)
            {
                var pb = new PictureBox
                {
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - 16),
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
                    Location = new Point(5, 5), Size = new Size(t.W, t.H - 16),
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
            // keeping what's there is the default until the user picks something
            SelectTile(key, p, null);
        }

        Panel AddAssetTile(AType t, FlowLayoutPanel flow, string url)
        {
            var p = new Panel
            {
                Size = new Size(t.W + 10, t.H + 10), BackColor = PANEL,
                Margin = new Padding(4), Tag = url
            };
            var pb = new PictureBox
            {
                Location = new Point(5, 5), Size = new Size(t.W, t.H),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = FIELD, Cursor = Cursors.Hand
            };
            p.Controls.Add(pb);
            string key = t.Key;
            EventHandler h = delegate { SelectTile(key, p, url); };
            p.Click += h; pb.Click += h;
            flow.Controls.Add(p);
            tiles[key].Add(p);
            return p;
        }

        async void LoadThumb(PictureBox pb, string thumbUrl, int g)
        {
            byte[] data;
            await thumbSem.WaitAsync();
            try
            {
                if (g != gen) return;
                data = await Sgdb.Download(thumbUrl);
            }
            catch (Exception) { return; }
            finally { thumbSem.Release(); }
            if (g != gen || pb.IsDisposed) return;
            try { pb.Image = Image.FromStream(new MemoryStream(data)); }
            catch (Exception) { }  // undecodable format -> leave blank tile
        }

        void SelectTile(string typeKey, Panel tile, string url)
        {
            List<Panel> list;
            if (!tiles.TryGetValue(typeKey, out list)) return;
            foreach (Panel p in list)
                if (!p.IsDisposed) p.BackColor = PANEL;
            tile.BackColor = ACC;
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
            DialogResult r = MessageBox.Show(this,
                "Fill in missing artwork for all " + shortcuts.Count + " games?\n\n" +
                "Empty slots get the official Steam default art (SteamGridDB top result " +
                "if the game isn't on Steam).\n\nArtwork you already have is never touched.",
                "Auto-apply all", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            busy = true;
            UpdateButtons();
            int updated = 0, complete = 0, notFound = 0;
            try
            {
                for (int i = 0; i < shortcuts.Count; i++)
                {
                    Shortcut sc = shortcuts[i];
                    var missing = Cfg.Types.Where(t => FindExisting(sc.AppId, t.Suffix) == null).ToList();
                    if (missing.Count == 0) { complete++; continue; }

                    SetStatus("[" + (i + 1) + "/" + shortcuts.Count + "] " + sc.Name +
                              "  (" + missing.Count + " empty slot(s))...", DIM);
                    bool wrote = false;

                    List<SgdbGame> res = null;
                    try { res = await api.Search(sc.Name); }
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
