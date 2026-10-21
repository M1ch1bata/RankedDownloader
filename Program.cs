using System;
using System.Threading;
using System.Windows.Forms;

namespace RankedDownloader
{
    static class Program
    {
        // 本机(每会话)唯一实例名; 自测模式不占用
        private const string InstanceMutex = @"Local\osu!MapsDownloader.SingleInstance";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] == "--smoke") return SmokeTest.Run(args);
                if (args[0] == "--detect") return SmokeTest.RunDetect();
                if (args[0] == "--cancel-test") return SmokeTest.RunCancelTest();
                if (args[0] == "--dedup-test") return SmokeTest.RunDedupTest();
                if (args[0] == "--custom-dir-test") return SmokeTest.RunCustomDirTest();
                if (args[0] == "--store-test") return SmokeTest.RunStoreTest();
                if (args[0] == "--pause-test") return SmokeTest.RunPauseTest();
                if (args[0] == "--scan-test") return SmokeTest.RunScanTest();
                if (args[0] == "--perf-test") return SmokeTest.RunPerfTest();
                if (args[0] == "--check-update") return SmokeTest.RunCheckUpdate(args.Length > 1 ? args[1] : null);
            }

            // 单实例: 两个实例向同一目录写 "<sid>.osz.part" 会互相破坏
            using (var mutex = new Mutex(false, InstanceMutex))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // 上个实例异常退出, 接管

                if (!owned)
                {
                    MessageBox.Show("osu!MapsDownloader 已在运行。\r\n请先关闭已有窗口后再启动。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                    return 0;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}
