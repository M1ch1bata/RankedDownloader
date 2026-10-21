using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RankedDownloader
{
    /// <summary>
    /// 自更新落地: 启动一个 PowerShell 辅助进程, 等本进程退出后覆盖 exe 并重新启动。
    /// 不依赖额外组件; 覆盖失败时弹窗提示手动替换(更新包保留在 %TEMP%)。
    /// </summary>
    public static class UpdateApplier
    {
        /// <summary>目标目录/文件是否可写(用于提前判断能否自动替换)。</summary>
        public static bool CanWriteTarget(string targetPath)
        {
            try
            {
                var full = Path.GetFullPath(targetPath);
                var dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) return false;
                var probe = Path.Combine(dir, "~upd_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>下载校验完成后调用: 启动替换脚本; 本进程应随后退出。</summary>
        public static void ApplyAndRestart(string downloadedExe, string targetExe)
        {
            var script = Path.Combine(Path.GetTempPath(),
                "osu!MapsDownloader-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            // 带 BOM: Windows PowerShell 5.1 对无 BOM 脚本按 ANSI 解析, 中文会乱码
            File.WriteAllText(script,
                BuildScript(Process.GetCurrentProcess().Id, downloadedExe, targetExe, true),
                new UTF8Encoding(true));

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }

        /// <summary>生成替换脚本(公开以便自测)。</summary>
        public static string BuildScript(int processId, string source, string target, bool restart)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine("$pidToWait = " + processId);
            sb.AppendLine("$src = " + PsQuote(source));
            sb.AppendLine("$dst = " + PsQuote(target));
            sb.AppendLine("# 等待旧进程退出(最多 2 分钟)");
            sb.AppendLine("for ($i = 0; $i -lt 240; $i++) {");
            sb.AppendLine("  if (-not (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { break }");
            sb.AppendLine("  Start-Sleep -Milliseconds 500");
            sb.AppendLine("}");
            sb.AppendLine("Start-Sleep -Milliseconds 400");
            sb.AppendLine("# 覆盖目标(杀毒/索引可能短暂占用, 重试 30 次)");
            sb.AppendLine("$ok = $false");
            sb.AppendLine("for ($i = 0; $i -lt 30; $i++) {");
            sb.AppendLine("  try { Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop; $ok = $true; break }");
            sb.AppendLine("  catch { Start-Sleep -Milliseconds 500 }");
            sb.AppendLine("}");
            sb.AppendLine("if ($ok) {");
            if (restart)
            {
                sb.AppendLine("  Start-Process -FilePath $dst -WorkingDirectory (Split-Path -Parent $dst)");
            }
            sb.AppendLine("  Remove-Item -LiteralPath $src -Force");
            sb.AppendLine("} else {");
            sb.AppendLine("  Add-Type -AssemblyName System.Windows.Forms");
            sb.AppendLine("  [System.Windows.Forms.MessageBox]::Show(\"自动更新失败: 无法写入`n\" + $dst + \"`n`n新版本文件保留在:`n\" + $src, \"更新失败\") | Out-Null");
            sb.AppendLine("}");
            sb.AppendLine("$me = $MyInvocation.MyCommand.Path");
            sb.AppendLine("if ($me) { Remove-Item -LiteralPath $me -Force }");
            return sb.ToString();
        }

        private static string PsQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }
    }
}
