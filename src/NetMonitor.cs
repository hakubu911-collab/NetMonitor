using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetMonitorApp
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class NicSnapshot
    {
        public string Id;
        public string Name;
        public string Description;
        public NetworkInterfaceType Type;
        public OperationalStatus Status;
        public long SpeedBitsPerSec;
        public string IpAddress;
        public long BytesReceived;
        public long BytesSent;
        public long PacketsReceived;
        public long PacketsSent;
        public long Errors;

        // Calculated rates & session totals
        public double DownSpeedBps;
        public double UpSpeedBps;
        public long SessionDownBytes;
        public long SessionUpBytes;
        public bool IsVirtualOrVpn;
    }

    public class MainForm : Form
    {
        // DPI scale factor
        private float dpi = 1.0f;
        private int S(int px) { return (int)Math.Round(px * dpi); }

        // UI Controls
        private Panel topBar;
        private ComboBox cmbAdapter;
        private CheckBox chkTopMost;
        private Button btnMiniMode;
        private Button btnViewSwitch;
        private Button btnResetSession;
        private Button btnExportCsv;
        private CanvasPanel dashboardCanvas;
        private ListView bottomListView;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private Timer updateTimer;

        // State
        private bool isMiniMode = false;
        private bool showTcpConnections = false;
        private Size normalSize;
        private Point normalLocation;
        private DateTime sessionStartTime;

        // Traffic Tracking
        private Dictionary<string, long> prevRecv = new Dictionary<string, long>();
        private Dictionary<string, long> prevSent = new Dictionary<string, long>();
        private Dictionary<string, long> baseRecv = new Dictionary<string, long>();
        private Dictionary<string, long> baseSent = new Dictionary<string, long>();
        private DateTime lastSampleTime = DateTime.MinValue;

        // Current Selected Metrics
        private string selectedMode = "AUTO"; // "AUTO", "ALL", or NIC Name
        private string activeNicDisplayName = "自动识别中...";
        private string activeIpAddress = "-";
        private long activeLinkSpeed = 0;
        private double currentDownSpeed = 0;
        private double currentUpSpeed = 0;
        private double peakDownSpeed = 0;
        private double peakUpSpeed = 0;
        private long sessionDownTotal = 0;
        private long sessionUpTotal = 0;
        private long bootDownTotal = 0;
        private long bootUpTotal = 0;
        private long totalPacketsRecv = 0;
        private long totalPacketsSent = 0;
        private long totalErrors = 0;

        // Persistent Daily Tracking
        private string dailyStatsFile;
        private string currentDayKey;
        private long savedDailyDown = 0;
        private long savedDailyUp = 0;
        private long lastFlushedSessionDown = 0;
        private long lastFlushedSessionUp = 0;
        private int tickCount = 0;

        // History for 60-second chart
        private const int HistoryLength = 60;
        private List<double> downHistory = new List<double>();
        private List<double> upHistory = new List<double>();

        // Network Health
        private int tcpEstablishedCount = 0;
        private int tcpListenCount = 0;
        private int lastPingMs = -1;
        private bool pingInFlight = false;
        private List<NicSnapshot> latestNics = new List<NicSnapshot>();

        // Dragging support for Mini Mode
        private bool isDragging = false;
        private Point dragStartPoint;

        public MainForm()
        {
            using (Graphics g = this.CreateGraphics())
            {
                dpi = g.DpiX / 96.0f;
                if (dpi < 1.0f) dpi = 1.0f;
            }

            sessionStartTime = DateTime.Now;
            for (int i = 0; i < HistoryLength; i++)
            {
                downHistory.Add(0);
                upHistory.Add(0);
            }

            InitDailyPersistence();
            InitUI();
            PopulateAdapterCombo();
            SampleNetwork(true);

            updateTimer = new Timer();
            updateTimer.Interval = 1000;
            updateTimer.Tick += (s, e) => OnTimerTick();
            updateTimer.Start();
            TriggerPingAsync();
        }

        private void InitDailyPersistence()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetMonitorPro");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                dailyStatsFile = Path.Combine(dir, "daily_traffic.txt");
                currentDayKey = DateTime.Now.ToString("yyyy-MM-dd");
                if (File.Exists(dailyStatsFile))
                {
                    string[] parts = File.ReadAllText(dailyStatsFile, Encoding.UTF8).Trim().Split('|');
                    if (parts.Length >= 3 && parts[0] == currentDayKey)
                    {
                        long.TryParse(parts[1], out savedDailyDown);
                        long.TryParse(parts[2], out savedDailyUp);
                    }
                }
            }
            catch { }
        }

        private void SaveDailyPersistence()
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (today != currentDayKey)
                {
                    currentDayKey = today;
                    savedDailyDown = 0;
                    savedDailyUp = 0;
                    lastFlushedSessionDown = sessionDownTotal;
                    lastFlushedSessionUp = sessionUpTotal;
                }
                long deltaDown = Math.Max(0, sessionDownTotal - lastFlushedSessionDown);
                long deltaUp = Math.Max(0, sessionUpTotal - lastFlushedSessionUp);
                lastFlushedSessionDown = sessionDownTotal;
                lastFlushedSessionUp = sessionUpTotal;
                savedDailyDown += deltaDown;
                savedDailyUp += deltaUp;
                File.WriteAllText(dailyStatsFile, string.Format("{0}|{1}|{2}", currentDayKey, savedDailyDown, savedDailyUp), Encoding.UTF8);
            }
            catch { }
        }

        private void InitUI()
        {
            this.AutoScaleMode = AutoScaleMode.None;
            this.Text = "实时网络流量与连接监测器 Pro";
            this.Size = new Size(S(980), S(680));
            this.MinimumSize = new Size(S(320), S(64));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(241, 245, 249);
            this.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) this.Icon = appIcon;
            }
            catch { }

            // Top Control Bar
            topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = S(48);
            topBar.BackColor = Color.FromArgb(22, 32, 56);
            topBar.Padding = new Padding(S(12), S(8), S(12), S(8));

            Label lblNic = new Label();
            lblNic.Text = "监测网卡:";
            lblNic.ForeColor = Color.FromArgb(148, 163, 184);
            lblNic.AutoSize = true;
            lblNic.Location = new Point(S(12), S(14));
            topBar.Controls.Add(lblNic);

            cmbAdapter = new ComboBox();
            cmbAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAdapter.BackColor = Color.FromArgb(30, 41, 59);
            cmbAdapter.ForeColor = Color.White;
            cmbAdapter.FlatStyle = FlatStyle.Flat;
            cmbAdapter.Bounds = new Rectangle(S(84), S(10), S(280), S(28));
            cmbAdapter.SelectedIndexChanged += (s, e) =>
            {
                if (cmbAdapter.SelectedIndex == 0) selectedMode = "AUTO";
                else if (cmbAdapter.SelectedIndex == 1) selectedMode = "ALL";
                else selectedMode = cmbAdapter.SelectedItem.ToString();
                SampleNetwork(false);
                dashboardCanvas.Invalidate();
            };
            topBar.Controls.Add(cmbAdapter);

            chkTopMost = new CheckBox();
            chkTopMost.Text = "窗口置顶";
            chkTopMost.ForeColor = Color.FromArgb(226, 232, 240);
            chkTopMost.AutoSize = true;
            chkTopMost.Location = new Point(S(378), S(13));
            chkTopMost.CheckedChanged += (s, e) => { this.TopMost = chkTopMost.Checked; };
            topBar.Controls.Add(chkTopMost);

            btnMiniMode = CreateStyledButton("悬浮窗模式", S(468), S(9), S(98), S(30), Color.FromArgb(14, 116, 144));
            btnMiniMode.Click += (s, e) => ToggleMiniMode();
            topBar.Controls.Add(btnMiniMode);

            btnViewSwitch = CreateStyledButton("切换: 进程连接表", S(574), S(9), S(126), S(30), Color.FromArgb(51, 65, 85));
            btnViewSwitch.Click += (s, e) => ToggleBottomView();
            topBar.Controls.Add(btnViewSwitch);

            btnResetSession = CreateStyledButton("重置本次累计", S(708), S(9), S(108), S(30), Color.FromArgb(51, 65, 85));
            btnResetSession.Click += (s, e) => ResetSessionCounters();
            topBar.Controls.Add(btnResetSession);

            btnExportCsv = CreateStyledButton("导出报表", S(824), S(9), S(86), S(30), Color.FromArgb(5, 150, 105));
            btnExportCsv.Click += (s, e) => ExportCsvReport();
            topBar.Controls.Add(btnExportCsv);

            // Dashboard Canvas (Cards + Live Waveform Chart)
            dashboardCanvas = new CanvasPanel();
            dashboardCanvas.Dock = DockStyle.Top;
            dashboardCanvas.Height = S(365);
            dashboardCanvas.BackColor = Color.FromArgb(15, 23, 42);
            dashboardCanvas.Paint += DashboardCanvas_Paint;
            dashboardCanvas.MouseDown += Canvas_MouseDown;
            dashboardCanvas.MouseMove += Canvas_MouseMove;
            dashboardCanvas.MouseUp += Canvas_MouseUp;
            dashboardCanvas.DoubleClick += (s, e) => { if (isMiniMode) ToggleMiniMode(); };

            // Bottom ListView (NIC Breakdown / Active TCP Connections)
            bottomListView = new ListView();
            bottomListView.Dock = DockStyle.Fill;
            bottomListView.View = View.Details;
            bottomListView.FullRowSelect = true;
            bottomListView.GridLines = true;
            bottomListView.BorderStyle = BorderStyle.None;
            bottomListView.BackColor = Color.FromArgb(20, 30, 52);
            bottomListView.ForeColor = Color.FromArgb(226, 232, 240);
            bottomListView.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular);
            EnableDoubleBuffering(bottomListView);
            SetupNicColumns();

            this.Controls.Add(bottomListView);
            this.Controls.Add(dashboardCanvas);
            this.Controls.Add(topBar);

            // System Tray Icon
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, (s, e) => { if (isMiniMode) ToggleMiniMode(); this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); });
            trayMenu.Items.Add("切换迷你悬浮窗", null, (s, e) => { this.Show(); if (!isMiniMode) ToggleMiniMode(); });
            trayMenu.Items.Add("重置本次统计", null, (s, e) => ResetSessionCounters());
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("退出监测器", null, (s, e) => { SaveDailyPersistence(); trayIcon.Visible = false; Application.Exit(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon ?? SystemIcons.Application;
            trayIcon.Text = "实时网络流量监测器 Pro";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                if (this.WindowState == FormWindowState.Minimized) this.WindowState = FormWindowState.Normal;
                this.Activate();
            };

            this.FormClosing += (s, e) =>
            {
                SaveDailyPersistence();
                trayIcon.Visible = false;
            };
        }

        private Button CreateStyledButton(string text, int x, int y, int w, int h, Color bg)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Bounds = new Rectangle(x, y, w, h);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            btn.FlatAppearance.BorderSize = 1;
            btn.BackColor = bg;
            btn.ForeColor = Color.White;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static void EnableDoubleBuffering(Control control)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(control, true, null);
            }
            catch { }
        }

        private void SetupNicColumns()
        {
            bottomListView.Columns.Clear();
            bottomListView.Columns.Add("网卡名称", S(150));
            bottomListView.Columns.Add("状态/类型", S(95));
            bottomListView.Columns.Add("IPv4 地址", S(115));
            bottomListView.Columns.Add("↓ 实时下载", S(95));
            bottomListView.Columns.Add("↑ 实时上传", S(95));
            bottomListView.Columns.Add("↓ 本次累计下载", S(105));
            bottomListView.Columns.Add("↑ 本次累计上传", S(105));
            bottomListView.Columns.Add("↓ 开机总下载", S(100));
            bottomListView.Columns.Add("↑ 开机总上传", S(100));
        }

        private void SetupTcpColumns()
        {
            bottomListView.Columns.Clear();
            bottomListView.Columns.Add("进程名称", S(150));
            bottomListView.Columns.Add("PID", S(65));
            bottomListView.Columns.Add("协议/状态", S(110));
            bottomListView.Columns.Add("本地地址:端口", S(180));
            bottomListView.Columns.Add("远程目标地址:端口", S(210));
            bottomListView.Columns.Add("进程路径", S(230));
        }

        private void ToggleBottomView()
        {
            showTcpConnections = !showTcpConnections;
            if (showTcpConnections)
            {
                btnViewSwitch.Text = "切换: 网卡流量表";
                btnViewSwitch.BackColor = Color.FromArgb(79, 70, 229);
                SetupTcpColumns();
                RefreshTcpListView();
            }
            else
            {
                btnViewSwitch.Text = "切换: 进程连接表";
                btnViewSwitch.BackColor = Color.FromArgb(51, 65, 85);
                SetupNicColumns();
                RefreshNicListView();
            }
        }

        private void ToggleMiniMode()
        {
            isMiniMode = !isMiniMode;
            if (isMiniMode)
            {
                normalSize = this.Size;
                normalLocation = this.Location;
                topBar.Visible = false;
                bottomListView.Visible = false;
                dashboardCanvas.Dock = DockStyle.Fill;
                this.FormBorderStyle = FormBorderStyle.None;
                this.TopMost = true;
                this.Size = new Size(S(380), S(78));
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                this.Location = new Point(workArea.Right - this.Width - S(20), workArea.Bottom - this.Height - S(20));
            }
            else
            {
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.TopMost = chkTopMost.Checked;
                dashboardCanvas.Dock = DockStyle.Top;
                dashboardCanvas.Height = S(365);
                topBar.Visible = true;
                bottomListView.Visible = true;
                this.Size = normalSize;
                this.Location = normalLocation;
            }
            dashboardCanvas.Invalidate();
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (isMiniMode && e.Button == MouseButtons.Left)
            {
                // Check if clicked the "展开" button on the right of mini window
                Rectangle expandBtn = new Rectangle(dashboardCanvas.Width - S(58), S(22), S(48), S(34));
                if (expandBtn.Contains(e.Location))
                {
                    ToggleMiniMode();
                    return;
                }
                isDragging = true;
                dragStartPoint = e.Location;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (isMiniMode && isDragging)
            {
                Point p = PointToScreen(e.Location);
                this.Location = new Point(p.X - dragStartPoint.X, p.Y - dragStartPoint.Y);
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
        }

        private void PopulateAdapterCombo()
        {
            cmbAdapter.Items.Clear();
            cmbAdapter.Items.Add("智能主网卡 (自动排除VPN/虚拟网卡重复计数)");
            cmbAdapter.Items.Add("全部网卡汇总 (不含本地环回)");
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    cmbAdapter.Items.Add(nic.Name);
                }
            }
            catch { }
            cmbAdapter.SelectedIndex = 0;
        }

        private bool IsVirtualOrVpnAdapter(NetworkInterface nic)
        {
            string combined = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                return true;

            string[] virtualKeywords = new string[] {
                "vethernet", "virtual", "vmware", "vbox", "hyper-v", "wsl",
                "wireguard", "cyberghost", "tap-", "tun", "wintun", "clash",
                "sing-box", "vpn", "pseudo", "teredo", "isatap", "bluetooth"
            };
            foreach (string kw in virtualKeywords)
            {
                if (combined.Contains(kw)) return true;
            }
            return false;
        }

        private void ResetSessionCounters()
        {
            SaveDailyPersistence();
            baseRecv.Clear();
            baseSent.Clear();
            lastFlushedSessionDown = 0;
            lastFlushedSessionUp = 0;
            peakDownSpeed = 0;
            peakUpSpeed = 0;
            sessionStartTime = DateTime.Now;
            downHistory.Clear();
            upHistory.Clear();
            for (int i = 0; i < HistoryLength; i++)
            {
                downHistory.Add(0);
                upHistory.Add(0);
            }
            SampleNetwork(true);
            dashboardCanvas.Invalidate();
        }

        private void OnTimerTick()
        {
            tickCount++;
            SampleNetwork(false);

            downHistory.Add(currentDownSpeed);
            if (downHistory.Count > HistoryLength) downHistory.RemoveAt(0);

            upHistory.Add(currentUpSpeed);
            if (upHistory.Count > HistoryLength) upHistory.RemoveAt(0);

            if (tickCount % 3 == 0)
            {
                UpdateTcpStats();
            }
            if (tickCount % 5 == 0)
            {
                TriggerPingAsync();
                SaveDailyPersistence();
            }

            if (!isMiniMode)
            {
                if (!showTcpConnections)
                    RefreshNicListView();
                else if (tickCount % 2 == 0)
                    RefreshTcpListView();
            }

            string trayText = string.Format("↓ {0}/s  ↑ {1}/s\n本次累计: ↓ {2}  ↑ {3}",
                FormatBytes((long)currentDownSpeed),
                FormatBytes((long)currentUpSpeed),
                FormatBytes(sessionDownTotal),
                FormatBytes(sessionUpTotal));
            if (trayText.Length >= 63) trayText = trayText.Substring(0, 62);
            trayIcon.Text = trayText;

            dashboardCanvas.Invalidate();
        }

        private void SampleNetwork(bool isInit)
        {
            DateTime now = DateTime.Now;
            double elapsedSec = (lastSampleTime == DateTime.MinValue) ? 1.0 : (now - lastSampleTime).TotalSeconds;
            if (elapsedSec <= 0.05) elapsedSec = 1.0;
            lastSampleTime = now;

            List<NicSnapshot> list = new List<NicSnapshot>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    IPv4InterfaceStatistics stats = null;
                    try { stats = nic.GetIPv4Statistics(); } catch { continue; }

                    long recv = stats.BytesReceived;
                    long sent = stats.BytesSent;
                    string key = nic.Id;

                    if (!baseRecv.ContainsKey(key) || isInit)
                    {
                        baseRecv[key] = recv;
                        baseSent[key] = sent;
                    }

                    double dSpeed = 0;
                    double uSpeed = 0;
                    if (prevRecv.ContainsKey(key) && !isInit)
                    {
                        dSpeed = Math.Max(0, (recv - prevRecv[key]) / elapsedSec);
                        uSpeed = Math.Max(0, (sent - prevSent[key]) / elapsedSec);
                    }
                    prevRecv[key] = recv;
                    prevSent[key] = sent;

                    string ip = "-";
                    try
                    {
                        var props = nic.GetIPProperties();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }

                    list.Add(new NicSnapshot
                    {
                        Id = key,
                        Name = nic.Name,
                        Description = nic.Description,
                        Type = nic.NetworkInterfaceType,
                        Status = nic.OperationalStatus,
                        SpeedBitsPerSec = nic.Speed,
                        IpAddress = ip,
                        BytesReceived = recv,
                        BytesSent = sent,
                        PacketsReceived = stats.UnicastPacketsReceived,
                        PacketsSent = stats.UnicastPacketsSent,
                        Errors = stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors + stats.IncomingPacketsDiscarded,
                        DownSpeedBps = dSpeed,
                        UpSpeedBps = uSpeed,
                        SessionDownBytes = Math.Max(0, recv - baseRecv[key]),
                        SessionUpBytes = Math.Max(0, sent - baseSent[key]),
                        IsVirtualOrVpn = IsVirtualOrVpnAdapter(nic)
                    });
                }
            }
            catch { }

            latestNics = list.OrderByDescending(x => x.BytesReceived + x.BytesSent).ToList();

            // Aggregate according to selectedMode
            if (selectedMode == "AUTO")
            {
                // Pick active physical NICs first; fallback to highest traffic NIC
                var physicalUp = latestNics.Where(x => !x.IsVirtualOrVpn && x.Status == OperationalStatus.Up && (x.BytesReceived > 0 || x.BytesSent > 0)).ToList();
                if (physicalUp.Count == 0)
                    physicalUp = latestNics.Where(x => !x.IsVirtualOrVpn && (x.BytesReceived > 0 || x.BytesSent > 0)).ToList();
                if (physicalUp.Count == 0 && latestNics.Count > 0)
                    physicalUp = new List<NicSnapshot> { latestNics[0] };

                if (physicalUp.Count > 0)
                {
                    var primary = physicalUp.OrderByDescending(x => x.DownSpeedBps + x.UpSpeedBps).ThenByDescending(x => x.BytesReceived).First();
                    activeNicDisplayName = primary.Name + " (物理主网卡)";
                    activeIpAddress = primary.IpAddress;
                    activeLinkSpeed = primary.SpeedBitsPerSec;
                    currentDownSpeed = physicalUp.Sum(x => x.DownSpeedBps);
                    currentUpSpeed = physicalUp.Sum(x => x.UpSpeedBps);
                    sessionDownTotal = physicalUp.Sum(x => x.SessionDownBytes);
                    sessionUpTotal = physicalUp.Sum(x => x.SessionUpBytes);
                    bootDownTotal = physicalUp.Sum(x => x.BytesReceived);
                    bootUpTotal = physicalUp.Sum(x => x.BytesSent);
                    totalPacketsRecv = physicalUp.Sum(x => x.PacketsReceived);
                    totalPacketsSent = physicalUp.Sum(x => x.PacketsSent);
                    totalErrors = physicalUp.Sum(x => x.Errors);
                }
            }
            else if (selectedMode == "ALL")
            {
                activeNicDisplayName = "全部网卡汇总";
                var upNic = latestNics.FirstOrDefault(x => x.IpAddress != "-");
                activeIpAddress = upNic != null ? upNic.IpAddress : "-";
                activeLinkSpeed = latestNics.Where(x => x.Status == OperationalStatus.Up).Select(x => x.SpeedBitsPerSec).DefaultIfEmpty(0).Max();
                currentDownSpeed = latestNics.Sum(x => x.DownSpeedBps);
                currentUpSpeed = latestNics.Sum(x => x.UpSpeedBps);
                sessionDownTotal = latestNics.Sum(x => x.SessionDownBytes);
                sessionUpTotal = latestNics.Sum(x => x.SessionUpBytes);
                bootDownTotal = latestNics.Sum(x => x.BytesReceived);
                bootUpTotal = latestNics.Sum(x => x.BytesSent);
                totalPacketsRecv = latestNics.Sum(x => x.PacketsReceived);
                totalPacketsSent = latestNics.Sum(x => x.PacketsSent);
                totalErrors = latestNics.Sum(x => x.Errors);
            }
            else
            {
                var target = latestNics.FirstOrDefault(x => x.Name == selectedMode);
                if (target != null)
                {
                    activeNicDisplayName = target.Name;
                    activeIpAddress = target.IpAddress;
                    activeLinkSpeed = target.SpeedBitsPerSec;
                    currentDownSpeed = target.DownSpeedBps;
                    currentUpSpeed = target.UpSpeedBps;
                    sessionDownTotal = target.SessionDownBytes;
                    sessionUpTotal = target.SessionUpBytes;
                    bootDownTotal = target.BytesReceived;
                    bootUpTotal = target.BytesSent;
                    totalPacketsRecv = target.PacketsReceived;
                    totalPacketsSent = target.PacketsSent;
                    totalErrors = target.Errors;
                }
            }

            if (currentDownSpeed > peakDownSpeed) peakDownSpeed = currentDownSpeed;
            if (currentUpSpeed > peakUpSpeed) peakUpSpeed = currentUpSpeed;
        }

        private void UpdateTcpStats()
        {
            try
            {
                IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
                var conns = props.GetActiveTcpConnections();
                tcpEstablishedCount = conns.Count(c => c.State == TcpState.Established);
                tcpListenCount = props.GetActiveTcpListeners().Length;
            }
            catch { }
        }

        private void TriggerPingAsync()
        {
            if (pingInFlight) return;
            pingInFlight = true;
            Task.Run(() =>
            {
                try
                {
                    using (Ping p = new Ping())
                    {
                        PingReply reply = p.Send("223.5.5.5", 1200);
                        if (reply != null && reply.Status == IPStatus.Success)
                            lastPingMs = (int)reply.RoundtripTime;
                        else
                            lastPingMs = -1;
                    }
                }
                catch { lastPingMs = -1; }
                finally { pingInFlight = false; }
            });
        }

        private void RefreshNicListView()
        {
            bottomListView.BeginUpdate();
            int idx = 0;
            foreach (var nic in latestNics)
            {
                // Skip completely zero unused virtual adapters to keep table clean unless active
                if (nic.BytesReceived == 0 && nic.BytesSent == 0 && nic.Status != OperationalStatus.Up)
                    continue;

                string statusTag = (nic.Status == OperationalStatus.Up ? "已连接" : "已断开") + (nic.IsVirtualOrVpn ? " (虚拟/VPN)" : " (物理)");
                string[] row = new string[]
                {
                    nic.Name,
                    statusTag,
                    nic.IpAddress,
                    FormatBytes((long)nic.DownSpeedBps) + "/s",
                    FormatBytes((long)nic.UpSpeedBps) + "/s",
                    FormatBytes(nic.SessionDownBytes),
                    FormatBytes(nic.SessionUpBytes),
                    FormatBytes(nic.BytesReceived),
                    FormatBytes(nic.BytesSent)
                };

                if (idx < bottomListView.Items.Count)
                {
                    ListViewItem item = bottomListView.Items[idx];
                    for (int c = 0; c < row.Length && c < item.SubItems.Count; c++)
                    {
                        if (item.SubItems[c].Text != row[c])
                            item.SubItems[c].Text = row[c];
                    }
                }
                else
                {
                    ListViewItem item = new ListViewItem(row);
                    bottomListView.Items.Add(item);
                }
                idx++;
            }
            while (bottomListView.Items.Count > idx)
            {
                bottomListView.Items.RemoveAt(bottomListView.Items.Count - 1);
            }
            bottomListView.EndUpdate();
        }

        // P/Invoke for GetExtendedTcpTable to show PID for each TCP connection
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public byte remotePort1;
            public byte remotePort2;
            public byte remotePort3;
            public byte remotePort4;
            public int owningPid;
        }

        private void RefreshTcpListView()
        {
            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true, 2, 5, 0); // AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4, ALL = 5
            if (buffSize <= 0) return;

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(buffSize);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref buffSize, true, 2, 5, 0) != 0) return;
                int rowCount = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));

                List<string[]> rows = new List<string[]>();
                Dictionary<int, Tuple<string, string>> procCache = new Dictionary<int, Tuple<string, string>>();

                for (int i = 0; i < rowCount; i++)
                {
                    MIB_TCPROW_OWNER_PID r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                    rowPtr = (IntPtr)((long)rowPtr + rowSize);

                    // Show Established (5), CloseWait (8), SynSent (3), TimeWait (11)
                    if (r.state != 5 && r.state != 8 && r.state != 3) continue;

                    int lPort = (r.localPort1 << 8) + r.localPort2;
                    int rPort = (r.remotePort1 << 8) + r.remotePort2;
                    string lIp = new IPAddress(r.localAddr).ToString();
                    string rIp = new IPAddress(r.remoteAddr).ToString();
                    if (lIp == "127.0.0.1" && rIp == "127.0.0.1") continue;

                    Tuple<string, string> pInfo;
                    if (!procCache.TryGetValue(r.owningPid, out pInfo))
                    {
                        string pName = "System";
                        string pPath = "-";
                        try
                        {
                            Process p = Process.GetProcessById(r.owningPid);
                            pName = p.ProcessName + ".exe";
                            try { pPath = p.MainModule.FileName; } catch { }
                        }
                        catch { }
                        pInfo = Tuple.Create(pName, pPath);
                        procCache[r.owningPid] = pInfo;
                    }

                    string stateStr = r.state == 5 ? "ESTABLISHED (活跃)" : (r.state == 8 ? "CLOSE_WAIT" : "SYN_SENT");
                    rows.Add(new string[]
                    {
                        pInfo.Item1,
                        r.owningPid.ToString(),
                        stateStr,
                        lIp + ":" + lPort,
                        rIp + ":" + rPort,
                        pInfo.Item2
                    });
                    if (rows.Count >= 80) break;
                }

                bottomListView.BeginUpdate();
                int idx = 0;
                foreach (var row in rows)
                {
                    if (idx < bottomListView.Items.Count)
                    {
                        ListViewItem item = bottomListView.Items[idx];
                        for (int c = 0; c < row.Length && c < item.SubItems.Count; c++)
                        {
                            if (item.SubItems[c].Text != row[c])
                                item.SubItems[c].Text = row[c];
                        }
                    }
                    else
                    {
                        bottomListView.Items.Add(new ListViewItem(row));
                    }
                    idx++;
                }
                while (bottomListView.Items.Count > idx)
                {
                    bottomListView.Items.RemoveAt(bottomListView.Items.Count - 1);
                }
                bottomListView.EndUpdate();
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
        }

        private void ExportCsvReport()
        {
            try
            {
                string fileName = string.Format("网络流量报表_{0:yyyyMMdd_HHmmss}.csv", DateTime.Now);
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("报表生成时间," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("当前监测模式," + activeNicDisplayName);
                sb.AppendLine("本次监测时长," + (DateTime.Now - sessionStartTime).ToString(@"hh\:mm\:ss"));
                sb.AppendLine("本次累计下载," + FormatBytes(sessionDownTotal));
                sb.AppendLine("本次累计上传," + FormatBytes(sessionUpTotal));
                sb.AppendLine("今日累计下载," + FormatBytes(savedDailyDown + Math.Max(0, sessionDownTotal - lastFlushedSessionDown)));
                sb.AppendLine("今日累计上传," + FormatBytes(savedDailyUp + Math.Max(0, sessionUpTotal - lastFlushedSessionUp)));
                sb.AppendLine("开机累计下载," + FormatBytes(bootDownTotal));
                sb.AppendLine("开机累计上传," + FormatBytes(bootUpTotal));
                sb.AppendLine("峰值下载速度," + FormatBytes((long)peakDownSpeed) + "/s");
                sb.AppendLine("峰值上传速度," + FormatBytes((long)peakUpSpeed) + "/s");
                sb.AppendLine();
                sb.AppendLine("网卡名称,状态,IPv4地址,实时下载,实时上传,本次累计下载,本次累计上传,开机总下载,开机总上传,收发总包数");
                foreach (var n in latestNics)
                {
                    sb.AppendFormat("\"{0}\",{1},{2},{3}/s,{4}/s,{5},{6},{7},{8},{9}\r\n",
                        n.Name, n.Status, n.IpAddress,
                        FormatBytes((long)n.DownSpeedBps), FormatBytes((long)n.UpSpeedBps),
                        FormatBytes(n.SessionDownBytes), FormatBytes(n.SessionUpBytes),
                        FormatBytes(n.BytesReceived), FormatBytes(n.BytesSent),
                        n.PacketsReceived + n.PacketsSent);
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("流量统计报表已导出至桌面：\n" + path, "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DashboardCanvas_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int w = dashboardCanvas.Width;
            int h = dashboardCanvas.Height;

            if (isMiniMode)
            {
                PaintMiniHud(g, w, h);
                return;
            }

            int pad = S(12);
            int cardGap = S(10);
            int cardW = (w - pad * 2 - cardGap * 3) / 4;
            int cardH = S(108);

            // 1. Card 1: Real-time Download
            double downMbps = (currentDownSpeed * 8.0) / (1000.0 * 1000.0);
            DrawMetricCard(g, new Rectangle(pad, pad, cardW, cardH),
                "↓ 实时下载速度",
                FormatBytes((long)currentDownSpeed) + "/s",
                string.Format("带宽: {0:0.0} Mbps | 峰值: {1}/s", downMbps, FormatBytes((long)peakDownSpeed)),
                Color.FromArgb(56, 189, 248),
                Color.FromArgb(14, 116, 144));

            // 2. Card 2: Real-time Upload
            double upMbps = (currentUpSpeed * 8.0) / (1000.0 * 1000.0);
            DrawMetricCard(g, new Rectangle(pad + (cardW + cardGap) * 1, pad, cardW, cardH),
                "↑ 实时上传速度",
                FormatBytes((long)currentUpSpeed) + "/s",
                string.Format("带宽: {0:0.0} Mbps | 峰值: {1}/s", upMbps, FormatBytes((long)peakUpSpeed)),
                Color.FromArgb(245, 158, 11),
                Color.FromArgb(180, 83, 9));

            // 3. Card 3: Session & Daily Cumulative
            TimeSpan elapsed = DateTime.Now - sessionStartTime;
            long todayDown = savedDailyDown + Math.Max(0, sessionDownTotal - lastFlushedSessionDown);
            long todayUp = savedDailyUp + Math.Max(0, sessionUpTotal - lastFlushedSessionUp);
            DrawSummaryCard(g, new Rectangle(pad + (cardW + cardGap) * 2, pad, cardW, cardH),
                string.Format("⏱ 本次与今日累计 ({0:00}:{1:00}:{2:00})", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds),
                "本次 ↓ " + FormatBytes(sessionDownTotal),
                "本次 ↑ " + FormatBytes(sessionUpTotal),
                string.Format("今日总计: ↓ {0}  ↑ {1}", FormatBytes(todayDown), FormatBytes(todayUp)),
                Color.FromArgb(16, 185, 129));

            // 4. Card 4: Boot Total & Network Health
            string pingStr = lastPingMs >= 0 ? (lastPingMs + " ms") : "检测中";
            DrawSummaryCard(g, new Rectangle(pad + (cardW + cardGap) * 3, pad, cardW, cardH),
                "🖥 开机累计流量 & 网络状态",
                "开机 ↓ " + FormatBytes(bootDownTotal),
                "开机 ↑ " + FormatBytes(bootUpTotal),
                string.Format("TCP连接: {0} | 延迟: {1} | 错包: {2}", tcpEstablishedCount, pingStr, totalErrors),
                Color.FromArgb(168, 85, 247));

            // 5. Real-time 60s Waveform Chart Area
            int chartTop = pad + cardH + S(12);
            int chartH = h - chartTop - S(10);
            if (chartH > S(60))
            {
                Rectangle chartRect = new Rectangle(pad, chartTop, w - pad * 2, chartH);
                DrawWaveformChart(g, chartRect);
            }
        }

        private void PaintMiniHud(Graphics g, int w, int h)
        {
            Rectangle r = new Rectangle(1, 1, w - 3, h - 3);
            using (GraphicsPath path = RoundedRect(r, S(12)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(20, 30, 52)))
            using (Pen border = new Pen(Color.FromArgb(56, 189, 248), 1.5f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (Font fSpeed = new Font("Microsoft YaHei UI", 11.0f, FontStyle.Bold))
            using (Font fSub = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular))
            using (Brush bDown = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (Brush bUp = new SolidBrush(Color.FromArgb(245, 158, 11)))
            using (Brush bGray = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                g.DrawString("↓ " + FormatBytes((long)currentDownSpeed) + "/s", fSpeed, bDown, S(14), S(12));
                g.DrawString("↑ " + FormatBytes((long)currentUpSpeed) + "/s", fSpeed, bUp, S(165), S(12));
                g.DrawString(string.Format("本次累计: ↓ {0}   ↑ {1}   (双击还原)", FormatBytes(sessionDownTotal), FormatBytes(sessionUpTotal)),
                    fSub, bGray, S(14), S(45));
            }

            // Expand button
            Rectangle btnRect = new Rectangle(w - S(58), S(22), S(46), S(32));
            using (GraphicsPath bp = RoundedRect(btnRect, S(6)))
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(51, 65, 85)))
            using (Font bf = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            {
                g.FillPath(bb, bp);
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("展开", bf, Brushes.White, btnRect, sf);
            }
        }

        private void DrawMetricCard(Graphics g, Rectangle rect, string title, string mainVal, string subVal, Color accentColor, Color barColor)
        {
            using (GraphicsPath path = RoundedRect(rect, S(10)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(25, 36, 62)))
            using (Pen border = new Pen(Color.FromArgb(45, 60, 92), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            // Top accent line
            using (SolidBrush acc = new SolidBrush(accentColor))
            {
                g.FillRectangle(acc, rect.X + S(14), rect.Y + S(10), S(4), S(16));
            }

            using (Font fTitle = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold))
            using (Font fMain = new Font("Microsoft YaHei UI", 16.5f, FontStyle.Bold))
            using (Font fSub = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Regular))
            using (Brush bTitle = new SolidBrush(Color.FromArgb(203, 213, 225)))
            using (Brush bMain = new SolidBrush(accentColor))
            using (Brush bSub = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                g.DrawString(title, fTitle, bTitle, rect.X + S(24), rect.Y + S(8));
                g.DrawString(mainVal, fMain, bMain, rect.X + S(12), rect.Y + S(34));
                g.DrawString(subVal, fSub, bSub, rect.X + S(12), rect.Y + S(80));
            }
        }

        private void DrawSummaryCard(Graphics g, Rectangle rect, string title, string line1Left, string line1Right, string subVal, Color accentColor)
        {
            using (GraphicsPath path = RoundedRect(rect, S(10)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(25, 36, 62)))
            using (Pen border = new Pen(Color.FromArgb(45, 60, 92), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (SolidBrush acc = new SolidBrush(accentColor))
            {
                g.FillRectangle(acc, rect.X + S(14), rect.Y + S(10), S(4), S(16));
            }

            using (Font fTitle = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold))
            using (Font fMid = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
            using (Font fSub = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Regular))
            using (Brush bTitle = new SolidBrush(Color.FromArgb(203, 213, 225)))
            using (Brush bCyan = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (Brush bAmber = new SolidBrush(Color.FromArgb(245, 158, 11)))
            using (Brush bSub = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                g.DrawString(title, fTitle, bTitle, rect.X + S(24), rect.Y + S(8));
                g.DrawString(line1Left, fMid, bCyan, rect.X + S(12), rect.Y + S(36));
                g.DrawString(line1Right, fMid, bAmber, rect.X + rect.Width / 2 + S(2), rect.Y + S(36));
                g.DrawString(subVal, fSub, bSub, rect.X + S(12), rect.Y + S(78));
            }
        }

        private void DrawWaveformChart(Graphics g, Rectangle rect)
        {
            using (GraphicsPath path = RoundedRect(rect, S(10)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(22, 32, 56)))
            using (Pen border = new Pen(Color.FromArgb(45, 60, 92), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            // Header bar inside chart
            string linkMbps = activeLinkSpeed > 0 ? (activeLinkSpeed / 1000000) + " Mbps" : "自适应";
            string headerInfo = string.Format("实时速率波形图 (近60秒)   |   当前网卡: {0}   |   本机IP: {1}   |   协商速率: {2}   |   累计收发包: {3:N0}",
                activeNicDisplayName, activeIpAddress, linkMbps, totalPacketsRecv + totalPacketsSent);

            using (Font fHeader = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold))
            using (Brush bHeader = new SolidBrush(Color.FromArgb(203, 213, 225)))
            {
                g.DrawString(headerInfo, fHeader, bHeader, rect.X + S(14), rect.Y + S(10));
            }

            // Legend on right side of chart header
            using (Font fLeg = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (Brush bDown = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (Brush bUp = new SolidBrush(Color.FromArgb(245, 158, 11)))
            {
                g.DrawString("● 下载曲线", fLeg, bDown, rect.Right - S(175), rect.Y + S(10));
                g.DrawString("● 上传曲线", fLeg, bUp, rect.Right - S(90), rect.Y + S(10));
            }

            Rectangle plot = new Rectangle(rect.X + S(72), rect.Y + S(38), rect.Width - S(88), rect.Height - S(52));
            if (plot.Width <= 20 || plot.Height <= 20) return;

            double maxVal = 1024 * 100; // Min scale 100 KB/s
            foreach (double v in downHistory) if (v > maxVal) maxVal = v;
            foreach (double v in upHistory) if (v > maxVal) maxVal = v;
            maxVal *= 1.15;

            // Horizontal grid lines & Y-axis labels
            using (Pen gridPen = new Pen(Color.FromArgb(36, 50, 82), 1f))
            using (Font fAxis = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
            using (Brush bAxis = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                gridPen.DashStyle = DashStyle.Dot;
                int rows = 4;
                for (int r = 0; r <= rows; r++)
                {
                    int y = plot.Top + (plot.Height * r) / rows;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    double val = maxVal * (rows - r) / rows;
                    string lbl = r == rows ? "0 B/s" : FormatBytes((long)val) + "/s";
                    g.DrawString(lbl, fAxis, bAxis, rect.X + S(8), y - S(7));
                }
            }

            // Draw Download & Upload series
            DrawSeriesArea(g, plot, downHistory, maxVal, Color.FromArgb(56, 189, 248), Color.FromArgb(95, 56, 189, 248));
            DrawSeriesArea(g, plot, upHistory, maxVal, Color.FromArgb(245, 158, 11), Color.FromArgb(85, 245, 158, 11));
        }

        private void DrawSeriesArea(Graphics g, Rectangle plot, List<double> data, double maxVal, Color lineColor, Color fillTopColor)
        {
            if (data.Count < 2) return;
            PointF[] pts = new PointF[data.Count];
            for (int i = 0; i < data.Count; i++)
            {
                float x = plot.Left + (float)i / (data.Count - 1) * plot.Width;
                float ratio = (float)Math.Min(1.0, Math.Max(0.0, data[i] / maxVal));
                float y = plot.Bottom - ratio * plot.Height;
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath areaPath = new GraphicsPath())
            {
                areaPath.AddLines(pts);
                areaPath.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                areaPath.CloseFigure();

                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new Point(plot.Left, plot.Top),
                    new Point(plot.Left, plot.Bottom),
                    fillTopColor,
                    Color.FromArgb(5, lineColor)))
                {
                    g.FillPath(grad, areaPath);
                }
            }

            using (Pen linePen = new Pen(lineColor, 2.0f))
            {
                g.DrawLines(linePen, pts);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString(kb < 100 ? "0.0" : "0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString(mb < 100 ? "0.00" : "0.0") + " MB";
            double gb = mb / 1024.0;
            if (gb < 1024) return gb.ToString("0.00") + " GB";
            double tb = gb / 1024.0;
            return tb.ToString("0.00") + " TB";
        }
    }

    public class CanvasPanel : Panel
    {
        public CanvasPanel()
        {
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }
}

