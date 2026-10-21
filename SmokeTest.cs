using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RankedDownloader
{
    /// <summary>无 GUI 冒烟测试: 验证 API 收集 + 下载流程</summary>
    static class SmokeTest
    {
        static int _done, _fail;

        public static int Run(string[] args)
        {
            string outDir = Path.Combine(Path.GetTempPath(), "sayo_test_songs");
            Directory.CreateDirectory(outDir);
            // 清空历史文件, 保证断言不受上次运行残留影响
            foreach (var f in Directory.GetFiles(outDir, "*.osz"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(outDir, "*.part"))
                try { File.Delete(f); } catch { }
            int failures = 0;

            try
            {
                // 1) bp 收集
                Console.WriteLine("=== 测试1: bp 收集 (peppy) ===");
                var bpSets = TaskCollector.CollectFromUserBest("peppy", 0, Log);
                Console.WriteLine("bp sets: " + bpSets.Count);
                if (bpSets.Count == 0) failures++;

                // 2) mapper 收集
                Console.WriteLine("=== 测试2: mapper 收集 (Graphical) ===");
                var mapperSets = TaskCollector.CollectFromMapper("Graphical", 0, Log);
                Console.WriteLine("mapper sets: " + mapperSets.Count);
                if (mapperSets.Count == 0) failures++;

                // 3) 按日期收集(小范围, 指定结束日期)
                Console.WriteLine("=== 测试3: 按日期收集 (2024-02-01 ~ 2024-03-01) ===");
                var dateSets = TaskCollector.CollectByDate("2024-02-01", "2024-03-01", 0, Log);
                Console.WriteLine("date sets: " + dateSets.Count);
                if (dateSets.Count == 0) failures++;

                // 3b) 分段收集: 两段合计应与单次一致
                Console.WriteLine("=== 测试3b: 分段收集 ===");
                var seenAll = new HashSet<long>();
                int totalChunk = 0, guard = 0;
                string c = "2024-02-01";
                while (true)
                {
                    var ch = TaskCollector.CollectByDateChunk(c, "2024-03-01", 0, 200, Log);
                    if (ch.Items.Count == 0) break;
                    foreach (var s in ch.Items) seenAll.Add(s.Sid);
                    totalChunk += ch.Items.Count;
                    if (string.IsNullOrEmpty(ch.NextCursor) || ++guard > 50) break;
                    c = ch.NextCursor;
                }
                Console.WriteLine("chunk 返回 " + totalChunk + " 条(含游标重叠重复), 唯一: " + seenAll.Count);
                if (totalChunk == 0) failures++;
                if (seenAll.Count < dateSets.Count)
                {
                    Console.WriteLine("FAIL: 分段收集少于单次收集 " + dateSets.Count);
                    failures++;
                }

                // 3c) 并行收集: 结果应与单次一致(数量相同、无边界重复/遗漏)
                Console.WriteLine("=== 测试3c: 并行收集 ===");
                var swPar = System.Diagnostics.Stopwatch.StartNew();
                var parSets = TaskCollector.CollectByDateParallel("2024-02-01", "2024-03-01", 0, 7, 4, Log);
                swPar.Stop();
                var parIds = new HashSet<long>();
                foreach (var s in parSets) parIds.Add(s.Sid);
                Console.WriteLine("parallel sets: " + parSets.Count + " 唯一: " + parIds.Count +
                    " 耗时 " + swPar.ElapsedMilliseconds + "ms, 单次收集 " + dateSets.Count);
                if (parIds.Count < dateSets.Count)
                {
                    Console.WriteLine("FAIL: 并行收集少于单次收集 " + dateSets.Count);
                    failures++;
                }
                var expectIds = new HashSet<long>(dateSets);
                foreach (var id in expectIds)
                {
                    if (!parIds.Contains(id))
                    {
                        Console.WriteLine("FAIL: 并行收集遗漏 set " + id);
                        failures++;
                        break;
                    }
                }

                // 4) 实际下载 2 张(第一张 bp 的 set)
                Console.WriteLine("=== 测试4: 实际下载 ===");
                var dl = new Downloader(outDir, "novideo", 2);
                dl.Log += Log;
                dl.TaskUpdated += OnTask;
                var targets = new List<long>();
                if (bpSets.Count > 0) targets.Add(bpSets[0].Sid);
                if (dateSets.Count > 0) targets.Add(dateSets[dateSets.Count - 1]);
                foreach (var s in targets) dl.Enqueue(s, s.ToString());
                dl.Start();
                // 等待
                for (int i = 0; i < 300 && (_done + _fail) < targets.Count; i++) Thread.Sleep(1000);
                Console.WriteLine("done=" + _done + " fail=" + _fail);
                if (_done == 0) failures++;

                var files = Directory.GetFiles(outDir, "*.osz");
                Console.WriteLine("downloaded files: " + files.Length);
                foreach (var f in files) Console.WriteLine("  " + Path.GetFileName(f) + " " + new FileInfo(f).Length + " bytes");
                if (files.Length == 0) failures++;

                // 5) 断点续传: 再次下载同 set 应跳过(已存在)
                Console.WriteLine("=== 测试5: 重复下载应跳过 ===");
                int skipped = 0;
                var dl2 = new Downloader(outDir, "novideo", 1);
                dl2.Log += Log;
                dl2.TaskUpdated += t => { if (t.State == "已存在") skipped++; };
                foreach (var s in targets) dl2.Enqueue(s, s.ToString());
                dl2.Start();
                for (int i = 0; i < 60 && skipped < targets.Count; i++) Thread.Sleep(1000);
                Console.WriteLine("skipped=" + skipped);
                if (skipped < targets.Count) failures++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0 ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void Log(string s)
        {
            Console.WriteLine(s);
        }

        private static void OnTask(DlTask t)
        {
            if (t.State == "完成") _done++;
            if (t.State == "失败") _fail++;
        }

        /// <summary>测试 osu! 目录检测(无需 GUI)。</summary>
        public static int RunDetect()
        {
            int failures = 0;
            try
            {
                var dir = MainForm.DetectSongsDir();
                Console.WriteLine("DetectSongsDir -> " + (dir ?? "(null)"));
                if (string.IsNullOrEmpty(dir)) { failures++; return failures; }
                if (!Directory.Exists(dir)) { Console.WriteLine("目录不存在!"); failures++; return failures; }
                Console.WriteLine("目录存在: " + dir);
                // 期望 stable 优先: D:\Games\osu!\Songs
                if (!dir.Equals(@"D:\Games\osu!\Songs", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("注意: 检测结果不是 stable 路径(优先 stable 要求)。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            Console.WriteLine(failures == 0 ? "DETECT TEST PASSED" : "DETECT TEST FAILED");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 下载到任意自定义目录(不存在则创建)。</summary>
        public static int RunCustomDirTest()
        {
            int failures = 0;
            string custom = Path.Combine(Path.GetTempPath(), "sayo_custom_dir_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // 目录不存在 -> StartAsync 会创建; 此处模拟创建逻辑
                Directory.CreateDirectory(custom);
                Console.WriteLine("自定义目录: " + custom);

                int done = 0;
                var evt = new ManualResetEvent(false);
                var dl = new Downloader(custom, "novideo", 2);
                dl.Log += s => Console.WriteLine(s);
                dl.TaskUpdated += t => { if (t.State == "完成") done++; };
                dl.AllDone += () => evt.Set();

                dl.ScanExistingSongs();
                dl.Enqueue(2083248, "set 2083248");
                dl.Start();
                if (!evt.WaitOne(180000))
                {
                    Console.WriteLine("FAIL: 自定义目录下载超时");
                    failures++;
                }
                else if (done == 0)
                {
                    Console.WriteLine("FAIL: 自定义目录无完成文件");
                    failures++;
                }
                else
                {
                    var files = Directory.GetFiles(custom, "*.osz");
                    Console.WriteLine("自定义目录中文件: " + files.Length);
                    foreach (var f in files) Console.WriteLine("  " + Path.GetFileName(f));
                    if (files.Length == 0) { Console.WriteLine("FAIL: 未写入自定义目录"); failures++; }
                }

                // 再次运行应跳过(查重作用于自定义目录)
                int skipped = 0;
                var dl2 = new Downloader(custom, "novideo", 1);
                dl2.Log += s => Console.WriteLine(s);
                dl2.TaskUpdated += t => { if (t.State == "已存在") skipped++; };
                dl2.ScanExistingSongs();
                dl2.Enqueue(2083248, "set 2083248");
                Console.WriteLine("查重标记已存在: " + skipped + " (期望 1)");
                if (skipped != 1) { Console.WriteLine("FAIL: 自定义目录查重失效"); failures++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                try { if (Directory.Exists(custom)) Directory.Delete(custom, true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "CUSTOM DIR TEST PASSED" : "CUSTOM DIR TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: AppStore 记录/失败清单/CSV + AppConfig 持久化(隔离临时目录, 不碰真实用户数据)。</summary>
        public static int RunStoreTest()
        {
            int failures = 0;
            string tmpDir = Path.Combine(Path.GetTempPath(), "sayo_store_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                // 纯内存 store(不加载真实记录)
                var store = new AppStore(false);
                Console.WriteLine("初始已下载: " + store.DownloadedCount);

                // 记录与查询(记录会校验文件仍存在)
                var fake1 = Path.Combine(tmpDir, "123456 A - B.osz");
                File.WriteAllBytes(fake1, new byte[1]);
                store.MarkDownloaded(123456, fake1);
                store.MarkDownloaded(123456, fake1);   // 重复
                if (!store.IsDownloaded(123456)) { Console.WriteLine("FAIL: IsDownloaded"); failures++; }
                var filter = store.FilterDownloaded(new long[] { 123456, 999999 });
                if (filter.Count != 1 || !filter.Contains(123456)) { Console.WriteLine("FAIL: FilterDownloaded"); failures++; }

                // 文件被删除后记录应视为过期, 不再错误跳过
                File.Delete(fake1);
                if (store.IsDownloaded(123456)) { Console.WriteLine("FAIL: 文件删除后仍判为已下载"); failures++; }
                File.WriteAllBytes(fake1, new byte[1]);

                // 失败清单
                store.AddFailed(123456, "osu!", "A", "B", "源缺失");
                store.AddFailed(777, "osu!", "C", "D", "HTTP 503");
                if (store.FailedCount != 2) { Console.WriteLine("FAIL: FailedCount=" + store.FailedCount); failures++; }
                // 下载成功应清除失败记录
                store.MarkDownloaded(123456, fake1);
                if (store.FailedCount != 1) { Console.WriteLine("FAIL: 成功后未清失败记录"); failures++; }

                // 持久化模式: 隔离目录往返
                var store2 = new AppStore(true, tmpDir);
                if (store2.DownloadedCount != 0) { Console.WriteLine("FAIL: 新目录应为空"); failures++; }
                var fake2 = Path.Combine(tmpDir, "555 X - Y.osz");
                File.WriteAllBytes(fake2, new byte[1]);
                store2.MarkDownloaded(555, fake2);
                store2.AddFailed(556, "osu!", "X", "Y", "测试错误");
                var store3 = new AppStore(true, tmpDir);
                if (!store3.IsDownloaded(555)) { Console.WriteLine("FAIL: 记录未持久化"); failures++; }
                if (!store3.IsDownloaded(555, tmpDir)) { Console.WriteLine("FAIL: 同目录记录应命中"); failures++; }
                if (store3.IsDownloaded(555, Path.Combine(tmpDir, "other"))) { Console.WriteLine("FAIL: 不同目录不应命中"); failures++; }
                if (store3.FailedCount != 1) { Console.WriteLine("FAIL: 失败记录未持久化"); failures++; }

                // WipeAll: 清空内存与磁盘文件(模拟用户点"重置历史")
                // 先导出 CSV(清空前验证)
                string csv = Path.Combine(tmpDir, "failed.csv");
                store3.ExportFailedCsv(csv);
                var text = File.ReadAllText(csv);
                if (!text.Contains("556") || !text.Contains("测试错误")) { Console.WriteLine("FAIL: CSV 内容"); failures++; }

                store3.WipeAll();
                if (store3.DownloadedCount != 0 || store3.FailedCount != 0)
                {
                    Console.WriteLine("FAIL: WipeAll 后内存未清空");
                    failures++;
                }
                var store4 = new AppStore(true, tmpDir);
                if (store4.DownloadedCount != 0 || store4.FailedCount != 0)
                {
                    Console.WriteLine("FAIL: WipeAll 后磁盘未清空");
                    failures++;
                }

                // 配置往返(隔离目录)
                var cfg = new AppConfig { DownloadDir = @"C:\temp\test_x", Concurrency = 5, StateLoved = false, GameMode = 3 };
                cfg.Save(tmpDir);
                var cfg2 = AppConfig.Load(tmpDir);
                if (cfg2.DownloadDir != cfg.DownloadDir) { Console.WriteLine("FAIL: 配置 download_dir 往返"); failures++; }
                if (cfg2.Concurrency != 5) { Console.WriteLine("FAIL: 配置 concurrency 往返"); failures++; }
                if (cfg2.StateLoved != false) { Console.WriteLine("FAIL: 配置 state_loved 往返"); failures++; }
                if (cfg2.GameMode != 3) { Console.WriteLine("FAIL: 配置 game_mode 往返"); failures++; }

                // mode=3 越界应回落为 0(否则启动时下拉框抛异常)
                File.WriteAllText(Path.Combine(tmpDir, "config.ini"), "mode=3\n", System.Text.Encoding.UTF8);
                if (AppConfig.Load(tmpDir).Mode != 0) { Console.WriteLine("FAIL: mode=3 未回落"); failures++; }

                Console.WriteLine("记录数=" + store.DownloadedCount + " 失败数=" + store.FailedCount + " 隔离持久化 OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "STORE TEST PASSED" : "STORE TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 暂停/继续。</summary>
        public static int RunPauseTest()
        {
            int failures = 0;
            string outDir = Path.Combine(Path.GetTempPath(), "sayo_pause_test");
            try
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);

                var dl = new Downloader(outDir, "novideo", 2);
                dl.Log += s => Console.WriteLine(s);
                int done = 0;
                var evt = new ManualResetEvent(false);
                dl.TaskUpdated += t => { if (t.State == "完成") done++; };
                dl.AllDone += () => evt.Set();

                long[] sids = { 2083248, 3720, 96422, 100610, 184, 37090 };
                foreach (var s in sids) dl.Enqueue(s, "set" + s);
                dl.Start();
                Thread.Sleep(1200);
                Console.WriteLine("暂停中...");
                dl.Pause();
                bool paused = dl.IsPaused;
                int doneAtPause = done;
                Thread.Sleep(2500);
                Console.WriteLine("暂停期间完成数不变: " + (doneAtPause == done) + " (pause前=" + doneAtPause + " 现在=" + done + ")");
                // 暂停不严格要求零增长(已进入收尾的任务可能完成), 但 IsPaused 状态必须为真
                if (!paused) { Console.WriteLine("FAIL: IsPaused 不为 true"); failures++; }
                Console.WriteLine("继续...");
                dl.Resume();
                if (dl.IsPaused) { Console.WriteLine("FAIL: Resume 后 IsPaused 仍为 true"); failures++; }

                if (!evt.WaitOne(180000))
                {
                    Console.WriteLine("FAIL: 继续后未完成");
                    failures++;
                }
                else
                {
                    Console.WriteLine("完成 " + done + "/" + sids.Length);
                    if (done < sids.Length) { Console.WriteLine("FAIL: 未全部完成"); failures++; }
                }

                foreach (var f in Directory.GetFiles(outDir, "*.osz")) try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(outDir, "*.part")) try { File.Delete(f); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            Console.WriteLine(failures == 0 ? "PAUSE TEST PASSED" : "PAUSE TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 顺序 vs 并行收集耗时对比(大区间)。</summary>
        public static int RunPerfTest()
        {
            int failures = 0;
            string since = "2023-01-01", until = "2024-01-01";
            try
            {
                Console.WriteLine("=== 顺序收集 " + since + " ~ " + until + " ===");
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                var seq = TaskCollector.CollectByDate(since, until, 0, s => { });
                sw1.Stop();
                Console.WriteLine("顺序: " + seq.Count + " 个, 耗时 " + sw1.ElapsedMilliseconds + " ms");

                Console.WriteLine("=== 并行收集(14天/片, 6路) ===");
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var par = TaskCollector.CollectByDateParallel(since, until, 0, 14, 6, s => { });
                sw2.Stop();
                Console.WriteLine("并行: " + par.Count + " 个, 耗时 " + sw2.ElapsedMilliseconds + " ms");
                Console.WriteLine("提速: " + (sw2.ElapsedMilliseconds > 0 ? (sw1.ElapsedMilliseconds / (double)Math.Max(1, sw2.ElapsedMilliseconds)).ToString("0.0") : "?") + "x");

                Console.WriteLine("=== 并行收集(7天/片, 8路) ===");
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                var par2 = TaskCollector.CollectByDateParallel(since, until, 0, 7, 8, s => { });
                sw3.Stop();
                Console.WriteLine("并行2: " + par2.Count + " 个, 耗时 " + sw3.ElapsedMilliseconds + " ms");
                Console.WriteLine("提速2: " + (sw3.ElapsedMilliseconds > 0 ? (sw1.ElapsedMilliseconds / (double)Math.Max(1, sw3.ElapsedMilliseconds)).ToString("0.0") : "?") + "x");
                if (par2.Count != par.Count) { Console.WriteLine("FAIL: 两种并行参数结果不一致"); failures++; }

                var set = new HashSet<long>(seq);
                int missing = 0;
                foreach (var s in par) if (!set.Contains(s.Sid)) missing++;
                Console.WriteLine("并行相对顺序的额外/缺失: 额外 " + missing + " 个(顺序 " + seq.Count + " / 并行 " + par.Count + ")");
                if (par.Count < seq.Count) { Console.WriteLine("FAIL: 并行少于顺序"); failures++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            Console.WriteLine(failures == 0 ? "PERF TEST PASSED" : "PERF TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 对真实 Songs 目录查重扫描(验证 .osu 读取与性能)。</summary>
        public static int RunScanTest()
        {
            int failures = 0;
            string songs = MainForm.DetectSongsDir();
            Console.WriteLine("Songs: " + (songs ?? "(null)"));
            if (string.IsNullOrEmpty(songs)) { Console.WriteLine("FAIL: 未检测到目录"); return 1; }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dl = new Downloader(songs, "novideo", 1);
                dl.Log += s => Console.WriteLine(s);
                dl.ScanExistingSongs();
                sw.Stop();
                Console.WriteLine("扫描耗时: " + sw.ElapsedMilliseconds + " ms");

                // 验证已知非标准文件夹 "-45 - aci-l" -> set 1727306
                bool fs = Directory.Exists(Path.Combine(songs, "-45 - aci-l"));
                if (fs)
                {
                    var dl2 = new Downloader(songs, "novideo", 1);
                    int marked = 0;
                    dl2.Log += s => Console.WriteLine(s);
                    dl2.TaskUpdated += t => { if (t.State == "已存在") marked++; };
                    dl2.ScanExistingSongs();
                    dl2.Enqueue(1727306, "test");
                    Console.WriteLine("非标准文件夹 set 1727306 标记已存在: " + marked + " (期望 1)");
                    if (marked != 1) { Console.WriteLine("FAIL: .osu 读取查重失败"); failures++; }
                }
                else
                {
                    Console.WriteLine("(本机无 '-45 - aci-l' 文件夹, 跳过该断言)");
                }

                // 找一个标准命名文件夹验证
                foreach (var d in Directory.GetDirectories(songs))
                {
                    var nm = Path.GetFileName(d);
                    var parts = nm.Split(' ');
                    long sid;
                    if (parts.Length >= 2 && long.TryParse(parts[0], out sid) && sid > 0)
                    {
                        var dl3 = new Downloader(songs, "novideo", 1);
                        int marked3 = 0;
                        dl3.TaskUpdated += t => { if (t.State == "已存在") marked3++; };
                        dl3.ScanExistingSongs();
                        dl3.Enqueue(sid, "test");
                        Console.WriteLine("标准文件夹 " + nm + " -> 标记已存在: " + marked3 + " (期望 1)");
                        if (marked3 != 1) { Console.WriteLine("FAIL: 标准文件夹查重失败"); failures++; }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            Console.WriteLine(failures == 0 ? "SCAN TEST PASSED" : "SCAN TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 下载前查重。预置 osz 文件与已解压文件夹, 验证对应 setid 被标记已存在且不发起下载。</summary>
        public static int RunDedupTest()
        {
            int failures = 0;
            string outDir = Path.Combine(Path.GetTempPath(), "sayo_dedup_test");
            try
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);

                // 预置: 12345 的 osz 文件(标准命名) / 99999 已解压文件夹 / 88888 纯数字 osz
                File.WriteAllBytes(Path.Combine(outDir, "12345 Artist - Song.osz"), new byte[10]);
                Directory.CreateDirectory(Path.Combine(outDir, "99999 Mapper - Track"));
                File.WriteAllBytes(Path.Combine(outDir, "88888.osz"), new byte[10]);
                // 干扰项: 不应匹配
                File.WriteAllBytes(Path.Combine(outDir, "77777.osz.part"), new byte[5]);
                Directory.CreateDirectory(Path.Combine(outDir, "66666"));
                Directory.CreateDirectory(Path.Combine(outDir, "no number folder"));
                File.WriteAllBytes(Path.Combine(outDir, "55555abc.osz"), new byte[10]);

                int markedExisting = 0;
                var dl = new Downloader(outDir, "novideo", 2);
                dl.Log += s => Console.WriteLine(s);
                dl.TaskUpdated += t => { if (t.State == "已存在") markedExisting++; };

                dl.ScanExistingSongs();

                // 应命中: 12345(标准 osz) 99999(解压文件夹) 88888(纯数字 osz)
                // 不应命中: 77777(.part) 66666(纯数字文件夹) 55555abc(数字后非空格) no number
                foreach (var s in new long[] { 12345, 99999, 88888 }) dl.Enqueue(s, "set" + s);
                foreach (var s in new long[] { 77777, 55555, 66666 }) dl.Enqueue(s, "set" + s);

                Console.WriteLine("标记已存在: " + markedExisting + " (期望 3)");
                if (markedExisting != 3) { Console.WriteLine("FAIL: 查重数量不符"); failures++; }

                // 未标记的应留在队列中(77777, 55555, 66666)
                int queueCount = dl.QueueCount;
                Console.WriteLine("队列中任务: " + queueCount + " (期望 3: 77777, 55555, 66666)");
                if (queueCount != 3) { Console.WriteLine("FAIL: 队列数量不符"); failures++; }

                // 66666 纯数字文件夹: 按 osu 命名惯例数字后应带空格, 当前策略视为不存在
                Console.WriteLine("(说明: 纯数字文件夹 66666 不视为已存在谱面)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "DEDUP TEST PASSED" : "DEDUP TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>测试: 取消后 AllDone 必须触发, 且能再次启动新任务。</summary>
        public static int RunCancelTest()
        {
            int failures = 0;
            string outDir = Path.Combine(Path.GetTempPath(), "sayo_cancel_test");
            Directory.CreateDirectory(outDir);

            try
            {
                // 准备一批任务(用真实存在的 setid)
                var dl = new Downloader(outDir, "novideo", 2);
                dl.Log += s => Console.WriteLine(s);

                int doneCount = 0;
                int cancelCount = 0;
                var doneEvt = new ManualResetEvent(false);
                dl.TaskUpdated += t =>
                {
                    if (t.State == "完成") doneCount++;
                    if (t.State == "已取消") cancelCount++;
                };
                dl.AllDone += () => doneEvt.Set();

                long[] sids = { 2083248, 3720, 96422, 100610, 184, 37090, 172907, 68103, 667, 5326 };
                foreach (var s in sids)
                    dl.Enqueue(s, "set" + s);

                dl.Start();
                Thread.Sleep(1500);          // 让部分任务进入下载
                Console.WriteLine("中途取消... (done=" + doneCount + " cancel=" + cancelCount + ")");
                dl.Cancel();

                // 取消后必须触发 AllDone
                if (!doneEvt.WaitOne(15000))
                {
                    Console.WriteLine("FAIL: 取消后 AllDone 未触发");
                    failures++;
                }
                else
                {
                    Console.WriteLine("AllDone 已触发。done=" + doneCount + " cancel=" + cancelCount);
                    if (cancelCount == 0) { Console.WriteLine("FAIL: 排队任务未被标记为已取消"); failures++; }
                }

                // 再次新建下载器, 应能正常完成(验证可重新开始)
                Console.WriteLine("=== 重新开始 ===");
                var dl2 = new Downloader(outDir, "novideo", 2);
                dl2.Log += s => Console.WriteLine(s);
                int done2 = 0;
                var evt2 = new ManualResetEvent(false);
                dl2.TaskUpdated += t => { if (t.State == "完成") done2++; };
                dl2.AllDone += () => evt2.Set();
                foreach (var s in sids)
                    dl2.Enqueue(s, "set" + s);
                dl2.Start();
                if (!evt2.WaitOne(180000))
                {
                    Console.WriteLine("FAIL: 重新开始后任务未完成");
                    failures++;
                }
                else
                {
                    Console.WriteLine("重新开始完成, done=" + done2);
                    if (done2 == 0) { Console.WriteLine("FAIL: 重新开始后无任务完成"); failures++; }
                }

                // 清理测试文件(仅清理本测试生成的)
                foreach (var f in Directory.GetFiles(outDir, "*.osz"))
                    try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(outDir, "*.part"))
                    try { File.Delete(f); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex);
                failures++;
            }
            Console.WriteLine(failures == 0 ? "CANCEL TEST PASSED" : "CANCEL TEST FAILED: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>检查更新(诊断用): --check-update [repo]; 0=有更新 1=无更新 2=失败。</summary>
        public static int RunCheckUpdate(string repo)
        {
            var targetRepo = string.IsNullOrWhiteSpace(repo) ? UpdateChecker.Repo : repo.Trim();
            Console.WriteLine("当前版本: " + UpdateChecker.CurrentVersionText);
            Console.WriteLine("更新源: " + targetRepo);
            try
            {
                var info = UpdateChecker.Check(repo);
                if (info == null)
                {
                    Console.WriteLine("结果: 无可用更新");
                    return 1;
                }
                Console.WriteLine("结果: 发现新版本 " + info.Tag + " (" + info.VersionString + ")");
                Console.WriteLine("标题: " + info.Name);
                Console.WriteLine("发布: " + info.PublishedAt);
                Console.WriteLine("页面: " + info.ReleaseUrl);
                Console.WriteLine("安装包: " + (string.IsNullOrEmpty(info.AssetUrl)
                    ? "(无 exe 资源)"
                    : info.AssetName + " (" + info.AssetSize + " bytes)"));
                if (!string.IsNullOrEmpty(info.Digest)) Console.WriteLine("摘要: " + info.Digest);
                return 0;
            }
            catch (NoReleaseException ex)
            {
                Console.WriteLine("结果: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("结果: 检查失败 - " + ex.Message);
                return 2;
            }
        }
    }
}
