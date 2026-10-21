using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RankedDownloader
{
    /// <summary>单张谱面难度信息(api.sayobot.cn get_beatmaps 返回, 仅保留使用到的字段)</summary>
    public class Beatmap
    {
        public string beatmapset_id { get; set; }
        public string beatmap_id { get; set; }
        public string approved { get; set; }
        public string approved_date { get; set; }
        public string artist { get; set; }
        public string title { get; set; }
        public string creator { get; set; }

        public long Sid { get { long v; return long.TryParse(beatmapset_id, out v) ? v : 0; } }
        public long Bid { get { long v; return long.TryParse(beatmap_id, out v) ? v : 0; } }
    }

    /// <summary>玩家单曲成绩(get_user_best 返回)</summary>
    public class UserBest
    {
        public string beatmap_id { get; set; }
        public long Bid { get { long v; return long.TryParse(beatmap_id, out v) ? v : 0; } }
    }

    /// <summary>
    /// Sayobot 镜像 API 客户端。
    /// 接口(已实测确认):
    ///   GET https://api.sayobot.cn/ppy/get_beatmaps?since=YYYY-MM-DD[ HH:mm:ss]&m=<mode>[&limit=N]
    ///       -> 按 approved_date 升序返回, 每次最多 500 条(每难度一行); 翻页用上一页最后一条的 approved_date 作为 since
    ///   GET https://api.sayobot.cn/ppy/get_beatmaps?u=<mapper>&m=<mode>     -> 某 mapper 的全部难度
    ///   GET https://api.sayobot.cn/ppy/get_beatmaps?b=<beatmap_id>          -> 按难度 id 查(含 beatmapset_id)
    ///   GET https://api.sayobot.cn/ppy/get_user_best?u=<user>&m=<mode>[&limit=N] -> 玩家 bp(上限 100)
    /// 下载:
    ///   GET https://txy1.sayobot.cn/beatmaps/download/{full|novideo|mini}/{sid}?server=auto
    ///       -> 302 跳转到 CDN, 支持 Range 断点续传, Content-Disposition 提供文件名
    /// </summary>
    public static class SayobotApi
    {
        public const string ApiBase = "https://api.sayobot.cn/ppy/";
        public const string DlBase = "https://txy1.sayobot.cn/beatmaps/download/";

        private const int PageLimit = 500;

        static SayobotApi()
        {
            // 并行收集需要多连接; .NET 默认每主机仅 2 个连接会成为瓶颈
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
        }

        private static byte[] GetBytes(string url)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                    req.Accept = "application/json";
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        if (s == null) throw new IOException("empty response");
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) Thread.Sleep(1500 * (attempt + 1));
                }
            }
            throw last;
        }

        private static string GetString(string url)
        {
            var b = GetBytes(url);
            return Encoding.UTF8.GetString(b).Trim();
        }

        private static List<T> Parse<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim().StartsWith("{"))
                return new List<T>();
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser.Deserialize<List<T>>(json) ?? new List<T>();
        }

        /// <summary>按 approved 日期拉一页(每页 500, 升序)。since 为空表示从最早开始。</summary>
        public static List<Beatmap> GetBeatmapsByDate(string since, int mode, int limit = PageLimit)
        {
            var url = ApiBase + "get_beatmaps?m=" + mode + "&limit=" + limit;
            if (!string.IsNullOrEmpty(since))
                url += "&since=" + Uri.EscapeDataString(since);
            return Parse<Beatmap>(GetString(url));
        }

        /// <summary>指定 mapper 的全部难度(每难度一行)。</summary>
        public static List<Beatmap> GetBeatmapsByMapper(string mapper, int mode, int limit = PageLimit)
        {
            var url = ApiBase + "get_beatmaps?u=" + Uri.EscapeDataString(mapper) + "&m=" + mode + "&limit=" + limit;
            return Parse<Beatmap>(GetString(url));
        }

        /// <summary>按 beatmap_id 查一张难度, 用于把 bp 的 bid 映射到 setid。</summary>
        public static List<Beatmap> GetBeatmapByBid(long bid)
        {
            return Parse<Beatmap>(GetString(ApiBase + "get_beatmaps?b=" + bid));
        }

        /// <summary>玩家 bp 列表(最多 100 条)。u 可为用户名或 uid。</summary>
        public static List<UserBest> GetUserBest(string user, int mode, int limit = 100)
        {
            var url = ApiBase + "get_user_best?u=" + Uri.EscapeDataString(user) + "&m=" + mode + "&limit=" + limit;
            return Parse<UserBest>(GetString(url));
        }
    }
}
