using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RankedDownloader
{
    public class MainForm : Form
    {
        // ---------------- 顶部设置区 ----------------
        private readonly ComboBox _modeCombo;      // 1 日期 / 2 bp / 3 mapper
        private readonly ComboBox _gameMode;       // 0 osu 1 taiko 2 catch 3 mania
        private readonly ComboBox _variantCombo;   // full / novideo / mini
        private readonly ComboBox _serverCombo;    // auto / cmcc / telecom / unicom
        private readonly TextBox _inputBox;        // 模式2/3: 玩家名 / mapper名
        private readonly DateTimePicker _startPicker;  // 模式1: 起始日期(下拉日历)
        private readonly DateTimePicker _endPicker;    // 模式1: 结束日期(默认今天 = 至今)
        private readonly Label _endLabel;
        private readonly Label _inputHint;
        private readonly Label _bpLabel;
        private readonly TextBox _songsBox;
        private readonly NumericUpDown _bpLimit;
        private readonly CheckBox _chkRanked, _chkQualified, _chkLoved;
        private readonly Label _stateLabel;
        private readonly FlowLayoutPanel _statePanel;

        // ---------------- 底部按钮/状态 ----------------
        private readonly Button _detectBtn;
        private readonly Button _startBtn;
        private readonly Button _pauseBtn;
        private readonly Button _stopBtn;
        private readonly Button _settingsBtn;
        private readonly Label _statLabel;

        // ---------------- 任务列表 ----------------
        private readonly ListView _list;
        private readonly TextProgressBar _progress;

        // ---------------- 失败清单 ----------------
        private readonly ListView _failedList;
        private readonly Button _retrySelBtn, _retryAllBtn, _exportBtn, _removeBtn, _clearFailedBtn, _refreshFailedBtn;

        private readonly TextBox _log;

        private Downloader _dl;
        private AppStore _store;
        private AppConfig _config;
        private readonly HashSet<long> _counted = new HashSet<long>();
        private readonly Dictionary<long, ListViewItem> _listIndex = new Dictionary<long, ListViewItem>(); // sid -> 行 O(1) 索引
        private int _done, _skip, _fail, _canceled;
        private int _totalTasks;
        private volatile bool _batchRunning;   // 分批下载进行中(分批时 AllDone 不提前恢复按钮)
        private string _pendingUpdateFile;     // 设置里确认更新后, 待替换的更新包

        // 设置项(仅"确定"时从设置弹窗写回; 取消不影响)
        private int _concurrency;
        private int _retries;
        private int _timeout;

        private static readonly string[][] ServerOptions = new string[][]
        {
            // 实测: auto/cmcc -> cmcc 节点; telecom/unicom -> tc1 节点; 其余键(CDN/DE/USA 等)均回落默认节点, 已移除
            new[] { "auto", "自动" },
            new[] { "cmcc", "中国移动 (cmcc)" },
            new[] { "telecom", "中国电信 (telecom)" },
            new[] { "unicom", "中国联通 (unicom)" },
        };

        private static readonly string[] ModeNames = { "osu!", "太鼓", "接水果", "mania" };

        public MainForm()
        {
            Text = "osu!MapsDownloader " + AppVersion + " - Sayobot 谱面下载器";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(920, 620);

            _store = new AppStore();
            _config = AppConfig.Load();
            _concurrency = Math.Max(1, Math.Min(8, _config.Concurrency));
            _retries = Math.Max(0, Math.Min(10, _config.Retries));
            _timeout = Math.Max(10, Math.Min(600, _config.Timeout));

            // ================= 顶部 =================
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 152, Padding = new Padding(10, 6, 10, 4) };
            top.RowCount = 4;
            for (int i = 0; i < 4; i++) top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            top.ColumnCount = 6;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

            top.Controls.Add(MkLabel("下载模式"), 0, 0);
            _modeCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _modeCombo.Items.AddRange(new object[] { "1. Ranked 谱面下载", "2. 下载玩家 bp", "3. 下载 mapper 谱面" });
            _modeCombo.SelectedIndex = Math.Max(0, Math.Min(_modeCombo.Items.Count - 1, _config.Mode));
            top.Controls.Add(_modeCombo, 1, 0);

            top.Controls.Add(MkLabel("下载类型"), 2, 0);
            _variantCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _variantCombo.Items.AddRange(new object[] { "完整版(带视频)", "无视频", "mini" });
            _variantCombo.SelectedIndex = _config.DownloadType == "novideo" ? 1 : (_config.DownloadType == "mini" ? 2 : 0);
            top.Controls.Add(_variantCombo, 3, 0);

            top.Controls.Add(MkLabel("下载线路"), 4, 0);
            _serverCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var s in ServerOptions) _serverCombo.Items.Add(s[1]);
            _serverCombo.SelectedIndex = Math.Max(0, Array.FindIndex(ServerOptions, s => s[0] == _config.Server));
            top.Controls.Add(_serverCombo, 5, 0);

            top.Controls.Add(MkLabel("游戏模式"), 0, 1);
            _gameMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _gameMode.Items.AddRange(new object[] { "osu! 戳泡泡", "太鼓", "接水果", "下落式(mania)" });
            _gameMode.SelectedIndex = Math.Max(0, Math.Min(_gameMode.Items.Count - 1, _config.GameMode));
            top.Controls.Add(_gameMode, 1, 1);

            _stateLabel = MkLabel("谱面状态");
            top.Controls.Add(_stateLabel, 2, 1);
            var chkPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            _chkRanked = new CheckBox { Text = "Ranked", Checked = _config.StateRanked, AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
            _chkQualified = new CheckBox { Text = "Qualified", Checked = _config.StateQualified, AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
            _chkLoved = new CheckBox { Text = "Loved", Checked = _config.StateLoved, AutoSize = true, Margin = new Padding(0, 7, 0, 0) };
            chkPanel.Controls.Add(_chkRanked);
            chkPanel.Controls.Add(_chkQualified);
            chkPanel.Controls.Add(_chkLoved);
            _statePanel = chkPanel;
            top.Controls.Add(chkPanel, 3, 1);
            top.SetColumnSpan(chkPanel, 3);

            top.Controls.Add(MkLabel("起始/输入"), 0, 2);
            _inputBox = new TextBox { Dock = DockStyle.Fill };
            top.Controls.Add(_inputBox, 1, 2);
            _startPicker = new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Value = DateTime.Today.AddDays(-7),
                Visible = false,
            };
            top.Controls.Add(_startPicker, 1, 2);
            _endLabel = MkLabel("结束日期");
            _endLabel.Visible = false;
            top.Controls.Add(_endLabel, 2, 2);
            _endPicker = new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Value = DateTime.Today,
                Visible = false,
            };
            top.Controls.Add(_endPicker, 3, 2);
            _inputHint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "",
            };
            top.Controls.Add(_inputHint, 3, 2);
            _bpLabel = MkLabel("BP数量");
            _bpLabel.Visible = false;
            top.Controls.Add(_bpLabel, 4, 2);
            _bpLimit = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 100, Value = Math.Max(1, Math.Min(100, _config.BpLimit)), Visible = false };
            top.Controls.Add(_bpLimit, 5, 2);

            top.Controls.Add(MkLabel("输出目录"), 0, 3);
            var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            _songsBox = new TextBox { Dock = DockStyle.Fill };
            dirRow.Controls.Add(_songsBox, 1, 0);
            var browseBtn = new Button { Text = "浏览...", Dock = DockStyle.Fill };
            browseBtn.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择谱面下载目录(可任意文件夹, 不必是 osu! Songs)";
                    if (Directory.Exists(_songsBox.Text)) fbd.SelectedPath = _songsBox.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK)
                        _songsBox.Text = fbd.SelectedPath;
                }
            };
            dirRow.Controls.Add(browseBtn, 2, 0);
            top.Controls.Add(dirRow, 1, 3);
            top.SetColumnSpan(dirRow, 3);

            _settingsBtn = new Button { Text = "设置...", Dock = DockStyle.Fill };
            _settingsBtn.Click += (s, e) => OpenSettings();
            top.Controls.Add(_settingsBtn, 5, 3);

            // ================= 页签 =================
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

            // --- 任务页 ---
            var taskPage = new TabPage("任务列表");
            var taskLayout = new TableLayoutPanel { Dock = DockStyle.Fill };
            taskLayout.RowCount = 4;
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

            // 进度条行: 进度条 + 右侧统计文字(进度说明紧邻进度条)
            var progRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            progRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));
            _progress = new TextProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
            progRow.Controls.Add(_progress, 0, 0);
            _statLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "已完成 0   未完成 0   跳过 0   失败 0   取消 0   总数 0",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
            };
            progRow.Controls.Add(_statLabel, 1, 0);
            taskLayout.Controls.Add(progRow, 0, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
            };
            _list.Columns.Add("SID", 70);
            _list.Columns.Add("标题", 290);
            _list.Columns.Add("文件名", 250);
            _list.Columns.Add("状态", 70);
            _list.Columns.Add("进度", 60);
            _list.Columns.Add("大小", 80);
            _list.Columns.Add("速度", 80);
            taskLayout.Controls.Add(_list, 0, 1);

            var modeHint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "模式说明: ① 按日期下载——输入起止日期, 下载时段内所选状态(Ranked/Qualified/Loved)的谱面;  " +
                        "② 下载玩家 bp——输入 osu! 玩家名或 uid, 下载其 bp 对应的全部谱面(不限状态);  " +
                        "③ 下载 mapper 谱面——输入 mapper 名, 下载其全部谱面(不限状态)。",
            };
            taskLayout.Controls.Add(modeHint, 0, 2);

            var credit = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.FromArgb(90, 90, 90),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "制作: osuID: Michibata     致谢: Sayobot 镜像站(osu.sayobot.cn)及其开发者 夜妈妈",
            };
            taskLayout.Controls.Add(credit, 0, 3);
            taskPage.Controls.Add(taskLayout);

            // --- 失败清单页 ---
            var failPage = new TabPage("失败清单");
            var failLayout = new TableLayoutPanel { Dock = DockStyle.Fill };
            failLayout.RowCount = 2;
            failLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            failLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var failBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _retrySelBtn = MkBtn("重试选中", (s, e) => RetryFailed(true));
            _retryAllBtn = MkBtn("重试全部", (s, e) => RetryFailed(false));
            _exportBtn = MkBtn("导出 CSV", (s, e) => ExportFailedCsv());
            _removeBtn = MkBtn("移除选中", (s, e) => RemoveFailedSelected());
            _clearFailedBtn = MkBtn("清空", (s, e) =>
            {
                if (MessageBox.Show(this, "确定清空全部失败记录?", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    _store.ClearFailed();
                    RefreshFailedList();
                }
            });
            _refreshFailedBtn = MkBtn("刷新", (s, e) => RefreshFailedList());
            failBtns.Controls.Add(_retrySelBtn);
            failBtns.Controls.Add(_retryAllBtn);
            failBtns.Controls.Add(_exportBtn);
            failBtns.Controls.Add(_removeBtn);
            failBtns.Controls.Add(_clearFailedBtn);
            failBtns.Controls.Add(_refreshFailedBtn);
            failLayout.Controls.Add(failBtns, 0, 0);

            _failedList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
            };
            _failedList.Columns.Add("SID", 80);
            _failedList.Columns.Add("艺术家", 180);
            _failedList.Columns.Add("标题", 250);
            _failedList.Columns.Add("模式", 70);
            _failedList.Columns.Add("失败原因", 320);
            failLayout.Controls.Add(_failedList, 0, 1);
            failPage.Controls.Add(failLayout);

            tabs.TabPages.Add(taskPage);
            tabs.TabPages.Add(failPage);

            // --- 运行日志页 ---
            var logPage = new TabPage("运行日志");
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            logPage.Controls.Add(_log);
            tabs.TabPages.Add(logPage);

            // ================= 全局底部: 按钮 =================
            var bottomBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 4, 10, 8) };
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var btnBar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            _stopBtn = MkBtn("停止", (s, e) => { if (_dl != null) _dl.Cancel(); });
            _pauseBtn = MkBtn("暂停", (s, e) => TogglePause());
            _startBtn = MkBtn("开始", (s, e) => StartAsync());
            _startBtn.BackColor = Color.FromArgb(150, 220, 150);
            _detectBtn = MkBtn("自动检测", (s, e) => AutoDetectSongs());
            btnBar.Controls.Add(_stopBtn);
            btnBar.Controls.Add(_pauseBtn);
            btnBar.Controls.Add(_startBtn);
            btnBar.Controls.Add(_detectBtn);
            bottomBar.Controls.Add(btnBar, 0, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(tabs, 0, 1);
            root.Controls.Add(bottomBar, 0, 2);
            Controls.Add(root);

            _modeCombo.SelectedIndexChanged += (s, e) => UpdateInputHint();
            UpdateInputHint();

            // 初始配置目录
            if (!string.IsNullOrEmpty(_config.DownloadDir) && Directory.Exists(_config.DownloadDir))
                _songsBox.Text = _config.DownloadDir;
            else
            {
                try
                {
                    var dir = DetectSongsDir();
                    if (dir != null) _songsBox.Text = dir;
                }
                catch { }
            }

            _store.Error += msg =>
            {
                try { BeginInvoke(new Action(() => LogLine("存储错误: " + msg))); } catch { }
            };
            if (!string.IsNullOrEmpty(_store.LastError)) LogLine("存储: " + _store.LastError);

            RefreshFailedList();
            LogLine("osu!MapsDownloader " + AppVersion + " 就绪。已载入历史记录 " + _store.DownloadedCount +
                    " 条、失败 " + _store.FailedCount + " 条(可在 设置 中重置)。");
        }

        private static string AppVersion
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        // ---------------- 控件工厂 ----------------

        private static Label MkLabel(string t)
        {
            return new Label { Dock = DockStyle.Fill, Text = t, TextAlign = ContentAlignment.MiddleRight, AutoSize = false };
        }

        private static Button MkBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Width = 92, Height = 28 };
            b.Click += onClick;
            return b;
        }

        // ---------------- 配置读写 ----------------

        private int SelectedMode { get { return Math.Max(0, Math.Min(3, _gameMode.SelectedIndex)); } }
        private string SelectedVariant
        {
            get { return _variantCombo.SelectedIndex == 1 ? "novideo" : (_variantCombo.SelectedIndex == 2 ? "mini" : "full"); }
        }
        private string SelectedServer { get { return ServerOptions[Math.Max(0, _serverCombo.SelectedIndex)][0]; } }
        private string SelectedServerName { get { return ServerOptions[Math.Max(0, _serverCombo.SelectedIndex)][1]; } }

        private HashSet<string> SelectedApproved()
        {
            return TaskCollector.BuildApproved(_chkRanked.Checked, _chkQualified.Checked, _chkLoved.Checked);
        }

        private void SaveConfig()
        {
            _config.DownloadDir = _songsBox.Text.Trim();
            _config.Server = SelectedServer;
            _config.DownloadType = SelectedVariant;
            _config.Mode = Math.Max(0, _modeCombo.SelectedIndex);
            _config.GameMode = Math.Max(0, _gameMode.SelectedIndex);
            _config.Concurrency = _concurrency;
            _config.Retries = _retries;
            _config.Timeout = _timeout;
            _config.BpLimit = (int)_bpLimit.Value;
            _config.StateRanked = _chkRanked.Checked;
            _config.StateQualified = _chkQualified.Checked;
            _config.StateLoved = _chkLoved.Checked;
            _config.Save();
        }

        private void OpenSettings()
        {
            using (var dlg = new Form
            {
                Text = "设置",
                Font = Font,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(350, 240),
            })
            {
                var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 5 };
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var concurrency = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 8, Value = _concurrency };
                var retries = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 10, Value = _retries };
                var timeout = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 10, Maximum = 600, Value = _timeout };

                t.Controls.Add(MkLabel("并发数"), 0, 0);
                t.Controls.Add(concurrency, 1, 0);
                t.Controls.Add(MkLabel("失败重试次数"), 0, 1);
                t.Controls.Add(retries, 1, 1);
                t.Controls.Add(MkLabel("超时(秒)"), 0, 2);
                t.Controls.Add(timeout, 1, 2);

                // 历史记录不再随启动清空, 重置改为显式按钮
                var reset = new Button { Text = "清空下载历史与失败清单", Width = 200, Height = 28 };
                reset.Click += (s, e) =>
                {
                    if (_dl != null)
                    {
                        MessageBox.Show(dlg, "下载进行中, 请先停止后再重置历史。", "提示");
                        return;
                    }
                    if (MessageBox.Show(dlg, "确定清空全部下载记录与失败清单?\r\n(不会删除已下载的谱面文件)", "确认",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    _store.WipeAll();
                    RefreshFailedList();
                    ResetTaskListUI();
                    _totalTasks = 0;
                    _done = _skip = _fail = _canceled = 0;
                    UpdateStatsText();
                    LogLine("已清空下载历史与失败清单");
                };
                var checkBtn = new Button { Text = "检查更新", Width = 90, Height = 28 };
                checkBtn.Click += (s, e) => CheckForUpdates(dlg);
                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                actions.Controls.Add(reset);
                actions.Controls.Add(checkBtn);
                t.Controls.Add(actions, 0, 3);
                t.SetColumnSpan(actions, 2);

                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 80 };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80 };
                var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                btns.Controls.Add(ok);
                btns.Controls.Add(cancel);
                t.Controls.Add(btns, 0, 4);
                t.SetColumnSpan(btns, 2);

                dlg.Controls.Add(t);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                // 只有"确定"才写回; "取消"不影响当前设置
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _concurrency = (int)concurrency.Value;
                _retries = (int)retries.Value;
                _timeout = (int)timeout.Value;
            }

            SaveConfig();
            LogLine("设置已保存: 并发 " + _concurrency + ", 重试 " + _retries + ", 超时 " + _timeout + "s");

            // 设置里点了"检查更新"并确认下载完成后, 在这里收尾并重启
            if (!string.IsNullOrEmpty(_pendingUpdateFile))
            {
                var file = _pendingUpdateFile;
                _pendingUpdateFile = null;
                ApplyUpdateAndExit(file);
            }
        }

        // ---------------- 通用 ----------------

        private void UpdateInputHint()
        {
            bool isDate = _modeCombo.SelectedIndex == 0;
            bool isBp = _modeCombo.SelectedIndex == 1;

            // 模式1: 显示日期选择器; 模式2/3: 显示文本框+提示
            _startPicker.Visible = isDate;
            _inputBox.Visible = !isDate;
            _endLabel.Visible = isDate;
            _endPicker.Visible = isDate;
            _inputHint.Visible = !isDate;
            _inputHint.Text = isBp ? "玩家名或 uid, 如 peppy" : "mapper 用户名, 如 peppy";

            // BP 数量仅模式2可见
            _bpLabel.Visible = isBp;
            _bpLimit.Visible = isBp;

            // 谱面状态仅在模式1(按日期)生效; 模式2/3 下载全部相关谱面, 隐藏勾选以免误解
            _stateLabel.Visible = isDate;
            _statePanel.Visible = isDate;
        }

        private void AutoDetectSongs()
        {
            try
            {
                var dir = DetectSongsDir();
                if (dir != null)
                {
                    _songsBox.Text = dir;
                    LogLine("自动检测到 osu! 谱面目录: " + dir);
                }
                else LogLine("未找到 osu! 注册表项, 请手动选择目录");
            }
            catch (Exception ex) { LogLine("检测失败: " + ex.Message); }
        }

        /// <summary>
        /// 探测 osu! Songs 目录(GUI 无关, 供测试复用)。stable 优先; 找不到返回 null。
        /// </summary>
        public static string DetectSongsDir()
        {
            string exe = null;

            foreach (var k in new[] { @"osustable.File.osz\DefaultIcon", @"osustable.Uri.osu\DefaultIcon",
                                      @"osustable.File.osr\DefaultIcon", @"osustable.File.osk\DefaultIcon" })
            {
                var v = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(k);
                if (v == null) continue;
                var icon = (string)v.GetValue("") ?? (string)v.GetValue(null);
                if (string.IsNullOrEmpty(icon)) continue;
                var p = ParseExePath(icon);
                if (p != null) { exe = p; break; }
            }

            if (exe == null)
            {
                foreach (var k in new[] { @"osustable.Uri.osu\shell\open\command", @"osustable.File.osz\shell\open\command" })
                {
                    var v = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(k);
                    if (v == null) continue;
                    var cmd = (string)v.GetValue("") ?? (string)v.GetValue(null);
                    if (string.IsNullOrEmpty(cmd)) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(cmd, "\"([^\"]+)\"");
                    if (m.Success) { exe = m.Groups[1].Value; break; }
                }
            }

            if (exe == null)
            {
                foreach (var k in new[] { @"osu\DefaultIcon", @"osu!\DefaultIcon", @"Applications\osu!.exe\DefaultIcon" })
                {
                    var v = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(k);
                    if (v == null) continue;
                    var icon = (string)v.GetValue("") ?? (string)v.GetValue(null);
                    if (string.IsNullOrEmpty(icon)) continue;
                    var p = ParseExePath(icon);
                    if (p != null) { exe = p; break; }
                }
            }

            if (exe != null && !string.IsNullOrEmpty(exe))
            {
                exe = exe.Replace("/", "\\");
                var dir = Path.GetDirectoryName(exe);
                if (dir != null && Directory.Exists(dir))
                {
                    var songs = Path.Combine(dir, "Songs");
                    if (Directory.Exists(songs)) return songs;
                }
            }

            foreach (var k in new[] { @"osu.File.osz\DefaultIcon", @"osu.File.osr\DefaultIcon", @"osu.File.osk\DefaultIcon" })
            {
                var v = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(k);
                if (v == null) continue;
                var icon = ((string)v.GetValue("") ?? (string)v.GetValue(null) ?? "").Trim().Trim('"');
                if (icon.IndexOf("osulazer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var songs = Path.Combine(local, "osu!", "Songs");
                    if (Directory.Exists(songs)) return songs;
                }
            }

            return null;
        }

        private static string ParseExePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim().Trim('"');
            var comma = s.LastIndexOf(',');
            var back = s.LastIndexOf('\\');
            if (comma > back) s = s.Substring(0, comma);
            s = s.Trim().Trim('"');
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // ---------------- 下载流程 ----------------

        private void TogglePause()
        {
            if (_dl == null) return;
            if (_dl.IsPaused)
            {
                _dl.Resume();
                _pauseBtn.Text = "暂停";
            }
            else
            {
                _dl.Pause();
                _pauseBtn.Text = "继续";
            }
        }

        private void StartAsync()
        {
            string songs = _songsBox.Text.Trim();
            if (string.IsNullOrEmpty(songs))
            {
                MessageBox.Show(this, "请填写或选择输出目录。", "提示");
                return;
            }
            try
            {
                if (!Directory.Exists(songs)) Directory.CreateDirectory(songs);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法创建输出目录:\n" + ex.Message, "提示");
                return;
            }

            int modeIdx = _modeCombo.SelectedIndex;
            int gameMode = SelectedMode;
            string input;
            string until = "";
            if (modeIdx == 0)
            {
                input = _startPicker.Value.ToString("yyyy-MM-dd");
                until = _endPicker.Value.ToString("yyyy-MM-dd");
                if (_endPicker.Value.Date < _startPicker.Value.Date)
                {
                    MessageBox.Show(this, "结束日期不能早于起始日期。", "提示");
                    return;
                }
            }
            else
            {
                input = _inputBox.Text.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    MessageBox.Show(this, "请输入玩家名或 mapper 名。", "提示");
                    return;
                }
            }
            var approved = SelectedApproved();
            if (modeIdx == 0 && approved.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一种谱面状态(Ranked/Qualified/Loved)。", "提示");
                return;
            }

            SaveConfig();

            // UI 线程快照: 后台线程只使用这些局部变量, 任务执行中修改界面不影响本次任务
            int threads = _concurrency;
            int retries = _retries;
            int timeout = _timeout;
            int bpLimit = (int)_bpLimit.Value;
            string variant = SelectedVariant;
            string server = SelectedServer;

            BeginBatchUI();
            _log.Clear();
            LogLine("准备中... (线路 " + SelectedServerName + ", 类型 " + variant + ")");

            var dl = NewDownloader(songs, variant, server, threads, retries, timeout);
            _dl = dl;

            var w = new System.ComponentModel.BackgroundWorker();
            w.DoWork += (s2, e2) =>
            {
                try
                {
                    Func<bool> isCancelled = () => dl.IsCancelled;

                    if (modeIdx == 0)
                    {
                        // 1) 先并行收集全部谱面集
                        dl.WriteLog("正在统计 " + input + " ~ " + until + " 的谱面总数(并行)...");
                        var all = TaskCollector.CollectByDateParallel(input, until, gameMode,
                            14, 6, s => dl.WriteLog(s), isCancelled, approved);
                        if (isCancelled()) { dl.WriteLog("已取消"); dl.Finish(); return; }
                        if (all.Count == 0) { dl.WriteLog("未收集到任何谱面, 结束。"); dl.Finish(); return; }

                        // 2) 扫描已有文件/记录, 先过滤(跳过已有的), 再建任务清单
                        dl.WriteLog("扫描本地已有谱面...");
                        dl.ScanExistingSongs();
                        var pending = new List<SetItem>();
                        int skipped = 0;
                        foreach (var item in all)
                        {
                            if (dl.IsAlreadyPresent(item.Sid)) skipped++;
                            else pending.Add(item);
                        }
                        _totalTasks = all.Count;
                        _skip = skipped;
                        BeginInvoke(new Action(() => UpdateStatsText()));
                        dl.WriteLog("去重完成: 已存在跳过 " + skipped + " 个, 待下载 " + pending.Count + " 个");

                        // 3) 分批入队+下载, 分批间隔不清列表
                        _batchRunning = true;
                        try
                        {
                            int batchSize = Math.Max(20, threads * 20);
                            for (int i = 0; i < pending.Count && !isCancelled(); i += batchSize)
                            {
                                int end = Math.Min(i + batchSize, pending.Count);
                                for (int j = i; j < end; j++)
                                    dl.Enqueue(pending[j].Sid, pending[j].Display, pending[j].Artist, ModeNames[gameMode]);
                                dl.WriteLog("已入队 " + end + "/" + pending.Count + ", 下载本批(并发 " + threads + ")...");
                                dl.Start();
                                while (!isCancelled() && !dl.IsIdle)
                                    System.Threading.Thread.Sleep(150);
                            }
                            if (isCancelled()) { dl.WriteLog("已取消"); dl.Cancel(); }
                            else dl.WriteLog("全部日期段下载完成");
                        }
                        finally
                        {
                            _batchRunning = false;
                            BeginInvoke(new Action(() => RestoreButtons()));
                        }
                    }
                    else
                    {
                        // 1) 收集(带元数据)
                        List<SetItem> sets;
                        if (modeIdx == 1) sets = TaskCollector.CollectFromUserBest(input, gameMode, s => dl.WriteLog(s), isCancelled, bpLimit);
                        else sets = TaskCollector.CollectFromMapper(input, gameMode, s => dl.WriteLog(s), isCancelled);

                        if (isCancelled()) { dl.WriteLog("已取消"); dl.Finish(); return; }
                        if (sets.Count == 0) { dl.WriteLog("未收集到任何谱面, 结束。"); dl.Finish(); return; }

                        // 2) 扫描已有, 过滤跳过
                        dl.WriteLog("扫描本地已有谱面...");
                        dl.ScanExistingSongs();
                        var pending = new List<SetItem>();
                        int skipped = 0;
                        foreach (var item in sets)
                        {
                            if (dl.IsAlreadyPresent(item.Sid)) skipped++;
                            else pending.Add(item);
                        }
                        _totalTasks = sets.Count;
                        _skip = skipped;
                        BeginInvoke(new Action(() => UpdateStatsText()));
                        dl.WriteLog("去重完成: 已存在跳过 " + skipped + " 个, 待下载 " + pending.Count + " 个");

                        // 3) 入队下载
                        foreach (var item in pending)
                            dl.Enqueue(item.Sid, item.Display, item.Artist, ModeNames[gameMode]);
                        dl.WriteLog("开始下载(并发 " + threads + ")...");
                        dl.Start();
                    }
                }
                catch (OperationCanceledException)
                {
                    dl.WriteLog("已取消");
                    dl.Finish();
                }
                catch (Exception ex)
                {
                    dl.WriteLog("错误: " + ex.Message);
                    BeginInvoke(new Action(() => RestoreButtons()));
                }
            };
            w.RunWorkerAsync();
        }

        private readonly object _dlLock = new object();

        private Downloader NewDownloader(string songs, string variant, string server, int threads,
            int retries, int timeout)
        {
            var dl = new Downloader(songs, variant, threads, server, retries, timeout, _store);
            dl.Log += s3 => BeginInvoke(new Action(() => LogLine(s3)));
            dl.TaskUpdated += t3 => BeginInvoke(new Action(() => RefreshTask(t3)));
            var self = dl;
            dl.AllDone += () => BeginInvoke(new Action(() =>
            {
                if (!_batchRunning)
                {
                    lock (_dlLock)
                    {
                        if (ReferenceEquals(_dl, self)) RestoreButtons();
                    }
                }
                RefreshFailedList();
            }));
            return dl;
        }

        private void RestoreButtons()
        {
            _startBtn.Enabled = true;
            _stopBtn.Enabled = false;
            _pauseBtn.Enabled = false;
            _pauseBtn.Text = "暂停";
            _dl = null;
            LogLine("全部任务结束");
        }

        /// <summary>批次开始: 复位按钮、计数与任务列表。</summary>
        private void BeginBatchUI()
        {
            _startBtn.Enabled = false;
            _stopBtn.Enabled = true;
            _pauseBtn.Enabled = true;
            _pauseBtn.Text = "暂停";
            _counted.Clear();
            _done = _skip = _fail = _canceled = 0;
            _totalTasks = 0;
            UpdateStatsText();
            ResetTaskListUI();
        }

        /// <summary>清空任务列表与 O(1) 索引(UI 线程调用)。</summary>
        private void ResetTaskListUI()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            _listIndex.Clear();
            _list.EndUpdate();
            _progress.Value = 0;
        }

        private void RefreshTask(DlTask t)
        {
            ListViewItem item;
            if (!_listIndex.TryGetValue(t.Sid, out item))
            {
                item = new ListViewItem(t.Sid.ToString());
                item.Tag = t.Sid;
                item.SubItems.Add(t.Title);
                item.SubItems.Add(t.FileName ?? "");
                item.SubItems.Add(t.State);
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("");
                _listIndex[t.Sid] = item;
                _list.Items.Add(item);
            }
            // 仅在文本变化时写入, 减少 ListView 重绘(进度更新非常频繁)
            SetSubItem(item, 1, t.Title);
            SetSubItem(item, 2, t.FileName ?? "");
            SetSubItem(item, 3, t.State);
            SetSubItem(item, 4, t.TotalBytes > 0 ? t.Percent + "%" : (t.State == "完成" ? "100%" : "--"));
            SetSubItem(item, 5, t.TotalBytes > 0 ? (t.TotalBytes / 1048576.0).ToString("0.0") + " MB" : "");
            SetSubItem(item, 6, t.Speed > 0 ? HumanSpeed(t.Speed) : "");

            // 终态统计: 每个任务只计入一次; 仅此时需要刷新统计与进度条(进度只随终态变化)
            if ((t.State == "完成" || t.State == "已存在" || t.State == "跳过" || t.State == "失败" || t.State == "已取消")
                && _counted.Add(t.Sid))
            {
                if (t.State == "完成") { _done++; }
                else if (t.State == "已存在" || t.State == "跳过") { _skip++; }
                else if (t.State == "已取消") { _canceled++; }
                else
                {
                    _fail++;
                    RefreshFailedList();   // 失败即时刷新清单, 不等批次结束
                }
                UpdateStatsText();
            }
        }

        private static void SetSubItem(ListViewItem item, int index, string text)
        {
            if (item.SubItems[index].Text != text) item.SubItems[index].Text = text;
        }

        /// <summary>状态文字 + 进度条: 已完成 / 未完成 / 跳过 / 失败 / 取消 / 总数</summary>
        private void UpdateStatsText()
        {
            int total = _totalTasks;
            int finished = _done + _skip + _fail;
            int unfinished = Math.Max(0, total - finished - _canceled);
            _statLabel.Text = string.Format("已完成 {0}   未完成 {1}   跳过 {2}   失败 {3}   取消 {4}   总数 {5}",
                _done, unfinished, _skip, _fail, _canceled, total);

            if (total > 0)
            {
                // 取消的批次进度不强行凑满 100%, 未完成数 = 取消数
                int val = (int)(finished * 1000.0 / total);
                _progress.Value = Math.Min(Math.Max(val, 0), 1000);
            }
        }

        private static string HumanSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "";
            if (bytesPerSec >= 1048576) return (bytesPerSec / 1048576.0).ToString("0.0") + " MB/s";
            if (bytesPerSec >= 1024) return (bytesPerSec / 1024.0).ToString("0.0") + " KB/s";
            return bytesPerSec.ToString("0") + " B/s";
        }

        // ---------------- 失败清单 ----------------

        private void RefreshFailedList()
        {
            var list = _store.GetFailedList();
            _failedList.BeginUpdate();
            _failedList.Items.Clear();
            foreach (var f in list)
            {
                var it = new ListViewItem(f.Sid.ToString());
                it.Tag = f.Sid;
                it.SubItems.Add(f.Artist);
                it.SubItems.Add(f.Title);
                it.SubItems.Add(f.Mode);
                it.SubItems.Add(f.Error);
                _failedList.Items.Add(it);
            }
            _failedList.EndUpdate();
        }

        private void RetryFailed(bool selectedOnly)
        {
            if (_dl != null)
            {
                MessageBox.Show(this, "当前有下载任务进行中, 请先等待完成或停止。", "提示");
                return;
            }
            var all = _store.GetFailedList();
            List<FailedInfo> targets;
            if (selectedOnly)
            {
                targets = new List<FailedInfo>();
                foreach (ListViewItem it in _failedList.SelectedItems)
                {
                    long sid = (long)it.Tag;
                    var f = all.FirstOrDefault(x => x.Sid == sid);
                    if (f != null) targets.Add(f);
                }
                if (targets.Count == 0)
                {
                    MessageBox.Show(this, "请先在失败清单中选择条目。", "提示");
                    return;
                }
            }
            else
            {
                targets = all;
                if (targets.Count == 0)
                {
                    MessageBox.Show(this, "失败清单为空。", "提示");
                    return;
                }
            }

            string songs = _songsBox.Text.Trim();
            if (string.IsNullOrEmpty(songs)) { MessageBox.Show(this, "请填写输出目录。", "提示"); return; }
            try { if (!Directory.Exists(songs)) Directory.CreateDirectory(songs); }
            catch (Exception ex) { MessageBox.Show(this, "无法创建输出目录:\n" + ex.Message, "提示"); return; }

            SaveConfig();
            int threads = _concurrency;
            int retries = _retries;
            int timeout = _timeout;

            BeginBatchUI();
            _totalTasks = targets.Count;
            UpdateStatsText();
            LogLine("重试 " + targets.Count + " 个失败任务...");

            var dl = NewDownloader(songs, SelectedVariant, SelectedServer, threads, retries, timeout);
            _dl = dl;

            // 扫描/入队放到后台线程: 谱面库很大时 ScanExistingSongs 会卡住界面
            var w = new System.ComponentModel.BackgroundWorker();
            w.DoWork += (s2, e2) =>
            {
                try
                {
                    dl.WriteLog("扫描本地已有谱面...");
                    dl.ScanExistingSongs();
                    foreach (var f in targets)
                        dl.Enqueue(f.Sid, string.IsNullOrEmpty(f.Artist + f.Title) ? f.Sid.ToString() : f.Artist + " - " + f.Title, f.Artist, f.Mode);
                    dl.WriteLog("开始下载(并发 " + threads + ")...");
                    dl.Start();
                }
                catch (Exception ex)
                {
                    dl.WriteLog("错误: " + ex.Message);
                    BeginInvoke(new Action(() => RestoreButtons()));
                }
            };
            w.RunWorkerAsync();
        }

        private void RemoveFailedSelected()
        {
            if (_failedList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先选择要移除的条目。", "提示");
                return;
            }
            foreach (ListViewItem it in _failedList.SelectedItems)
                _store.RemoveFailed((long)it.Tag);
            RefreshFailedList();
        }

        private void ExportFailedCsv()
        {
            if (_store.FailedCount == 0)
            {
                MessageBox.Show(this, "失败清单为空。", "提示");
                return;
            }
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "CSV 文件|*.csv";
                sfd.FileName = "failed_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        _store.ExportFailedCsv(sfd.FileName);
                        LogLine("失败清单已导出: " + sfd.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "导出失败: " + ex.Message, "错误");
                    }
                }
            }
        }

        private void LogLine(string s)
        {
            // 只裁掉最旧的一部分, 避免整窗清空把历史全丢
            if (_log.TextLength > 200000)
            {
                var text = _log.Text;
                int cut = text.IndexOf("\r\n", Math.Max(0, text.Length - 120000));
                _log.Text = cut > 0 ? text.Substring(cut + 2) : text.Substring(text.Length - 100000);
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + "\r\n");
        }

        // ---------------- 检查更新(仅设置里的按钮) ----------------

        /// <summary>查固定 GitHub 仓库: 有新版本就询问, 确认后下载校验, 关闭设置窗并重启更新。</summary>
        private void CheckForUpdates(Form owner)
        {
            UpdateInfo info;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                info = UpdateChecker.Check();
            }
            catch (NoReleaseException ex)
            {
                Cursor.Current = Cursors.Default;
                MessageBox.Show(owner, ex.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            catch (Exception ex)
            {
                Cursor.Current = Cursors.Default;
                MessageBox.Show(owner, "检查更新失败:\n" + ex.Message, "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Cursor.Current = Cursors.Default;

            if (info == null)
            {
                MessageBox.Show(owner, "当前已是最新版本（" + UpdateChecker.CurrentVersionText + "）。",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrEmpty(info.AssetUrl))
            {
                if (MessageBox.Show(owner,
                        "发现新版本 " + info.Tag + "，但该 Release 没有提供 exe 安装包。\r\n\r\n是否打开下载页？",
                        "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    OpenUrl(info.ReleaseUrl);
                }
                return;
            }

            if (MessageBox.Show(owner,
                    "发现新版本 " + info.Tag + "（当前 " + UpdateChecker.CurrentVersionText + "）。\r\n\r\n是否立即更新？",
                    "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            // 确认后直接下载并校验(更新包很小, 同步下载)
            Cursor.Current = Cursors.WaitCursor;
            string error;
            var file = UpdateChecker.DownloadAndVerify(info, out error);
            Cursor.Current = Cursors.Default;
            if (file == null)
            {
                MessageBox.Show(owner, "下载更新失败:\n" + error, "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var target = Application.ExecutablePath;
            if (!UpdateApplier.CanWriteTarget(target))
            {
                MessageBox.Show(owner,
                    "无法写入程序所在目录，更新包已下载到:\n" + file +
                    "\n\n请关闭程序后手动替换:\n" + target,
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 关闭设置窗, 回到 OpenSettings 收尾后由 ApplyUpdateAndExit 替换并重启
            _pendingUpdateFile = file;
            owner.DialogResult = DialogResult.OK;
        }

        private void ApplyUpdateAndExit(string file)
        {
            try
            {
                if (_dl != null) _dl.Cancel();
                UpdateApplier.ApplyAndRestart(file, Application.ExecutablePath);
                LogLine("正在应用更新，程序将自动重启...");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动更新失败:\n" + ex.Message + "\n\n更新包:\n" + file,
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开下载页失败:\n" + ex.Message, "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { SaveConfig(); } catch { }
            if (_dl != null) _dl.Cancel();
            base.OnFormClosing(e);
        }
    }

    /// <summary>带中央百分比文字的进度条(自绘, 无需第三方控件)。</summary>
    public class TextProgressBar : ProgressBar
    {
        public TextProgressBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) // WM_PAINT: 在原生绘制完成后叠加百分比
            {
                try
                {
                    int pct = Maximum > 0 ? (int)(Value * 100.0 / Maximum) : 0;
                    using (var g = Graphics.FromHwnd(Handle))
                    {
                        var rect = new Rectangle(0, 0, Width, Height);
                        TextRenderer.DrawText(g, pct + "%", Font, rect, Color.Black,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
                catch { }
            }
        }
    }
}
