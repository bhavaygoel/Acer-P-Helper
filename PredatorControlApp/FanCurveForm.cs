using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    public class FanCurveApplyEventArgs : EventArgs
    {
        public List<Point> CpuPoints { get; set; } = new();
        public List<Point> GpuPoints { get; set; } = new();
        public List<Point> PerfCpuPoints { get; set; } = new();
        public List<Point> PerfGpuPoints { get; set; } = new();
        public bool Success { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public class FanCurveForm : Form
    {
        #region Theme Colors & Fonts

        private static readonly Color FormBg = Color.FromArgb(22, 22, 26);
        private static readonly Color TitleBarBg = Color.FromArgb(18, 18, 21);
        private static readonly Color SeparatorColor = Color.FromArgb(40, 40, 44);
        private static readonly Color TitleTextColor = Color.FromArgb(200, 200, 205);
        private static readonly Color CloseHoverColor = Color.FromArgb(220, 50, 50);

        private static readonly Color CpuCurveColor = Color.FromArgb(0, 180, 255);
        private static readonly Color GpuCurveColor = Color.FromArgb(255, 77, 109);

        private static readonly Color FlashGreen = Color.FromArgb(0, 180, 80);
        private static readonly Color FlashRed = Color.FromArgb(220, 50, 50);

        private static readonly Font FontTitle = new("Segoe UI", 10f, FontStyle.Bold);

        #endregion

        #region Fields

        private FanCurveGraph _graphCpu = null!;
        private FanCurveGraph _graphGpu = null!;
        private PredatorButton _btnReset = null!;
        private PredatorButton _btnEveryday = null!;
        private PredatorButton _btnPerformance = null!;

        // Two curve sets: Everyday (Quiet/Balanced/Eco) and Performance (Perf mode).
        // The graphs show whichever set is selected; the other set is kept here.
        private readonly List<Point>[] _cpuSets = { new(), new() };
        private readonly List<Point>[] _gpuSets = { new(), new() };
        private int _set;
        private const int ProfileRowHeight = 48;
        private PredatorButton _btnQuiet = null!;
        private PredatorButton _btnGaming = null!;
        private PredatorButton _btnApply = null!;
        private System.Windows.Forms.Timer _flashTimer = null!;

        private const int SidePad = 16;
        private const int GraphHeight = 220;
        private const int TitleBarHeight = 36;
        private const int SepHeight = 1;
        private const int ButtonRowHeight = 50;

        #endregion

        #region Presets

        // Tuned on a Helios 300 (PH315-53). The GPU ramps earlier than the CPU because
        // the RTX 3060 starts shedding clocks around 87°C.
        internal static readonly List<Point> QuietCpu = new() { new(30,10), new(50,15), new(60,25), new(70,40), new(78,55), new(85,75), new(92,95), new(100,100) };
        internal static readonly List<Point> QuietGpu = new() { new(30,10), new(50,15), new(60,30), new(68,45), new(74,60), new(80,80), new(85,100), new(100,100) };
        internal static readonly List<Point> GamingCpu = new() { new(30,20), new(50,30), new(60,40), new(68,55), new(75,70), new(82,85), new(88,100), new(100,100) };
        internal static readonly List<Point> GamingGpu = new() { new(30,20), new(50,30), new(58,45), new(65,60), new(70,75), new(76,90), new(80,100), new(100,100) };

        #endregion

        #region Events

        public event EventHandler<FanCurveApplyEventArgs>? ApplyClicked;

        #endregion

        #region Window Dragging

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        #endregion

        #region Constructor

        public FanCurveForm()
        {
            Text = "Fan Curve Editor";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = FormBg;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int totalHeight = TitleBarHeight + SepHeight
                            + ProfileRowHeight + SepHeight
                            + GraphHeight + SepHeight
                            + GraphHeight + SepHeight
                            + ButtonRowHeight;

            int totalWidth = 460;
            ClientSize = new Size(totalWidth, totalHeight);
            
            _flashTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _flashTimer.Tick += FlashTimer_Tick;

            BuildUI();
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            int y = 0;

            var pnlTitle = new Panel
            {
                Height = TitleBarHeight,
                Dock = DockStyle.Top,
                BackColor = TitleBarBg
            };
            pnlTitle.MouseDown += TitleBar_MouseDown;
            Controls.Add(pnlTitle);

            var lblTitle = new Label
            {
                Text = "Fan Curve Editor",
                Font = FontTitle,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(SidePad, (TitleBarHeight - FontTitle.Height) / 2),
                BackColor = Color.Transparent
            };
            lblTitle.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(lblTitle);

            var lblClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(140, 140, 145),
                AutoSize = true,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            lblClose.Location = new Point(
                ClientSize.Width - SidePad - lblClose.PreferredWidth - 2,
                (TitleBarHeight - lblClose.PreferredHeight) / 2);
            lblClose.Click += (s, e) => Close();
            lblClose.MouseEnter += (s, e) => lblClose.ForeColor = CloseHoverColor;
            lblClose.MouseLeave += (s, e) => lblClose.ForeColor = Color.FromArgb(140, 140, 145);
            pnlTitle.Controls.Add(lblClose);

            y = TitleBarHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            int tabW = (ClientSize.Width - SidePad * 2 - 12) / 2;
            int tabH = 32;
            int tabY = y + (ProfileRowHeight - tabH) / 2;
            _btnEveryday = new PredatorButton
            {
                Text = "Everyday (Quiet / Balanced)",
                Size = new Size(tabW, tabH),
                Location = new Point(SidePad, tabY)
            };
            _btnEveryday.Click += (s, e) => SelectSet(0);
            Controls.Add(_btnEveryday);

            _btnPerformance = new PredatorButton
            {
                Text = "Performance (Perf)",
                Size = new Size(tabW, tabH),
                Location = new Point(_btnEveryday.Right + 12, tabY)
            };
            _btnPerformance.Click += (s, e) => SelectSet(1);
            Controls.Add(_btnPerformance);
            y += ProfileRowHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            _graphCpu = new FanCurveGraph
            {
                CurveColor = CpuCurveColor,
                FanLabel = "CPU FAN CURVE",
                Location = new Point(SidePad, y),
                Size = new Size(ClientSize.Width - SidePad * 2, GraphHeight)
            };
            Controls.Add(_graphCpu);
            y += GraphHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            _graphGpu = new FanCurveGraph
            {
                CurveColor = GpuCurveColor,
                FanLabel = "GPU FAN CURVE",
                Location = new Point(SidePad, y),
                Size = new Size(ClientSize.Width - SidePad * 2, GraphHeight)
            };
            Controls.Add(_graphGpu);
            y += GraphHeight;

            Controls.Add(MakeSeparator(y));
            y += SepHeight;

            int btnWidth = 96;
            int btnHeight = 34;
            int btnY = y + (ButtonRowHeight - btnHeight) / 2;

            _btnReset = new PredatorButton
            {
                Text = "Reset",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(SidePad, btnY)
            };
            _btnReset.Click += BtnReset_Click;
            Controls.Add(_btnReset);

            int presetGap = 12;
            _btnQuiet = new PredatorButton
            {
                Text = "Quiet",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(_btnReset.Right + presetGap, btnY)
            };
            _btnQuiet.Click += (s, e) => LoadPreset(QuietCpu, QuietGpu);
            Controls.Add(_btnQuiet);

            _btnGaming = new PredatorButton
            {
                Text = "Gaming",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(_btnQuiet.Right + presetGap, btnY)
            };
            _btnGaming.Click += (s, e) => LoadPreset(GamingCpu, GamingGpu);
            Controls.Add(_btnGaming);

            _btnApply = new PredatorButton
            {
                Text = "Apply",
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(ClientSize.Width - SidePad - btnWidth, btnY)
            };
            _btnApply.Click += BtnApply_Click;
            Controls.Add(_btnApply);
        }

        private Panel MakeSeparator(int yPos)
        {
            return new Panel
            {
                Location = new Point(0, yPos),
                Size = new Size(ClientSize.Width, SepHeight),
                BackColor = SeparatorColor
            };
        }

        #endregion

        #region Button Handlers

        private void BtnReset_Click(object? sender, EventArgs e)
        {
            if (_set == 1)
                LoadPreset(GamingCpu, GamingGpu);
            else
            {
                _graphCpu.Points = _graphCpu.DefaultPoints;
                _graphGpu.Points = _graphGpu.DefaultPoints;
            }
        }

        private void SelectSet(int set)
        {
            _cpuSets[_set] = new List<Point>(_graphCpu.Points);
            _gpuSets[_set] = new List<Point>(_graphGpu.Points);
            _set = set;
            _graphCpu.Points = new List<Point>(_cpuSets[set]);
            _graphGpu.Points = new List<Point>(_gpuSets[set]);
            _btnEveryday.IsActive = set == 0;
            _btnPerformance.IsActive = set == 1;
        }

        // Loads the preset into the editor; it takes effect once the user presses Apply
        private void LoadPreset(List<Point> cpu, List<Point> gpu)
        {
            _graphCpu.Points = new List<Point>(cpu);
            _graphGpu.Points = new List<Point>(gpu);
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            _cpuSets[_set] = new List<Point>(_graphCpu.Points);
            _gpuSets[_set] = new List<Point>(_graphGpu.Points);
            var args = new FanCurveApplyEventArgs
            {
                CpuPoints = new List<Point>(_cpuSets[0]),
                GpuPoints = new List<Point>(_gpuSets[0]),
                PerfCpuPoints = new List<Point>(_cpuSets[1]),
                PerfGpuPoints = new List<Point>(_gpuSets[1]),
                Success = false
            };

            ApplyClicked?.Invoke(this, args);

            Color flashColor = args.Success ? FlashGreen : FlashRed;
            _btnApply.CustomActiveColor = flashColor;
            _btnApply.IsActive = true;

            _flashTimer.Stop();
            _flashTimer.Start();
        }

        private void FlashTimer_Tick(object? sender, EventArgs e)
        {
            _flashTimer.Stop();
            _btnApply.IsActive = false;
            _btnApply.CustomActiveColor = null;
        }

        #endregion

        #region Public Methods

        public void UpdateTemps(int cpuTemp, int gpuTemp)
        {
            _graphCpu.CurrentTemp = cpuTemp;
            _graphGpu.CurrentTemp = gpuTemp;
        }

        public void SetCpuCurve(List<Point> points)
        {
            _graphCpu.Points = new List<Point>(points);
        }

        public void SetGpuCurve(List<Point> points)
        {
            _graphGpu.Points = new List<Point>(points);
        }

        /// <summary>Loads both curve sets and shows the one in use right now.</summary>
        public void SetCurves(List<Point> cpu, List<Point> gpu, List<Point> perfCpu, List<Point> perfGpu, bool showPerformance)
        {
            _cpuSets[0] = new List<Point>(cpu);
            _gpuSets[0] = new List<Point>(gpu);
            _cpuSets[1] = new List<Point>(perfCpu);
            _gpuSets[1] = new List<Point>(perfGpu);
            _set = showPerformance ? 1 : 0;
            _graphCpu.Points = new List<Point>(_cpuSets[_set]);
            _graphGpu.Points = new List<Point>(_gpuSets[_set]);
            _btnEveryday.IsActive = _set == 0;
            _btnPerformance.IsActive = _set == 1;
        }

        public List<Point> GetCpuCurve() => new(_graphCpu.Points);

        public List<Point> GetGpuCurve() => new(_graphGpu.Points);

        public int InterpolateCpuSpeed(int temp) => _graphCpu.InterpolateSpeed(temp);

        public int InterpolateGpuSpeed(int temp) => _graphGpu.InterpolateSpeed(temp);

        #endregion

        #region Cleanup

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flashTimer?.Stop();
                _flashTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
