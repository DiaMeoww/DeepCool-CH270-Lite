using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Threading;

namespace CH270.ConfigUI
{
    public class MainForm : Form
    {
        // Controls
        private RadioButton rbOn;
        private RadioButton rbOff;
        private CheckBox cbAutoStart;
        private Button btnExitComplete;

        private Button btnModeCpu;
        private Button btnModeGpu;
        private Button btnModeCycle;

        private Button btnInterval3;
        private Button btnInterval5;
        private Button btnInterval7;
        private NumericUpDown numCustomInterval;

        private ComboBox cmbFan;
        private RadioButton rbUnitC;
        private RadioButton rbUnitF;

        private Label lblServiceStatus;
        private System.Windows.Forms.Timer statusTimer;

        // Current state
        private bool enableDisplay = true;
        private string currentMode = "CPU"; // "CPU", "GPU", "CYCLE"
        private int intervalSeconds = 3;
        private string fanInterface = "CPUFANIN0";
        private string tempUnit = "C";
        private bool autoStart = true;

        private readonly Color PrimaryColor = Color.FromArgb(0, 150, 136); // Teal / DeepCool Green
        private readonly Color InactiveBtnBg = Color.FromArgb(245, 246, 248);
        private readonly Color InactiveBtnBorder = Color.FromArgb(210, 214, 220);

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--install-service")
                {
                    DoInstallService();
                    return;
                }
                else if (args[0] == "--uninstall-service")
                {
                    DoUninstallService();
                    return;
                }
                else if (args[0] == "--stop-all")
                {
                    DoStopAll();
                    return;
                }
            }

            EnsureHardwareServiceReady();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        public MainForm()
        {
            InitializeComponent();
            LoadConfig();
            UpdateUIFromState();
            EnsureServiceRunning();

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1500;
            statusTimer.Tick += (s, e) => UpdateServiceStatus();
            statusTimer.Start();
        }

        private void InitializeComponent()
        {
            this.Text = "九州风神 CH270 数显控制中心";
            this.Size = new Size(680, 520);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            // Top Header Panel
            Panel topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 65;
            topPanel.BackColor = Color.FromArgb(248, 249, 251);
            topPanel.Paint += (s, e) => {
                e.Graphics.DrawLine(new Pen(Color.FromArgb(228, 231, 237), 1), 0, topPanel.Height - 1, topPanel.Width, topPanel.Height - 1);
            };
            this.Controls.Add(topPanel);

            Label lblTitle = new Label();
            lblTitle.Text = "数显系统";
            lblTitle.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            lblTitle.Location = new Point(20, 18);
            lblTitle.AutoSize = true;
            topPanel.Controls.Add(lblTitle);

            rbOff = new RadioButton();
            rbOff.Text = "OFF";
            rbOff.Location = new Point(125, 20);
            rbOff.AutoSize = true;
            rbOff.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            rbOff.CheckedChanged += (s, e) => { if (rbOff.Checked) { enableDisplay = false; SaveConfig(); } };
            topPanel.Controls.Add(rbOff);

            rbOn = new RadioButton();
            rbOn.Text = "ON";
            rbOn.Location = new Point(180, 20);
            rbOn.AutoSize = true;
            rbOn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            rbOn.Checked = true;
            rbOn.CheckedChanged += (s, e) => { if (rbOn.Checked) { enableDisplay = true; SaveConfig(); EnsureServiceRunning(); } };
            topPanel.Controls.Add(rbOn);

            cbAutoStart = new CheckBox();
            cbAutoStart.Text = "开机自启动";
            cbAutoStart.Location = new Point(340, 20);
            cbAutoStart.AutoSize = true;
            cbAutoStart.CheckedChanged += (s, e) => {
                autoStart = cbAutoStart.Checked;
                SaveConfig();
                SetAutoStart(autoStart);
            };
            topPanel.Controls.Add(cbAutoStart);

            btnExitComplete = new Button();
            btnExitComplete.Text = "完全退出";
            btnExitComplete.Size = new Size(82, 30);
            btnExitComplete.Location = new Point(475, 16);
            btnExitComplete.FlatStyle = FlatStyle.Flat;
            btnExitComplete.ForeColor = Color.FromArgb(220, 53, 69);
            btnExitComplete.BackColor = Color.White;
            btnExitComplete.FlatAppearance.BorderColor = Color.FromArgb(220, 53, 69);
            btnExitComplete.Cursor = Cursors.Hand;
            btnExitComplete.Click += (s, e) => {
                this.Hide();
                this.Opacity = 0;
                ThreadPool.QueueUserWorkItem(state => {
                    StopService();
                    Environment.Exit(0);
                });
            };
            topPanel.Controls.Add(btnExitComplete);

            Button btnUninstall = new Button();
            btnUninstall.Text = "卸载服务";
            btnUninstall.Size = new Size(82, 30);
            btnUninstall.Location = new Point(570, 16);
            btnUninstall.FlatStyle = FlatStyle.Flat;
            btnUninstall.ForeColor = Color.FromArgb(108, 117, 125);
            btnUninstall.BackColor = Color.White;
            btnUninstall.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 212);
            btnUninstall.Cursor = Cursors.Hand;
            btnUninstall.Click += (s, e) => {
                DialogResult dr = MessageBox.Show(
                    "确定要彻底卸载并清理 CH270 的后台系统服务与自启动项吗？\n\n（仅注销系统服务，不会删除本文件夹）",
                    "卸载清理确认",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--uninstall-service");
                        psi.Verb = "runas";
                        psi.UseShellExecute = true;
                        Process p = Process.Start(psi);
                        if (p != null) p.WaitForExit();
                    }
                    catch { }
                    this.Close();
                }
            };
            topPanel.Controls.Add(btnUninstall);

            // Main Content Layout
            int contentY = 80;

            // Group 1: 显示模式
            Label lblGroup1 = new Label();
            lblGroup1.Text = "显示模式选择：";
            lblGroup1.Location = new Point(25, contentY);
            lblGroup1.AutoSize = true;
            lblGroup1.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            this.Controls.Add(lblGroup1);

            contentY += 30;
            btnModeCpu = CreateModeButton("CPU 模式", 25, contentY, "CPU");
            btnModeGpu = CreateModeButton("GPU 模式", 185, contentY, "GPU");
            btnModeCycle = CreateModeButton("自动轮播 (CPU/GPU)", 345, contentY, "CYCLE");

            this.Controls.Add(btnModeCpu);
            this.Controls.Add(btnModeGpu);
            this.Controls.Add(btnModeCycle);

            contentY += 60;

            // Group 2: 轮播间隔设置
            Label lblGroup2 = new Label();
            lblGroup2.Text = "轮播间隔设置：";
            lblGroup2.Location = new Point(25, contentY);
            lblGroup2.AutoSize = true;
            lblGroup2.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            this.Controls.Add(lblGroup2);

            contentY += 30;
            btnInterval3 = CreateIntervalButton("3 秒", 25, contentY, 3);
            btnInterval5 = CreateIntervalButton("5 秒", 145, contentY, 5);
            btnInterval7 = CreateIntervalButton("7 秒", 265, contentY, 7);

            this.Controls.Add(btnInterval3);
            this.Controls.Add(btnInterval5);
            this.Controls.Add(btnInterval7);

            Label lblCustomInt = new Label();
            lblCustomInt.Text = "自定义间隔:";
            lblCustomInt.Location = new Point(400, contentY + 6);
            lblCustomInt.AutoSize = true;
            this.Controls.Add(lblCustomInt);

            numCustomInterval = new NumericUpDown();
            numCustomInterval.Location = new Point(485, contentY + 3);
            numCustomInterval.Size = new Size(60, 26);
            numCustomInterval.Minimum = 1;
            numCustomInterval.Maximum = 120;
            numCustomInterval.Value = 3;
            numCustomInterval.ValueChanged += (s, e) => {
                intervalSeconds = (int)numCustomInterval.Value;
                HighlightIntervalButton(intervalSeconds);
                SaveConfig();
            };
            this.Controls.Add(numCustomInterval);

            Label lblSec = new Label();
            lblSec.Text = "秒";
            lblSec.Location = new Point(550, contentY + 6);
            lblSec.AutoSize = true;
            this.Controls.Add(lblSec);

            contentY += 60;

            // Group 3: 硬件探针与单位
            Label lblGroup3 = new Label();
            lblGroup3.Text = "风扇探针与温度单位：";
            lblGroup3.Location = new Point(25, contentY);
            lblGroup3.AutoSize = true;
            lblGroup3.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            this.Controls.Add(lblGroup3);

            contentY += 30;
            Label lblFan = new Label();
            lblFan.Text = "CPU风扇接口:";
            lblFan.Location = new Point(25, contentY + 4);
            lblFan.AutoSize = true;
            this.Controls.Add(lblFan);

            cmbFan = new ComboBox();
            cmbFan.Location = new Point(130, contentY);
            cmbFan.Size = new Size(160, 26);
            cmbFan.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFan.Items.AddRange(new object[] { "CPUFANIN0", "SYSFANIN", "AUXFANIN3" });
            cmbFan.SelectedIndex = 0;
            cmbFan.SelectedIndexChanged += (s, e) => {
                if (cmbFan.SelectedItem != null)
                {
                    fanInterface = cmbFan.SelectedItem.ToString();
                    SaveConfig();
                }
            };
            this.Controls.Add(cmbFan);

            Label lblUnit = new Label();
            lblUnit.Text = "温度单位:";
            lblUnit.Location = new Point(340, contentY + 4);
            lblUnit.AutoSize = true;
            this.Controls.Add(lblUnit);

            rbUnitC = new RadioButton();
            rbUnitC.Text = "摄氏度 (℃)";
            rbUnitC.Location = new Point(415, contentY + 2);
            rbUnitC.AutoSize = true;
            rbUnitC.Checked = true;
            rbUnitC.CheckedChanged += (s, e) => { if (rbUnitC.Checked) { tempUnit = "C"; SaveConfig(); } };
            this.Controls.Add(rbUnitC);

            rbUnitF = new RadioButton();
            rbUnitF.Text = "华氏度 (℉)";
            rbUnitF.Location = new Point(520, contentY + 2);
            rbUnitF.AutoSize = true;
            rbUnitF.CheckedChanged += (s, e) => { if (rbUnitF.Checked) { tempUnit = "F"; SaveConfig(); } };
            this.Controls.Add(rbUnitF);

            contentY += 65;

            // Bottom Panel for Service Status & Notes
            Panel bottomPanel = new Panel();
            bottomPanel.Dock = DockStyle.Bottom;
            bottomPanel.Height = 60;
            bottomPanel.BackColor = Color.FromArgb(245, 247, 250);
            bottomPanel.Paint += (s, e) => {
                e.Graphics.DrawLine(new Pen(Color.FromArgb(228, 231, 237), 1), 0, 0, bottomPanel.Width, 0);
            };
            this.Controls.Add(bottomPanel);

            lblServiceStatus = new Label();
            lblServiceStatus.Text = "● 后台服务状态：检查中...";
            lblServiceStatus.ForeColor = Color.FromArgb(40, 167, 69);
            lblServiceStatus.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            lblServiceStatus.Location = new Point(20, 10);
            lblServiceStatus.AutoSize = true;
            bottomPanel.Controls.Add(lblServiceStatus);

            Label lblTip = new Label();
            lblTip.Text = "说明：所有配置实时生效。CPU模式自动交替轮播睿频与风扇；关闭窗口服务静默运行，不常驻托盘。";
            lblTip.ForeColor = Color.FromArgb(120, 125, 135);
            lblTip.Font = new Font("Microsoft YaHei UI", 8.5f);
            lblTip.Location = new Point(20, 32);
            lblTip.AutoSize = true;
            bottomPanel.Controls.Add(lblTip);
        }

        private Button CreateModeButton(string text, int x, int y, string modeVal)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(145, 38);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Microsoft YaHei UI", 9.5f);
            btn.Click += (s, e) => {
                currentMode = modeVal;
                HighlightModeButtons();
                SaveConfig();
            };
            return btn;
        }

        private Button CreateIntervalButton(string text, int x, int y, int secVal)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(100, 34);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Microsoft YaHei UI", 9.5f);
            btn.Click += (s, e) => {
                intervalSeconds = secVal;
                numCustomInterval.Value = secVal;
                HighlightIntervalButton(secVal);
                SaveConfig();
            };
            return btn;
        }

        private void HighlightModeButtons()
        {
            SetBtnActive(btnModeCpu, currentMode == "CPU");
            SetBtnActive(btnModeGpu, currentMode == "GPU");
            SetBtnActive(btnModeCycle, currentMode == "CYCLE");
        }

        private void HighlightIntervalButton(int sec)
        {
            SetBtnActive(btnInterval3, sec == 3);
            SetBtnActive(btnInterval5, sec == 5);
            SetBtnActive(btnInterval7, sec == 7);
        }

        private void SetBtnActive(Button btn, bool active)
        {
            if (active)
            {
                btn.BackColor = PrimaryColor;
                btn.ForeColor = Color.White;
                btn.FlatAppearance.BorderColor = PrimaryColor;
                btn.Font = new Font(btn.Font, FontStyle.Bold);
            }
            else
            {
                btn.BackColor = InactiveBtnBg;
                btn.ForeColor = Color.FromArgb(30, 35, 45);
                btn.FlatAppearance.BorderColor = InactiveBtnBorder;
                btn.Font = new Font(btn.Font, FontStyle.Regular);
            }
        }

        private void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath, Encoding.UTF8);
                    enableDisplay = GetJsonBool(json, "enable_display", true);
                    currentMode = GetJsonString(json, "mode", "CPU");
                    intervalSeconds = GetJsonInt(json, "interval_seconds", 3);
                    fanInterface = GetJsonString(json, "fan_interface", "CPUFANIN0");
                    tempUnit = GetJsonString(json, "temp_unit", "C");
                    autoStart = GetJsonBool(json, "auto_start", true);
                }
            }
            catch { }
        }

        private void UpdateUIFromState()
        {
            if (enableDisplay) rbOn.Checked = true; else rbOff.Checked = true;
            cbAutoStart.Checked = autoStart;
            HighlightModeButtons();
            HighlightIntervalButton(intervalSeconds);
            if (numCustomInterval.Minimum <= intervalSeconds && intervalSeconds <= numCustomInterval.Maximum)
                numCustomInterval.Value = intervalSeconds;

            if (cmbFan.Items.Contains(fanInterface))
                cmbFan.SelectedItem = fanInterface;

            if (tempUnit.ToUpper() == "F") rbUnitF.Checked = true; else rbUnitC.Checked = true;
        }

        private void SaveConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine(string.Format("  \"enable_display\": {0},", enableDisplay ? "true" : "false"));
                sb.AppendLine(string.Format("  \"mode\": \"{0}\",", currentMode));
                sb.AppendLine(string.Format("  \"interval_seconds\": {0},", intervalSeconds));
                sb.AppendLine(string.Format("  \"fan_interface\": \"{0}\",", fanInterface));
                sb.AppendLine(string.Format("  \"temp_unit\": \"{0}\",", tempUnit));
                sb.AppendLine(string.Format("  \"auto_start\": {0}", autoStart ? "true" : "false"));
                sb.AppendLine("}");
                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void UpdateServiceStatus()
        {
            bool displayRunning = Process.GetProcessesByName("CH270.Service").Length > 0;
            bool sensorRunning = CheckServiceRunning("CH270_SensorService") || CheckServiceRunning("Deep Cool Helper Service");

            if (displayRunning && sensorRunning)
            {
                lblServiceStatus.Text = "● 系统运行状态：正常运行 (硬件探针 + 数显推送稳定连接)";
                lblServiceStatus.ForeColor = Color.FromArgb(40, 167, 69);
            }
            else if (displayRunning)
            {
                lblServiceStatus.Text = "▲ 系统运行状态：数显推送运行中，等待硬件探针启动...";
                lblServiceStatus.ForeColor = Color.FromArgb(255, 152, 0);
            }
            else
            {
                lblServiceStatus.Text = "○ 系统运行状态：后台服务已停止";
                lblServiceStatus.ForeColor = Color.FromArgb(108, 117, 125);
            }
        }

        private void EnsureServiceRunning()
        {
            if (!enableDisplay) return;
            if (Process.GetProcessesByName("CH270.Service").Length == 0)
            {
                string srvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CH270.Service.exe");
                if (File.Exists(srvPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(srvPath);
                    psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                }
            }
        }

        private void StopService()
        {
            try
            {
                if (statusTimer != null) statusTimer.Stop();

                // 1. Immediately terminate display pusher service
                foreach (var p in Process.GetProcessesByName("CH270.Service"))
                {
                    try { p.Kill(); } catch { }
                }

                // 2. Instruct sensor service to stop
                RunCmd("sc.exe", "stop CH270_SensorService");

                // 3. Wait briefly for SensorBridgeServer to exit (up to 800ms)
                for (int i = 0; i < 8; i++)
                {
                    if (Process.GetProcessesByName("SensorBridgeServer").Length == 0) break;
                    Thread.Sleep(100);
                }
            }
            catch { }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                // Disable official DeepCool startup entry
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (key.GetValue("DeepCool") != null)
                        {
                            key.DeleteValue("DeepCool", false);
                        }

                        string srvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CH270.Service.exe");
                        if (enable)
                        {
                            key.SetValue("CH270_Service", srvPath);
                        }
                        else
                        {
                            if (key.GetValue("CH270_Service") != null)
                            {
                                key.DeleteValue("CH270_Service", false);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        static string GetJsonString(string json, string key, string def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                int firstQuote = json.IndexOf("\"", colon + 1);
                if (firstQuote == -1) return def;

                int secondQuote = json.IndexOf("\"", firstQuote + 1);
                if (secondQuote == -1) return def;

                return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
            catch { return def; }
        }

        static int GetJsonInt(string json, string key, int def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                int start = colon + 1;
                while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
                int end = start;
                while (end < json.Length && char.IsDigit(json[end])) end++;
                if (end > start)
                {
                    int val;
                    if (int.TryParse(json.Substring(start, end - start), out val)) return val;
                }
            }
            catch { }
            return def;
        }

        static bool GetJsonBool(string json, string key, bool def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                string sub = json.Substring(colon + 1).Trim().ToLower();
                if (sub.StartsWith("true")) return true;
                if (sub.StartsWith("false")) return false;
            }
            catch { }
            return def;
        }

        static void EnsureHardwareServiceReady()
        {
            string expectedBin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SensorDriver", "SensorBridgeServer.exe");
            bool ch270Installed = CheckServiceExists("CH270_SensorService");
            string currentBin = ch270Installed ? GetServiceBinPath("CH270_SensorService") : string.Empty;
            bool pathMatches = ch270Installed && string.Equals(currentBin, expectedBin, StringComparison.OrdinalIgnoreCase);
            bool canStop = ch270Installed && CheckServiceCanBeStopped("CH270_SensorService");

            if (!ch270Installed || !pathMatches || !canStop)
            {
                string promptMsg = !ch270Installed
                    ? "欢迎使用 九州风神 CH270 数显独立控制系统！\n\n检测到首次运行，需要为您一键注册并启动底层硬件传感器系统服务。\n点击【确定】将自动完成配置并点亮屏幕！"
                    : "检测到底层硬件服务需要配置完全退出权限（释放文件夹锁定）与路径同步。\n\n点击【确定】将自动更新服务路径与免提权退出权限，支持自由重命名文件夹！";

                DialogResult dr = MessageBox.Show(
                    promptMsg,
                    "九州风神 CH270 初始化配置",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information);

                if (dr == DialogResult.OK)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--install-service");
                        psi.Verb = "runas";
                        psi.UseShellExecute = true;
                        Process p = Process.Start(psi);
                        if (p != null) p.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("未能获取管理员权限：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            else
            {
                bool ch270Running = CheckServiceRunning("CH270_SensorService");
                bool deepCoolRunning = CheckServiceRunning("Deep Cool Helper Service");
                if (!ch270Running && !deepCoolRunning)
                {
                    RunCmd("sc.exe", "start CH270_SensorService");
                }
            }
        }

        static void DoInstallService()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sensorExe = Path.Combine(baseDir, "SensorDriver", "SensorBridgeServer.exe");
            string serviceExe = Path.Combine(baseDir, "CH270.Service.exe");

            KillProcess("CH270.Service");

            RunCmd("sc.exe", "stop \"Deep Cool Helper Service\"");
            RunCmd("sc.exe", "config \"Deep Cool Helper Service\" start= disabled");
            RunCmd("sc.exe", "stop \"Deep Cool Display Service\"");
            RunCmd("sc.exe", "config \"Deep Cool Display Service\" start= disabled");

            RunCmd("sc.exe", "stop CH270_SensorService");
            RunCmd("taskkill.exe", "/F /IM SensorBridgeServer.exe");
            RunCmd("sc.exe", "delete CH270_SensorService");
            Thread.Sleep(500);

            RunCmd("sc.exe", string.Format("create CH270_SensorService binPath= \"{0}\" start= auto DisplayName= \"CH270 硬件传感器独立服务\"", sensorExe));
            RunCmd("sc.exe", "description CH270_SensorService \"九州风神 CH270 机箱数显硬件传感器数据桥接服务 (完全独立运行)\"");

            // Ensure NO failure restart actions so SCM never revives the process when stopped
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CH270_SensorService", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("FailureActions", false);
                        key.DeleteValue("FailureActionsFlag", false);
                    }
                }
            }
            catch { }

            // Grant standard user Start and Stop rights
            RunCmd("sc.exe", "sdset CH270_SensorService \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWRPWPLOCRRC;;;AU)\"");

            RunCmd("sc.exe", "start CH270_SensorService");
            Thread.Sleep(1000);

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue("CH270_Service", serviceExe);
                        if (key.GetValue("DeepCool") != null) key.DeleteValue("DeepCool", false);
                    }
                }
            }
            catch { }

            if (File.Exists(serviceExe))
            {
                ProcessStartInfo psi = new ProcessStartInfo(serviceExe);
                psi.WorkingDirectory = baseDir;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
        }

        static void DoUninstallService()
        {
            KillProcess("CH270.Service");

            RunCmd("sc.exe", "stop CH270_SensorService");
            RunCmd("taskkill.exe", "/F /IM SensorBridgeServer.exe");
            RunCmd("sc.exe", "delete CH270_SensorService");

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null && key.GetValue("CH270_Service") != null)
                    {
                        key.DeleteValue("CH270_Service", false);
                    }
                }
            }
            catch { }

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, "CH270 数显控制中心.lnk");
                if (File.Exists(lnkPath)) File.Delete(lnkPath);
            }
            catch { }
        }

        static void DoStopAll()
        {
            KillProcess("CH270.Service");
            RunCmd("sc.exe", "stop CH270_SensorService");
            RunCmd("taskkill.exe", "/F /IM SensorBridgeServer.exe");
        }

        static string GetServiceBinPath(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "qc " + name);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    string marker = "BINARY_PATH_NAME";
                    int idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx != -1)
                    {
                        int colon = output.IndexOf(":", idx);
                        if (colon != -1)
                        {
                            int end = output.IndexOf("\r\n", colon);
                            string val = (end != -1) ? output.Substring(colon + 1, end - colon - 1) : output.Substring(colon + 1);
                            return val.Trim().Trim('\"');
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        static bool CheckServiceCanBeStopped(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "sdshow " + name);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return output.Contains("(A;;CCLCSWRPWPLOCRRC;;;IU)") || (output.Contains("WP") && output.Contains("IU"));
                }
            }
            catch { return false; }
        }

        static bool CheckServiceExists(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "query " + name);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return !output.Contains("1060");
                }
            }
            catch { return false; }
        }

        static bool CheckServiceRunning(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "query " + name);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return output.Contains("RUNNING");
                }
            }
            catch { return false; }
        }

        static void RunCmd(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(6000);
                }
            }
            catch { }
        }

        static void KillProcess(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); } catch { }
            }
        }
    }
}
