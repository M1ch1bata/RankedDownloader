using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RankedDownloader
{
    /// <summary>GitHub 上最新的可用版本信息</summary>
    public class UpdateInfo
    {
        public string Tag;
        public Version Version;
        public string Name;
        public string ReleaseUrl;
        public string PublishedAt;
        public string AssetName;
        public string AssetUrl;
        public long AssetSize;
        public string Digest;

        public string VersionString { get { return Version == null ? "" : Version.ToString(); } }
    }

    /// <summary>更新源还没有发布任何 Release。</summary>
    public class NoReleaseException : Exception
    {
        public NoReleaseException(string message) : base(message) { }
    }

    /// <summary>
    /// 手动检查更新: 查询固定仓库的 GitHub Releases(latest), 与本地 AssemblyVersion 比较。
    /// 仅在 设置 → 检查更新 时调用; 不自动检查。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>固定更新源(本项目自己的仓库)。</summary>
        public const string Repo = "M1ch1bata/RankedDownloader";
        public const string ApiBase = "https://api.github.com";
        private const int TimeoutMs = 15000;

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string CurrentVersionText { get { return CurrentVersion.ToString(); } }

        /// <summary>检查固定更新源。</summary>
        public static UpdateInfo Check()
        {
            return CheckCore(Repo);
        }

        /// <summary>诊断用: 指定其它仓库检查(正式按钮始终用固定源)。</summary>
        public static UpdateInfo Check(string repoOverride)
        {
            return CheckCore(string.IsNullOrWhiteSpace(repoOverride) ? Repo : repoOverride.Trim());
        }

        private static UpdateInfo CheckCore(string repo)
        {
            var url = ApiBase + "/repos/" + repo + "/releases/latest";
            string json;
            try
            {
                json = HttpGet(url);
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 404)
                    throw new NoReleaseException("更新源还没有发布 Release: " + repo);
                if (hr != null && (int)hr.StatusCode == 403)
                    throw new Exception("GitHub API 访问受限(可能超过每小时 60 次的频率限制)");
                throw new Exception("访问 GitHub 失败: " + ex.Message);
            }

            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root;
            try { root = ser.DeserializeObject(json) as Dictionary<string, object>; }
            catch { throw new Exception("更新信息解析失败"); }
            if (root == null) return null;

            var tag = GetString(root, "tag_name");
            var version = ParseVersion(tag);
            if (version == null) throw new Exception("更新源版本号无法解析: " + tag);
            if (version <= CurrentVersion) return null;

            var info = new UpdateInfo
            {
                Tag = tag,
                Version = version,
                Name = GetString(root, "name"),
                ReleaseUrl = GetString(root, "html_url"),
                PublishedAt = GetString(root, "published_at"),
            };
            if (string.IsNullOrEmpty(info.Name)) info.Name = tag;

            // 只接受和本程序相关的 exe, 避免把 Release 里无关的 exe 当作更新包
            var assets = root.ContainsKey("assets") ? root["assets"] as object[] : null;
            if (assets != null)
            {
                string fallbackName = null, fallbackUrl = null, fallbackDigest = null;
                long fallbackSize = 0;
                foreach (var a in assets)
                {
                    var d = a as Dictionary<string, object>;
                    if (d == null) continue;
                    var name = GetString(d, "name");
                    if (string.IsNullOrEmpty(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    bool exact = string.Equals(name, "osu!MapsDownloader.exe", StringComparison.OrdinalIgnoreCase);
                    if (!exact && name.IndexOf("MapsDownloader", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (exact)
                    {
                        info.AssetName = name;
                        info.AssetUrl = GetString(d, "browser_download_url");
                        info.AssetSize = GetLong(d, "size");
                        info.Digest = GetString(d, "digest");
                        break;
                    }
                    if (fallbackName == null)
                    {
                        fallbackName = name;
                        fallbackUrl = GetString(d, "browser_download_url");
                        fallbackSize = GetLong(d, "size");
                        fallbackDigest = GetString(d, "digest");
                    }
                }
                if (info.AssetUrl == null && fallbackName != null)
                {
                    info.AssetName = fallbackName;
                    info.AssetUrl = fallbackUrl;
                    info.AssetSize = fallbackSize;
                    info.Digest = fallbackDigest;
                }
            }
            return info;
        }

        /// <summary>下载并校验更新包; 成功返回本地路径, 失败返回 null 并给出原因。</summary>
        public static string DownloadAndVerify(UpdateInfo info, out string error)
        {
            error = null;
            if (info == null || string.IsNullOrEmpty(info.AssetUrl))
            {
                error = "该 Release 没有提供 exe 安装包";
                return null;
            }
            string path = null;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "osu!MapsDownloader_update");
                Directory.CreateDirectory(dir);
                var name = string.IsNullOrEmpty(info.AssetName) ? "osu!MapsDownloader.exe" : Path.GetFileName(info.AssetName);
                path = Path.Combine(dir, info.VersionString + "_" + name);
                if (File.Exists(path)) File.Delete(path);

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "osu!MapsDownloader/" + CurrentVersionText);
                    wc.DownloadFile(info.AssetUrl, path);
                }

                var fi = new FileInfo(path);
                if (info.AssetSize > 0 && fi.Length != info.AssetSize)
                {
                    error = "大小不一致: " + fi.Length + " / " + info.AssetSize;
                    TryDelete(path);
                    return null;
                }
                if (!string.IsNullOrEmpty(info.Digest) &&
                    info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    var expect = info.Digest.Substring(7).Trim();
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(path))
                    {
                        var actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                        if (!string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase))
                        {
                            error = "SHA256 校验不一致";
                            TryDelete(path);
                            return null;
                        }
                    }
                }
                // 至少确认是 PE 文件, 防止把错误页/压缩包当成更新包
                using (var fs = File.OpenRead(path))
                {
                    var mz = new byte[2];
                    if (fs.Read(mz, 0, 2) != 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                    {
                        error = "下载内容不是有效的 exe";
                        TryDelete(path);
                        return null;
                    }
                }
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDelete(path);
                return null;
            }
        }

        /// <summary>解析 tag(支持 v1.2.3 / 1.2.3.4 / v1.2.3-beta), 失败返回 null。</summary>
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            var parts = s.Split('.');
            if (parts.Length == 0 || parts.Length > 4) return null;
            var nums = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (i < parts.Length)
                {
                    int n;
                    if (!int.TryParse(parts[i], out n) || n < 0) return null;
                    nums[i] = n;
                }
            }
            return new Version(nums[0], nums[1], nums[2], nums[3]);
        }

        private static string HttpGet(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.UserAgent = "osu!MapsDownloader/" + CurrentVersionText + " (update-check)";
            req.Accept = "application/vnd.github+json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                if (s == null) throw new IOException("empty response");
                s.CopyTo(ms);
                return Encoding.UTF8.GetString(ms.ToArray()).Trim();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                long n;
                if (long.TryParse(v.ToString(), out n)) return n;
            }
            return 0;
        }
    }
}
