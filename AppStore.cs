using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RankedDownloader
{
    /// <summary>失败记录</summary>
    public class FailedInfo
    {
        public long Sid;
        public string Artist = "";
        public string Title = "";
        public string Error = "";
        public string Mode = "";
        public override string ToString()
        {
            return (Artist + " - " + Title).Trim(' ', '-') + " (" + Sid + ")";
        }
    }

    /// <summary>
    /// 本地持久化(%APPDATA%\osu!MapsDownloader):
    ///   records.txt  已下载记录 每行: sid|完整文件路径(跨运行去重; 只有文件仍存在且目录匹配才算数)
    ///   failed.txt   失败清单 每行: sid|模式|artist|title|error (跨运行保留)
    ///   config.ini   用户配置 key=value
    /// 用纯文本替代 Python 版的 SQLite, 规模(10 万条内)性能足够且零依赖。
    /// 不再在启动时清空: 需要重置时由界面"设置 -> 清空下载历史"显式调用 WipeAll()。
    /// </summary>
    public class AppStore
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu!MapsDownloader");

        private readonly string _recordsFile;
        private readonly string _failedFile;
        private readonly Dictionary<long, string> _downloaded = new Dictionary<long, string>();  // sid -> 文件路径
        private readonly Dictionary<long, FailedInfo> _failed = new Dictionary<long, FailedInfo>();
        private readonly object _lock = new object();     // 内存状态
        private readonly object _ioLock = new object();   // 文件写入串行化(多下载线程共用)
        private readonly bool _persist;

        /// <summary>写盘失败等非致命错误的通知(UI 订阅后写入运行日志)。</summary>
        public event Action<string> Error;

        /// <summary>最近一次错误(供构造完成后再订阅 Error 的界面补记)。</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// persist=false: 纯内存实例(测试用), 不读写真实用户数据。
        /// customDir: 自定义数据目录(测试隔离用)。
        /// </summary>
        public AppStore(bool persist = true, string customDir = null)
        {
            _persist = persist;
            var dir = customDir ?? DataDir;
            _recordsFile = Path.Combine(dir, "records.txt");
            _failedFile = Path.Combine(dir, "failed.txt");
            if (persist)
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { Report("创建数据目录失败", ex); }
                Load();
            }
        }

        public int DownloadedCount { get { lock (_lock) return _downloaded.Count; } }
        public int FailedCount { get { lock (_lock) return _failed.Count; } }

        private void Report(string context, Exception ex)
        {
            var msg = context + ": " + ex.Message;
            LastError = msg;
            var h = Error;
            if (h != null)
            {
                try { h(msg); } catch { }
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_recordsFile))
                {
                    foreach (var line in File.ReadAllLines(_recordsFile, Encoding.UTF8))
                    {
                        var p = line.Split(new[] { '|' }, 2);
                        long sid;
                        if (p.Length >= 2 && long.TryParse(p[0], out sid) && sid > 0)
                            _downloaded[sid] = p[1];
                    }
                }
                if (File.Exists(_failedFile))
                {
                    foreach (var line in File.ReadAllLines(_failedFile, Encoding.UTF8))
                    {
                        var p = line.Split('|');
                        long sid;
                        if (p.Length >= 5 && long.TryParse(p[0], out sid))
                        {
                            _failed[sid] = new FailedInfo
                            {
                                Sid = sid,
                                Mode = p[1],
                                Artist = p[2],
                                Title = p[3],
                                Error = p[4],
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Report("读取历史记录失败", ex);
            }
        }

        // ---------------- 已下载记录 ----------------

        /// <summary>该 sid 是否已下载且文件仍在磁盘上(不校验目录, 测试用)。</summary>
        public bool IsDownloaded(long sid)
        {
            return IsDownloaded(sid, null);
        }

        /// <summary>
        /// 该 sid 是否已下载。会校验: 记录路径所在目录与 songsDir 一致, 且该文件/文件夹仍存在。
        /// 这样删除/移动谱面后不会被过期记录错误跳过(旧格式记录只有文件名, 一律视为过期)。
        /// </summary>
        public bool IsDownloaded(long sid, string songsDir)
        {
            string path;
            lock (_lock)
            {
                if (!_downloaded.TryGetValue(sid, out path)) return false;
            }
            if (string.IsNullOrEmpty(path)) return false;
            if (!string.IsNullOrEmpty(songsDir))
            {
                string recordedDir;
                try { recordedDir = Path.GetDirectoryName(path); }
                catch { recordedDir = null; }
                if (string.IsNullOrEmpty(recordedDir)) return false;
                if (!SameDir(recordedDir, songsDir)) return false;
            }
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }

        /// <summary>批量过滤已记录集合</summary>
        public HashSet<long> FilterDownloaded(IEnumerable<long> sids)
        {
            var found = new HashSet<long>();
            foreach (var s in sids)
                if (IsDownloaded(s)) found.Add(s);
            return found;
        }

        /// <summary>记录一个已完成的谱面(写入完整路径, 便于跨运行/跨目录校验)。</summary>
        public void MarkDownloaded(long sid, string filePath)
        {
            bool isNew, removedFailed;
            lock (_lock)
            {
                isNew = !_downloaded.ContainsKey(sid);
                _downloaded[sid] = filePath ?? "";
                removedFailed = _failed.Remove(sid);
            }
            // 文件 IO 在锁外执行(但串行化), 避免阻塞其他下载线程
            if (isNew && _persist)
            {
                lock (_ioLock)
                {
                    try
                    {
                        File.AppendAllText(_recordsFile,
                            sid + "|" + Clean(filePath) + Environment.NewLine, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Report("写入下载记录失败", ex);
                    }
                }
            }
            if (removedFailed) SaveFailed();
        }

        // ---------------- 失败清单 ----------------

        public void AddFailed(long sid, string mode, string artist, string title, string error)
        {
            lock (_lock)
            {
                _failed[sid] = new FailedInfo { Sid = sid, Mode = mode, Artist = artist, Title = title, Error = error };
            }
            SaveFailed();
        }

        public void RemoveFailed(long sid)
        {
            lock (_lock) { _failed.Remove(sid); }
            SaveFailed();
        }

        public void ClearFailed()
        {
            lock (_lock) { _failed.Clear(); }
            SaveFailed();
        }

        public List<FailedInfo> GetFailedList()
        {
            lock (_lock) return _failed.Values.OrderByDescending(f => f.Sid).ToList();
        }

        /// <summary>
        /// 清空全部历史(已下载记录 + 失败清单)并删除磁盘文件。
        /// 只在用户显式"重置历史"时调用(不再随程序启动清空)。
        /// </summary>
        public void WipeAll()
        {
            lock (_lock)
            {
                _downloaded.Clear();
                _failed.Clear();
            }
            if (!_persist) return;
            lock (_ioLock)
            {
                try { if (File.Exists(_recordsFile)) File.Delete(_recordsFile); }
                catch (Exception ex) { Report("删除下载记录失败", ex); }
                try { if (File.Exists(_failedFile)) File.Delete(_failedFile); }
                catch (Exception ex) { Report("删除失败清单失败", ex); }
            }
        }

        private void SaveFailed()
        {
            if (!_persist) return;
            List<string> lines;
            lock (_lock)
            {
                lines = _failed.Values.Select(f =>
                    f.Sid + "|" + Clean(f.Mode) + "|" + Clean(f.Artist) + "|" + Clean(f.Title) + "|" + Clean(f.Error)).ToList();
            }
            lock (_ioLock)
            {
                try { File.WriteAllLines(_failedFile, lines, Encoding.UTF8); }
                catch (Exception ex) { Report("写入失败清单失败", ex); }
            }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('|', '/').Replace("\r", " ").Replace("\n", " ");
        }

        private static bool SameDir(string a, string b)
        {
            try
            {
                var fa = Path.GetFullPath(a).TrimEnd('\\', '/');
                var fb = Path.GetFullPath(b).TrimEnd('\\', '/');
                return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>导出失败清单为 CSV(带 BOM, Excel 可直接打开)</summary>
        public void ExportFailedCsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("sid,模式,艺术家,标题,失败原因");
            foreach (var f in GetFailedList())
                sb.AppendLine(f.Sid + "," + Csv(f.Mode) + "," + Csv(f.Artist) + "," + Csv(f.Title) + "," + Csv(f.Error));
            lock (_ioLock)
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    /// <summary>用户配置(ini 文本, 免依赖)</summary>
    public class AppConfig
    {
        public string DownloadDir = "";
        public string Server = "auto";
        public string DownloadType = "full";   // full / novideo / mini
        public int Mode = 0;                   // 下载模式(0 日期 / 1 bp / 2 mapper)
        public int GameMode = 0;               // 游戏模式(0 osu / 1 taiko / 2 catch / 3 mania)
        public int Concurrency = 3;
        public int Retries = 3;
        public int Timeout = 60;
        public int BpLimit = 100;
        public bool StateRanked = true;
        public bool StateQualified = true;
        public bool StateLoved = true;

        private static string FilePath(string dir)
        {
            return Path.Combine(dir ?? AppStore.DataDir, "config.ini");
        }

        public static AppConfig Load(string customDir = null)
        {
            var path = FilePath(customDir);
            var c = new AppConfig();
            try
            {
                if (!File.Exists(path)) return c;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "download_dir": c.DownloadDir = v; break;
                        case "server": c.Server = v; break;
                        case "download_type": c.DownloadType = v; break;
                        case "mode": c.Mode = Int(v, 0); break;
                        case "game_mode": c.GameMode = Int(v, 0); break;
                        case "concurrency": c.Concurrency = Clamp(Int(v, 3), 1, 8); break;
                        case "retries": c.Retries = Clamp(Int(v, 3), 0, 10); break;
                        case "timeout": c.Timeout = Clamp(Int(v, 60), 10, 600); break;
                        case "bp_limit": c.BpLimit = Clamp(Int(v, 100), 1, 100); break;
                        case "state_ranked": c.StateRanked = Bool(v, true); break;
                        case "state_qualified": c.StateQualified = Bool(v, true); break;
                        case "state_loved": c.StateLoved = Bool(v, true); break;
                    }
                }
            }
            catch { }
            if (c.DownloadType != "full" && c.DownloadType != "novideo" && c.DownloadType != "mini")
                c.DownloadType = "full";
            // 下拉框只有 3 项(0..2), 越界值必须回落, 否则 MainForm 构造时抛异常
            if (c.Mode < 0 || c.Mode > 2) c.Mode = 0;
            if (c.GameMode < 0 || c.GameMode > 3) c.GameMode = 0;
            return c;
        }

        public void Save(string customDir = null)
        {
            var path = FilePath(customDir);
            var sb = new StringBuilder();
            sb.AppendLine("# osu!MapsDownloader 配置");
            sb.AppendLine("download_dir=" + DownloadDir);
            sb.AppendLine("server=" + Server);
            sb.AppendLine("download_type=" + DownloadType);
            sb.AppendLine("mode=" + Mode);
            sb.AppendLine("game_mode=" + GameMode);
            sb.AppendLine("concurrency=" + Concurrency);
            sb.AppendLine("retries=" + Retries);
            sb.AppendLine("timeout=" + Timeout);
            sb.AppendLine("bp_limit=" + BpLimit);
            sb.AppendLine("state_ranked=" + (StateRanked ? "1" : "0"));
            sb.AppendLine("state_qualified=" + (StateQualified ? "1" : "0"));
            sb.AppendLine("state_loved=" + (StateLoved ? "1" : "0"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static int Int(string s, int d)
        {
            int v;
            return int.TryParse(s, out v) ? v : d;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static bool Bool(string s, bool d)
        {
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return d;
        }
    }
}
