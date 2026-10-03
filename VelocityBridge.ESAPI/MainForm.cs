using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPIApp = VMS.TPS.Common.Model.API.Application;
using Dicom;
using Dicom.Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoFLC
{
    public class MainForm : Form
    {
        private ESAPIApp _esapiApp;
        private Patient _openPatient;

        // ESAPI enforces thread affinity on every object it hands out, so
        // CreateApplication() and all subsequent ESAPI calls must run on the UI
        // thread. To keep a stuck connect (e.g. when the Aria DB service is not
        // reachable on a development machine) from freezing the process, a
        // separate watchdog thread offers a force-quit dialog after
        // ConnectWatchdogTimeoutMs.
        private const int ConnectWatchdogTimeoutMs = 30000;
        private volatile bool _connectCallReturned;

        private TabControl tabControl;
        private TextBox txtPatientId;
        private Button btnSearchPatient;
        private Button btnConnect;
        private TextBox txtInfoBar;
        private DataGridView dgvImages;
        private TextBox txtSharedFolder;
        private Button btnBrowseFolder;
        private Button btnExport4DCT;
        private TextBox txtBdfPath;
        private Button btnBrowseBdf;
        private CheckBox chkUseStep1Ref;
        private TextBox txtReferenceDir;
        private Button btnBrowseReferenceDir;
        private Label lblRefStatus;
        private Button btnProcessBdf;
        private Button btnImportRtstruct;
        private TextBox txtLog;
        private Label lblDetectedPhases;
        private ProgressBar progressBar;
        private NumericUpDown numSmoothSigma;
        private NumericUpDown numPercentile;

        private CheckBox chkExcludeGtv;
        private CheckBox chkOutputRtstruct;
        private CheckBox chkOutputJacobian;
        private CheckBox chkOutputVentilation;

        // DIR QA Results panel (Step 2): Jacobian determinant QA
        private GroupBox grpDirQa;
        private Label lblDirQaWarning;
        private DataGridView dgvDirQa;

        private BackgroundWorker _worker;

        private System.Windows.Forms.Timer _settingsDebounceTimer;

        private List<ImageInfo> _images = new List<ImageInfo>();
        private string _lastRtstructPath;
        // All RTSTRUCTs of the last run (Both mode produces Jacobian + HU).
        private List<string> _lastRtstructPaths = new List<string>();
        private DaemonExportArgs _lastDaemonExportArgs;
        private UserSettings _userSettings;
        private bool _loadingSettings;

        // DICOM Daemon settings
        private TextBox txtDaemonAe;
        private TextBox txtDaemonIp;
        private NumericUpDown numDaemonPort;
        private TextBox txtLocalAe;
        private NumericUpDown numLocalPort;
        private Button btnTestDaemon;
        private CheckBox chkAutoImport;

        // Dual-engine controls (design V2)
        private RadioButton radioEngineVelocity;
        private RadioButton radioEngineEclipse;
        private Button btnPrepareExpRef;
        private Label lblExpRefStatus;
        private RadioButton radioMetricJac;
        private RadioButton radioMetricHu;
        private RadioButton radioMetricBoth;
        private NumericUpDown numCtSigma;

        // Plastimatch engine branch (third engine, local DIR)
        private RadioButton radioEnginePlastimatch;
        private Button btnRunPlasti;
        private Label lblPlastiStatus;
        private Label lblPlastiLambda;
        private NumericUpDown numPlastiLambda;
        private Label lblPlastiPreset;
        private ComboBox cboPlastiPreset;
        private Label lblPlastiExe;
        private TextBox txtPlastiExe;
        private Button btnBrowsePlastiExe;
        private string _lastPlastiVfPath;

        private const int DefaultTimeoutMs = 600000; // 10 minutes
        private const string NLq = "\n";

        // UI theme inspired by Lattice_Evaluation: clean clinical white/gray with blue accent
        private static readonly Color RefBackground = Color.White;
        private static readonly Color RefText = Color.FromArgb(51, 51, 51);          // #333333
        private static readonly Color RefControlBack = Color.FromArgb(240, 240, 240); // #F0F0F0
        private static readonly Color RefBorder = Color.FromArgb(180, 180, 180);      // #B4B4B4
        private static readonly Color RefAccent = Color.FromArgb(0, 120, 215);        // #0078D7
        private static readonly Color RefAccentHover = Color.FromArgb(0, 90, 160);
        private static readonly Color RefAccentLight = Color.FromArgb(230, 243, 255);
        private static readonly Color RefError = Color.FromArgb(220, 20, 60);
        private static readonly Color RefSuccess = Color.FromArgb(0, 128, 0);
        private static readonly Color RefAltRow = Color.FromArgb(245, 245, 245);

        private static readonly Font RefFont = new Font("Segoe UI", 9F);
        private static readonly Font RefFontBold = new Font("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font RefHeaderFont = new Font("Segoe UI", 10F, FontStyle.Bold);

        public MainForm()
        {
            Text = "AutoFLC";
            Width = 1280;
            Height = 960;
            StartPosition = FormStartPosition.CenterScreen;
            SuspendLayout();
            InitializeComponents();
            InitializeBackgroundWorker();
            LoadUserSettings();
            ApplyStyles();
            ResumeLayout(false);
            PerformLayout();
            ActiveControl = tabControl;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Debounce rapid TextChanged events into a single settings save.
            _settingsDebounceTimer = new System.Windows.Forms.Timer();
            _settingsDebounceTimer.Interval = 300;
            _settingsDebounceTimer.Tick += (s, args) =>
            {
                _settingsDebounceTimer.Stop();
                SaveDaemonSettings();
            };

            // Refresh the reference status asynchronously so the constructor
            // does not block on disk access.
            BeginInvoke((Action)(() =>
            {
                if (!_loadingSettings)
                    UpdateReferenceStatus();
            }));
        }

        private void LoadUserSettings()
        {
            _loadingSettings = true;
            try
            {
                _userSettings = SettingsManager.Load();
                if (!string.IsNullOrEmpty(_userSettings.LastOutputDirectory) && Directory.Exists(_userSettings.LastOutputDirectory))
                    txtSharedFolder.Text = _userSettings.LastOutputDirectory;
                if (!string.IsNullOrEmpty(_userSettings.LastBdfPath) && File.Exists(_userSettings.LastBdfPath))
                    txtBdfPath.Text = _userSettings.LastBdfPath;

                LoadDaemonSettings();
                LoadReferenceSettings();
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        private void LoadReferenceSettings()
        {
            if (_userSettings == null || chkUseStep1Ref == null) return;
            chkUseStep1Ref.Checked = _userSettings.UseStep1Reference;
            if (!string.IsNullOrEmpty(_userSettings.ReferenceDirectory) && Directory.Exists(_userSettings.ReferenceDirectory))
                txtReferenceDir.Text = _userSettings.ReferenceDirectory;
            ChkUseStep1Ref_CheckedChanged(null, EventArgs.Empty);
        }

        private void LoadDaemonSettings()
        {
            if (_userSettings == null) return;
            txtDaemonAe.Text = _userSettings.DaemonAETitle ?? string.Empty;
            txtDaemonIp.Text = _userSettings.DaemonIP ?? string.Empty;
            numDaemonPort.Value = _userSettings.DaemonPort > 0 ? _userSettings.DaemonPort : 104;
            txtLocalAe.Text = _userSettings.LocalAETitle ?? string.Empty;
            numLocalPort.Value = _userSettings.LocalPort > 0 ? _userSettings.LocalPort : 1040;
            chkAutoImport.Checked = _userSettings.AutoImportRtstruct;

            // Plastimatch engine settings (defaults = validated baseline).
            string exe = PlastimatchRunner.FindDefaultExecutable(_userSettings.PlastimatchExePath);
            txtPlastiExe.Text = exe ?? string.Empty;
            int preset = _userSettings.PlastiPresetIndex;
            cboPlastiPreset.SelectedIndex = preset >= 0 && preset < cboPlastiPreset.Items.Count ? preset : 0;
            double lambda = _userSettings.PlastiLambda > 0 ? _userSettings.PlastiLambda : 1.0;
            numPlastiLambda.Value = (decimal)Math.Min(lambda, (double)numPlastiLambda.Maximum);
        }

        private void SaveDaemonSettings()
        {
            if (_userSettings == null || _loadingSettings) return;
            _userSettings.DaemonAETitle = txtDaemonAe.Text.Trim();
            _userSettings.DaemonIP = txtDaemonIp.Text.Trim();
            _userSettings.DaemonPort = (int)numDaemonPort.Value;
            _userSettings.LocalAETitle = txtLocalAe.Text.Trim();
            _userSettings.LocalPort = (int)numLocalPort.Value;
            _userSettings.AutoImportRtstruct = chkAutoImport.Checked;
            _userSettings.UseStep1Reference = chkUseStep1Ref.Checked;
            if (!chkUseStep1Ref.Checked)
                _userSettings.ReferenceDirectory = txtReferenceDir.Text.Trim();

            _userSettings.PlastimatchExePath = txtPlastiExe.Text.Trim();
            _userSettings.PlastiPresetIndex = cboPlastiPreset.SelectedIndex >= 0 ? cboPlastiPreset.SelectedIndex : 0;
            _userSettings.PlastiLambda = (double)numPlastiLambda.Value;
            SettingsManager.Save(_userSettings);
        }

        private void InitializeComponents()
        {
            // Top info bar (like Lattice_Evaluation patientInfo_tb)
            txtInfoBar = new TextBox();
            txtInfoBar.Dock = DockStyle.Top;
            txtInfoBar.Height = 36;
            txtInfoBar.ReadOnly = true;
            txtInfoBar.BorderStyle = BorderStyle.None;
            txtInfoBar.BackColor = RefControlBack;
            txtInfoBar.ForeColor = RefText;
            txtInfoBar.Font = RefFontBold;
            txtInfoBar.Text = "  AutoFLC | Disconnected";
            txtInfoBar.TabStop = false;
            Controls.Add(txtInfoBar);

            // Activity log panel
            GroupBox grpLog = new GroupBox();
            grpLog.Dock = DockStyle.Bottom;
            grpLog.Height = 150;
            grpLog.Text = "Activity Log";
            Controls.Add(grpLog);

            // Main tab control (anchored between info bar and log panel)
            tabControl = new TabControl();
            tabControl.Appearance = TabAppearance.FlatButtons;
            tabControl.Font = RefFontBold;
            tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tabControl.SetBounds(0, txtInfoBar.Height, ClientSize.Width, ClientSize.Height - txtInfoBar.Height - grpLog.Height);
            Controls.Add(tabControl);

            TabPage tabExport = new TabPage("Step 1: Data preparation");
            tabControl.TabPages.Add(tabExport);
            InitializeExportTab(tabExport);

            TabPage tabBdf = new TabPage("Step 2: AutoFLC & Import");
            tabControl.TabPages.Add(tabBdf);
            InitializeBdfTab(tabBdf);

            txtSharedFolder.TextChanged += TxtSharedFolder_TextChanged;
            txtBdfPath.TextChanged += TxtBdfPath_TextChanged;

            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.BackColor = RefBackground;
            txtLog.ForeColor = RefText;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            grpLog.Controls.Add(txtLog);
        }

        private void InitializeExportTab(TabPage tab)
        {
            tab.Padding = new Padding(15);
            tab.BackColor = RefBackground;
            tab.SuspendLayout();

            int cw = 1230;
            int left = 15;

            // --- Eclipse Connection ---
            GroupBox grpConn = new GroupBox();
            grpConn.Text = "Eclipse Connection";
            grpConn.SetBounds(left, 10, cw, 65);
            tab.Controls.Add(grpConn);

            Label lblPid = new Label();
            lblPid.Text = "Patient ID:";
            lblPid.SetBounds(12, 24, 65, 18);
            grpConn.Controls.Add(lblPid);

            txtPatientId = new TextBox();
            txtPatientId.SetBounds(82, 22, 150, 22);
            grpConn.Controls.Add(txtPatientId);

            btnConnect = new Button();
            btnConnect.Text = "Connect Eclipse";
            btnConnect.SetBounds(240, 20, 115, 28);
            btnConnect.Tag = "primary";
            btnConnect.Click += BtnConnect_Click;
            grpConn.Controls.Add(btnConnect);

            btnSearchPatient = new Button();
            btnSearchPatient.Text = "Search Patient";
            btnSearchPatient.SetBounds(362, 20, 100, 28);
            btnSearchPatient.Enabled = false;
            btnSearchPatient.Click += BtnSearchPatient_Click;
            grpConn.Controls.Add(btnSearchPatient);

            // --- 4DCT Image List ---
            GroupBox grpImages = new GroupBox();
            grpImages.Text = "4DCT Image List - phase auto-detection";
            grpImages.SetBounds(left, 85, cw, 220);
            tab.Controls.Add(grpImages);

            dgvImages = new DataGridView();
            dgvImages.SetBounds(12, 22, cw - 24, 191);
            dgvImages.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            dgvImages.AllowUserToAddRows = false;
            dgvImages.ReadOnly = false;
            dgvImages.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvImages.MultiSelect = false;
            dgvImages.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grpImages.Controls.Add(dgvImages);

            lblDetectedPhases = new Label();
            lblDetectedPhases.Text = "Detected phases: None";
            lblDetectedPhases.SetBounds(left, 310, 400, 18);
            lblDetectedPhases.ForeColor = RefText;
            tab.Controls.Add(lblDetectedPhases);

            // --- Output Folder ---
            GroupBox grpFolder = new GroupBox();
            grpFolder.Text = "Output Folder";
            grpFolder.SetBounds(left, 335, cw, 65);
            tab.Controls.Add(grpFolder);

            Label lblFolder = new Label();
            lblFolder.Text = "Folder:";
            lblFolder.SetBounds(12, 24, 42, 18);
            grpFolder.Controls.Add(lblFolder);

            txtSharedFolder = new TextBox();
            txtSharedFolder.SetBounds(58, 22, cw - 150, 22);
            txtSharedFolder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            grpFolder.Controls.Add(txtSharedFolder);

            btnBrowseFolder = new Button();
            btnBrowseFolder.Text = "Browse...";
            btnBrowseFolder.SetBounds(cw - 85, 20, 70, 28);
            btnBrowseFolder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowseFolder.Click += BtnBrowseFolder_Click;
            grpFolder.Controls.Add(btnBrowseFolder);

            // --- DICOM DB Daemon Settings ---
            GroupBox grpDaemon = new GroupBox();
            grpDaemon.Text = "DICOM DB Daemon Settings";
            grpDaemon.SetBounds(left, 410, cw, 112);
            tab.Controls.Add(grpDaemon);

            // Use a TableLayoutPanel so the two rows stay aligned and fields do not clip.
            TableLayoutPanel tlpDaemon = new TableLayoutPanel();
            tlpDaemon.Dock = DockStyle.Fill;
            tlpDaemon.Padding = new Padding(10, 24, 10, 6);
            tlpDaemon.Margin = new Padding(0);
            tlpDaemon.ColumnCount = 7;
            tlpDaemon.RowCount = 2;
            // Label, Box, Label, Box, Label, Box, Button-area
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F)); // Daemon/Local AE label
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F)); // AE text box
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F)); // Daemon IP / Local Port label
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F)); // IP box / Local Port numeric
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));  // Port label
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));  // Port numeric
            tlpDaemon.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F)); // Test button
            tlpDaemon.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tlpDaemon.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            Func<string, Control> makeLabel = (text) =>
            {
                Label lbl = new Label();
                lbl.Text = text;
                lbl.AutoSize = false;
                lbl.Dock = DockStyle.Fill;
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                return lbl;
            };

            Func<Control, Control> makeCell = (ctrl) =>
            {
                ctrl.Dock = DockStyle.Fill;
                ctrl.Margin = new Padding(2, 4, 8, 4);
                return ctrl;
            };

            txtDaemonAe = new TextBox();
            txtDaemonIp = new TextBox();
            numDaemonPort = new NumericUpDown();
            numDaemonPort.Minimum = 1; numDaemonPort.Maximum = 65535; numDaemonPort.Value = 104;
            numDaemonPort.Width = 75;
            txtLocalAe = new TextBox();
            numLocalPort = new NumericUpDown();
            numLocalPort.Minimum = 1; numLocalPort.Maximum = 65535; numLocalPort.Value = 1040;
            numLocalPort.Width = 75;
            numLocalPort.Dock = DockStyle.None;
            numLocalPort.Anchor = AnchorStyles.Left;

            SetCueBanner(txtDaemonAe, "e.g. DB_DAEMON");
            SetCueBanner(txtDaemonIp, "e.g. 10.0.0.5");
            SetCueBanner(txtLocalAe, "e.g. AUTOFLC");

            tlpDaemon.Controls.Add(makeLabel("Daemon AE:"), 0, 0);
            tlpDaemon.Controls.Add(makeCell(txtDaemonAe), 1, 0);
            tlpDaemon.Controls.Add(makeLabel("Daemon IP:"), 2, 0);
            tlpDaemon.Controls.Add(makeCell(txtDaemonIp), 3, 0);
            tlpDaemon.Controls.Add(makeLabel("Port:"), 4, 0);
            tlpDaemon.Controls.Add(makeCell(numDaemonPort), 5, 0);

            tlpDaemon.Controls.Add(makeLabel("Local AE:"), 0, 1);
            tlpDaemon.Controls.Add(makeCell(txtLocalAe), 1, 1);
            tlpDaemon.Controls.Add(makeLabel("Local Port:"), 2, 1);
            tlpDaemon.Controls.Add(makeCell(numLocalPort), 3, 1);

            btnTestDaemon = new Button();
            btnTestDaemon.Text = "Test Connection";
            btnTestDaemon.Dock = DockStyle.Fill;
            btnTestDaemon.Margin = new Padding(8, 3, 10, 3);
            btnTestDaemon.Click += BtnTestDaemon_Click;
            tlpDaemon.Controls.Add(btnTestDaemon, 6, 0);
            tlpDaemon.SetRowSpan(btnTestDaemon, 2);

            grpDaemon.Controls.Add(tlpDaemon);

            // --- Engine branch (design V2 + Plastimatch): Velocity / Eclipse / Plastimatch ---
            GroupBox grpEngine = new GroupBox();
            grpEngine.Text = "Deformable Registration Engine (data preparation branch)";
            grpEngine.SetBounds(left, 590, cw, 82);
            tab.Controls.Add(grpEngine);

            radioEngineVelocity = new RadioButton();
            radioEngineVelocity.Text = "Velocity AI";
            radioEngineVelocity.SetBounds(14, 20, 95, 20);
            radioEngineVelocity.Checked = true;
            radioEngineVelocity.CheckedChanged += EngineRadio_CheckedChanged;
            grpEngine.Controls.Add(radioEngineVelocity);

            radioEngineEclipse = new RadioButton();
            radioEngineEclipse.Text = "Eclipse DIR";
            radioEngineEclipse.SetBounds(112, 20, 100, 20);
            radioEngineEclipse.CheckedChanged += EngineRadio_CheckedChanged;
            grpEngine.Controls.Add(radioEngineEclipse);

            radioEnginePlastimatch = new RadioButton();
            radioEnginePlastimatch.Text = "Plastimatch (local)";
            radioEnginePlastimatch.SetBounds(222, 20, 160, 20);
            radioEnginePlastimatch.CheckedChanged += EngineRadio_CheckedChanged;
            grpEngine.Controls.Add(radioEnginePlastimatch);

            // Plastimatch registration parameters (visible only for the
            // Plastimatch engine). Defaults = the Lim 2026 baseline
            // validated against the published Plastimatch baseline (Lim 2026).
            lblPlastiLambda = new Label();
            lblPlastiLambda.Text = "Lambda:";
            lblPlastiLambda.SetBounds(395, 22, 45, 18);
            lblPlastiLambda.Visible = false;
            grpEngine.Controls.Add(lblPlastiLambda);

            numPlastiLambda = new NumericUpDown();
            numPlastiLambda.SetBounds(442, 18, 55, 24);
            numPlastiLambda.DecimalPlaces = 1;
            numPlastiLambda.Increment = 0.1m;
            numPlastiLambda.Minimum = 0m;
            numPlastiLambda.Maximum = 10m;
            numPlastiLambda.Value = 1.0m;
            numPlastiLambda.Visible = false;
            grpEngine.Controls.Add(numPlastiLambda);



            lblPlastiPreset = new Label();
            lblPlastiPreset.Text = "Stages:";
            lblPlastiPreset.SetBounds(688, 22, 48, 18);
            lblPlastiPreset.Visible = false;
            grpEngine.Controls.Add(lblPlastiPreset);

            cboPlastiPreset = new ComboBox();
            cboPlastiPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPlastiPreset.SetBounds(738, 19, 305, 24);
            cboPlastiPreset.Items.Add("VESPIR baseline (Lim et al. 2026)");
            cboPlastiPreset.Items.Add("Four-stage B-spline 60-15 mm (faster)");
            cboPlastiPreset.Items.Add("Three-stage B-spline 40-15 mm (fastest)");
            cboPlastiPreset.SelectedIndex = 0;
            cboPlastiPreset.Visible = false;
            grpEngine.Controls.Add(cboPlastiPreset);

            btnExport4DCT = new Button();
            btnExport4DCT.Text = "Export Selected 4DCT Phases";
            btnExport4DCT.SetBounds(14, 46, 220, 28);
            btnExport4DCT.Enabled = false;
            btnExport4DCT.Tag = "primary";
            btnExport4DCT.Click += BtnExport4DCT_Click;
            grpEngine.Controls.Add(btnExport4DCT);

            btnPrepareExpRef = new Button();
            btnPrepareExpRef.Text = "Prepare Exp reference";
            btnPrepareExpRef.SetBounds(14, 46, 180, 28);
            btnPrepareExpRef.Visible = false;
            btnPrepareExpRef.Tag = "primary";
            btnPrepareExpRef.Click += BtnPrepareExpRef_Click;
            grpEngine.Controls.Add(btnPrepareExpRef);

            lblExpRefStatus = new Label();
            lblExpRefStatus.SetBounds(245, 46, cw - 265, 28);
            lblExpRefStatus.Text = "";
            lblExpRefStatus.ForeColor = RefText;
            lblExpRefStatus.Visible = false;
            grpEngine.Controls.Add(lblExpRefStatus);

            btnRunPlasti = new Button();
            btnRunPlasti.Text = "Run Registration (Plastimatch)";
            btnRunPlasti.SetBounds(14, 46, 230, 28);
            btnRunPlasti.Enabled = false;
            btnRunPlasti.Visible = false;
            btnRunPlasti.Tag = "primary";
            btnRunPlasti.Click += BtnRunPlasti_Click;
            grpEngine.Controls.Add(btnRunPlasti);

            lblPlastiStatus = new Label();
            lblPlastiStatus.SetBounds(255, 46, 385, 28);
            lblPlastiStatus.Text = "";
            lblPlastiStatus.ForeColor = RefText;
            lblPlastiStatus.Visible = false;
            grpEngine.Controls.Add(lblPlastiStatus);

            lblPlastiExe = new Label();
            lblPlastiExe.Text = "plastimatch.exe:";
            lblPlastiExe.SetBounds(648, 50, 98, 18);
            lblPlastiExe.Visible = false;
            grpEngine.Controls.Add(lblPlastiExe);

            txtPlastiExe = new TextBox();
            txtPlastiExe.SetBounds(750, 47, 355, 22);
            SetCueBanner(txtPlastiExe, @"C:\Program Files\Plastimatch\bin\plastimatch.exe");
            txtPlastiExe.Visible = false;
            grpEngine.Controls.Add(txtPlastiExe);

            btnBrowsePlastiExe = new Button();
            btnBrowsePlastiExe.Text = "Browse...";
            btnBrowsePlastiExe.SetBounds(1112, 44, 75, 28);
            btnBrowsePlastiExe.Visible = false;
            btnBrowsePlastiExe.Click += BtnBrowsePlastiExe_Click;
            grpEngine.Controls.Add(btnBrowsePlastiExe);

            progressBar = new ProgressBar();
            progressBar.SetBounds(left, 678, cw, 18);
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Visible = false;
            tab.Controls.Add(progressBar);

            tab.ResumeLayout(false);
        }

        private void InitializeBdfTab(TabPage tab)
        {
            tab.Padding = new Padding(15);
            tab.BackColor = RefBackground;
            tab.SuspendLayout();

            int cw = 1230;
            int left = 15;

            // --- Velocity BDF File ---
            GroupBox grpBdf = new GroupBox();
            grpBdf.Text = "DVF File (Velocity BDF / Eclipse DICOM DR / Plastimatch VF)";
            grpBdf.SetBounds(left, 10, cw, 70);
            tab.Controls.Add(grpBdf);

            TableLayoutPanel tlpBdf = new TableLayoutPanel();
            tlpBdf.Dock = DockStyle.Fill;
            tlpBdf.Padding = new Padding(10, 14, 10, 6);
            tlpBdf.ColumnCount = 3;
            tlpBdf.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            tlpBdf.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlpBdf.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
            tlpBdf.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));

            Label lblBdf = new Label();
            lblBdf.Text = "DVF file path:";
            lblBdf.AutoSize = false;
            lblBdf.Dock = DockStyle.Fill;
            lblBdf.TextAlign = ContentAlignment.MiddleLeft;
            tlpBdf.Controls.Add(lblBdf, 0, 0);

            txtBdfPath = new TextBox();
            txtBdfPath.Dock = DockStyle.Fill;
            txtBdfPath.Margin = new Padding(2, 4, 8, 4);
            tlpBdf.Controls.Add(txtBdfPath, 1, 0);

            btnBrowseBdf = new Button();
            btnBrowseBdf.Text = "Browse...";
            btnBrowseBdf.Dock = DockStyle.Fill;
            btnBrowseBdf.Margin = new Padding(0, 3, 0, 3);
            btnBrowseBdf.Click += BtnBrowseBdf_Click;
            tlpBdf.Controls.Add(btnBrowseBdf, 2, 0);

            grpBdf.Controls.Add(tlpBdf);

            // --- Reference Data ---
            GroupBox grpRef = new GroupBox();
            grpRef.Text = "Reference Data (50% phase)";
            grpRef.SetBounds(left, 80, cw, 130);
            tab.Controls.Add(grpRef);

            TableLayoutPanel tlpRef = new TableLayoutPanel();
            tlpRef.Dock = DockStyle.Fill;
            tlpRef.Padding = new Padding(10, 16, 10, 6);
            tlpRef.ColumnCount = 3;
            tlpRef.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            tlpRef.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlpRef.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
            tlpRef.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            tlpRef.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tlpRef.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));

            chkUseStep1Ref = new CheckBox();
            chkUseStep1Ref.Text = "Use Step 1 exported reference data (CT + RTSTRUCT)";
            chkUseStep1Ref.Checked = true;
            chkUseStep1Ref.Dock = DockStyle.Fill;
            chkUseStep1Ref.CheckedChanged += ChkUseStep1Ref_CheckedChanged;
            tlpRef.Controls.Add(chkUseStep1Ref, 0, 0);
            tlpRef.SetColumnSpan(chkUseStep1Ref, 3);

            Label lblRefDir = new Label();
            lblRefDir.Text = "Reference DICOM folder:";
            lblRefDir.AutoSize = false;
            lblRefDir.Dock = DockStyle.Fill;
            lblRefDir.TextAlign = ContentAlignment.MiddleLeft;
            tlpRef.Controls.Add(lblRefDir, 0, 1);

            txtReferenceDir = new TextBox();
            txtReferenceDir.ReadOnly = true;
            txtReferenceDir.Dock = DockStyle.Fill;
            txtReferenceDir.Margin = new Padding(2, 4, 8, 4);
            tlpRef.Controls.Add(txtReferenceDir, 1, 1);

            btnBrowseReferenceDir = new Button();
            btnBrowseReferenceDir.Text = "Browse...";
            btnBrowseReferenceDir.Dock = DockStyle.Fill;
            btnBrowseReferenceDir.Margin = new Padding(0, 3, 0, 3);
            btnBrowseReferenceDir.Click += BtnBrowseReferenceDir_Click;
            tlpRef.Controls.Add(btnBrowseReferenceDir, 2, 1);

            lblRefStatus = new Label();
            lblRefStatus.Text = "Reference data will be read from the Step 1 output folder.";
            lblRefStatus.Dock = DockStyle.Fill;
            lblRefStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblRefStatus.ForeColor = RefText;
            tlpRef.Controls.Add(lblRefStatus, 0, 2);
            tlpRef.SetColumnSpan(lblRefStatus, 3);

            grpRef.Controls.Add(tlpRef);

            // --- BDF Processing Options ---
            GroupBox grpOptions = new GroupBox();
            grpOptions.Text = "BDF Processing Options";
            grpOptions.SetBounds(left, 215, cw, 190);
            tab.Controls.Add(grpOptions);

            TableLayoutPanel tlpOptions = new TableLayoutPanel();
            tlpOptions.Dock = DockStyle.Fill;
            tlpOptions.Padding = new Padding(10, 16, 10, 6);
            tlpOptions.ColumnCount = 5;
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlpOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tlpOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tlpOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tlpOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            Func<string, Label> makeOptLabel = (text) =>
            {
                Label lbl = new Label();
                lbl.Text = text;
                lbl.AutoSize = false;
                lbl.Dock = DockStyle.Fill;
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                return lbl;
            };

            Func<Control, Control> makeOptCell = (ctrl) =>
            {
                ctrl.Dock = DockStyle.Fill;
                ctrl.Margin = new Padding(2, 4, 16, 4);
                return ctrl;
            };

            numSmoothSigma = new NumericUpDown();
            numSmoothSigma.DecimalPlaces = 1;
            numSmoothSigma.Increment = 0.5m;
            numSmoothSigma.Minimum = 0m;
            numSmoothSigma.Maximum = 20m;
            numSmoothSigma.Value = 2.0m;

            numPercentile = new NumericUpDown();
            numPercentile.DecimalPlaces = 0;
            numPercentile.Minimum = 1m;
            numPercentile.Maximum = 99m;
            numPercentile.Value = 75m;

            tlpOptions.Controls.Add(makeOptLabel("DVF smoothing sigma (mm):"), 0, 0);
            tlpOptions.Controls.Add(makeOptCell(numSmoothSigma), 1, 0);
            tlpOptions.Controls.Add(makeOptLabel("Percentile threshold (%):"), 2, 0);
            tlpOptions.Controls.Add(makeOptCell(numPercentile), 3, 0);

            chkExcludeGtv = new CheckBox();
            chkExcludeGtv.Text = "Exclude GTV region";
            chkExcludeGtv.Checked = true;
            chkExcludeGtv.Dock = DockStyle.Fill;
            tlpOptions.Controls.Add(chkExcludeGtv, 0, 1);
            tlpOptions.SetColumnSpan(chkExcludeGtv, 5);

            FlowLayoutPanel flowOutputs = new FlowLayoutPanel();
            flowOutputs.Dock = DockStyle.Fill;
            flowOutputs.FlowDirection = FlowDirection.LeftToRight;
            flowOutputs.Margin = new Padding(0);
            flowOutputs.WrapContents = false;

            chkOutputRtstruct = new CheckBox();
            chkOutputRtstruct.Text = "Output RTSTRUCT";
            chkOutputRtstruct.Checked = true;
            chkOutputRtstruct.AutoSize = true;
            chkOutputRtstruct.Margin = new Padding(2, 4, 24, 4);
            flowOutputs.Controls.Add(chkOutputRtstruct);

            chkOutputJacobian = new CheckBox();
            chkOutputJacobian.Text = "Output Jacobian NIfTI";
            chkOutputJacobian.Checked = true;
            chkOutputJacobian.AutoSize = true;
            chkOutputJacobian.Margin = new Padding(2, 4, 24, 4);
            flowOutputs.Controls.Add(chkOutputJacobian);

            chkOutputVentilation = new CheckBox();
            chkOutputVentilation.Text = "Output Ventilation NIfTI";
            chkOutputVentilation.Checked = true;
            chkOutputVentilation.AutoSize = true;
            chkOutputVentilation.Margin = new Padding(2, 4, 0, 4);
            flowOutputs.Controls.Add(chkOutputVentilation);

            tlpOptions.Controls.Add(flowOutputs, 0, 2);
            tlpOptions.SetColumnSpan(flowOutputs, 5);

            // Ventilation metric selection (design V2, section 3.2) -
            // absolute layout below the option table to avoid clipping.
            tlpOptions.Dock = DockStyle.Top;
            tlpOptions.Height = 116;

            Label lblMetric = new Label();
            lblMetric.Text = "Ventilation metric:";
            lblMetric.SetBounds(14, 152, 120, 20);
            lblMetric.TextAlign = ContentAlignment.MiddleLeft;
            grpOptions.Controls.Add(lblMetric);

            radioMetricJac = new RadioButton();
            radioMetricJac.Text = "Jacobian";
            radioMetricJac.SetBounds(150, 151, 85, 22);
            radioMetricJac.Checked = true;
            radioMetricJac.AutoSize = true;
            grpOptions.Controls.Add(radioMetricJac);

            radioMetricHu = new RadioButton();
            radioMetricHu.Text = "HU (density)";
            radioMetricHu.SetBounds(245, 151, 100, 22);
            radioMetricHu.AutoSize = true;
            grpOptions.Controls.Add(radioMetricHu);

            radioMetricBoth = new RadioButton();
            radioMetricBoth.Text = "Both";
            radioMetricBoth.SetBounds(355, 151, 60, 22);
            radioMetricBoth.AutoSize = true;
            grpOptions.Controls.Add(radioMetricBoth);

            Label lblCtSigma = new Label();
            lblCtSigma.Text = "HU CT smoothing sigma^2 (mm^2):";
            lblCtSigma.SetBounds(430, 152, 195, 20);
            lblCtSigma.TextAlign = ContentAlignment.MiddleRight;
            grpOptions.Controls.Add(lblCtSigma);

            numCtSigma = new NumericUpDown();
            numCtSigma.SetBounds(632, 149, 60, 24);
            numCtSigma.DecimalPlaces = 2;
            numCtSigma.Increment = 0.5m;
            numCtSigma.Minimum = 0.1m;
            numCtSigma.Maximum = 36m;
            numCtSigma.Value = 9.0m;
            grpOptions.Controls.Add(numCtSigma);

            grpOptions.Controls.Add(tlpOptions);

            // --- Action row ---
            chkAutoImport = new CheckBox();
            chkAutoImport.Text = "Auto C-STORE back to Eclipse after DVF processing";
            chkAutoImport.SetBounds(left, 415, 400, 20);
            chkAutoImport.Checked = true;
            chkAutoImport.CheckedChanged += ChkAutoImport_CheckedChanged;
            tab.Controls.Add(chkAutoImport);

            btnProcessBdf = new Button();
            btnProcessBdf.Text = "Process DVF & Generate Functional-Lung Structures";
            btnProcessBdf.SetBounds(left, 445, 350, 34);
            btnProcessBdf.Tag = "primary";
            btnProcessBdf.Click += BtnProcessBdf_Click;
            tab.Controls.Add(btnProcessBdf);

            btnImportRtstruct = new Button();
            btnImportRtstruct.Text = "Re-send C-STORE to Eclipse";
            btnImportRtstruct.SetBounds(375, 445, 190, 34);
            btnImportRtstruct.Enabled = false;
            btnImportRtstruct.Click += BtnImportRtstruct_Click;
            tab.Controls.Add(btnImportRtstruct);

            Label lblNote = new Label();
            lblNote.Text = "After DVF processing, the generated DICOM RTSTRUCT is pushed back to Eclipse via DICOM C-STORE.\n" +
                           "If auto-import is disabled or fails, click Re-send C-STORE to retry.";
            lblNote.SetBounds(left, 487, cw, 40);
            lblNote.ForeColor = RefText;
            tab.Controls.Add(lblNote);

            // --- DIR QA Results (Jacobian determinant) ---
            grpDirQa = new GroupBox();
            grpDirQa.Text = "DIR QA Results (Jacobian determinant)";
            grpDirQa.SetBounds(left, 532, cw, 165);
            tab.Controls.Add(grpDirQa);

            lblDirQaWarning = new Label();
            lblDirQaWarning.SetBounds(12, 20, cw - 24, 32);
            lblDirQaWarning.Text = "No DIR QA available yet. Run BDF processing to compute Jacobian QA statistics.";
            lblDirQaWarning.ForeColor = RefText;
            lblDirQaWarning.Font = RefFontBold;
            lblDirQaWarning.AutoSize = false;
            lblDirQaWarning.TextAlign = ContentAlignment.MiddleLeft;
            grpDirQa.Controls.Add(lblDirQaWarning);

            dgvDirQa = new DataGridView();
            dgvDirQa.SetBounds(12, 54, cw - 24, 103);
            dgvDirQa.AllowUserToAddRows = false;
            dgvDirQa.AllowUserToDeleteRows = false;
            dgvDirQa.ReadOnly = true;
            dgvDirQa.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvDirQa.MultiSelect = false;
            dgvDirQa.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvDirQa.RowHeadersVisible = false;
            dgvDirQa.BackgroundColor = RefBackground;
            dgvDirQa.BorderStyle = BorderStyle.FixedSingle;
            dgvDirQa.EnableHeadersVisualStyles = false;
            dgvDirQa.ColumnHeadersDefaultCellStyle.BackColor = RefControlBack;
            dgvDirQa.ColumnHeadersDefaultCellStyle.ForeColor = RefText;
            dgvDirQa.ColumnHeadersDefaultCellStyle.Font = RefFontBold;
            dgvDirQa.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            dgvDirQa.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgvDirQa.DefaultCellStyle.Font = RefFont;
            dgvDirQa.DefaultCellStyle.BackColor = Color.White;
            dgvDirQa.DefaultCellStyle.ForeColor = RefText;
            dgvDirQa.DefaultCellStyle.SelectionBackColor = RefAccentLight;
            dgvDirQa.DefaultCellStyle.SelectionForeColor = RefText;
            dgvDirQa.AlternatingRowsDefaultCellStyle.BackColor = RefAltRow;
            dgvDirQa.GridColor = RefBorder;
            grpDirQa.Controls.Add(dgvDirQa);

            InitDirQaTable();

            tab.ResumeLayout(false);
        }

        private void InitDirQaTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Metric", typeof(string));
            dt.Columns.Add("Whole image", typeof(string));
            dt.Columns.Add("Lung region", typeof(string));
            dt.Rows.Add("Folding (detJ<0)", "-", "-");
            dt.Rows.Add("Contraction (0<=detJ<1)", "-", "-");
            dt.Rows.Add("Identity (detJ=1)", "-", "-");
            dt.Rows.Add("Expansion (detJ>1)", "-", "-");
            dt.Rows.Add("Non-finite (NaN/Inf)", "-", "-");
            dt.Rows.Add("Total voxels", "-", "-");
            dt.Rows.Add("Min detJ", "-", "-");
            dt.Rows.Add("Max detJ", "-", "-");
            dt.Rows.Add("Mean detJ", "-", "-");
            dt.Rows.Add("Std detJ", "-", "-");
            dt.Rows.Add("HU mean (lung)", "-", "-");
            dt.Rows.Add("HU SD (lung)", "-", "-");
            dt.Rows.Add("HU mass factor", "-", "-");
            dt.Rows.Add("Mutual rho(Jac, HU)", "-", "-");
            dgvDirQa.DataSource = dt;
        }

        private void ApplyStyles()
        {
            BackColor = RefBackground;
            Font = RefFont;
            foreach (Control c in Controls)
                StyleControl(c);
        }

        private void StyleControl(Control c)
        {
            Button b = c as Button;
            if (b != null)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.Font = RefFontBold;
                b.Cursor = Cursors.Hand;
                b.UseVisualStyleBackColor = false;

                b.MouseEnter -= Button_MouseEnter;
                b.MouseLeave -= Button_MouseLeave;
                b.MouseEnter += Button_MouseEnter;
                b.MouseLeave += Button_MouseLeave;

                bool primary = "primary".Equals(b.Tag as string);
                if (primary)
                {
                    b.BackColor = RefAccent;
                    b.ForeColor = Color.White;
                    b.FlatAppearance.BorderSize = 0;
                }
                else
                {
                    b.BackColor = RefControlBack;
                    b.ForeColor = RefText;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = RefBorder;
                }
            }

            TextBox t = c as TextBox;
            if (t != null && t != txtLog && t != txtInfoBar)
            {
                t.BorderStyle = BorderStyle.FixedSingle;
                t.BackColor = RefControlBack;
                t.ForeColor = RefText;
                t.Font = RefFont;
            }

            NumericUpDown n = c as NumericUpDown;
            if (n != null)
            {
                n.BorderStyle = BorderStyle.FixedSingle;
                n.BackColor = RefControlBack;
                n.ForeColor = RefText;
                n.Font = RefFont;
            }

            Label l = c as Label;
            if (l != null)
            {
                l.Font = RefFont;
                if (l.ForeColor == SystemColors.ControlText || l.ForeColor == Color.Black)
                    l.ForeColor = RefText;
            }

            CheckBox cb = c as CheckBox;
            if (cb != null)
            {
                cb.Font = RefFont;
                cb.ForeColor = RefText;
                cb.BackColor = cb.Parent != null ? cb.Parent.BackColor : RefBackground;
            }

            GroupBox g = c as GroupBox;
            if (g != null)
            {
                g.Font = RefHeaderFont;
                g.ForeColor = RefText;
                g.BackColor = RefBackground;
            }

            DataGridView dgv = c as DataGridView;
            if (dgv != null)
            {
                dgv.BackgroundColor = RefBackground;
                dgv.BorderStyle = BorderStyle.FixedSingle;
                dgv.ColumnHeadersDefaultCellStyle.BackColor = RefControlBack;
                dgv.ColumnHeadersDefaultCellStyle.ForeColor = RefText;
                dgv.ColumnHeadersDefaultCellStyle.Font = RefFontBold;
                dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
                dgv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                dgv.EnableHeadersVisualStyles = false;
                dgv.DefaultCellStyle.Font = RefFont;
                dgv.DefaultCellStyle.BackColor = Color.White;
                dgv.DefaultCellStyle.ForeColor = RefText;
                dgv.DefaultCellStyle.SelectionBackColor = RefAccentLight;
                dgv.DefaultCellStyle.SelectionForeColor = RefText;
                dgv.AlternatingRowsDefaultCellStyle.BackColor = RefAltRow;
                dgv.GridColor = RefBorder;
                dgv.RowHeadersVisible = false;
            }

            TabControl tc = c as TabControl;
            if (tc != null)
                tc.Font = RefFontBold;

            ProgressBar pb = c as ProgressBar;
            if (pb != null)
            {
                pb.BackColor = RefControlBack;
                pb.ForeColor = RefAccent;
            }

            foreach (Control child in c.Controls)
                StyleControl(child);
        }

        private void Button_MouseEnter(object sender, EventArgs e)
        {
            Button b = sender as Button;
            if (b == null || !b.Enabled) return;
            if ("primary".Equals(b.Tag as string))
                b.BackColor = RefAccentHover;
            else
            {
                b.BackColor = RefAccentLight;
                b.ForeColor = RefAccent;
                b.FlatAppearance.BorderColor = RefAccent;
            }
        }

        private void Button_MouseLeave(object sender, EventArgs e)
        {
            Button b = sender as Button;
            if (b == null || !b.Enabled) return;
            if ("primary".Equals(b.Tag as string))
                b.BackColor = RefAccent;
            else
            {
                b.BackColor = RefControlBack;
                b.ForeColor = RefText;
                b.FlatAppearance.BorderColor = RefBorder;
            }
        }

        private void InitializeBackgroundWorker()
        {
            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.WorkerSupportsCancellation = true;
            _worker.DoWork += Worker_DoWork;
            _worker.ProgressChanged += Worker_ProgressChanged;
            _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
        }

        private void Log(string msg)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action<string>(Log), msg);
                return;
            }
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, msg, Environment.NewLine));
        }

        private void SetBusy(bool busy)
        {
            progressBar.Visible = busy;
            progressBar.Style = ProgressBarStyle.Marquee;
                btnExport4DCT.Enabled = !busy && _images.Count > 0;
            btnPrepareExpRef.Enabled = !busy && radioEngineEclipse.Checked;
            btnRunPlasti.Enabled = !busy && radioEnginePlastimatch.Checked && _images.Count > 0;
            btnProcessBdf.Enabled = !busy;
            btnImportRtstruct.Enabled = !busy && !string.IsNullOrEmpty(_lastRtstructPath) && File.Exists(_lastRtstructPath);
            btnConnect.Enabled = !busy;
            btnSearchPatient.Enabled = !busy && _esapiApp != null;
            btnTestDaemon.Enabled = !busy;
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            Log("Connecting to Eclipse...");
            DisposeEsapiApp();
            _connectCallReturned = false;
            StartConnectWatchdog();
            try
            {
                // MUST be called on the UI thread: Varian's vmod layer asserts
                // thread affinity on the resulting Application object and every
                // object it produces (OpenPatient, PatientSummaries, etc.).
                // See the comment block above the field declarations for the
                // dump analysis that proved cross-thread use is fatal.
                _esapiApp = ESAPIApp.CreateApplication();
                string vinfo = string.Empty;
                try
                {
                    if (_esapiApp != null && _esapiApp.ScriptEnvironment != null)
                        vinfo = _esapiApp.ScriptEnvironment.VersionInfo;
                }
                catch (Exception vx)
                {
                    Log("ScriptEnvironment.VersionInfo read failed: " + vx.Message);
                }
                txtInfoBar.Text = string.Format("AutoFLC | Connected: {0}", vinfo);
                txtInfoBar.ForeColor = RefSuccess;
                btnSearchPatient.Enabled = true;
                Log("Eclipse connected successfully.");
            }
            catch (System.Runtime.InteropServices.COMException cex)
            {
                txtInfoBar.Text = "AutoFLC | Disconnected (Eclipse unavailable)";
                txtInfoBar.ForeColor = RefError;
                string msg =
                    "ESAPI COM server could not be activated.\n\n"
                    + "HRESULT 0x" + cex.ErrorCode.ToString("X") + "\n"
                    + cex.Message + "\n\n"
                    + "This usually means the Varian ESAPI runtime / Aria\n"
                    + "Database Daemon is not installed or not running on\n"
                    + "this machine. Run AutoFLC on the radiotherapy\n"
                    + "workstation that has Eclipse installed.";
                Log("Connection failed: COMException - " + cex.Message);
                MessageBox.Show(msg, "Eclipse Connect Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                txtInfoBar.Text = "AutoFLC | Disconnected (Eclipse unavailable)";
                txtInfoBar.ForeColor = RefError;
                Log(string.Format("Connection failed: {0} - {1}", ex.GetType().Name, ex.Message));
                MessageBox.Show(
                    string.Format(
                        "Failed to connect to Eclipse:\n\n{0}\n\n{1}",
                        ex.GetType().Name, ex.Message),
                    "Eclipse Connect Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _connectCallReturned = true;
            }
        }

        // If CreateApplication() blocks (typical only when the Aria DB Daemon
        // is unreachable on a non-Eclipse machine), the UI thread is stuck and
        // no button can dismiss the app. A separate background thread watches
        // _connectCallReturned; if the connect still hasn't come back after
        // ConnectWatchdogTimeoutMs it pops a non-modal MessageBox (which spins
        // its own message pump on this watchdog thread) offering a force quit.
        // On a real Eclipse machine the connect returns within seconds and the
        // watchdog exits silently.
        private void StartConnectWatchdog()
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    int waited = 0;
                    while (!_connectCallReturned && waited < ConnectWatchdogTimeoutMs)
                    {
                        Thread.Sleep(250);
                        waited += 250;
                    }
                    if (_connectCallReturned) return;

                    DialogResult r = MessageBox.Show(
                        string.Format(
                            "Eclipse connect has not returned after {0} seconds.\n\n"
                            + "The Aria / Varian ESAPI service is probably not reachable.\n"
                            + "Force quit AutoFLC?",
                            ConnectWatchdogTimeoutMs / 1000),
                        "Eclipse Connect Timeout",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (r == DialogResult.Yes)
                    {
                        Environment.Exit(70);
                    }
                }
                catch (Exception)
                {
                    // Watchdog never throws.
                }
            }) { IsBackground = true, Name = "ConnectWatchdog" };
            t.Start();
        }

        private async void BtnTestDaemon_Click(object sender, EventArgs e)
        {
            string daemonAe = txtDaemonAe.Text.Trim();
            string daemonIp = txtDaemonIp.Text.Trim();
            int daemonPort = (int)numDaemonPort.Value;
            string localAe = txtLocalAe.Text.Trim();

            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 || string.IsNullOrEmpty(localAe))
            {
                MessageBox.Show(
                    "Daemon AE / Daemon IP / Local AE are not configured.\n\n"
                    + "These point at the Varian DICOM DB Daemon running on the\n"
                    + "Aria / Eclipse workstation (typical AE title e.g. DB_DAEMON,\n"
                    + "port 104, IP of that machine). Local AE is any name you\n"
                    + "choose for this PC and Local Port is a free TCP port on it.\n\n"
                    + "On a dev machine without the Varian DB Daemon reachable,\n"
                    + "C-ECHO will always fail - this is expected.",
                    "Daemon settings incomplete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SaveDaemonSettings();
            btnTestDaemon.Enabled = false;
            Log(string.Format("Testing DICOM Daemon C-ECHO ({0}@{1}:{2})...", daemonAe, daemonIp, daemonPort));
            try
            {
                var service = new DicomDaemonService(daemonAe, daemonIp, daemonPort, localAe, 0, Log);
                bool ok = await service.TestConnectionAsync();
                MessageBox.Show(ok ? "C-ECHO succeeded." : "C-ECHO failed, please check settings and daemon status.", "Connection Test", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Test error: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnTestDaemon.Enabled = true;
            }
        }

        private void ChkAutoImport_CheckedChanged(object sender, EventArgs e)
        {
            SaveDaemonSettings();
        }

        private void BtnSearchPatient_Click(object sender, EventArgs e)
        {
            if (_esapiApp == null) return;
            string pid = txtPatientId.Text.Trim();
            if (string.IsNullOrEmpty(pid))
            {
                MessageBox.Show("Please enter a patient ID.", "Information");
                return;
            }

            try
            {
                if (_openPatient != null)
                {
                    _esapiApp.ClosePatient();
                    _openPatient = null;
                }

                PatientSummary ps = _esapiApp.PatientSummaries.FirstOrDefault(p => p.Id == pid);
                if (ps == null && pid != pid.TrimStart('0'))
                    ps = _esapiApp.PatientSummaries.FirstOrDefault(p => p.Id == pid.TrimStart('0'));
                if (ps == null && pid.Length < 7)
                    ps = _esapiApp.PatientSummaries.FirstOrDefault(p => p.Id == pid.PadLeft(7, '0'));

                if (ps == null)
                {
                    MessageBox.Show(string.Format("Patient not found: {0}", pid), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Log(string.Format("Patient not found: {0}", pid));
                    return;
                }

                _openPatient = _esapiApp.OpenPatient(ps);
                Log(string.Format("Patient opened: {0} {1}{2}", _openPatient.Id, _openPatient.LastName, _openPatient.FirstName));
                LoadImages();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Search patient failed: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log(string.Format("Search failed: {0}", ex.Message));
            }
        }

        private void LoadImages()
        {
            _images.Clear();
            try
            {
                List<VMS.TPS.Common.Model.API.Image> rawImages = new List<VMS.TPS.Common.Model.API.Image>();
                HashSet<string> seenRaw = new HashSet<string>();

                // 1. Scan the images referenced by courses/plans/structure sets,
                //    preserving the plan context.
                int courseCount = 0;
                int planCount = 0;
                if (_openPatient.Courses != null)
                {
                    foreach (Course c in _openPatient.Courses)
                    {
                        courseCount++;
                        if (c.PlanSetups == null) continue;
                        foreach (PlanSetup p in c.PlanSetups)
                        {
                            planCount++;
                            if (p.StructureSet == null || p.StructureSet.Image == null)
                            {
                                Log(string.Format("  Course {0} / Plan {1}: StructureSet or Image is empty, skipped.", c.Id, p.Id));
                                continue;
                            }
                            CollectRawImage(p.StructureSet.Image, rawImages, seenRaw);
                        }
                    }
                }
                Log(string.Format("Scanned courses: {0} course(s), {1} plan(s).", courseCount, planCount));

                // 2. Scan Patient.Studies -> Series -> Images as the main image
                //    source for patients without a plan.
                int studyCount = 0;
                int seriesCount = 0;
                int imageCount = 0;
                if (_openPatient.Studies != null)
                {
                    foreach (Study study in _openPatient.Studies)
                    {
                        if (study == null) continue;
                        studyCount++;
                        if (study.Series == null) continue;
                        foreach (Series series in study.Series)
                        {
                            if (series == null) continue;
                            seriesCount++;
                            if (series.Images == null) continue;
                            foreach (VMS.TPS.Common.Model.API.Image img in series.Images)
                            {
                                if (img == null) continue;
                                imageCount++;
                                CollectRawImage(img, rawImages, seenRaw);
                            }
                        }
                    }
                }
                Log(string.Format("Scanned studies: {0} study(s), {1} series, {2} image(s).", studyCount, seriesCount, imageCount));

                // 3. Group by series UID; merge single-slice images into one volume.
                // Previously pushed-back "-E" copies are excluded: their IDs
                // still contain the phase token (e.g. "CT_50_1-E" detects as
                // 50%), so they would reappear as selectable phase rows and
                // could be exported as if they were the original phase CT.
                int excludedPushedBack = 0;
                Dictionary<string, List<VMS.TPS.Common.Model.API.Image>> groups = new Dictionary<string, List<VMS.TPS.Common.Model.API.Image>>();
                foreach (VMS.TPS.Common.Model.API.Image img in rawImages)
                {
                    if (IsPushedBackImage(img.Id))
                    {
                        excludedPushedBack++;
                        continue;
                    }

                    string uid = string.Empty;
                    if (img.Series != null && img.Series.UID != null)
                        uid = img.Series.UID;

                    if (string.IsNullOrEmpty(uid))
                        uid = (img.Id ?? string.Empty) + "|" + Guid.NewGuid().ToString("N");

                    if (!groups.ContainsKey(uid))
                        groups[uid] = new List<VMS.TPS.Common.Model.API.Image>();
                    groups[uid].Add(img);
                }
                if (excludedPushedBack > 0)
                    Log(string.Format("Excluded {0} previously pushed-back image(s) (ending in -E).", excludedPushedBack));

                foreach (var kvp in groups)
                {
                    List<VMS.TPS.Common.Model.API.Image> list = kvp.Value;

                    // One series can carry several images (e.g. CT_0_1 and
                    // CT_50_1 inside the same series). The list shows one row
                    // per series, but the 0% and 50% phase images must never
                    // be hidden by that rule — each gets its own row and the
                    // remaining non-phase images are dropped.
                    List<VMS.TPS.Common.Model.API.Image> phaseImgs = new List<VMS.TPS.Common.Model.API.Image>();
                    HashSet<int> phasesShown = new HashSet<int>();
                    foreach (VMS.TPS.Common.Model.API.Image img in list)
                    {
                        if (img.ZSize <= 1) continue;
                        int ph = PhaseOfRawImage(img);
                        if (ph >= 0 && !phasesShown.Contains(ph))
                        {
                            phasesShown.Add(ph);
                            phaseImgs.Add(img);
                        }
                    }

                    if (phaseImgs.Count > 0)
                    {
                        foreach (VMS.TPS.Common.Model.API.Image img in phaseImgs)
                            AddVolumeImageInfo(img);
                        continue;
                    }

                    VMS.TPS.Common.Model.API.Image vol3d = null;
                    foreach (VMS.TPS.Common.Model.API.Image img in list)
                    {
                        if (img.ZSize > 1)
                        {
                            vol3d = img;
                            break;
                        }
                    }

                    if (vol3d != null)
                    {
                        AddVolumeImageInfo(vol3d);
                    }
                    else if (list.Count > 0)
                    {
                        AddSliceGroupImageInfo(list);
                    }
                }

                DataTable dt = new DataTable();
                dt.Columns.Add("Series", typeof(string));
                dt.Columns.Add("Image ID", typeof(string));
                dt.Columns.Add("Dimensions", typeof(string));
                dt.Columns.Add("Detected Phase", typeof(string));
                dt.Columns.Add("Export", typeof(bool));

                foreach (var img in _images)
                {
                    string phaseStr = img.Phase == 0 ? "0% (end-inspiration)" : img.Phase == 50 ? "50% (end-expiration)" : "Unknown";
                    dt.Rows.Add(img.SeriesId, img.Id, string.Format("{0}x{1}x{2}", img.XSize, img.YSize, img.ZSize), phaseStr, img.Phase >= 0);
                    Log(string.Format("  Series '{0}' / Image '{1}' ({2}x{3}x{4}) -> phase {5}",
                        img.SeriesId, img.Id, img.XSize, img.YSize, img.ZSize,
                        img.Phase < 0 ? "unknown" : img.Phase.ToString() + "%"));
                }

                dgvImages.DataSource = dt;
                foreach (DataGridViewColumn col in dgvImages.Columns)
                    col.ReadOnly = col.Name != "Export";

                int n0 = _images.Count(i => i.Phase == 0);
                int n50 = _images.Count(i => i.Phase == 50);
                lblDetectedPhases.Text = string.Format("Detected phases: 0%={0}, 50%={1}", n0, n50);
                btnExport4DCT.Enabled = _images.Count > 0 && !_worker.IsBusy;
                btnRunPlasti.Enabled = _images.Count > 0 && !_worker.IsBusy && radioEnginePlastimatch.Checked;
                Log(string.Format("Loaded {0} image series, detected {1} 0% phase(s) and {2} 50% phase(s).", _images.Count, n0, n50));
                UpdateReferenceStatus();
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to load images: {0}", ex.Message));
            }
        }

        private void CollectRawImage(VMS.TPS.Common.Model.API.Image img, List<VMS.TPS.Common.Model.API.Image> rawImages, HashSet<string> seenRaw)
        {
            string seriesId = string.Empty;
            if (img.Series != null && img.Series.Id != null)
                seriesId = img.Series.Id;

            string imageId = img.Id ?? string.Empty;
            string key = imageId + "|" + seriesId;
            if (seenRaw.Contains(key)) return;
            seenRaw.Add(key);
            rawImages.Add(img);
        }

        private static int PhaseOfRawImage(VMS.TPS.Common.Model.API.Image img)
        {
            string seriesId = string.Empty;
            string comment = string.Empty;
            if (img.Series != null)
            {
                if (img.Series.Id != null) seriesId = img.Series.Id;
                if (img.Series.Comment != null) comment = img.Series.Comment;
            }
            return PhaseDetector.DetectPhase(seriesId, comment, img.Id);
        }

        private void AddVolumeImageInfo(VMS.TPS.Common.Model.API.Image img)
        {
            string seriesId = string.Empty;
            string comment = string.Empty;
            if (img.Series != null)
            {
                if (img.Series.Id != null) seriesId = img.Series.Id;
                if (img.Series.Comment != null) comment = img.Series.Comment;
            }

            int phase = PhaseDetector.DetectPhase(seriesId, comment, img.Id);
            _images.Add(new ImageInfo
            {
                Id = img.Id ?? string.Empty,
                SeriesId = seriesId,
                SeriesDescription = string.IsNullOrEmpty(comment) ? seriesId : comment,
                XSize = img.XSize,
                YSize = img.YSize,
                ZSize = img.ZSize,
                Phase = phase,
                ESAPIImage = img
            });
        }

        private void AddSliceGroupImageInfo(List<VMS.TPS.Common.Model.API.Image> slices)
        {
            // Sort the slices by their Z position (axial CT moves along Origin.z).
            slices.Sort((a, b) => a.Origin.z.CompareTo(b.Origin.z));

            VMS.TPS.Common.Model.API.Image first = slices[0];
            string seriesId = string.Empty;
            string comment = string.Empty;
            if (first.Series != null)
            {
                if (first.Series.Id != null) seriesId = first.Series.Id;
                if (first.Series.Comment != null) comment = first.Series.Comment;
            }

            int phase = PhaseDetector.DetectPhase(seriesId, comment, first.Id);
            _images.Add(new ImageInfo
            {
                Id = first.Id ?? string.Empty,
                SeriesId = seriesId,
                SeriesDescription = string.IsNullOrEmpty(comment) ? seriesId : comment,
                XSize = first.XSize,
                YSize = first.YSize,
                ZSize = slices.Count,
                Phase = phase,
                SliceImages = slices
            });
        }

        private void BtnBrowseFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                if (!string.IsNullOrEmpty(txtSharedFolder.Text) && Directory.Exists(txtSharedFolder.Text))
                    dlg.SelectedPath = txtSharedFolder.Text;

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtSharedFolder.Text = dlg.SelectedPath;
                    _userSettings.LastOutputDirectory = dlg.SelectedPath;
                    SettingsManager.Save(_userSettings);
                }
            }
        }

        private void BtnExport4DCT_Click(object sender, EventArgs e)
        {
            if (_worker.IsBusy) { MessageBox.Show("Another operation is already in progress."); return; }
            if (_openPatient == null) { MessageBox.Show("Please search a patient first."); return; }

            string outDir = txtSharedFolder.Text.Trim();
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            {
                MessageBox.Show("Please select a valid output folder.");
                return;
            }

            string daemonAe = txtDaemonAe.Text.Trim();
            string daemonIp = txtDaemonIp.Text.Trim();
            int daemonPort = (int)numDaemonPort.Value;
            string localAe = txtLocalAe.Text.Trim();
            int localPort = (int)numLocalPort.Value;

            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 ||
                string.IsNullOrEmpty(localAe) || localPort <= 0)
            {
                MessageBox.Show("Please fill in all DICOM DB Daemon settings (Daemon AE, IP, Port, Local AE, Local Port).");
                return;
            }

            var toExport = GetSelectedImagesToExport();
            if (toExport.Count == 0) { MessageBox.Show("Please select at least one image series to export."); return; }

            var phaseGroups = toExport.GroupBy(i => i.Phase).ToList();
            var phase0Group = phaseGroups.FirstOrDefault(g => g.Key == 0);
            var phase50Group = phaseGroups.FirstOrDefault(g => g.Key == 50);

            if (phase0Group == null)
            {
                MessageBox.Show("A 0% phase must be selected (exported alongside the 50% phase).", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (phase50Group == null)
            {
                MessageBox.Show("A 50% phase must be selected as the registration reference (end-expiration).", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (phase0Group.Count() > 1)
            {
                MessageBox.Show("Multiple 0% phases selected. Please select only one.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (phase50Group.Count() > 1)
            {
                MessageBox.Show("Multiple 50% phases selected. Please select only one.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var phases = new List<PhaseExportInfo>();
            foreach (ImageInfo info in toExport)
            {
                string seriesUid = GetSeriesUidForImageInfo(info);
                string studyUid = GetStudyUidForImageInfo(info);
                if (string.IsNullOrEmpty(seriesUid))
                {
                    MessageBox.Show(string.Format("Cannot obtain SeriesInstanceUID for phase {0}.", info.SeriesId));
                    return;
                }
                if (string.IsNullOrEmpty(studyUid))
                {
                    MessageBox.Show(string.Format("StudyInstanceUID for phase {0} not obtained from ESAPI; will try C-FIND.", info.SeriesId), "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                phases.Add(new PhaseExportInfo { Phase = info.Phase, CtSeriesUid = seriesUid, StudyUid = studyUid });
            }

            // Fetch the StructureSet UID for each phase from Eclipse
            // (StructureSet.UID matches the MAAS convention). Both the 0% and
            // 50% structure sets are linked to their own phase.
            Action<int> linkPhaseStructureSet = (int phaseVal) =>
            {
                ImageInfo info = toExport.FirstOrDefault(i => i.Phase == phaseVal);
                if (info == null) return;
                StructureSet ss = FindReferenceStructureSet(info);
                if (ss != null && !string.IsNullOrEmpty(ss.UID))
                {
                    PhaseExportInfo pe = phases.FirstOrDefault(p => p.Phase == phaseVal);
                    if (pe != null)
                    {
                        pe.RtstructSeriesUid = ss.UID;
                        Log(string.Format("Linked {0}% phase StructureSet: '{1}' (on image '{2}')", phaseVal, ss.Id, ss.Image != null ? ss.Image.Id : "?"));
                    }
                }
                else
                {
                    Log(string.Format("No {0}% phase StructureSet found in Eclipse; RTSTRUCT will not be exported for this phase.", phaseVal));
                }
            };
            linkPhaseStructureSet(0);
            linkPhaseStructureSet(50);

            _userSettings.LastOutputDirectory = outDir;
            SaveDaemonSettings();

            var args = new DaemonExportArgs
            {
                BaseDir = outDir,
                PatientId = _openPatient.Id,
                Phases = phases,
                DaemonAETitle = daemonAe,
                DaemonIP = daemonIp,
                DaemonPort = daemonPort,
                LocalAETitle = localAe,
                LocalPort = localPort
            };

            _lastDaemonExportArgs = args;
            SetBusy(true);
            _worker.RunWorkerAsync(args);
        }

        private void BtnBrowseBdf_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "DVF files (Velocity BDF / Eclipse DICOM DR / Plastimatch VF)|*.bdf;*.dcm;*.mha;*.mhd|Velocity BDF|*.bdf|DICOM DR|*.dcm|Plastimatch VF|*.mha;*.mhd|All files|*.*";
                if (!string.IsNullOrEmpty(txtBdfPath.Text) && File.Exists(txtBdfPath.Text))
                    dlg.InitialDirectory = Path.GetDirectoryName(txtBdfPath.Text);
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtBdfPath.Text = dlg.FileName;
                    _userSettings.LastBdfPath = dlg.FileName;
                    SettingsManager.Save(_userSettings);
                }
            }
        }

        private void ChkUseStep1Ref_CheckedChanged(object sender, EventArgs e)
        {
            if (_loadingSettings) return;
            if (chkUseStep1Ref.Checked)
            {
                txtReferenceDir.Text = txtSharedFolder.Text;
                txtReferenceDir.ReadOnly = true;
            }
            else
            {
                txtReferenceDir.ReadOnly = false;
                if (!string.IsNullOrEmpty(_userSettings.ReferenceDirectory) && Directory.Exists(_userSettings.ReferenceDirectory))
                    txtReferenceDir.Text = _userSettings.ReferenceDirectory;
                else
                    txtReferenceDir.Clear();
            }
            // No directory scan on checkbox toggle — the reference status is
            // only meaningful after Step 1 has written data to the folder.
            // The actual CT/RTSTRUCT lookup happens when Process is clicked.
            SaveDaemonSettings();
        }

        private void TxtSharedFolder_TextChanged(object sender, EventArgs e)
        {
            if (_loadingSettings) return;
            string path = txtSharedFolder.Text.Trim();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                _userSettings.LastOutputDirectory = path;
                _settingsDebounceTimer.Stop();
                _settingsDebounceTimer.Start();
            }
            if (chkUseStep1Ref != null && chkUseStep1Ref.Checked)
            {
                txtReferenceDir.Text = txtSharedFolder.Text;
                lblRefStatus.Text = "Reference data will be read from the Step 1 output folder.";
                lblRefStatus.ForeColor = RefText;
            }
        }

        private void TxtBdfPath_TextChanged(object sender, EventArgs e)
        {
            if (_loadingSettings) return;
            string path = txtBdfPath.Text.Trim();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _userSettings.LastBdfPath = path;
                _settingsDebounceTimer.Stop();
                _settingsDebounceTimer.Start();
            }
        }

        private void BtnBrowseReferenceDir_Click(object sender, EventArgs e)
        {
            // Browsing implies a manual reference folder: leave Step-1 mode first.
            if (chkUseStep1Ref.Checked)
            {
                chkUseStep1Ref.Checked = false;
                txtReferenceDir.ReadOnly = false;
            }
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select the reference DICOM folder containing the 50% phase CT and RTSTRUCT";
                if (!string.IsNullOrEmpty(txtReferenceDir.Text) && Directory.Exists(txtReferenceDir.Text))
                    dlg.SelectedPath = txtReferenceDir.Text;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtReferenceDir.Text = dlg.SelectedPath;
                    _userSettings.ReferenceDirectory = dlg.SelectedPath;
                    SaveDaemonSettings();
                    UpdateReferenceStatus();
                }
            }
        }

        private System.Windows.Forms.Timer _refStatusDebounce;
        private void UpdateReferenceStatusAsync()
        {
            if (_refStatusDebounce == null)
            {
                _refStatusDebounce = new System.Windows.Forms.Timer();
                _refStatusDebounce.Interval = 800;
                _refStatusDebounce.Tick += (s, e) =>
                {
                    _refStatusDebounce.Stop();
                    lblRefStatus.Text = "Scanning reference folder...";
                    lblRefStatus.ForeColor = RefText;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { BeginInvoke((Action)(() => UpdateReferenceStatus())); }
                        catch { }
                    });
                };
            }
            _refStatusDebounce.Stop();
            _refStatusDebounce.Start();
        }

        private void UpdateReferenceStatus()
        {
            string refDir = txtReferenceDir.Text.Trim();
            if (string.IsNullOrEmpty(refDir) || !Directory.Exists(refDir))
            {
                lblRefStatus.Text = "No reference folder selected.";
                lblRefStatus.ForeColor = RefText;
                return;
            }

            string patientId = _openPatient != null ? _openPatient.Id : null;
            if (chkUseStep1Ref.Checked && string.IsNullOrEmpty(patientId))
                patientId = DiscoverPatientIdFromFolder(refDir);

            string ctDir = GetAutoPhaseCtDir(refDir, patientId)
                        ?? FindExportedCtDir(refDir, "CT_4DCT_50p")
                        ?? FindAnyCtDir(refDir);
            string rtstructPath = GetAutoPhaseRtstructPath(refDir, patientId)
                               ?? FindRtstructInDirectory(refDir);

            if (ctDir != null && !string.IsNullOrEmpty(rtstructPath))
            {
                lblRefStatus.Text = string.Format("Found CT ({0}) and RTSTRUCT ({1}).", Path.GetFileName(ctDir), Path.GetFileName(rtstructPath));
                lblRefStatus.ForeColor = RefSuccess;
            }
            else if (ctDir != null)
            {
                if (_openPatient == null && string.IsNullOrEmpty(rtstructPath))
                {
                    lblRefStatus.Text = string.Format("Found CT ({0}); RTSTRUCT not found. Connect Eclipse to auto-fetch, or select a folder containing an RTSTRUCT.", Path.GetFileName(ctDir));
                    lblRefStatus.ForeColor = RefError;
                }
                else
                {
                    lblRefStatus.Text = string.Format("Found CT ({0}); RTSTRUCT not found, will auto-fetch from Eclipse 50% StructureSet.", Path.GetFileName(ctDir));
                    lblRefStatus.ForeColor = RefText;
                }
            }
            else
            {
                lblRefStatus.Text = "No CT series found in the selected folder.";
                lblRefStatus.ForeColor = RefError;
            }
        }

        private void BtnProcessBdf_Click(object sender, EventArgs e)
        {
            if (_worker.IsBusy) { MessageBox.Show("Another operation is already in progress."); return; }
            string bdfPath = txtBdfPath.Text.Trim();
            if (!File.Exists(bdfPath)) { MessageBox.Show("Please select a valid BDF file."); return; }
            if (string.IsNullOrWhiteSpace(txtSharedFolder.Text)) { MessageBox.Show("Please select an output folder."); return; }
            if (chkAutoImport.Checked && !chkOutputRtstruct.Checked)
            {
                MessageBox.Show("Auto C-STORE is enabled but RTSTRUCT output is disabled. Please enable Output RTSTRUCT or disable auto C-STORE.");
                return;
            }

            // Resolve the single reference folder that contains CT + RTSTRUCT
            string patientId = _openPatient != null ? _openPatient.Id : null;
            string referenceDir = chkUseStep1Ref.Checked ? txtSharedFolder.Text.Trim() : txtReferenceDir.Text.Trim();
            if (string.IsNullOrEmpty(referenceDir) || !Directory.Exists(referenceDir))
            {
                MessageBox.Show("Please select a valid reference DICOM folder.");
                return;
            }

            // If Step 1 reference is selected but no patient is open, try to infer the patient ID from the folder structure.
            if (chkUseStep1Ref.Checked && string.IsNullOrEmpty(patientId))
                patientId = DiscoverPatientIdFromFolder(referenceDir);

            if (chkUseStep1Ref.Checked && string.IsNullOrEmpty(GetAutoPhaseCtDir(referenceDir, patientId)))
            {
                MessageBox.Show("Step 1 exported reference data not found. Please run Step 1 export first or switch to manual reference folder.");
                return;
            }

            // Ensure patientId is available for the output directory name
            if (string.IsNullOrEmpty(patientId) && _openPatient != null)
                patientId = _openPatient.Id;
            string outDirSuffix = string.IsNullOrEmpty(patientId) ? "BDF_Output" : "BDF_Output_" + patientId;
            string outDir = Path.Combine(txtSharedFolder.Text.Trim(), outDirSuffix);
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Unable to create output directory: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Remember the BDF path.
            _userSettings.LastBdfPath = bdfPath;
            SettingsManager.Save(_userSettings);

            string ctDir = GetAutoPhaseCtDir(referenceDir, patientId)
                        ?? FindExportedCtDir(referenceDir, "CT_4DCT_50p")
                        ?? FindAnyCtDir(referenceDir);
            string rtstructPath = GetAutoPhaseRtstructPath(referenceDir, patientId)
                               ?? FindRtstructInDirectory(referenceDir);
            string source = chkUseStep1Ref.Checked ? "Step 1 output" : "Manual folder";

            if (string.IsNullOrEmpty(ctDir) || !Directory.Exists(ctDir))
            {
                MessageBox.Show("No reference CT series found in the selected folder.");
                return;
            }
            Log(string.Format("Using reference CT [{0}]: {1}", source, ctDir));

            string lungContoursJson = null;
            if (!string.IsNullOrEmpty(rtstructPath) && File.Exists(rtstructPath))
            {
                lblRefStatus.Text = string.Format("Using RTSTRUCT: {0}.", Path.GetFileName(rtstructPath));
                lblRefStatus.ForeColor = RefSuccess;
                Log(string.Format("Using reference RTSTRUCT: {0}", rtstructPath));
            }
            else
            {
                rtstructPath = null;
                if (_openPatient == null)
                {
                    lblRefStatus.Text = "RTSTRUCT not found and Eclipse is not connected. Please connect Eclipse or select a reference folder that contains an RTSTRUCT.";
                    lblRefStatus.ForeColor = RefError;
                    Log("RTSTRUCT not found; cannot auto-fetch from Eclipse because no patient is connected.");
                }
                else
                {
                    lblRefStatus.Text = "RTSTRUCT not found; lung contours will be auto-fetched from the 50% phase StructureSet.";
                    lblRefStatus.ForeColor = RefText;

                    // Auto-fetch the lung contours from the Eclipse 50% phase
                    // StructureSet (the registration reference).
                    ImageInfo phase50Info = _images.FirstOrDefault(i => i.Phase == 50);
                    StructureSet refSs = FindReferenceStructureSet(phase50Info);
                    Structure lungL = null;
                    Structure lungR = null;
                    List<Structure> gtvs = null;
                    if (refSs != null && TryFindLungStructures(refSs, out lungL, out lungR, out gtvs))
                    {
                        lungContoursJson = ExportLungContoursToJson(refSs, lungL, lungR, gtvs, outDir);
                        Log(string.Format("Auto-linked reference structure set: {0}, exported lung contour JSON: {1}", refSs.Id, lungContoursJson));
                    }
                    else
                    {
                        Log("No 50% phase lung structure set found in Eclipse; BDF processing will fail.");
                    }
                }
            }

            // Ventilation metric selection (design V2, section 3.2)
            string metric = radioMetricHu.Checked ? "hu"
                : (radioMetricBoth.Checked ? "both" : "jacobian");
            string inspDir = null;
            if (metric != "jacobian")
            {
                // The HU metric needs the inspiration CT: resolve the Step 1
                // 0% phase export, falling back to sibling phase folders.
                // Phase_0\CT is the export layout of BOTH Step-1 branches.
                // GetAutoPhaseCtDir is deliberately NOT used here: it returns
// Derive the inspiration CT from the RESOLVED reference CT directory:
                // both Step-1 branches export <patient>/Phase_0/CT next to
                // <patient>/Phase_50/CT, so the sibling of the reference is
                // the inspiration series. GetAutoPhaseCtDir is deliberately
                // NOT used here: it returns the 50% (expiration) reference.
                try
                {
                    DirectoryInfo refParent = Directory.GetParent(ctDir);                    // .../Phase_50/CT -> .../Phase_50
                    DirectoryInfo patientDir = refParent != null ? refParent.Parent : null;  // -> <patient>
                    string root = patientDir != null ? patientDir.FullName : referenceDir;
                    string sibling = Path.Combine(root, "Phase_0", "CT");
                    if (Directory.Exists(sibling) && Directory.GetFiles(sibling, "*.dcm").Length > 0)
                        inspDir = sibling;
                }
                catch { }
                if (inspDir == null && !string.IsNullOrEmpty(patientId))
                {
                    string byPatient = Path.Combine(referenceDir, patientId, "Phase_0", "CT");
                    if (Directory.Exists(byPatient) && Directory.GetFiles(byPatient, "*.dcm").Length > 0)
                        inspDir = byPatient;
                }
                if (inspDir == null)
                    inspDir = FindExportedCtDir(referenceDir, "Phase_0")
                        ?? FindExportedCtDir(referenceDir, "CT_4DCT_0p");
                if (string.IsNullOrEmpty(inspDir) || !Directory.Exists(inspDir))
                {
                    MessageBox.Show(
                        "The HU (density) metric requires the inspiration CT series. " +
                        "Export both phases in Step 1 (Velocity branch) or place the 0% phase in the reference folder.",
                        "Inspiration CT missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Log("Inspiration CT for the HU metric: " + inspDir);
            }

            var config = new BdfProcessingConfig
            {
                BdfPath = bdfPath,
                DvfPath = bdfPath,
                DvfType = "auto",
                Metric = metric,
                InspCtDir = inspDir,
                CtDir = ctDir,
                RtstructPath = rtstructPath,
                LungContoursJson = lungContoursJson,
                OutputDir = outDir,
                Options = new BdfOptions
                {
                    SmoothSigmaMm = (double)numSmoothSigma.Value,
                    CtSigmaMm2 = (double)numCtSigma.Value,
                    ExcludeGtv = chkExcludeGtv.Checked,
                    FunctionalMethod = "percentile",
                    PercentileThreshold = (int)numPercentile.Value,
                    OutputRtstruct = chkOutputRtstruct.Checked,
                    OutputJacobianNifti = chkOutputJacobian.Checked,
                    OutputVentilationNifti = chkOutputVentilation.Checked
                }
            };

            SetBusy(true);
            _worker.RunWorkerAsync(config);
        }

        /// <summary>
        /// Find the structure set attached to the SELECTED image. A series can
        /// carry several images (e.g. the original CT_50_1 next to a
        /// previously pushed-back "-E" copy whose structure set holds stale
        /// remapped contours), so a series-level match must never win over
        /// the image-level match. Order: exact image identity (ESAPI UID),
        /// then Aria image ID, then — only when no image-level match exists
        /// at all — a same-series structure set, skipping pushed-back "-E"
        /// copies and preferring sets that contain lung contours.
        /// </summary>
        private StructureSet FindReferenceStructureSet(ImageInfo info)
        {
            if (_openPatient == null || info == null || _openPatient.StructureSets == null)
                return null;

            VMS.TPS.Common.Model.API.Image srcImg = info.ESAPIImage;
            if (srcImg == null && info.SliceImages != null && info.SliceImages.Count > 0)
                srcImg = info.SliceImages[0];
            if (srcImg == null)
                return null;

            string imageUid = srcImg.UID;
            string seriesUid = srcImg.Series != null ? srcImg.Series.UID : null;
            bool hasImageUid = !string.IsNullOrEmpty(imageUid);
            bool hasImageId = !string.IsNullOrEmpty(info.Id);

            StructureSet seriesFallback = null;
            foreach (StructureSet ss in _openPatient.StructureSets)
            {
                if (ss == null || ss.Image == null)
                    continue;
                VMS.TPS.Common.Model.API.Image ssImg = ss.Image;
                if (ssImg == srcImg)
                    return ss;
                if (hasImageUid && !string.IsNullOrEmpty(ssImg.UID) && ssImg.UID == imageUid)
                    return ss;
                if (hasImageId && ssImg.Id == info.Id)
                    return ss;

                // Same-series candidates are only a last resort, and never
                // the structure set of a pushed-back copy.
                if (!string.IsNullOrEmpty(seriesUid) && ssImg.Series != null && ssImg.Series.UID == seriesUid
                    && !IsPushedBackImage(ssImg.Id))
                {
                    Structure lungL, lungR;
                    List<Structure> gtvs;
                    if (seriesFallback == null || TryFindLungStructures(ss, out lungL, out lungR, out gtvs))
                        seriesFallback = ss;
                }
            }
            return seriesFallback;
        }

        /// <summary>True for images this application pushed back to Eclipse
        /// (named "&lt;id&gt;-E"); their structure sets hold remapped copies,
        /// never the source contours.</summary>
        private static bool IsPushedBackImage(string imageId)
        {
            return !string.IsNullOrEmpty(imageId) && imageId.Trim().EndsWith("-E", StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeStructureName(string name)
        {
            if (name == null)
                return string.Empty;
            return name.ToUpperInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        private bool TryFindLungStructures(StructureSet ss, out Structure lungL, out Structure lungR, out List<Structure> gtvs)
        {
            lungL = null;
            lungR = null;
            gtvs = new List<Structure>();
            if (ss == null || ss.Structures == null)
                return false;

            foreach (Structure s in ss.Structures)
            {
                if (s == null)
                    continue;
                string n = NormalizeStructureName(s.Id);
                if (n == "LUNGL" || n == "LEFTLUNG" || n == "LUNGLEFT")
                    lungL = s;
                else if (n == "LUNGR" || n == "RIGHTLUNG" || n == "LUNGRIGHT")
                    lungR = s;
                else if (n.StartsWith("GTV"))
                    gtvs.Add(s);
            }
            return lungL != null && lungR != null;
        }

        private string ExportLungContoursToJson(StructureSet ss, Structure lungL, Structure lungR, List<Structure> gtvs, string outputDir)
        {
            var structures = new Dictionary<string, object>();
            IEnumerable<Structure> toExport = new Structure[] { lungL, lungR }
                .Concat(gtvs ?? new List<Structure>())
                .Where(s => s != null)
                .Distinct();

            foreach (Structure s in toExport)
            {
                var sliceContours = new Dictionary<string, object>();
                for (int z = 0; z < ss.Image.ZSize; z++)
                {
                    VVector[][] contours = s.GetContoursOnImagePlane(z);
                    if (contours == null || contours.Length == 0)
                        continue;

                    var loops = new List<List<List<double>>>();
                    foreach (VVector[] contour in contours)
                    {
                        if (contour == null || contour.Length == 0)
                            continue;
                        var pts = new List<List<double>>();
                        foreach (VVector v in contour)
                            pts.Add(new List<double> { v.x, v.y, v.z });
                        loops.Add(pts);
                    }
                    if (loops.Count > 0)
                        sliceContours[z.ToString()] = loops;
                }
                if (sliceContours.Count > 0)
                    structures[s.Id] = sliceContours;
            }

            var root = new Dictionary<string, object>
            {
                { "reference_image_uid", ss.Image.UID ?? string.Empty },
                { "reference_series_uid", (ss.Image.Series != null && ss.Image.Series.UID != null) ? ss.Image.Series.UID : string.Empty },
                { "structures", structures }
            };

            string path = Path.Combine(outputDir, "lung_contours.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(root, Formatting.Indented));
            return path;
        }

        private async void BtnImportRtstruct_Click(object sender, EventArgs e)
        {
            if (_lastRtstructPaths.Count == 0 && (string.IsNullOrEmpty(_lastRtstructPath) || !File.Exists(_lastRtstructPath))) { MessageBox.Show("RTSTRUCT file does not exist."); return; }

            string daemonAe = txtDaemonAe.Text.Trim();
            string daemonIp = txtDaemonIp.Text.Trim();
            int daemonPort = (int)numDaemonPort.Value;
            string localAe = txtLocalAe.Text.Trim();
            int localPort = (int)numLocalPort.Value;

            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 ||
                string.IsNullOrEmpty(localAe) || localPort <= 0)
            {
                MessageBox.Show("Please fill in all DICOM DB Daemon settings before importing.");
                return;
            }

            btnImportRtstruct.Enabled = false;
            try
            {
                bool ok = await AutoImportRtstructAsync();
                if (ok)
                    MessageBox.Show("RTSTRUCT has been C-STORED to Eclipse.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show("RTSTRUCT C-STORE failed, please check the log.", "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Import failed: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log(string.Format("Import failed: {0}", ex.Message));
            }
            finally
            {
                btnImportRtstruct.Enabled = !string.IsNullOrEmpty(_lastRtstructPath) && File.Exists(_lastRtstructPath);
            }
        }

        private void BtnCancel_Click_REMOVED(object sender, EventArgs e)
        {
            if (_worker.IsBusy)
            {
                _worker.CancelAsync();
                Log("Cancellation requested...");
            }
        }

        // ================================================================
        //  Dual-engine branch (design V2)
        // ================================================================

        private void EngineRadio_CheckedChanged(object sender, EventArgs e)
        {
            // Fires from user interaction only (the default radio is checked
            // before its handler is wired), but stay null-safe during init.
            if (btnExport4DCT == null) return;

            bool eclipse = radioEngineEclipse.Checked;
            bool plas = radioEnginePlastimatch.Checked;
            btnExport4DCT.Visible = !eclipse && !plas;
            btnPrepareExpRef.Visible = eclipse;
            lblExpRefStatus.Visible = eclipse;

            bool plastiUi = radioEnginePlastimatch != null && plas;
            if (lblPlastiLambda != null) lblPlastiLambda.Visible = plastiUi;
            if (numPlastiLambda != null) numPlastiLambda.Visible = plastiUi;

            if (lblPlastiPreset != null) lblPlastiPreset.Visible = plastiUi;
            if (cboPlastiPreset != null) cboPlastiPreset.Visible = plastiUi;
            if (btnRunPlasti != null) btnRunPlasti.Visible = plastiUi;
            if (lblPlastiStatus != null) lblPlastiStatus.Visible = plastiUi;
            if (lblPlastiExe != null) lblPlastiExe.Visible = plastiUi;
            if (txtPlastiExe != null) txtPlastiExe.Visible = plastiUi;
            if (btnBrowsePlastiExe != null) btnBrowsePlastiExe.Visible = plastiUi;

            if (eclipse)
                btnPrepareExpRef.Enabled = _esapiApp != null && !_worker.IsBusy;
            else if (plas)
                btnRunPlasti.Enabled = _images.Count > 0 && !_worker.IsBusy;
            else
                btnExport4DCT.Enabled = _images.Count > 0 && !_worker.IsBusy;
        }

        private void BtnPrepareExpRef_Click(object sender, EventArgs e)
        {
            if (_worker.IsBusy) { MessageBox.Show("Another operation is already in progress."); return; }
            if (string.IsNullOrWhiteSpace(txtDaemonAe.Text) || string.IsNullOrWhiteSpace(txtDaemonIp.Text) ||
                (int)numDaemonPort.Value <= 0)
            {
                MessageBox.Show("DICOM DB Daemon settings are required to push the Exp reference back to Eclipse.");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtSharedFolder.Text) || !Directory.Exists(txtSharedFolder.Text))
            {
                MessageBox.Show("Please select a valid shared output folder first.");
                return;
            }

            // Both phases are exported exactly like the Velocity branch; the
            // only addition is the rebuilt ExpRef series for registration.
            var toExport = GetSelectedImagesToExport();
            var phaseGroups = toExport.GroupBy(i => i.Phase).ToList();
            var phase0Group = phaseGroups.FirstOrDefault(g => g.Key == 0);
            var phase50Group = phaseGroups.FirstOrDefault(g => g.Key == 50);
            if (phase0Group == null || phase0Group.Count() != 1)
            {
                MessageBox.Show("Select exactly one 0% (inspiration) series.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (phase50Group == null || phase50Group.Count() != 1)
            {
                MessageBox.Show("Select exactly one 50% (expiration) series.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var phases = new List<PhaseExportInfo>();
            foreach (ImageInfo info in toExport)
            {
                string seriesUid = GetSeriesUidForImageInfo(info);
                if (string.IsNullOrEmpty(seriesUid))
                {
                    MessageBox.Show(string.Format("Cannot obtain SeriesInstanceUID for phase {0}.", info.SeriesId));
                    return;
                }
                phases.Add(new PhaseExportInfo
                {
                    Phase = info.Phase,
                    CtSeriesUid = seriesUid,
                    StudyUid = GetStudyUidForImageInfo(info)
                });
            }

            // Link each phase's StructureSet (same convention as the Velocity branch).
            foreach (int phaseVal in new int[] { 0, 50 })
            {
                ImageInfo info = toExport.FirstOrDefault(i => i.Phase == phaseVal);
                if (info == null) continue;
                StructureSet ss = FindReferenceStructureSet(info);
                PhaseExportInfo pe = phases.FirstOrDefault(p => p.Phase == phaseVal);
                if (ss != null && !string.IsNullOrEmpty(ss.UID) && pe != null)
                {
                    pe.RtstructSeriesUid = ss.UID;
                    Log(string.Format("Linked {0}% phase StructureSet: '{1}' (on image '{2}')", phaseVal, ss.Id, ss.Image != null ? ss.Image.Id : "?"));
                }
                else
                {
                    Log(string.Format("No {0}% phase StructureSet found; RTSTRUCT will not be exported for this phase.", phaseVal));
                }
            }

            ExpRefArgs args = new ExpRefArgs();
            args.DaemonAETitle = txtDaemonAe.Text.Trim();
            args.DaemonIP = txtDaemonIp.Text.Trim();
            args.DaemonPort = (int)numDaemonPort.Value;
            args.LocalAETitle = txtLocalAe.Text.Trim();
            args.LocalPort = (int)numLocalPort.Value;
            args.PatientId = _openPatient != null ? _openPatient.Id : txtPatientId.Text.Trim();
            args.Phases = phases;
            args.BaseDir = txtSharedFolder.Text.Trim();
            args.TargetThicknessMm = 2.5; // degenerate fallback only; ExpRefBuilder uses the series max spacing (Velocity parity)
            ImageInfo expirationInfo = toExport.FirstOrDefault(i => i.Phase == 50);
            args.ExpirationImageId = expirationInfo != null ? expirationInfo.Id : null;

            lblExpRefStatus.Text = "Preparing (E1-E4)...";
            lblExpRefStatus.ForeColor = RefText;
            Log("Eclipse branch: exporting both phases and building the Exp reference...");
            SetBusy(true);
            _worker.RunWorkerAsync(args);
        }

        // ================================================================
        //  Plastimatch engine branch (third engine)
        // ================================================================

        private void BtnBrowsePlastiExe_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "plastimatch.exe|plastimatch.exe|Executable files|*.exe|All files|*.*";
                if (!string.IsNullOrEmpty(txtPlastiExe.Text) && File.Exists(txtPlastiExe.Text))
                    dlg.InitialDirectory = Path.GetDirectoryName(txtPlastiExe.Text);
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtPlastiExe.Text = dlg.FileName;
                    SaveDaemonSettings();
                }
            }
        }

        private void BtnRunPlasti_Click(object sender, EventArgs e)
        {
            if (_worker.IsBusy) { MessageBox.Show("Another operation is already in progress."); return; }
            if (_openPatient == null) { MessageBox.Show("Please search a patient first."); return; }

            string outDir = txtSharedFolder.Text.Trim();
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            {
                MessageBox.Show("Please select a valid output folder.");
                return;
            }

            string daemonAe = txtDaemonAe.Text.Trim();
            string daemonIp = txtDaemonIp.Text.Trim();
            int daemonPort = (int)numDaemonPort.Value;
            string localAe = txtLocalAe.Text.Trim();
            int localPort = (int)numLocalPort.Value;
            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 ||
                string.IsNullOrEmpty(localAe) || localPort <= 0)
            {
                MessageBox.Show("Please fill in all DICOM DB Daemon settings (Daemon AE, IP, Port, Local AE, Local Port).");
                return;
            }

            string exe = PlastimatchRunner.FindDefaultExecutable(txtPlastiExe.Text.Trim());
            if (exe == null)
            {
                MessageBox.Show(
                    "plastimatch.exe was not found.\n\nInstall Plastimatch (validated version 1.9.0) or set the " +
                    "correct path next to the engine selector.",
                    "Plastimatch not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            txtPlastiExe.Text = exe;

            // Phase selection: identical contract to the Velocity branch.
            var toExport = GetSelectedImagesToExport();
            if (toExport.Count == 0) { MessageBox.Show("Please select at least one image series to export."); return; }

            var phaseGroups = toExport.GroupBy(i => i.Phase).ToList();
            var phase0Group = phaseGroups.FirstOrDefault(g => g.Key == 0);
            var phase50Group = phaseGroups.FirstOrDefault(g => g.Key == 50);
            if (phase0Group == null || phase0Group.Count() != 1)
            {
                MessageBox.Show("Select exactly one 0% (inspiration) series.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (phase50Group == null || phase50Group.Count() != 1)
            {
                MessageBox.Show("Select exactly one 50% (expiration) series - the registration reference.", "Export Selection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var phases = new List<PhaseExportInfo>();
            foreach (ImageInfo info in toExport)
            {
                string seriesUid = GetSeriesUidForImageInfo(info);
                if (string.IsNullOrEmpty(seriesUid))
                {
                    MessageBox.Show(string.Format("Cannot obtain SeriesInstanceUID for phase {0}.", info.SeriesId));
                    return;
                }
                phases.Add(new PhaseExportInfo
                {
                    Phase = info.Phase,
                    CtSeriesUid = seriesUid,
                    StudyUid = GetStudyUidForImageInfo(info)
                });
            }

            // Link each phase's StructureSet (same convention as the other branches):
            // the 50% RTSTRUCT supplies the lung fixed_roi, Step 2 needs both.
            foreach (int phaseVal in new int[] { 0, 50 })
            {
                ImageInfo info = toExport.FirstOrDefault(i => i.Phase == phaseVal);
                if (info == null) continue;
                StructureSet ss = FindReferenceStructureSet(info);
                PhaseExportInfo pe = phases.FirstOrDefault(p => p.Phase == phaseVal);
                if (ss != null && !string.IsNullOrEmpty(ss.UID) && pe != null)
                {
                    pe.RtstructSeriesUid = ss.UID;
                    Log(string.Format("Linked {0}% phase StructureSet: '{1}' (on image '{2}')", phaseVal, ss.Id, ss.Image != null ? ss.Image.Id : "?"));
                }
                else
                {
                    Log(string.Format("No {0}% phase StructureSet found; RTSTRUCT will not be exported for this phase.", phaseVal));
                }
            }

            _userSettings.LastOutputDirectory = outDir;
            SaveDaemonSettings();

            var args = new PlastiRunArgs();
            args.DaemonAETitle = daemonAe;
            args.DaemonIP = daemonIp;
            args.DaemonPort = daemonPort;
            args.LocalAETitle = localAe;
            args.LocalPort = localPort;
            args.PatientId = _openPatient.Id;
            args.Phases = phases;
            args.BaseDir = outDir;
            args.ExecutablePath = exe;
            args.Lambda = (double)numPlastiLambda.Value;
            args.UseRoi = true;
            args.PresetIndex = cboPlastiPreset.SelectedIndex >= 0 ? cboPlastiPreset.SelectedIndex : 0;

            // Keep the Velocity-branch behaviour of pushing a locally
            // resampled CT/RTSTRUCT back into Eclipse.
            _lastDaemonExportArgs = new DaemonExportArgs
            {
                BaseDir = outDir,
                PatientId = args.PatientId,
                Phases = phases,
                DaemonAETitle = daemonAe,
                DaemonIP = daemonIp,
                DaemonPort = daemonPort,
                LocalAETitle = localAe,
                LocalPort = localPort
            };

            lblPlastiStatus.Text = "Exporting phases + registering (P0-P5)...";
            lblPlastiStatus.ForeColor = RefText;
            Log(string.Format(
                "Plastimatch branch: exporting both phases, then registering fixed=Exp(50%) moving=Insp(0%) ({0}, lambda={1})...",
                cboPlastiPreset.SelectedItem, args.Lambda.ToString("0.###")));
            SetBusy(true);
            _worker.RunWorkerAsync(args);
        }

        private void RunPlastimatchWorker(PlastiRunArgs args, DoWorkEventArgs e)
        {
            // P0: export both phases (CT + RTSTRUCT, uniformity resampling)
            // exactly like the Velocity branch, into <Base>/<Pid>/Phase_xx.
            DaemonExportArgs daemonArgs = new DaemonExportArgs();
            daemonArgs.BaseDir = args.BaseDir;
            daemonArgs.PatientId = args.PatientId;
            daemonArgs.Phases = args.Phases;
            daemonArgs.DaemonAETitle = args.DaemonAETitle;
            daemonArgs.DaemonIP = args.DaemonIP;
            daemonArgs.DaemonPort = args.DaemonPort;
            daemonArgs.LocalAETitle = args.LocalAETitle;
            daemonArgs.LocalPort = args.LocalPort;
            _worker.ReportProgress(0, "P0: exporting both phases (CT + RTSTRUCT)...");
            ExportPhasesViaDaemon(daemonArgs, e);

            // P1-P5: six-stage B-spline registration on the exported series
            // (fixed = expiration reference, moving = inspiration).
            string patDir = Path.Combine(args.BaseDir, args.PatientId);
            string rtstruct = FindRtstructInDirectory(Path.Combine(patDir, "Phase_50", "RTSTRUCT"));

            PlastimatchRunArgs runArgs = new PlastimatchRunArgs();
            runArgs.ExecutablePath = args.ExecutablePath;
            runArgs.FixedCtDir = Path.Combine(patDir, "Phase_50", "CT");
            runArgs.MovingCtDir = Path.Combine(patDir, "Phase_0", "CT");
            runArgs.RoiRtstructPath = rtstruct;
            runArgs.UseRoi = args.UseRoi;
            runArgs.Lambda = args.Lambda;
            runArgs.PresetIndex = args.PresetIndex;
            runArgs.OutputDir = Path.Combine(patDir, "Plastimatch");
            runArgs.Log = msg => _worker.ReportProgress(0, msg);

            PlastimatchResult res = PlastimatchRunner.Run(runArgs);
            _lastPlastiVfPath = res.VfPath;

            e.Result = "Plastimatch registration ready.\n"
                + "DVF: " + res.VfPath + "\n"
                + string.Format("Grid {0}x{1}x{2}, elapsed {3:F0} s.", res.VfNx, res.VfNy, res.VfNz, res.ElapsedSeconds)
                + "\n\nStep 2 has been pointed at vf.mha with sigma = 5 mm (validated robust region)."
                + "\nAdjust the options if needed, then click Process DVF.";
        }

        private class PlastiRunArgs
        {
            public string DaemonAETitle { get; set; }
            public string DaemonIP { get; set; }
            public int DaemonPort { get; set; }
            public string LocalAETitle { get; set; }
            public int LocalPort { get; set; }
            public string PatientId { get; set; }
            public List<PhaseExportInfo> Phases { get; set; }
            public string BaseDir { get; set; }
            public string ExecutablePath { get; set; }
            public double Lambda { get; set; }
            public bool UseRoi { get; set; }
            public int PresetIndex { get; set; }
        }

        private void RunExpRefWorker(ExpRefArgs args, DoWorkEventArgs e)
        {
            DicomDaemonService service = new DicomDaemonService(
                args.DaemonAETitle, args.DaemonIP, args.DaemonPort,
                args.LocalAETitle, args.LocalPort,
                msg => _worker.ReportProgress(0, msg));

            IDicomServer server = null;
            try
            {
                server = service.StartServerAsync().GetAwaiter().GetResult();

                DaemonExportArgs shellArgs = new DaemonExportArgs();
                shellArgs.PatientId = args.PatientId;

                // E1: export both phases (CT + RTSTRUCT) into the same folder
                // layout as the Velocity branch. Originals are kept as-is -
                // the ExpRef series takes over the slice-uniformization role,
                // so no Velocity-style resampling is performed here.
                foreach (PhaseExportInfo phase in args.Phases)
                {
                    string phaseName = phase.Phase == 0 ? "Phase_0" : "Phase_50";
                    _worker.ReportProgress(0, string.Format("E1: exporting {0} (C-MOVE)...", phaseName));

                    string studyUid = phase.StudyUid;
                    if (string.IsNullOrEmpty(studyUid))
                        studyUid = service.FindStudyUidAsync(args.PatientId, phase.CtSeriesUid).GetAwaiter().GetResult();
                    if (string.IsNullOrEmpty(studyUid))
                        throw new InvalidOperationException("Unable to find StudyInstanceUID for " + phaseName + ".");

                    string tempCtDir = Path.Combine(args.BaseDir, args.PatientId, phaseName,
                        "_ct_temp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempCtDir);
                    List<string> ctFiles = service.MoveSeriesWithServerAsync(
                        server, args.PatientId, studyUid, phase.CtSeriesUid, tempCtDir).GetAwaiter().GetResult();
                    _worker.ReportProgress(0, string.Format("  {0} CT received {1} file(s)",
                        phaseName, ctFiles == null ? 0 : ctFiles.Count));
                    if (ctFiles == null || ctFiles.Count == 0)
                        throw new InvalidOperationException("C-MOVE returned no CT slices for " + phaseName + ".");

                    string ctOutDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "CT");
                    if (Directory.Exists(ctOutDir)) Directory.Delete(ctOutDir, true);
                    Directory.Move(tempCtDir, ctOutDir);

                    if (!string.IsNullOrEmpty(phase.RtstructSeriesUid))
                    {
                        string rtstructOutDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "RTSTRUCT");
                        ExportRtstructForPhase(service, server, shellArgs, phase, studyUid, rtstructOutDir);
                    }
                    else
                    {
                        _worker.ReportProgress(0, string.Format("  {0}: no StructureSet linked; RTSTRUCT not exported.", phaseName));
                    }
                }

                // E2: build the ExpRef series from the exported expiration CT
                // (new FoR + uniform rebuild at the series' max spacing). The
                // exported expiration RTSTRUCT is remapped onto the ExpRef
                // series and sent along with it - images imported together
                // with a structure are auto-reconstructed in Eclipse.
                string expCtDir = Path.Combine(args.BaseDir, args.PatientId, "Phase_50", "CT");
                string expRefDir = Path.Combine(args.BaseDir, args.PatientId, "ExpRef", "CT");
                string expRtstruct = FindRtstructInDirectory(
                    Path.Combine(args.BaseDir, args.PatientId, "Phase_50", "RTSTRUCT"));
                if (expRtstruct == null)
                    _worker.ReportProgress(0, "E2: no expiration RTSTRUCT exported; the ExpRef series may need manual reconstruction in Eclipse.");
                ExpRefResult res = ExpRefBuilder.Build(expCtDir, expRefDir, args.TargetThicknessMm,
                    msg => _worker.ReportProgress(0, msg), expRtstruct, args.ExpirationImageId);

                // E3: push the REBUILT series back in InstanceNumber order on
                // one association so Eclipse assembles it as a ready series.
                // Auto-reconstruction is controlled by which DICOM DB Daemon
                // the user points the Daemon AE/Port settings at.
                _worker.ReportProgress(0, string.Format(
                    "E3: pushing the rebuilt ExpRef series to {0}:{1} (ordered C-STORE)...",
                    args.DaemonAETitle, args.DaemonPort));
                int stored = service.SendSeriesInOrderAsync(res.Files).GetAwaiter().GetResult();
                _worker.ReportProgress(0, string.Format("E3: C-STORE {0}/{1} slices OK",
                    stored, res.Files.Count));

                // E3b: push the ExpRef RTSTRUCT (lung contours remapped to the
                // ExpRef series/FoR) together with the CT, so the daemon
                // receives CT + structure as a pair (auto-reconstruction).
                if (res.RtstructPath != null && File.Exists(res.RtstructPath))
                {
                    bool rtOk = service.SendSingleFileAsync(res.RtstructPath).GetAwaiter().GetResult();
                    if (!rtOk)
                        rtOk = service.SendSingleFileAsync(res.RtstructPath).GetAwaiter().GetResult();
                    _worker.ReportProgress(0, string.Format("E3b: ExpRef RTSTRUCT {0}",
                        rtOk ? "OK" : "FAILED"));
                }

                // E4: C-FIND verification.
                _worker.ReportProgress(0, "E4: verifying with C-FIND...");
                int count = service.FindSeriesInstanceCountAsync(args.PatientId, res.NewSeriesUid)
                    .GetAwaiter().GetResult();

                string summary = string.Format(
                    "E1 phases exported | E2 FoR ...{0} | {1:F2} mm | {2} slices{3} | E4 C-FIND {4}{5}",
                    res.NewFrameOfReferenceUid.Substring(res.NewFrameOfReferenceUid.Length - 8),
                    res.TargetThickness, res.OutputSlices,
                    res.Resampled ? "" : " (uniform input, z kept)",
                    count < 0 ? "n/a" : count.ToString(),
                    count >= 0 && count == res.Files.Count ? " = OK" : (count >= 0 ? " MISMATCH" : ""));

                e.Result = "Exp reference ready.\n" + summary
                    + "\n\nPer-patient folders: Phase_0 / Phase_50 / ExpRef under:\n"
                    + Path.Combine(args.BaseDir, args.PatientId)
                    + "\n\nNext (manual): M1 register ExpRef (fixed) to the inspiration series in Eclipse "
                    + "(rigid + deformable); M2 export the DR object to the shared folder; M3 continue in Step 2.";
            }
            finally
            {
                if (server != null)
                {
                    try { server.Stop(); } catch { }
                    try { ((IDisposable)server).Dispose(); } catch { }
                }
                DicomStoreContext.SetCurrent(null);
            }
        }

        private class ExpRefArgs
        {
            public string DaemonAETitle { get; set; }
            public string DaemonIP { get; set; }
            public int DaemonPort { get; set; }
            public string LocalAETitle { get; set; }
            public int LocalPort { get; set; }
            public string PatientId { get; set; }
            public List<PhaseExportInfo> Phases { get; set; }
            public string BaseDir { get; set; }
            public double TargetThicknessMm { get; set; }
            /// <summary>Aria image ID of the selected 50% series; names the pushed ExpRef ("&lt;id&gt;-E").</summary>
            public string ExpirationImageId { get; set; }
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                ExpRefArgs expRefArgs = e.Argument as ExpRefArgs;
                if (expRefArgs != null)
                {
                    RunExpRefWorker(expRefArgs, e);
                    return;
                }

                PlastiRunArgs plastiRunArgs = e.Argument as PlastiRunArgs;
                if (plastiRunArgs != null)
                {
                    RunPlastimatchWorker(plastiRunArgs, e);
                    return;
                }

                DaemonExportArgs daemonArgs = e.Argument as DaemonExportArgs;
                if (daemonArgs != null)
                {
                    RunDaemonExportWorker(daemonArgs, e);
                    return;
                }

                BdfProcessingConfig config = e.Argument as BdfProcessingConfig;
                if (config != null)
                {
                    RunBdfWorker(config, e);
                    return;
                }
            }
            catch (Exception ex)
            {
                e.Result = ex;
            }
        }

        private void RunDaemonExportWorker(DaemonExportArgs args, DoWorkEventArgs e)
        {
            ExportPhasesViaDaemon(args, e);
            e.Result = string.Format("Exported {0} phase(s) via DICOM Daemon to:\n{1}", args.Phases.Count, Path.Combine(args.BaseDir, args.PatientId));
        }

        /// <summary>
        /// Shared per-phase export core (C-MOVE CT, slice-uniformity
        /// resampling, RTSTRUCT retrieval), used by the Velocity branch and
        /// the Plastimatch branch.
        /// </summary>
        private void ExportPhasesViaDaemon(DaemonExportArgs args, DoWorkEventArgs e)
        {
            var service = new DicomDaemonService(
                args.DaemonAETitle, args.DaemonIP, args.DaemonPort,
                args.LocalAETitle, args.LocalPort,
                msg => _worker.ReportProgress(0, msg));

            // Share one local SCP across all phases so the same port is not
            // stopped and restarted repeatedly.
            IDicomServer server = null;
            args.ResamplingPerformed = false;
            args.ResampledCtFiles = new List<string>();
            args.ResampledRtstructPath = null;
            try
            {
                server = service.StartServerAsync().GetAwaiter().GetResult();

                foreach (PhaseExportInfo phase in args.Phases)
                {
                    if (_worker.CancellationPending) { e.Cancel = true; return; }

                    string phaseName = phase.Phase == 0 ? "Phase_0" : "Phase_50";
                    _worker.ReportProgress(0, string.Format("Exporting {0} ...", phaseName));

                    string studyUid = phase.StudyUid;
                    if (string.IsNullOrEmpty(studyUid))
                    {
                        _worker.ReportProgress(0, string.Format("  {0}: StudyInstanceUID not obtained from ESAPI, trying C-FIND...", phaseName));
                        studyUid = service.FindStudyUidAsync(args.PatientId, phase.CtSeriesUid).GetAwaiter().GetResult();
                    }
                    if (string.IsNullOrEmpty(studyUid))
                        throw new InvalidOperationException(string.Format("Unable to find StudyInstanceUID for {0}.", phaseName));

                    // Pull the CT into a temp directory and check slice spacing.
                    string tempCtDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "_ct_temp_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempCtDir);
                    List<string> ctFiles = service.MoveSeriesWithServerAsync(server, args.PatientId, studyUid, phase.CtSeriesUid, tempCtDir).GetAwaiter().GetResult();
                    _worker.ReportProgress(0, string.Format("  {0} CT received {1} file(s)", phaseName, ctFiles.Count));

                    string ctOutDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "CT");
                    string rtstructOutDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "RTSTRUCT");

                    // Check the slice spacing uniformity.
                    double maxSpacing, minSpacing;
                    bool uniform = CtResampler.IsUniform(tempCtDir, out maxSpacing, out minSpacing);
                    bool needsResample = !uniform;

                    if (needsResample)
                    {
                        _worker.ReportProgress(0, string.Format("  {0}: slice spacing non-uniform (max={1:F3} min={2:F3}), resampling to uniform {3:F3}mm...",
                            phaseName, maxSpacing, minSpacing, maxSpacing));

                        // Export the RTSTRUCT to a temp location first, if any.
                        string tempRtstructPath = null;
                        if (phase.Phase == 0 || (phase.Phase == 50 && !string.IsNullOrEmpty(phase.RtstructSeriesUid)))
                        {
                            string tempRtDir = Path.Combine(args.BaseDir, args.PatientId, phaseName, "_rt_temp_" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(tempRtDir);
                            ExportRtstructForPhase(service, server, args, phase, studyUid, tempRtDir);
                            string[] rtFiles = Directory.GetFiles(tempRtDir, "*.dcm");
                            if (rtFiles.Length > 0)
                                tempRtstructPath = rtFiles[0];
                        }

                        // Resample the CT and remap the RTSTRUCT.
                        string outRtstructPath = tempRtstructPath != null
                            ? Path.Combine(rtstructOutDir, string.Format("RTSTRUCT_{0}.dcm", Guid.NewGuid().ToString("N")))
                            : null;
                        List<string> resampledFiles = CtResampler.Resample(tempCtDir, tempRtstructPath, ctOutDir, outRtstructPath,
                            msg => _worker.ReportProgress(0, msg));
                        _worker.ReportProgress(0, string.Format("  {0} resampled to {1} slices (spacing={2:F3}mm)", phaseName, resampledFiles.Count, maxSpacing));

                        args.ResamplingPerformed = true;
                        args.ResampledCtFiles.AddRange(resampledFiles);
                        if (outRtstructPath != null && File.Exists(outRtstructPath))
                            args.ResampledRtstructPath = outRtstructPath;

                        // Clean up the temp directories.
                        try { Directory.Delete(tempCtDir, true); } catch { }
                        try { if (Directory.Exists(Path.Combine(args.BaseDir, args.PatientId, phaseName, "_rt_temp_")))
                                Directory.Delete(Path.GetDirectoryName(tempRtstructPath), true); } catch { }
                    }
                    else
                    {
                        _worker.ReportProgress(0, string.Format("  {0}: slice spacing uniform (max={1:F3} min={2:F3}), no resampling needed", phaseName, maxSpacing, minSpacing));

                        // Move the CT from the temp directory to its destination.
                        if (Directory.Exists(ctOutDir))
                            Directory.Delete(ctOutDir, true);
                        Directory.Move(tempCtDir, ctOutDir);

                        // Export the RTSTRUCT normally.
                        if (phase.Phase == 0 || (phase.Phase == 50 && !string.IsNullOrEmpty(phase.RtstructSeriesUid)))
                        {
                            ExportRtstructForPhase(service, server, args, phase, studyUid, rtstructOutDir);
                        }
                    }
                }
            }
            finally
            {
                if (server != null)
                {
                    try { server.Stop(); } catch { }
                    try { ((IDisposable)server).Dispose(); } catch { }
                }
                DicomStoreContext.SetCurrent(null);
            }
        }

        /// <summary>
        /// Export RTSTRUCT for a given phase using the existing retry logic.
        /// </summary>
        private void ExportRtstructForPhase(DicomDaemonService service, IDicomServer server,
            DaemonExportArgs args, PhaseExportInfo phase, string studyUid, string rtstructOutDir)
        {
            List<string> rtFiles = null;

            // 1. C-FIND
            string rtstructUid = service.FindRtstructSeriesUidAsync(args.PatientId, studyUid, phase.RtstructSeriesUid).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(rtstructUid))
            {
                rtFiles = service.MoveSeriesWithServerAsync(server, args.PatientId, studyUid, rtstructUid, rtstructOutDir).GetAwaiter().GetResult();
                if (rtFiles.Count > 0)
                {
                    _worker.ReportProgress(0, string.Format("  RTSTRUCT (C-FIND) received {0} file(s)", rtFiles.Count));
                    return;
                }
            }

            // 2. Direct C-MOVE
            if ((rtFiles == null || rtFiles.Count == 0) && !string.IsNullOrEmpty(phase.RtstructSeriesUid))
            {
                _worker.ReportProgress(0, "  C-FIND could not locate RTSTRUCT; trying direct C-MOVE...");
                try
                {
                    rtFiles = service.MoveSeriesWithServerAsync(server, args.PatientId, studyUid, phase.RtstructSeriesUid, rtstructOutDir).GetAwaiter().GetResult();
                    if (rtFiles.Count > 0)
                    {
                        _worker.ReportProgress(0, string.Format("  Direct C-MOVE RTSTRUCT succeeded, received {0} file(s)", rtFiles.Count));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _worker.ReportProgress(0, string.Format("  Direct C-MOVE RTSTRUCT failed: {0}", ex.Message));
                }
            }

            // 3. Study-level fallback
            if (rtFiles == null || rtFiles.Count == 0)
            {
                _worker.ReportProgress(0, "  Trying Study-level C-MOVE to retrieve RTSTRUCT...");
                try
                {
                    rtFiles = service.MoveStudyAndPickRtstructAsync(server, args.PatientId, studyUid, rtstructOutDir).GetAwaiter().GetResult();
                    if (rtFiles.Count > 0)
                        _worker.ReportProgress(0, string.Format("  Study-level C-MOVE extracted RTSTRUCT successfully, received {0} file(s)", rtFiles.Count));
                    else
                        _worker.ReportProgress(0, "  Study-level C-MOVE still could not find RTSTRUCT.");
                }
                catch (Exception ex)
                {
                    _worker.ReportProgress(0, string.Format("  Study-level C-MOVE failed: {0}", ex.Message));
                }
            }
        }

        private void RunBdfWorker(BdfProcessingConfig config, DoWorkEventArgs e)
        {
            _worker.ReportProgress(0, "Starting BDF processing...");
            BdfProcessor processor = new BdfProcessor(msg => _worker.ReportProgress(0, msg));
            JObject result = processor.Run(config);
            e.Result = result;
        }

        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string msg = e.UserState as string;
            if (!string.IsNullOrEmpty(msg))
                Log(msg);
        }

        private async void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            try
            {
                if (e.Cancelled)
                {
                    Log("Operation cancelled.");
                    ResetDirQa("DIR QA unavailable: operation was cancelled.");
                    return;
                }

                if (e.Error != null)
                {
                    string msg = e.Error is TimeoutException ?
                        string.Format("Operation timed out: {0}", e.Error.Message) :
                        string.Format("Operation failed: {0}", e.Error.Message);
                    MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Log(msg);
                    ResetDirQa("DIR QA unavailable: operation failed.");
                    return;
                }

                Exception ex = e.Result as Exception;
                if (ex != null)
                {
                    MessageBox.Show(string.Format("Operation failed: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Log(string.Format("Operation failed: {0}", ex.Message));
                    ResetDirQa("DIR QA unavailable: operation failed.");
                    return;
                }

                string exportMsg = e.Result as string;
                if (exportMsg != null)
                {
                    MessageBox.Show(exportMsg, "Complete");
                    Log("Export complete.");
                    if (exportMsg.StartsWith("Exp reference ready") && lblExpRefStatus != null)
                    {
                        string firstLine = exportMsg.Contains(NLq) ? exportMsg.Substring(0, exportMsg.IndexOf(NLq)) : exportMsg;
                        lblExpRefStatus.Text = firstLine;
                        lblExpRefStatus.ForeColor = RefSuccess;
                    }

                    if (exportMsg.StartsWith("Plastimatch registration ready"))
                    {
                        if (lblPlastiStatus != null)
                        {
                            lblPlastiStatus.Text = "Registration complete.";
                            lblPlastiStatus.ForeColor = RefSuccess;
                        }
                        if (!string.IsNullOrEmpty(_lastPlastiVfPath) && File.Exists(_lastPlastiVfPath))
                        {
                            // Hand the DVF to Step 2 like any other DVF file:
                            // vf.mha carries the same target->source semantics
                            // as a Velocity BDF, so processing is identical.
                            txtBdfPath.Text = _lastPlastiVfPath;
                            _userSettings.LastBdfPath = _lastPlastiVfPath;
                            SettingsManager.Save(_userSettings);
                            // Keep the user's smoothing setting; do NOT override.
                            // All engines use the same default sigma = 2.0 mm
                            // for fair comparison.
                            Log("Step 2 DVF file set to: " + _lastPlastiVfPath);
                            tabControl.SelectedIndex = 1;
                        }
                    }

                    // If the CT was resampled, C-STORE it back to Eclipse.
                    if (_lastDaemonExportArgs != null && _lastDaemonExportArgs.ResamplingPerformed)
                    {
                        await AutoCStoreResampledDataAsync(_lastDaemonExportArgs);
                    }
                    _lastDaemonExportArgs = null;
                    return;
                }

                JObject result = e.Result as JObject;
                if (result != null)
                {
                    if (result.Value<bool>("success"))
                    {
                        JToken outputs = result["outputs"];
                        _lastRtstructPaths = new List<string>();
                        if (outputs != null)
                        {
                            if (outputs["rtstruct"] != null)
                                _lastRtstructPaths.Add(outputs["rtstruct"].ToString());
                            if (outputs["hu_rtstruct"] != null)
                                _lastRtstructPaths.Add(outputs["hu_rtstruct"].ToString());
                        }
                        _lastRtstructPath = _lastRtstructPaths.Count > 0 ? _lastRtstructPaths[0] : null;
                        Log(string.Format("Processing complete. RTSTRUCT: {0}", string.Join(", ", _lastRtstructPaths.ToArray())));
                        JToken stats = result["stats"];
                        if (stats != null)
                            Log(string.Format("Statistics: {0}", stats));
                        btnImportRtstruct.Enabled = !string.IsNullOrEmpty(_lastRtstructPath) && File.Exists(_lastRtstructPath);

                        // DIR QA statistics (Jacobian determinant)
                        PopulateDirQa(result["dir_qa"]);
                        PopulateMetricQa(result["stats"]);

                        string msg = "BDF processing complete.";
                        if (chkAutoImport.Checked)
                        {
                            bool imported = await AutoImportRtstructAsync();
                            msg = imported
                                ? "BDF processing complete and C-STORED back to Eclipse."
                                : "BDF processing complete. Auto-import was not successful; check the log or retry manually.";
                        }
                        MessageBox.Show(msg, "Complete");

                        // Save the processing log into the BDF_Output directory.
                        SaveProcessingLog(result, msg);
                    }
                    else
                    {
                        string err = result["message"] != null ? result["message"].ToString() : "Unknown error";
                        MessageBox.Show(string.Format("Processing failed: {0}", err), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Log(string.Format("Processing failed: {0}", err));
                        ResetDirQa("DIR QA unavailable: BDF processing failed.");

                        // Save the log even on failure.
                        SaveProcessingLog(result, "FAILED: " + err);
                    }
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// Save the activity log to a file in the BDF output directory.
        /// </summary>
        private void SaveProcessingLog(JObject result, string statusMsg)
        {
            try
            {
                string logDir = null;
                JToken outputs = result != null ? result["outputs"] : null;
                if (outputs != null && outputs["rtstruct"] != null)
                {
                    string rtstructPath = outputs["rtstruct"].ToString();
                    logDir = Path.GetDirectoryName(rtstructPath);
                }

                if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
                    return;

                string logPath = Path.Combine(logDir, "processing_log.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== AutoFLC BDF Processing Log ===");
                sb.AppendLine(string.Format("Timestamp: {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
                sb.AppendLine(string.Format("Status: {0}", statusMsg));
                sb.AppendLine();
                sb.AppendLine("--- Activity Log ---");
                sb.Append(txtLog.Text);
                sb.AppendLine();
                sb.AppendLine("--- End of Log ---");

                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("Warning: could not save processing log: " + ex.Message);
            }
        }

        private async Task<bool> AutoImportRtstructAsync()
        {
            if (string.IsNullOrEmpty(_lastRtstructPath) || !File.Exists(_lastRtstructPath))
                return false;

            string daemonAe = txtDaemonAe.Text.Trim();
            string daemonIp = txtDaemonIp.Text.Trim();
            int daemonPort = (int)numDaemonPort.Value;
            string localAe = txtLocalAe.Text.Trim();

            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 || string.IsNullOrEmpty(localAe))
            {
                Log("Auto-import: DICOM Daemon settings incomplete, skipped.");
                return false;
            }

            Log("Pushing RTSTRUCT via DICOM Daemon C-STORE...");
            var service = new DicomDaemonService(daemonAe, daemonIp, daemonPort, localAe, 0, Log);
            bool allOk = true;
            foreach (string rtPath in _lastRtstructPaths)
            {
                if (string.IsNullOrEmpty(rtPath) || !File.Exists(rtPath)) continue;
                bool ok = await service.SendSingleFileAsync(rtPath);
                if (ok)
                    Log("RTSTRUCT pushed back to Eclipse: " + Path.GetFileName(rtPath));
                else
                {
                    allOk = false;
                    Log("RTSTRUCT push failed: " + Path.GetFileName(rtPath));
                }
            }
            if (_lastRtstructPaths.Count == 0)
            {
                // Legacy single-path fallback.
                bool ok = await service.SendSingleFileAsync(_lastRtstructPath);
                if (ok) Log("RTSTRUCT automatically pushed back to Eclipse.");
                else { allOk = false; Log("RTSTRUCT automatic push failed."); }
            }
            return allOk;
        }

        /// <summary>
        /// C-STORE resampled CT and RTSTRUCT back to Eclipse after uniformity-based resampling.
        /// </summary>
        private async Task AutoCStoreResampledDataAsync(DaemonExportArgs args)
        {
            string daemonAe = args.DaemonAETitle;
            string daemonIp = args.DaemonIP;
            int daemonPort = args.DaemonPort;
            string localAe = args.LocalAETitle;

            if (string.IsNullOrEmpty(daemonAe) || string.IsNullOrEmpty(daemonIp) || daemonPort <= 0 || string.IsNullOrEmpty(localAe))
            {
                Log("Auto C-STORE after resampling: DICOM Daemon settings incomplete, skipped.");
                return;
            }

            if (args.ResampledCtFiles == null || args.ResampledCtFiles.Count == 0)
            {
                Log("Auto C-STORE after resampling: no resampled CT files to push.");
                return;
            }

            Log(string.Format("Pushing {0} resampled CT file(s) and RTSTRUCT back to Eclipse...", args.ResampledCtFiles.Count));
            var service = new DicomDaemonService(daemonAe, daemonIp, daemonPort, localAe, 0, Log);
            int okCount = 0;

            // CT files first
            foreach (string ctFile in args.ResampledCtFiles)
            {
                if (File.Exists(ctFile))
                {
                    bool ok = await service.SendSingleFileAsync(ctFile);
                    if (ok) okCount++;
                }
            }
            Log(string.Format("Resampled CT C-STORE: {0}/{1} succeeded.", okCount, args.ResampledCtFiles.Count));

            // RTSTRUCT next
            if (!string.IsNullOrEmpty(args.ResampledRtstructPath) && File.Exists(args.ResampledRtstructPath))
            {
                bool ok = await service.SendSingleFileAsync(args.ResampledRtstructPath);
                Log(string.Format("Resampled RTSTRUCT C-STORE: {0}", ok ? "succeeded" : "failed"));
            }
        }

        // ------------------------------------------------------------------
        // DIR QA Results (Jacobian determinant) helpers
        // ------------------------------------------------------------------

        private void PopulateMetricQa(JToken stats)
        {
            if (dgvDirQa == null) return;
            DataTable dt = dgvDirQa.DataSource as DataTable;
            if (dt == null) return;

            string huMean = FormatStatFloat(stats, "hu_mean_lung");
            string huSd = FormatStatFloat(stats, "hu_std_lung");
            string massF = FormatStatFloat(stats, "hu_mass_factor");
            string mutual = FormatStatFloat(stats, "mutual_rho_jac_hu");

            int n = dt.Rows.Count;
            SetRow(dt, Math.Max(0, n - 4), "HU mean (lung)", huMean, "-");
            SetRow(dt, Math.Max(0, n - 3), "HU SD (lung)", huSd, "-");
            SetRow(dt, Math.Max(0, n - 2), "HU mass factor", massF, "-");
            SetRow(dt, Math.Max(0, n - 1), "Mutual rho(Jac, HU)", mutual, "-");
        }

        private string FormatStatFloat(JToken stats, string key)
        {
            if (stats == null || stats[key] == null || stats[key].Type == JTokenType.Null)
                return "-";
            double v = stats[key].Value<double>();
            if (double.IsNaN(v)) return "-";
            return v.ToString("F4");
        }

        private void PopulateDirQa(JToken dirQa)
        {
            if (dgvDirQa == null || lblDirQaWarning == null) return;

            if (dirQa == null || dirQa.Type == JTokenType.Null)
            {
                ResetDirQa("DIR QA statistics were not produced by the backend. Run BDF processing again.");
                return;
            }

            JToken whole = dirQa["whole_image"];
            JToken lung = dirQa["lung"];
            bool hasLung = lung != null && lung.Type != JTokenType.Null;

            try
            {
                DataTable dt = dgvDirQa.DataSource as DataTable;
                if (dt == null)
                {
                    InitDirQaTable();
                    dt = (DataTable)dgvDirQa.DataSource;
                }

                string foldingWhole = FormatBucket(whole, "folding");
                string contractionWhole = FormatBucket(whole, "contraction");
                string identityWhole = FormatBucket(whole, "identity");
                string expansionWhole = FormatBucket(whole, "expansion");
                string nonfiniteWhole = FormatBucket(whole, "nonfinite");
                string totalWhole = FormatInt(whole, "total_voxels");
                string minWhole = FormatFloat(whole, "min_detj");
                string maxWhole = FormatFloat(whole, "max_detj");
                string meanWhole = FormatFloat(whole, "mean_detj");
                string stdWhole = FormatFloat(whole, "std_detj");

                string naLung = hasLung ? null : "N/A";
                string foldingLung = hasLung ? FormatBucket(lung, "folding") : naLung;
                string contractionLung = hasLung ? FormatBucket(lung, "contraction") : naLung;
                string identityLung = hasLung ? FormatBucket(lung, "identity") : naLung;
                string expansionLung = hasLung ? FormatBucket(lung, "expansion") : naLung;
                string nonfiniteLung = hasLung ? FormatBucket(lung, "nonfinite") : naLung;
                string totalLung = hasLung ? FormatInt(lung, "total_voxels") : naLung;
                string minLung = hasLung ? FormatFloat(lung, "min_detj") : naLung;
                string maxLung = hasLung ? FormatFloat(lung, "max_detj") : naLung;
                string meanLung = hasLung ? FormatFloat(lung, "mean_detj") : naLung;
                string stdLung = hasLung ? FormatFloat(lung, "std_detj") : naLung;

                SetRow(dt, 0, "Folding (detJ<0)", foldingWhole, foldingLung);
                SetRow(dt, 1, "Contraction (0<=detJ<1)", contractionWhole, contractionLung);
                SetRow(dt, 2, "Identity (detJ=1)", identityWhole, identityLung);
                SetRow(dt, 3, "Expansion (detJ>1)", expansionWhole, expansionLung);
                SetRow(dt, 4, "Non-finite (NaN/Inf)", nonfiniteWhole, nonfiniteLung);
                SetRow(dt, 5, "Total voxels", totalWhole, totalLung);
                SetRow(dt, 6, "Min detJ", minWhole, minLung);
                SetRow(dt, 7, "Max detJ", maxWhole, maxLung);
                SetRow(dt, 8, "Mean detJ", meanWhole, meanLung);
                SetRow(dt, 9, "Std detJ", stdWhole, stdLung);

                long wholeFolding = ReadLong(whole, "folding_count");
                long lungFolding = hasLung ? ReadLong(lung, "folding_count") : 0;
                long wholeNonfinite = ReadLong(whole, "nonfinite_count");
                long lungNonfinite = hasLung ? ReadLong(lung, "nonfinite_count") : 0;

                // Only warn about folding/non-finite within the LUNG region.
                // Whole-image folding occurs routinely at image borders where
                // the DVF extrapolates beyond anatomy and is not clinically
                // significant. Lung-region folding is the meaningful QA metric.
                bool lungFoldingDetected = lungFolding > 0;
                bool lungNonfiniteDetected = lungNonfinite > 0;

                string warning;
                if (lungFoldingDetected)
                {
                    warning = "WARNING: Negative det(J) detected in the lung region (registration folding). The deformable registration may be unreliable in these regions - use function-lung results with caution and review the DVF/DIR before clinical use.";
                    lblDirQaWarning.ForeColor = RefError;
                }
                else if (lungNonfiniteDetected)
                {
                    warning = "WARNING: Non-finite det(J) values (NaN/Inf) detected in the lung region. The deformable registration produced numerical breakdown in some regions - results may be unreliable.";
                    lblDirQaWarning.ForeColor = RefError;
                }
                else
                {
                    warning = "OK: No negative det(J) (folding) in the lung region. Whole-image border folding may be present but is not clinically significant.";
                    lblDirQaWarning.ForeColor = RefSuccess;
                }
                lblDirQaWarning.Text = warning;

                // Highlight the folding row red only if LUNG folding was detected.
                try
                {
                    Color foldColor = lungFoldingDetected ? RefError : RefText;
                    dgvDirQa.Rows[0].DefaultCellStyle.ForeColor = foldColor;
                }
                catch { }

                Log(string.Format("DIR QA: Folding (detJ<0) whole={0} lung={1}; min={2} max={3} mean={4} (whole image)",
                    foldingWhole, hasLung ? foldingLung : "N/A", minWhole, maxWhole, meanWhole));
            }
            catch (Exception ex)
            {
                ResetDirQa(string.Format("DIR QA results could not be displayed: {0}", ex.Message));
                Log(string.Format("DIR QA display failed: {0}", ex.Message));
            }
        }

        private void ResetDirQa(string message)
        {
            if (lblDirQaWarning != null)
            {
                lblDirQaWarning.Text = message ?? "DIR QA unavailable.";
                lblDirQaWarning.ForeColor = RefText;
            }
            if (dgvDirQa != null)
            {
                InitDirQaTable();
                try { dgvDirQa.Rows[0].DefaultCellStyle.ForeColor = RefText; }
                catch { }
            }
        }

        private static void SetRow(DataTable dt, int index, string metric, string whole, string lung)
        {
            if (dt == null || index < 0 || index >= dt.Rows.Count) return;
            dt.Rows[index][0] = metric;
            dt.Rows[index][1] = whole ?? "-";
            dt.Rows[index][2] = lung ?? "N/A";
        }

        private static long ReadLong(JToken token, string field)
        {
            if (token == null) return 0;
            JToken v = token[field];
            if (v == null) return 0;
            return (long)v;
        }

        private static double ReadDouble(JToken token, string field)
        {
            if (token == null) return double.NaN;
            JToken v = token[field];
            if (v == null) return double.NaN;
            return (double)v;
        }

        private static string FormatBucket(JToken token, string baseField)
        {
            long count = ReadLong(token, baseField + "_count");
            double pct = ReadDouble(token, baseField + "_percent");
            if (double.IsNaN(pct))
                return string.Format(CultureInfo.InvariantCulture, "{0:N0}", count);
            return string.Format(CultureInfo.InvariantCulture, "{0:N0}  ({1:F2}%)", count, pct);
        }

        private static string FormatInt(JToken token, string field)
        {
            long v = ReadLong(token, field);
            return string.Format(CultureInfo.InvariantCulture, "{0:N0}", v);
        }

        private static string FormatFloat(JToken token, string field)
        {
            double v = ReadDouble(token, field);
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "N/A";
            return string.Format(CultureInfo.InvariantCulture, "{0:F4}", v);
        }

        private string FindExportedCtDir(string baseDir, string prefix)
        {
            if (!Directory.Exists(baseDir)) return null;
            foreach (var d in Directory.GetDirectories(baseDir, prefix + "*"))
            {
                string dicomDir = Path.Combine(d, "DICOM");
                if (Directory.Exists(dicomDir) && Directory.GetFiles(dicomDir, "*.dcm").Length > 0)
                    return dicomDir;
                if (Directory.GetFiles(d, "*.dcm").Length > 0)
                    return d;
            }
            return null;
        }

        private string GetAutoPhaseCtDir(string baseDir, string patientId)
        {
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(patientId)) return null;
            // The registration reference is the 50% phase (end-expiration).
            string dir = Path.Combine(baseDir, patientId, "Phase_50", "CT");
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dcm").Length > 0)
                return dir;
            return null;
        }

        private string GetAutoPhaseRtstructPath(string baseDir, string patientId)
        {
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(patientId)) return null;
            string dir = Path.Combine(baseDir, patientId, "Phase_50", "RTSTRUCT");
            if (!Directory.Exists(dir)) return null;
            string[] files = Directory.GetFiles(dir, "*.dcm");
            if (files.Length > 0) return files[0];
            return null;
        }

        private string DiscoverPatientIdFromFolder(string baseDir)
        {
            if (!Directory.Exists(baseDir)) return null;
            try
            {
                string[] dirs = Directory.GetDirectories(baseDir);
                if (dirs.Length == 1)
                    return Path.GetFileName(dirs[0]);
            }
            catch { }
            return null;
        }

        private string FindAnyCtDir(string dir, int depth = 0)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || depth > 2) return null;
            if (HasCtFiles(dir)) return dir;
            foreach (string sub in Directory.GetDirectories(dir, "CT", SearchOption.TopDirectoryOnly))
                if (HasCtFiles(sub)) return sub;
            string best = null;
            int bestCount = 0;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("_study_move_temp_", StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = FindAnyCtDir(sub, depth + 1);
                if (candidate != null)
                {
                    int count = CountCtFiles(candidate);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = candidate;
                    }
                }
            }
            return best;
        }

        private bool HasCtFiles(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            foreach (string f in Directory.GetFiles(dir, "*.dcm"))
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.Default);
                    if (df.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "") == "CT")
                        return true;
                }
                catch { }
            }
            return false;
        }

        private int CountCtFiles(string dir)
        {
            int count = 0;
            if (!Directory.Exists(dir)) return 0;
            foreach (string f in Directory.GetFiles(dir, "*.dcm"))
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.Default);
                    if (df.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "") == "CT")
                        count++;
                }
                catch { }
            }
            return count;
        }

        private string FindRtstructInDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string[] files = Directory.GetFiles(dir, "*.dcm");
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (name.Contains("rtstruct") || name.Contains("rtss") || name.Contains("structureset"))
                    return f;
            }
            foreach (string f in files)
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.Default);
                    if (df.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "") == "RTSTRUCT")
                        return f;
                }
                catch { }
            }
            return null;
        }

        private string GetSeriesUidForImageInfo(ImageInfo info)
        {
            if (info == null) return null;
            VMS.TPS.Common.Model.API.Image img = info.ESAPIImage;
            if (img == null && info.SliceImages != null && info.SliceImages.Count > 0)
                img = info.SliceImages[0];
            if (img == null || img.Series == null) return null;
            return img.Series.UID;
        }

        private string GetStudyUidForImageInfo(ImageInfo info)
        {
            if (info == null) return null;
            VMS.TPS.Common.Model.API.Image img = info.ESAPIImage;
            if (img == null && info.SliceImages != null && info.SliceImages.Count > 0)
                img = info.SliceImages[0];
            if (img == null || img.Series == null || img.Series.Study == null) return null;
            return img.Series.Study.UID;
        }

        private List<ImageInfo> GetSelectedImagesToExport()
        {
            var selected = new List<ImageInfo>();
            if (dgvImages.Rows == null || dgvImages.Rows.Count == 0)
                return selected;

            foreach (DataGridViewRow row in dgvImages.Rows)
            {
                if (row == null) continue;
                bool export = false;
                try
                {
                    object val = row.Cells["Export"].Value;
                    export = val != null && Convert.ToBoolean(val);
                }
                catch { continue; }

                if (!export) continue;

                DataRowView drv = row.DataBoundItem as DataRowView;
                if (drv == null) continue;

                string seriesId = drv["Series"] as string ?? string.Empty;
                string imageId = drv["Image ID"] as string ?? string.Empty;
                ImageInfo match = _images.FirstOrDefault(i => i.SeriesId == seriesId && i.Id == imageId);
                if (match != null && !selected.Contains(match))
                    selected.Add(match);
            }
            return selected;
        }

        private void DisposeEsapiApp()
        {
            if (_openPatient != null && _esapiApp != null)
            {
                try { _esapiApp.ClosePatient(); } catch { }
                _openPatient = null;
            }
            if (_esapiApp != null)
            {
                try { ((IDisposable)_esapiApp).Dispose(); } catch { }
                _esapiApp = null;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DisposeEsapiApp();
            base.OnFormClosed(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const int EM_SETCUEBANNER = 0x1501;

        private static void SetCueBanner(TextBox box, string cue)
        {
            try { SendMessage(box.Handle, EM_SETCUEBANNER, IntPtr.Zero, cue ?? string.Empty); }
            catch { }
        }
    }

    public class ImageInfo
    {
        public string Id { get; set; }
        public string SeriesId { get; set; }
        public string SeriesDescription { get; set; }
        public int XSize { get; set; }
        public int YSize { get; set; }
        public int ZSize { get; set; }
        public int Phase { get; set; }

        // A 3D volume image (ESAPI Image.ZSize > 1).
        public VMS.TPS.Common.Model.API.Image ESAPIImage { get; set; }

        // When every image in a series is a single slice, the slices grouped by
        // series UID.
        public List<VMS.TPS.Common.Model.API.Image> SliceImages { get; set; }
    }

    public class DaemonExportArgs
    {
        public string BaseDir { get; set; }
        public string PatientId { get; set; }
        public List<PhaseExportInfo> Phases { get; set; }
        public string DaemonAETitle { get; set; }
        public string DaemonIP { get; set; }
        public int DaemonPort { get; set; }
        public string LocalAETitle { get; set; }
        public int LocalPort { get; set; }

        /// <summary>CT was resampled; files need to be C-STORED back to Eclipse.</summary>
        public bool ResamplingPerformed { get; set; }
        /// <summary>Resampled CT file paths to C-STORE.</summary>
        public List<string> ResampledCtFiles { get; set; }
        /// <summary>Resampled RTSTRUCT file path to C-STORE.</summary>
        public string ResampledRtstructPath { get; set; }
    }

    public class PhaseExportInfo
    {
        public int Phase { get; set; }
        public string CtSeriesUid { get; set; }
        public string StudyUid { get; set; }
        public string RtstructSeriesUid { get; set; }
    }
}
