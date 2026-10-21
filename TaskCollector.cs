using System;
using System.Collections.Generic;
using System.Globalization;

namespace RankedDownloader
{
    /// <summary>待下载谱面集(带元数据)</summary>
    public class SetItem
    {
        public long Sid;
        public string Artist = "";
        public string Title = "";
        public string Creator = "";
        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Artist) && !string.IsNullOrEmpty(Title))
                    return Artist + " - " + Title;
                return !string.IsNullOrEmpty(Title) ? Title : Sid.ToString();
            }
        }
    }

    /// <summary>一段收集结果: 谱面集列表 + 下次游标(用于分段续取)</summary>
    public class DateChunk
    {
        public List<SetItem> Items = new List<SetItem>();
        public string NextCursor;   // 本段最后一行的 approved_date; null 表示已取完
    }

    /// <summary>任务收集: 三种模式生成去重后的 setid 列表</summary>
    public static class TaskCollector
    {
        // approved 取值: 1=Ranked, 3=Qualified, 4=Loved(其余 pending/graveyard 不下载)
        private static readonly HashSet<string> _approvedAll = new HashSet<string> { "1", "3", "4" };

        /// <summary>按勾选状态生成 approved 过滤集合(供 UI 调用)</summary>
        public static HashSet<string> BuildApproved(bool ranked, bool qualified, bool loved)
        {
            var set = new HashSet<string>();
            if (ranked) set.Add("1");
            if (qualified) set.Add("3");
            if (loved) set.Add("4");
            return set;
        }

        /// <summary>模式2: 指定用户 bp 列表 -> 谱面集(带元数据, 去重)</summary>
        public static List<SetItem> CollectFromUserBest(string user, int mode, Action<string> log, Func<bool> cancelled = null, int limit = 100)
        {
            var result = new List<SetItem>();
            if (string.IsNullOrWhiteSpace(user))
                throw new Exception("请输入玩家名或 uid");

            log("正在获取 " + user + " 的 bp 列表(最多 " + limit + ")...");
            var bests = SayobotApi.GetUserBest(user.Trim(), mode, Math.Max(1, Math.Min(limit, 100)));
            if (bests.Count == 0)
                throw new Exception("未获取到 bp 记录(检查用户名/uid 或该模式下的游玩记录)");
            log("获取到 " + bests.Count + " 条 bp 记录");

            // 并发查 bid -> 谱面(8 路, 参照 Python 版 ThreadPoolExecutor)
            var mapsArr = new Beatmap[bests.Count];
            var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 };
            System.Threading.Tasks.Parallel.For(0, bests.Count, po, i =>
            {
                if (cancelled != null && cancelled()) return;
                try
                {
                    var maps = SayobotApi.GetBeatmapByBid(bests[i].Bid);
                    if (maps.Count > 0) mapsArr[i] = maps[0];
                }
                catch { /* 单条失败不阻塞整体, 下面统计 */ }
            });
            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消");

            var seen = new HashSet<long>();
            int miss = 0;
            for (int i = 0; i < bests.Count; i++)
            {
                var m = mapsArr[i];
                if (m == null) { miss++; continue; }
                var sid = m.Sid;
                if (sid > 0 && seen.Add(sid))
                {
                    result.Add(new SetItem { Sid = sid, Artist = m.artist, Title = m.title, Creator = m.creator });
                    log("  -> " + m.artist + " - " + m.title + " (set " + sid + ")");
                }
            }
            if (miss > 0) log(miss + " 张谱面未查询到(可能不存在或查询失败)");
            return result;
        }

        /// <summary>模式3: 指定 mapper 的全部谱面集(带元数据, 去重)</summary>
        public static List<SetItem> CollectFromMapper(string mapper, int mode, Action<string> log, Func<bool> cancelled = null)
        {
            var result = new List<SetItem>();
            if (string.IsNullOrWhiteSpace(mapper))
                throw new Exception("请输入 mapper 名");

            log("正在获取 " + mapper + " 的谱面...");
            var maps = SayobotApi.GetBeatmapsByMapper(mapper.Trim(), mode);
            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消");
            if (maps.Count == 0)
                throw new Exception("未获取到该 mapper 的谱面(检查拼写)");
            log("获取到 " + maps.Count + " 张难度图");

            var seen = new HashSet<long>();
            foreach (var m in maps)
            {
                var sid = m.Sid;
                if (sid > 0 && seen.Add(sid))
                    result.Add(new SetItem { Sid = sid, Artist = m.artist, Title = m.title, Creator = m.creator });
            }
            log("去重后共 " + result.Count + " 个谱面集");
            return result;
        }

        /// <summary>
        /// 模式1(单次): 按日期收集 Ranked/Loved 谱面。since/until 形如 YYYY-MM-DD(until 可空=不限)。
        /// 仅保留 setid 列表(内部委托分段收集, 共用翻页核心)。
        /// </summary>
        public static List<long> CollectByDate(string since, string until, int mode, Action<string> log, Func<bool> cancelled = null, HashSet<string> approved = null)
        {
            var chunk = CollectByDateChunk(since, until, mode, int.MaxValue, log, cancelled, approved);
            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消");
            var ids = new List<long>(chunk.Items.Count);
            foreach (var item in chunk.Items) ids.Add(item.Sid);
            return ids;
        }

        /// <summary>
        /// 模式1(并行): 按日期切片并行收集。API 每页上限 500 条且无计数接口,
        /// 大跨度区间按顺序翻页很慢; 切片间互不依赖, 可并行拉取显著提速。
        /// sliceDays=每片天数, degree=并行度。
        /// </summary>
        public static List<SetItem> CollectByDateParallel(string since, string until, int mode, int sliceDays,
            int degree, Action<string> log, Func<bool> cancelled = null, HashSet<string> approved = null)
        {
            if (approved == null) approved = _approvedAll;
            DateTime start;
            if (!DateTime.TryParse(since, out start))
                throw new Exception("请输入正确的起始日期, 格式: YYYY-MM-DD");
            DateTime end;
            if (string.IsNullOrWhiteSpace(until) || !DateTime.TryParse(until, out end))
                end = DateTime.Today;
            if (end < start) throw new Exception("结束日期不能早于起始日期");

            // 构造日期切片 [s, e) 左闭右开, 避免边界重复
            var slices = new List<DateTime[]>();
            var cur = start.Date;
            var cap = end.Date.AddDays(1);   // 含结束日
            while (cur < cap)
            {
                var next = cur.AddDays(Math.Max(1, sliceDays));
                if (next > cap) next = cap;
                slices.Add(new[] { cur, next });
                cur = next;
            }

            log("并行收集 " + start.ToString("yyyy-MM-dd") + " ~ " + end.ToString("yyyy-MM-dd") +
                " (共 " + slices.Count + " 个日期片, 并行 " + degree + ")...");

            var results = new List<SetItem>[slices.Count];
            var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) };
            int doneSlices = 0;
            System.Threading.Tasks.Parallel.For(0, slices.Count, po, i =>
            {
                if (cancelled != null && cancelled()) return;
                string ignored;
                var list = CollectRange(slices[i][0], slices[i][1], mode, approved, cancelled, int.MaxValue, out ignored);
                results[i] = list;
                int n = System.Threading.Interlocked.Increment(ref doneSlices);
                log("  日期片 " + slices[i][0].ToString("MM-dd") + "~" + slices[i][1].AddDays(-1).ToString("MM-dd") +
                    " 完成: " + list.Count + " 个谱面集 (" + n + "/" + slices.Count + ")");
            });
            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消");

            // 合并去重(保持日期片顺序)
            var seen = new HashSet<long>();
            var all = new List<SetItem>();
            foreach (var list in results)
            {
                if (list == null) continue;
                foreach (var item in list)
                    if (seen.Add(item.Sid)) all.Add(item);
            }
            log("收集完成, 共 " + all.Count + " 个谱面集");
            return all;
        }

        /// <summary>
        /// 模式1(分段): 收集 [since, until] 内至多 maxSets 个谱面集, 返回游标供下一段续取。
        /// </summary>
        public static DateChunk CollectByDateChunk(string since, string until, int mode, int maxSets,
            Action<string> log, Func<bool> cancelled = null, HashSet<string> approved = null)
        {
            if (approved == null) approved = _approvedAll;
            DateTime start;
            if (!DateTime.TryParse(since, out start))
                throw new Exception("请输入正确的起始日期, 格式: YYYY-MM-DD");
            DateTime endExclusive = DateTime.MaxValue;
            if (!string.IsNullOrWhiteSpace(until))
            {
                DateTime e;
                if (!DateTime.TryParse(until, out e))
                    throw new Exception("请输入正确的结束日期, 格式: YYYY-MM-DD(可留空)");
                endExclusive = e.Date.AddDays(1);   // 含结束日全天
            }

            log("收集分段(自 " + start.ToString("yyyy-MM-dd") + " 起, 至多 " + maxSets + " 个谱面集)...");
            string nextCursor;
            var items = CollectRange(start, endExclusive, mode, approved, cancelled, maxSets, out nextCursor);
            var chunk = new DateChunk { Items = items, NextCursor = nextCursor };
            log("  本段 " + chunk.Items.Count + " 个谱面集, 游标 " + (chunk.NextCursor ?? "(完成)"));
            return chunk;
        }

        /// <summary>
        /// 顺序翻页核心: 收集 [start, endExclusive) 内至多 maxSets 个谱面集。
        /// 满 maxSets 时通过 nextCursor 返回续取起点(该秒会重叠, 由调用方去重), 否则为 null。
        /// 注意: API 的 since 是"严格大于", 游标必须回退 1 秒, 否则同一秒里排在页边界之后的行会被静默跳过。
        /// </summary>
        private static List<SetItem> CollectRange(DateTime start, DateTime endExclusive, int mode,
            HashSet<string> approved, Func<bool> cancelled, int maxSets, out string nextCursor)
        {
            const string fmt = "yyyy-MM-dd HH:mm:ss";
            var list = new List<SetItem>();
            var seen = new HashSet<long>();
            nextCursor = null;
            string endStr = endExclusive == DateTime.MaxValue ? null : endExclusive.ToString(fmt);
            string cursor = CursorBefore(start, fmt);
            while (true)
            {
                if (cancelled != null && cancelled()) return list;
                var page = SayobotApi.GetBeatmapsByDate(cursor, mode);
                if (page.Count == 0) break;

                string lastDate = null;
                bool stop = false, full = false;
                foreach (var m in page)
                {
                    if (string.IsNullOrEmpty(m.approved_date)) continue;
                    lastDate = m.approved_date;
                    // 到达区间右边界 -> 结束(该行属于下一区间)
                    if (endStr != null && string.CompareOrdinal(m.approved_date, endStr) >= 0) { stop = true; break; }
                    if (!approved.Contains(m.approved)) continue;
                    var sid = m.Sid;
                    if (sid > 0 && seen.Add(sid))
                    {
                        list.Add(new SetItem { Sid = sid, Artist = m.artist, Title = m.title, Creator = m.creator });
                        if (list.Count >= maxSets) { full = true; break; }
                    }
                }
                if (full) { nextCursor = lastDate; break; }
                if (stop || page.Count < 500) break;
                if (string.IsNullOrEmpty(lastDate)) break;
                DateTime last;
                if (DateTime.TryParseExact(lastDate, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out last))
                    cursor = CursorBefore(last, fmt);
                else
                    cursor = lastDate;   // 格式意外变化时退回原行为
            }
            return list;
        }

        /// <summary>把 since 游标回退 1 秒, 以包含同一秒内排在页边界之后的行(排他比较)。</summary>
        private static string CursorBefore(DateTime t, string fmt)
        {
            if (t <= DateTime.MinValue.AddSeconds(1)) return t.ToString(fmt);
            return t.AddSeconds(-1).ToString(fmt);
        }
    }
}
