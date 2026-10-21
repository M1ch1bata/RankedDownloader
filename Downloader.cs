using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RankedDownloader
{
    /// <summary>单个下载任务</summary>
    public class DlTask
    {
        public long Sid { get; set; }
        public string Title { get; set; }        // 显示名(通常 "artist - title")
        public string FileName { get; set; }     // 目标文件名(不含路径)
        public string Artist { get; set; }
        public string ModeName { get; set; }     // 显示模式
        public long TotalBytes { get; set; }
        public long DoneBytes { get; set; }
        public double Speed { get; set; }        // 字节/秒
        public string State { get; set; }        // 等待中/获取信息/下载中/完成/已存在/失败/已取消
        public string Error { get; set; }
        public int Percent { get { return TotalBytes > 0 ? (int)(DoneBytes * 100 / TotalBytes) : 0; } }

        /// <summary>
        /// 本地标准命名: "{sid} {artist - title}.osz"(osu! 惯例, 与 Python 版一致)。
        /// 不依赖服务器 Content-Disposition, 保证文件名完整。
        /// </summary>
        public static string BuildFileName(long sid, string artist, string title)
        {
            string name = title;
            if (string.IsNullOrWhiteSpace(name)) name = artist;
            name = SanitizeComponent(name);
            if (string.IsNullOrEmpty(name)) return sid + ".osz";
            string baseName = sid + " " + name;
            if (baseName.Length > 160)
            {
                int cut = 160;
                // 不能在代理对(emoji 等)中间截断, 否则文件名含半个代理字符
                if (cut > 0 && char.IsHighSurrogate(baseName[cut - 1])) cut--;
                baseName = baseName.Substring(0, cut);
            }
            baseName = baseName.TrimEnd(' ', '.');
            return baseName + ".osz";
        }

        private static string SanitizeComponent(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0) sb.Append(' ');
                else sb.Append(c);
            }
            var s = Regex.Replace(sb.ToString(), "\\s+", " ");
            return s.Trim(' ', '.');
        }
    }

    /// <summary>下载引擎: 并发 + 断点续传 + 暂停/继续 + 重试 + 进度回调。</summary>
    public class Downloader
    {
        public event Action<DlTask> TaskUpdated;   // UI 刷新回调(工作线程)
        public event Action<string> Log;           // 日志回调(工作线程)
        public event Action AllDone;               // 队列清空(工作线程)

        private readonly Queue<long> _queue = new Queue<long>();
        private readonly Dictionary<long, DlTask> _tasks = new Dictionary<long, DlTask>();
        private readonly string _songsDir;
        private readonly string _variant;          // full / novideo / mini
        private readonly string _server;           // auto / Telecom / cmcc / ...
        private readonly int _threads;
        private readonly int _retries;             // 每个任务失败重试次数
        private readonly int _timeout;             // 秒
        private readonly AppStore _store;          // 持久化记录(可为 null)
        // 磁盘已存在的 setid(下载前查重)
        private readonly HashSet<long> _existingOnDisk = new HashSet<long>();
        private readonly object _lock = new object();
        private int _running;
        private bool _cancel;
        private bool _doneNotified;
        private volatile bool _paused;
        private readonly ManualResetEvent _pauseWaiter = new ManualResetEvent(true);

        public Downloader(string songsDir, string variant, int threads,
            string server = "auto", int retries = 3, int timeout = 60, AppStore store = null)
        {
            _songsDir = songsDir;
            _variant = variant;
            _threads = Math.Max(1, Math.Min(threads, 8));
            _server = string.IsNullOrEmpty(server) ? "auto" : server;
            _retries = Math.Max(0, Math.Min(retries, 10));
            _timeout = Math.Max(10, Math.Min(timeout, 600));
            _store = store;
        }

        public int QueueCount { get { lock (_lock) return _queue.Count; } }

        public bool IsCancelled { get { lock (_lock) return _cancel; } }

        /// <summary>队列空且无运行中任务(可用于分批追加任务间的等待)。</summary>
        public bool IsIdle { get { lock (_lock) return _queue.Count == 0 && _running == 0; } }

        /// <summary>该 setid 是否已存在(磁盘扫描结果 或 持久化记录)。用于收集后的预过滤。</summary>
        public bool IsAlreadyPresent(long sid)
        {
            lock (_lock)
            {
                if (_existingOnDisk.Contains(sid)) return true;
            }
            return _store != null && _store.IsDownloaded(sid, _songsDir);
        }

        public bool IsPaused { get { return _paused; } }

        /// <summary>暂停: 工作线程在任务边界与分块之间挂起, 连接保留。</summary>
        public void Pause()
        {
            _paused = true;
            _pauseWaiter.Reset();
            WriteLog("已暂停");
        }

        public void Resume()
        {
            _paused = false;
            _pauseWaiter.Set();
            WriteLog("已继续");
        }

        /// <summary>
        /// 下载前查重: 扫描 Songs 目录 + 持久化记录(store), 收集已存在的 setid。
        /// 数据源:
        ///   1. "&lt;setid&gt; artist - title.osz" 文件(含未下载完的 .part 不算)
        ///   2. "&lt;setid&gt; artist - title" 已解压谱面文件夹
        ///   3. 非标准命名的文件夹/文件: 读取文件夹内 .osu 文件的 BeatmapSetID(文本格式, 稳定)
        /// </summary>
        public void ScanExistingSongs()
        {
            var found = new HashSet<long>();
            try
            {
                if (!Directory.Exists(_songsDir)) return;

                foreach (var f in Directory.GetFiles(_songsDir, "*.osz"))
                {
                    long sid;
                    if (TryParseSetId(Path.GetFileNameWithoutExtension(f), out sid, false) && sid > 0)
                        found.Add(sid);
                }
                foreach (var d in Directory.GetDirectories(_songsDir))
                {
                    long sid;
                    if (TryParseSetId(Path.GetFileName(d), out sid, true) && sid > 0)
                    {
                        found.Add(sid);
                        continue;
                    }
                    // 非标准命名: 读该文件夹内 .osu 文件的 BeatmapSetID
                    sid = TryReadSetIdFromOsuFile(d);
                    if (sid > 0) found.Add(sid);
                }
            }
            catch (Exception ex)
            {
                WriteLog("扫描已存在谱面失败: " + ex.Message);
            }
            lock (_lock)
            {
                _existingOnDisk.Clear();
                foreach (var s in found) _existingOnDisk.Add(s);
            }
            int recorded = 0;
            if (_store != null) recorded = _store.DownloadedCount;
            WriteLog("已存在谱面 " + _existingOnDisk.Count + " 个(文件/文件夹) + " + recorded + " 个(历史记录)");
        }

        /// <summary>读取谱面文件夹中 .osu 文本文件的 "BeatmapSetID:" 字段; 失败返回 0。</summary>
        private static long TryReadSetIdFromOsuFile(string folder)
        {
            try
            {
                var osuFiles = Directory.GetFiles(folder, "*.osu");
                if (osuFiles.Length == 0) return 0;
                using (var sr = new StreamReader(osuFiles[0], System.Text.Encoding.UTF8, true))
                {
                    for (int i = 0; i < 120; i++)
                    {
                        var line = sr.ReadLine();
                        if (line == null) break;
                        if (line.StartsWith("BeatmapSetID:", StringComparison.OrdinalIgnoreCase))
                        {
                            long v;
                            if (long.TryParse(line.Substring(13).Trim(), out v)) return v;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 从 "12345 Artist - Title" 形式的名字中提取 setid。
        /// requireSpace=true(文件夹): 数字后必须为空格, 排除纯数字目录;
        /// requireSpace=false(文件): 允许 "12345.osz" 纯数字兜底命名。
        /// </summary>
        private static bool TryParseSetId(string name, out long sid, bool requireSpace)
        {
            sid = 0;
            if (string.IsNullOrEmpty(name)) return false;
            int i = 0;
            while (i < name.Length && name[i] >= '0' && name[i] <= '9') i++;
            if (i == 0) return false;
            if (i < name.Length)
            {
                if (name[i] != ' ') return false;
            }
            else if (requireSpace)
            {
                return false;
            }
            return long.TryParse(name.Substring(0, i), out sid);
        }

        /// <summary>加入任务(按 sid 去重)。title 用于显示。</summary>
        public void Enqueue(long sid, string title)
        {
            Enqueue(sid, title, "", "");
        }

        public void Enqueue(long sid, string title, string artist, string modeName)
        {
            DlTask existing = null;
            lock (_lock)
            {
                if (_tasks.ContainsKey(sid)) return;   // 按 sid 去重(_tasks 即已见集合)
                var t = new DlTask { Sid = sid, Title = title, Artist = artist, ModeName = modeName, State = "等待中" };
                // 下载前查重: 磁盘已存在 或 历史记录(文件仍在且目录一致)
                bool recorded = _store != null && _store.IsDownloaded(sid, _songsDir);
                if (_existingOnDisk.Contains(sid) || recorded)
                {
                    t.State = "已存在";
                    t.Error = recorded ? "已下载(记录)" : "文件已存在";
                    _tasks[sid] = t;
                    existing = t;
                }
                else
                {
                    _tasks[sid] = t;
                    _queue.Enqueue(sid);
                }
            }
            if (existing != null) Emit(existing);   // 锁外回调, 避免持锁触发 UI
        }

        public void Start()
        {
            for (int i = 0; i < _threads; i++)
            {
                var th = new Thread(WorkerLoop) { IsBackground = true };
                th.Start();
            }
        }

        /// <summary>供外部(收集阶段)写日志。</summary>
        public void WriteLog(string s)
        {
            var h = Log;
            if (h != null) h(s);
        }

        /// <summary>供外部在收集阶段出错/结束时触发完成通知。</summary>
        public void Finish()
        {
            var cb = AllDone;
            if (cb != null) cb();
        }

        /// <summary>取消: 清空队列, 标记排队任务, 确保 AllDone 一定触发。</summary>
        public void Cancel()
        {
            List<DlTask> waiting = new List<DlTask>();
            lock (_lock)
            {
                _cancel = true;
                _paused = false;
                _pauseWaiter.Set();
                while (_queue.Count > 0)
                {
                    long sid = _queue.Dequeue();
                    DlTask t;
                    if (_tasks.TryGetValue(sid, out t))
                    {
                        if (t.State == "等待中" || t.State == "获取信息")
                        {
                            t.State = "已取消";
                            waiting.Add(t);
                        }
                    }
                }
            }
            foreach (var t in waiting) Emit(t);
        }

        private void WorkerLoop()
        {
            while (!_cancel)
            {
                // 暂停时挂起(不取新任务)
                while (_paused && !_cancel) _pauseWaiter.WaitOne(500);
                if (_cancel) break;

                long sid;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    sid = _queue.Dequeue();
                    _running++;
                }
                DlTask task;
                lock (_lock) task = _tasks[sid];
                try { RunTask(task); }
                catch (Exception ex)
                {
                    task.State = "失败";
                    task.Error = ex.Message;
                    RecordFailed(task);
                    if (Log != null) Log("任务失败 " + task.Title + ": " + ex.Message);
                    Emit(task);
                }
                lock (_lock) _running--;
            }

            // 收尾: 无论因队列耗尽还是被取消, 只要没有在跑的任务, 就触发一次 AllDone
            lock (_lock)
            {
                if (_running == 0 && !_doneNotified)
                {
                    _doneNotified = true;
                    var cb = AllDone;
                    if (cb != null) ThreadPool.QueueUserWorkItem(_ => cb());
                }
            }
        }

        private void Emit(DlTask t) { var h = TaskUpdated; if (h != null) h(t); }

        private void RecordFailed(DlTask task)
        {
            if (_store == null) return;
            try { _store.AddFailed(task.Sid, task.ModeName ?? "", task.Artist ?? "", task.Title ?? "", task.Error ?? ""); }
            catch { }
        }

        private void RunTask(DlTask task)
        {
            if (_cancel) { task.State = "已取消"; Emit(task); return; }

            task.State = "获取信息";
            Emit(task);

            int attempts = _retries + 1;
            string lastError = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (_cancel) { task.State = "已取消"; Emit(task); return; }
                while (_paused && !_cancel) _pauseWaiter.WaitOne(500);
                try
                {
                    RunTaskOnce(task);
                    if (task.State == "完成" || task.State == "已存在") return;
                    lastError = task.Error;
                    // 源缺失(403/404/HTML)不重试, 并丢弃残留 .part(留着只会让下次续传到坏位置)
                    if (lastError != null && lastError.StartsWith("源缺失"))
                    {
                        TryDeleteFile(PartPath(task.Sid));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
                if (attempt < attempts)
                {
                    if (Log != null) Log("重试(" + attempt + "/" + _retries + ") " + task.Title + ": " + lastError);
                    // 退避等待期间响应暂停/取消
                    for (int w = 0; w < 15 && !_cancel; w++)
                    {
                        while (_paused && !_cancel) _pauseWaiter.WaitOne(500);
                        Thread.Sleep(100);
                    }
                }
            }
            // 全部尝试失败
            task.State = "失败";
            task.Error = lastError ?? "未知错误";
            task.Speed = 0;
            RecordFailed(task);
            if (Log != null) Log("失败: " + task.Title + " - " + task.Error);
            Emit(task);
        }

        /// <summary>单次下载尝试(含 302 解析、断点续传、流式写入)。</summary>
        private void RunTaskOnce(DlTask task)
        {
            try
            {
                RunTaskOnceCore(task);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    if (code == 403 || code == 404)
                        throw new Exception("源缺失(HTTP " + code + ")");
                }
                throw;
            }
        }

        /// <summary>续传位置已失效(416 / 服务器虽 200 但忽略 Range)时抛出, 由上层丢弃 .part 重下。</summary>
        private sealed class RangeInvalidException : Exception
        {
            public RangeInvalidException(string message) : base(message) { }
        }

        private string PartPath(long sid)
        {
            return Path.Combine(_songsDir, sid + ".osz.part");
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void RunTaskOnceCore(DlTask task)
        {
            var part = PartPath(task.Sid);
            long resume = 0;
            if (File.Exists(part))
            {
                try { resume = new FileInfo(part).Length; } catch { resume = 0; }
            }

            bool restarted = false;
            while (true)
            {
                if (_cancel) { task.State = "已取消"; task.Speed = 0; Emit(task); return; }
                try
                {
                    DownloadAttempt(task, part, resume);
                    return;
                }
                catch (RangeInvalidException ex)
                {
                    // .part 比远端长(或已完整) / 服务器不认 Range: 丢弃后从 0 重下, 只重启一次
                    if (restarted) throw new Exception("续传失败: " + ex.Message);
                    restarted = true;
                    if (Log != null) Log("续传位置失效(" + ex.Message + "), 丢弃 .part 重新下载: " + task.Sid);
                    TryDeleteFile(part);
                    resume = 0;
                }
            }
        }

        /// <summary>一次完整的 GET + 落盘尝试。失败抛出, 终态通过 task.State 表达。</summary>
        private void DownloadAttempt(DlTask task, string part, long resume)
        {
            var direct = ResolveRedirect(SayobotApi.DlBase + _variant + "/" + task.Sid + "?server=" + _server);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(direct);
            req.Method = "GET";
            req.Timeout = _timeout * 1000;
            req.ReadWriteTimeout = _timeout * 1000;
            req.UserAgent = "SayoMapsDownloader/3.2 (compatible; RankedDownloader)";
            req.Referer = "https://osu.sayobot.cn/SayoMapsDownloader.exe";
            req.AllowAutoRedirect = true;
            if (resume > 0) req.AddRange(resume);

            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 416)
                    throw new RangeInvalidException("HTTP 416(本地 .part 与远端不一致)");
                throw;
            }

            using (resp)
            {
                // Range 请求必须得到 206; 若服务器返回 200 而继续 Append, 会产生"看似完整"的损坏文件
                if (resume > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new RangeInvalidException("服务器返回 " + (int)resp.StatusCode + " 而非 206");

                long contentLength = resp.ContentLength;
                long total = contentLength >= 0 ? resume + contentLength : -1;
                if (contentLength < 0 && Log != null)
                    Log("警告: 服务器未提供 Content-Length, 跳过大小校验: " + task.Sid);

                var ct = (resp.ContentType ?? "").ToLowerInvariant();
                if (ct.Contains("text/html"))
                    throw new Exception("源缺失(服务器返回错误页)");

                // 服务器文件名仅作兜底参考; 优先使用本地标准命名(保证含 artist - title)
                string serverName = null;
                var cd = resp.Headers["Content-Disposition"];
                if (!string.IsNullOrEmpty(cd))
                {
                    var m = Regex.Match(cd, "filename=\"?([^\";]+)\"?");
                    if (m.Success)
                    {
                        // 只取文件名, 防止服务器返回带路径的名字写到目录外
                        var fn = Path.GetFileName(Uri.UnescapeDataString(m.Groups[1].Value));
                        if (fn.EndsWith(".osz", StringComparison.OrdinalIgnoreCase)) serverName = fn;
                    }
                }

                bool hasMeta = !string.IsNullOrWhiteSpace(task.Title) && task.Title != task.Sid.ToString();
                string fileName = hasMeta
                    ? DlTask.BuildFileName(task.Sid, task.Artist, task.Title)
                    : (serverName ?? (task.Sid + ".osz"));

                var target = Path.Combine(_songsDir, fileName);
                if (File.Exists(target))
                {
                    task.State = "已存在";
                    task.FileName = fileName;
                    task.Error = "文件已存在";
                    TryDeleteFile(part);
                    if (Log != null) Log("跳过(已存在): " + fileName);
                    Emit(task);
                    return;
                }

                task.State = "下载中";
                task.FileName = fileName;
                task.TotalBytes = total > 0 ? total : 0;
                task.DoneBytes = resume;   // 已落盘的部分计入进度(续传也能显示到 100%)
                task.Speed = 0;
                Emit(task);

                using (var s = resp.GetResponseStream())
                using (var fs = new FileStream(part, FileMode.Append, FileAccess.Write))
                {
                    var buf = new byte[81920];
                    long lastEmitBytes = task.DoneBytes;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!_cancel)
                    {
                        if (_paused) { _pauseWaiter.WaitOne(500); sw.Stop(); continue; }
                        sw.Start();
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        task.DoneBytes += n;
                        if (task.DoneBytes - lastEmitBytes > 256 * 1024 || (total > 0 && task.DoneBytes >= total))
                        {
                            double sec = sw.Elapsed.TotalSeconds;
                            if (sec > 0.2)
                            {
                                task.Speed = (task.DoneBytes - lastEmitBytes) / sec;
                                sw.Reset();
                                sw.Start();
                                lastEmitBytes = task.DoneBytes;
                            }
                            Emit(task);
                        }
                    }
                }

                if (_cancel)
                {
                    task.State = "已取消";
                    task.Speed = 0;
                    if (Log != null) Log("已取消: " + fileName);
                    Emit(task);
                    return;
                }

                // 用 .part 实际长度校验完整性(计数器在重试/续传时可能不可靠)
                long actual = 0;
                try { actual = new FileInfo(part).Length; } catch { }
                if (contentLength >= 0 && actual != total)
                {
                    task.State = "失败";
                    task.Error = "文件不完整 " + actual + "/" + total;
                    task.Speed = 0;
                    if (Log != null) Log("文件不完整: " + fileName + " " + actual + "/" + total);
                    Emit(task);
                    return;
                }

                try
                {
                    File.Move(part, target);
                }
                catch (Exception ex)
                {
                    // 目标已存在(例如另一个进程刚下好): 保留已有文件, 清掉 .part
                    if (File.Exists(target))
                    {
                        task.State = "已存在";
                        task.FileName = fileName;
                        task.Error = "文件已存在";
                        TryDeleteFile(part);
                        if (Log != null) Log("跳过(已存在): " + fileName);
                        Emit(task);
                        return;
                    }
                    throw new Exception("保存失败: " + ex.Message);
                }

                task.State = "完成";
                task.Speed = 0;
                if (_store != null)
                {
                    try { _store.MarkDownloaded(task.Sid, target); } catch { }
                }
                if (Log != null) Log("完成: " + fileName + (total > 0 ? " (" + (total / 1048576.0).ToString("0.0") + " MB)" : ""));
                Emit(task);
            }
        }

        /// <summary>跟随 302, 返回最终直链。</summary>
        public static string ResolveRedirect(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.Timeout = 30000;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                {
                    var loc = resp.Headers["Location"];
                    if (!string.IsNullOrEmpty(loc)) return loc;
                }
                if (code == 200) return url;
                if (code == 403 || code == 404)
                    throw new Exception("源缺失(HTTP " + code + ")");
                throw new Exception("HTTP " + code);
            }
        }
    }
}
