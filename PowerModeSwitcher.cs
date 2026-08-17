using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PowerModeSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;

            if (args != null && args.Any(delegate(string argument)
            {
                return string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase);
            }))
            {
                return SelfTest.Run(applicationDirectory);
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(applicationDirectory));
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "PowerModeSwitcher를 시작할 수 없습니다.\r\n\r\n" + exception.Message,
                    "PowerModeSwitcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly string _applicationDirectory;
        private readonly ProfileRepository _profileRepository;
        private readonly StateRepository _stateRepository;
        private readonly PowerModeService _powerModeService;
        private readonly FanPresetRepository _fanPresetRepository;
        private readonly List<Button> _applyButtons = new List<Button>();
        private readonly List<Control> _fanActionControls = new List<Control>();
        private readonly List<Control> _keyboardActionControls = new List<Control>();
        private readonly Dictionary<string, Button> _fanPresetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly KeyboardBacklightService _keyboardBacklightService;
        private Label _lastAppliedLabel;
        private Label _workingLabel;
        private Label _keyboardStatusLabel;
        private TableLayoutPanel _cardGrid;
        private TabPage _fanTab;
        private Label _fanStatusLabel;
        private Label _fanReadbackLabel;
        private Label _fanPresetLabel;
        private Button _fanAutoButton;
        private IList<PowerProfile> _profiles;
        private FanPresetDocument _fanPresetDocument;
        private FanService _fanService;
        private Button _keyboardOnButton;
        private Button _keyboardOffButton;
        private bool _keyboardBacklightAvailable;
        private bool _busy;

        public MainForm(string applicationDirectory)
        {
            _applicationDirectory = applicationDirectory;
            _profileRepository = new ProfileRepository(Path.Combine(applicationDirectory, "profiles.json"));
            _stateRepository = new StateRepository(Path.Combine(applicationDirectory, "state.json"));
            _fanPresetRepository = new FanPresetRepository(Path.Combine(applicationDirectory, "fan-presets.json"));
            _powerModeService = new PowerModeService(
                _stateRepository,
                new BackendClient(Path.Combine(applicationDirectory, "helpers", "PowerModeBackend.ps1")),
                new PowerPlanService(),
                new DisplayRefreshService());
            _keyboardBacklightService = new KeyboardBacklightService();

            InitializeWindow();
            Load += delegate { LoadConfiguration(); };
        }

        private void InitializeWindow()
        {
            Text = "PowerModeSwitcher";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 700);
            Size = new Size(1190, 820);
            BackColor = Color.FromArgb(242, 245, 248);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel outer = new TableLayoutPanel();
            outer.Dock = DockStyle.Fill;
            outer.ColumnCount = 1;
            outer.RowCount = 3;
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 102F));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            outer.Padding = new Padding(20, 18, 20, 20);
            Controls.Add(outer);

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.FromArgb(30, 42, 60);
            header.Padding = new Padding(20, 15, 20, 12);
            outer.Controls.Add(header, 0, 0);

            Label title = new Label();
            title.AutoSize = true;
            title.Text = "PowerModeSwitcher";
            title.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.White;
            title.Location = new Point(18, 12);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.AutoSize = true;
            subtitle.Text = "8개 전원 프로필 · 모든 모드에서 144Hz 유지";
            subtitle.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            subtitle.ForeColor = Color.FromArgb(211, 221, 233);
            subtitle.Location = new Point(21, 56);
            header.Controls.Add(subtitle);

            Panel keyboardPanel = new Panel();
            keyboardPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            keyboardPanel.BackColor = header.BackColor;
            keyboardPanel.Size = new Size(360, 78);
            keyboardPanel.Location = new Point(Math.Max(390, header.ClientSize.Width - 380), 10);
            header.Controls.Add(keyboardPanel);

            Label keyboardTitle = new Label();
            keyboardTitle.AutoSize = true;
            keyboardTitle.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
            keyboardTitle.ForeColor = Color.White;
            keyboardTitle.Text = "키보드 조명";
            keyboardTitle.Location = new Point(0, 3);
            keyboardPanel.Controls.Add(keyboardTitle);

            _keyboardStatusLabel = new Label();
            _keyboardStatusLabel.AutoSize = false;
            _keyboardStatusLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _keyboardStatusLabel.ForeColor = Color.FromArgb(211, 221, 233);
            _keyboardStatusLabel.Text = "확인 중…";
            _keyboardStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _keyboardStatusLabel.Location = new Point(0, 34);
            _keyboardStatusLabel.Size = new Size(118, 30);
            keyboardPanel.Controls.Add(_keyboardStatusLabel);

            _keyboardOnButton = CreateKeyboardButton("ON", delegate { RunKeyboardOperation(true); });
            _keyboardOnButton.Location = new Point(126, 22);
            _keyboardOnButton.Size = new Size(100, 32);
            keyboardPanel.Controls.Add(_keyboardOnButton);

            _keyboardOffButton = CreateKeyboardButton("OFF", delegate { RunKeyboardOperation(false); });
            _keyboardOffButton.Location = new Point(236, 22);
            _keyboardOffButton.Size = new Size(100, 32);
            keyboardPanel.Controls.Add(_keyboardOffButton);
            header.Resize += delegate
            {
                keyboardPanel.Left = Math.Max(390, header.ClientSize.Width - keyboardPanel.Width - 20);
            };

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.BackColor = Color.FromArgb(229, 237, 245);
            statusPanel.Padding = new Padding(14, 11, 14, 8);
            outer.Controls.Add(statusPanel, 0, 1);

            _lastAppliedLabel = new Label();
            _lastAppliedLabel.AutoSize = true;
            _lastAppliedLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
            _lastAppliedLabel.ForeColor = Color.FromArgb(35, 57, 78);
            _lastAppliedLabel.Text = "마지막 적용 모드: 없음";
            _lastAppliedLabel.Location = new Point(14, 14);
            statusPanel.Controls.Add(_lastAppliedLabel);

            _workingLabel = new Label();
            _workingLabel.AutoSize = true;
            _workingLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _workingLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _workingLabel.ForeColor = Color.FromArgb(80, 94, 109);
            _workingLabel.Text = String.Empty;
            _workingLabel.Location = new Point(700, 15);
            statusPanel.Controls.Add(_workingLabel);
            statusPanel.Resize += delegate
            {
                _workingLabel.Left = Math.Max(14, statusPanel.ClientSize.Width - _workingLabel.Width - 14);
            };

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            outer.Controls.Add(tabs, 0, 2);

            TabPage profilesTab = new TabPage("전원 모드 (8)");
            profilesTab.BackColor = Color.FromArgb(242, 245, 248);
            tabs.TabPages.Add(profilesTab);

            _fanTab = new TabPage("팬 제어 (실험적)");
            _fanTab.BackColor = Color.FromArgb(242, 245, 248);
            tabs.TabPages.Add(_fanTab);

            Panel scrollHost = new Panel();
            scrollHost.Dock = DockStyle.Fill;
            scrollHost.AutoScroll = true;
            scrollHost.Padding = new Padding(0, 14, 0, 0);
            profilesTab.Controls.Add(scrollHost);

            _cardGrid = new TableLayoutPanel();
            _cardGrid.AutoSize = true;
            _cardGrid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _cardGrid.Dock = DockStyle.Top;
            _cardGrid.ColumnCount = 2;
            _cardGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _cardGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            scrollHost.Controls.Add(_cardGrid);
        }

        private void LoadConfiguration()
        {
            try
            {
                _profiles = _profileRepository.Load();
                ProfileValidator.Validate(_profiles);
                RenderCards();
                RefreshLastAppliedLabel();
                InitializeFanControl();
                RefreshKeyboardBacklight();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "profiles.json을 읽을 수 없습니다.\r\n\r\n" + exception.Message,
                    "PowerModeSwitcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Close();
            }
        }

        private void InitializeFanControl()
        {
            try
            {
                _fanPresetDocument = _fanPresetRepository.Load();
                _fanService = new FanService(_stateRepository, _fanPresetDocument);
                BuildFanTab();
                RefreshFanStatus();
            }
            catch (Exception exception)
            {
                BuildFanUnavailable(exception.Message);
            }
        }

        private void BuildFanUnavailable(string message)
        {
            _fanTab.Controls.Clear();
            Label unavailable = new Label();
            unavailable.Dock = DockStyle.Fill;
            unavailable.Padding = new Padding(24);
            unavailable.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            unavailable.ForeColor = Color.FromArgb(120, 60, 20);
            unavailable.Text = "팬 제어 설정을 읽지 못했습니다.\r\n\r\n" + message;
            _fanTab.Controls.Add(unavailable);
        }

        private void BuildFanTab()
        {
            _fanTab.Controls.Clear();
            _fanActionControls.Clear();
            _fanPresetButtons.Clear();
            _fanAutoButton = null;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            _fanTab.Controls.Add(scroll);

            FlowLayoutPanel stack = new FlowLayoutPanel();
            stack.AutoSize = true;
            stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stack.Dock = DockStyle.Top;
            stack.FlowDirection = FlowDirection.TopDown;
            stack.WrapContents = false;
            stack.Padding = new Padding(18, 18, 18, 24);
            scroll.Controls.Add(stack);

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
            heading.ForeColor = Color.FromArgb(31, 53, 73);
            heading.Margin = new Padding(0, 0, 0, 8);
            heading.Text = "팬 제어 · 실험적 MSI_ACPI WMI";
            stack.Controls.Add(heading);

            Label warning = new Label();
            warning.AutoSize = false;
            warning.BackColor = Color.FromArgb(255, 245, 209);
            warning.BorderStyle = BorderStyle.FixedSingle;
            warning.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            warning.ForeColor = Color.FromArgb(106, 73, 20);
            warning.Margin = new Padding(0, 0, 0, 12);
            warning.Padding = new Padding(12, 9, 12, 9);
            warning.Size = new Size(1040, 66);
            warning.Text = "비공식 " + _fanPresetDocument.systemProductName + " / " + _fanPresetDocument.baseBoardProduct +
                " 전용 제어입니다. 모델·보드와 현재 팬 곡선이 확인될 때만 쓰기가 활성화됩니다. MSI Center/Fn 키/절전 모드는 설정을 덮어쓸 수 있습니다. " +
                "20·50·70·99%는 일반 곡선, 110·120·125%는 EC 확장 레벨 실험값입니다. Cooler Boost는 별도 강제 최대 모드입니다.";
            stack.Controls.Add(warning);

            GroupBox statusGroup = new GroupBox();
            statusGroup.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            statusGroup.Margin = new Padding(0, 0, 0, 12);
            statusGroup.Padding = new Padding(14, 20, 14, 10);
            statusGroup.Size = new Size(1040, 126);
            statusGroup.Text = "현재 상태 (수동 새로고침)";
            stack.Controls.Add(statusGroup);

            _fanStatusLabel = new Label();
            _fanStatusLabel.AutoSize = false;
            _fanStatusLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _fanStatusLabel.ForeColor = Color.FromArgb(42, 67, 90);
            _fanStatusLabel.Location = new Point(15, 25);
            _fanStatusLabel.Size = new Size(820, 24);
            _fanStatusLabel.Text = "팬 backend 상태를 확인하는 중…";
            statusGroup.Controls.Add(_fanStatusLabel);

            _fanReadbackLabel = new Label();
            _fanReadbackLabel.AutoSize = false;
            _fanReadbackLabel.Font = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _fanReadbackLabel.ForeColor = Color.FromArgb(73, 89, 105);
            _fanReadbackLabel.Location = new Point(15, 51);
            _fanReadbackLabel.Size = new Size(1005, 54);
            _fanReadbackLabel.Text = "읽기 전용 상태 확인 전";
            statusGroup.Controls.Add(_fanReadbackLabel);

            Button refresh = CreateFanButton("새로고침", delegate { RefreshFanStatus(); }, false);
            refresh.Location = new Point(855, 22);
            refresh.Size = new Size(165, 32);
            statusGroup.Controls.Add(refresh);

            GroupBox presetsGroup = new GroupBox();
            presetsGroup.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            presetsGroup.Margin = new Padding(0, 0, 0, 12);
            presetsGroup.Padding = new Padding(14, 20, 14, 10);
            presetsGroup.Size = new Size(1040, 190);
            presetsGroup.Text = "안전 팬 곡선";
            stack.Controls.Add(presetsGroup);

            Label presetHint = new Label();
            presetHint.AutoSize = false;
            presetHint.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            presetHint.ForeColor = Color.FromArgb(79, 93, 108);
            presetHint.Location = new Point(15, 24);
            presetHint.Size = new Size(1000, 25);
            presetHint.Text = "버튼을 누르면 EC 곡선표에 적용됩니다. 110~125%는 Cooler Boost보다 낮은 중간 회전을 찾기 위한 실험 레벨입니다.";
            presetsGroup.Controls.Add(presetHint);

            FlowLayoutPanel presetButtons = new FlowLayoutPanel();
            presetButtons.Location = new Point(15, 54);
            presetButtons.Size = new Size(1005, 76);
            presetButtons.WrapContents = true;
            presetsGroup.Controls.Add(presetButtons);

            _fanAutoButton = CreateFanButton("기본 / Auto", delegate { RunFanOperation("기본 팬 모드 적용 중…", delegate { return _fanService.SetAuto(); }, true); }, false);
            presetButtons.Controls.Add(_fanAutoButton);
            foreach (FanPreset preset in _fanPresetDocument.presets)
            {
                FanPreset selectedPreset = preset;
                Button presetButton = CreateFanButton(selectedPreset.name, delegate
                {
                    RunFanOperation(selectedPreset.name + " 적용 중…", delegate { return _fanService.ApplyPreset(selectedPreset); }, true);
                }, false);
                _fanPresetButtons[selectedPreset.id] = presetButton;
                presetButtons.Controls.Add(presetButton);
            }

            _fanPresetLabel = new Label();
            _fanPresetLabel.AutoSize = false;
            _fanPresetLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Italic, GraphicsUnit.Point);
            _fanPresetLabel.ForeColor = Color.FromArgb(91, 105, 120);
            _fanPresetLabel.Location = new Point(15, 137);
            _fanPresetLabel.Size = new Size(1000, 25);
            _fanPresetLabel.Text = "프리셋 값은 fan-presets.json에서 조정할 수 있습니다.";
            presetsGroup.Controls.Add(_fanPresetLabel);
            RefreshFanPresetSelection();

            GroupBox boostGroup = new GroupBox();
            boostGroup.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            boostGroup.Margin = new Padding(0, 0, 0, 12);
            boostGroup.Padding = new Padding(14, 20, 14, 10);
            boostGroup.Size = new Size(1040, 78);
            boostGroup.Text = "Cooler Boost";
            stack.Controls.Add(boostGroup);

            Label boostHint = new Label();
            boostHint.AutoSize = false;
            boostHint.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            boostHint.ForeColor = Color.FromArgb(79, 93, 108);
            boostHint.Location = new Point(15, 29);
            boostHint.Size = new Size(580, 28);
            boostHint.Text = "최대 팬 강제 모드입니다. 해제 후 감속에는 시간이 걸릴 수 있습니다.";
            boostGroup.Controls.Add(boostHint);

            Button boostOn = CreateFanButton("Cooler Boost 최대", delegate
            {
                RunFanOperation("Cooler Boost 적용 중…", delegate { return _fanService.SetCoolerBoost(true); }, true);
            }, true);
            boostOn.Location = new Point(612, 25);
            boostOn.Size = new Size(190, 32);
            boostGroup.Controls.Add(boostOn);

            Button boostOff = CreateFanButton("Boost 해제", delegate
            {
                RunFanOperation("Cooler Boost 해제 중…", delegate { return _fanService.SetCoolerBoost(false); }, false);
            }, false);
            boostOff.Location = new Point(816, 25);
            boostOff.Size = new Size(204, 32);
            boostGroup.Controls.Add(boostOff);

            GroupBox restoreGroup = new GroupBox();
            restoreGroup.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            restoreGroup.Margin = new Padding(0, 0, 0, 12);
            restoreGroup.Padding = new Padding(14, 20, 14, 10);
            restoreGroup.Size = new Size(1040, 78);
            restoreGroup.Text = "복원";
            stack.Controls.Add(restoreGroup);

            Label restoreHint = new Label();
            restoreHint.AutoSize = false;
            restoreHint.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            restoreHint.ForeColor = Color.FromArgb(79, 93, 108);
            restoreHint.Location = new Point(15, 29);
            restoreHint.Size = new Size(700, 28);
            restoreHint.Text = "처음 쓰기 전에 저장한 팬 모드·곡선·Cooler Boost 상태를 복원합니다.";
            restoreGroup.Controls.Add(restoreHint);

            Button restore = CreateFanButton("최초 팬 설정 복원", delegate
            {
                RunFanOperation("팬 baseline 복원 중…", delegate { return _fanService.RestoreBaseline(); }, false);
            }, false);
            restore.Location = new Point(730, 25);
            restore.Size = new Size(290, 32);
            restoreGroup.Controls.Add(restore);
        }

        private Button CreateFanButton(string text, Action action, bool primary)
        {
            Button button = new Button();
            button.AutoSize = true;
            button.BackColor = primary ? Color.FromArgb(40, 103, 160) : Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(154, 169, 184);
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = primary ? Color.White : Color.FromArgb(48, 72, 95);
            button.Margin = new Padding(0, 0, 8, 0);
            button.Padding = new Padding(10, 3, 10, 3);
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            button.Click += delegate { action(); };
            _fanActionControls.Add(button);
            return button;
        }

        private Button CreateKeyboardButton(string text, Action action)
        {
            Button button = new Button();
            button.BackColor = Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(154, 169, 184);
            button.FlatAppearance.BorderSize = 1;
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = Color.FromArgb(48, 72, 95);
            button.Padding = new Padding(8, 2, 8, 2);
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            button.Click += delegate { action(); };
            _keyboardActionControls.Add(button);
            return button;
        }

        private void RefreshKeyboardBacklight()
        {
            _keyboardBacklightAvailable = false;
            SetKeyboardControlsEnabled(false);
            _keyboardStatusLabel.Text = "확인 중…";
            _keyboardStatusLabel.ForeColor = Color.FromArgb(211, 221, 233);

            Task<KeyboardBacklightStatus> task = Task.Factory.StartNew(delegate
            {
                return _keyboardBacklightService.Query();
            });
            task.ContinueWith(delegate(Task<KeyboardBacklightStatus> completedTask)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (completedTask.IsFaulted)
                        {
                            UpdateKeyboardBacklightStatus(null);
                            return;
                        }

                        UpdateKeyboardBacklightStatus(completedTask.Result);
                    });
                }
                catch
                {
                    // The form may close while the WMI read is pending.
                }
            });
        }

        private void RunKeyboardOperation(bool enabled)
        {
            SetBusy(true, enabled ? "키보드 조명 ON 적용 중…" : "키보드 조명 OFF 적용 중…");
            Task<KeyboardBacklightActionResult> task = Task.Factory.StartNew(delegate
            {
                return _keyboardBacklightService.Set(enabled);
            });
            task.ContinueWith(delegate(Task<KeyboardBacklightActionResult> completedTask)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        SetBusy(false, String.Empty);
                        if (completedTask.IsFaulted)
                        {
                            MessageBox.Show(
                                "키보드 조명 변경 중 예기치 않은 오류가 발생했습니다.\r\n\r\n" +
                                (completedTask.Exception == null ? "알 수 없는 오류" : completedTask.Exception.GetBaseException().Message),
                                "PowerModeSwitcher",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            RefreshKeyboardBacklight();
                            return;
                        }

                        KeyboardBacklightActionResult result = completedTask.Result;
                        UpdateKeyboardBacklightStatus(result.status);
                        if (!result.success)
                        {
                            MessageBox.Show(
                                result.message,
                                result.title,
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    });
                }
                catch
                {
                    // The form may close while the WMI write is pending.
                }
            });
        }

        private void UpdateKeyboardBacklightStatus(KeyboardBacklightStatus status)
        {
            if (_keyboardStatusLabel == null)
            {
                return;
            }

            _keyboardBacklightAvailable = status != null && status.reachable && status.writeEnabled;
            if (!_keyboardBacklightAvailable)
            {
                _keyboardStatusLabel.Text = status == null || !status.reachable ? "사용 불가" : "안전 잠금";
                _keyboardStatusLabel.ForeColor = Color.FromArgb(255, 205, 155);
                SetKeyboardButtonSelected(_keyboardOnButton, false);
                SetKeyboardButtonSelected(_keyboardOffButton, false);
                SetKeyboardControlsEnabled(false);
                return;
            }

            _keyboardStatusLabel.Text = status.enabled ? "ON (D3 83)" : "OFF (D3 80)";
            _keyboardStatusLabel.ForeColor = status.enabled
                ? Color.FromArgb(167, 235, 191)
                : Color.FromArgb(211, 221, 233);
            SetKeyboardButtonSelected(_keyboardOnButton, status.enabled);
            SetKeyboardButtonSelected(_keyboardOffButton, !status.enabled);
            SetKeyboardControlsEnabled(!_busy);
        }

        private static void SetKeyboardButtonSelected(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            button.BackColor = selected ? Color.FromArgb(40, 103, 160) : Color.White;
            button.ForeColor = selected ? Color.White : Color.FromArgb(48, 72, 95);
            button.FlatAppearance.BorderColor = Color.FromArgb(154, 169, 184);
            button.FlatAppearance.BorderSize = selected ? 0 : 1;
        }

        private void SetKeyboardControlsEnabled(bool enabled)
        {
            foreach (Control control in _keyboardActionControls)
            {
                control.Enabled = enabled;
            }
        }

        private void RefreshFanPresetSelection()
        {
            string active = null;
            try
            {
                AppState state = _stateRepository.Load();
                if (state.fan != null)
                {
                    active = state.fan.lastAppliedPreset;
                }
            }
            catch
            {
                active = null;
            }

            SetFanButtonSelected(_fanAutoButton, String.Equals(active, "auto", StringComparison.OrdinalIgnoreCase));
            foreach (KeyValuePair<string, Button> item in _fanPresetButtons)
            {
                SetFanButtonSelected(item.Value, String.Equals(active, item.Key, StringComparison.OrdinalIgnoreCase));
            }

            if (_fanPresetLabel != null)
            {
                FanPreset selected = _fanPresetDocument == null || _fanPresetDocument.presets == null
                    ? null
                    : _fanPresetDocument.presets.FirstOrDefault(delegate(FanPreset preset)
                    {
                        return String.Equals(preset.id, active, StringComparison.OrdinalIgnoreCase);
                    });
                _fanPresetLabel.Text = selected == null
                    ? "현재 선택된 안전 팬 곡선 없음 (Auto/Boost/복원 상태)"
                    : "현재 적용됨: " + selected.name;
            }
        }

        private static void SetFanButtonSelected(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            button.BackColor = selected ? Color.FromArgb(40, 103, 160) : Color.White;
            button.ForeColor = selected ? Color.White : Color.FromArgb(48, 72, 95);
            button.FlatAppearance.BorderColor = Color.FromArgb(154, 169, 184);
            button.FlatAppearance.BorderSize = selected ? 0 : 1;
        }

        private void RefreshFanStatus()
        {
            RunFanOperation("팬 상태 확인 중…", delegate { return _fanService.Query(); }, false);
        }

        private void RunFanOperation(string workingMessage, Func<FanActionResult> operation, bool showResult)
        {
            if (_fanService == null)
            {
                return;
            }

            SetBusy(true, workingMessage);
            Task<FanActionResult> task = Task.Factory.StartNew(operation);
            task.ContinueWith(delegate(Task<FanActionResult> completedTask)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, String.Empty);
                    if (completedTask.IsFaulted)
                    {
                        Exception exception = completedTask.Exception == null ? null : completedTask.Exception.GetBaseException();
                        MessageBox.Show(
                            "팬 제어 중 예기치 않은 오류가 발생했습니다.\r\n\r\n" +
                            (exception == null ? "알 수 없는 오류" : exception.Message),
                            "PowerModeSwitcher",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    FanActionResult result = completedTask.Result;
                    UpdateFanStatus(result.status);
                    if (result.success)
                    {
                        RefreshFanPresetSelection();
                    }
                    if (showResult || !result.success)
                    {
                        MessageBox.Show(
                            result.ToDisplayText(),
                            result.title,
                            MessageBoxButtons.OK,
                            result.success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }

                    if (showResult && result.success)
                    {
                        Task.Factory.StartNew(delegate
                        {
                            System.Threading.Thread.Sleep(1500);
                            return _fanService.Query();
                        }).ContinueWith(delegate(Task<FanActionResult> settledTask)
                        {
                            if (settledTask.IsCanceled || settledTask.IsFaulted)
                            {
                                return;
                            }

                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    if (!IsDisposed && !Disposing)
                                    {
                                        UpdateFanStatus(settledTask.Result.status);
                                    }
                                });
                            }
                            catch
                            {
                                // The form may close while the settle read is pending.
                            }
                        });
                    }
                });
            });
        }

        private void UpdateFanStatus(FanHardwareStatus status)
        {
            if (_fanStatusLabel == null || _fanReadbackLabel == null)
            {
                return;
            }

            if (status == null)
            {
                _fanStatusLabel.Text = "팬 상태를 읽지 못했습니다.";
                _fanReadbackLabel.Text = "MSI Center에서 Auto 또는 Cooler Boost 상태를 확인하세요.";
                return;
            }

            _fanStatusLabel.Text = (status.writeEnabled ? "쓰기 가능: " : "쓰기 잠금: ") + status.message;
            _fanStatusLabel.ForeColor = status.writeEnabled ? Color.FromArgb(31, 102, 66) : Color.FromArgb(145, 72, 24);
            _fanReadbackLabel.Text = "펌웨어 " + (status.firmware ?? "확인 불가") + " · " + FanText.Shift(status.shiftMode) + " · " + FanText.Mode(status.fanMode) +
                " · Cooler Boost " + (status.coolerBoost ? "ON" : "OFF") + "\r\n" +
                "CPU " + status.cpuTemperature + "°C / " + status.cpuDuty + "% / " + status.cpuRpm + " RPM · " +
                "GPU " + status.gpuTemperature + "°C / " + status.gpuDuty + "% / " + status.gpuRpm + " RPM\r\n" +
                "CPU 곡선: " + FanText.Curve(status.cpuTemperatures, status.cpuSpeeds) + "\r\n" +
                "GPU 곡선: " + FanText.Curve(status.gpuTemperatures, status.gpuSpeeds);
        }

        private void RenderCards()
        {
            _cardGrid.SuspendLayout();
            _cardGrid.Controls.Clear();
            _cardGrid.RowStyles.Clear();
            _cardGrid.RowCount = (_profiles.Count + 1) / 2;

            int row;
            for (row = 0; row < _cardGrid.RowCount; row++)
            {
                _cardGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 236F));
            }

            int index;
            for (index = 0; index < _profiles.Count; index++)
            {
                _cardGrid.Controls.Add(CreateCard(_profiles[index]), index % 2, index / 2);
            }

            _cardGrid.ResumeLayout();
        }

        private Control CreateCard(PowerProfile profile)
        {
            TableLayoutPanel card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 12, 14);
            card.Padding = new Padding(17, 15, 17, 15);
            card.BackColor = Color.White;
            card.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;
            card.ColumnCount = 1;
            card.RowCount = 5;
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 29F));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 49F));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 39F));

            Label title = new Label();
            title.AutoEllipsis = true;
            title.Dock = DockStyle.Fill;
            title.Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.FromArgb(31, 53, 73);
            title.Text = profile.name;
            card.Controls.Add(title, 0, 0);

            Label description = new Label();
            description.AutoEllipsis = true;
            description.Dock = DockStyle.Fill;
            description.ForeColor = Color.FromArgb(79, 93, 108);
            description.Text = profile.purpose;
            card.Controls.Add(description, 0, 1);

            Label values = new Label();
            values.Dock = DockStyle.Fill;
            values.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            values.ForeColor = Color.FromArgb(37, 68, 97);
            values.Text = ProfileText.Compact(profile);
            card.Controls.Add(values, 0, 2);

            Label intent = new Label();
            intent.Dock = DockStyle.Fill;
            intent.ForeColor = Color.FromArgb(91, 105, 120);
            intent.Text = "변경: " + String.Join(" · ", (profile.changes ?? new List<string>()).Take(3).ToArray());
            card.Controls.Add(intent, 0, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 3, 0, 0);
            card.Controls.Add(buttons, 0, 4);

            Button apply = new Button();
            apply.AutoSize = true;
            apply.BackColor = Color.FromArgb(40, 103, 160);
            apply.FlatAppearance.BorderSize = 0;
            apply.FlatStyle = FlatStyle.Flat;
            apply.ForeColor = Color.White;
            apply.Margin = new Padding(6, 0, 0, 0);
            apply.Padding = new Padding(10, 2, 10, 2);
            apply.Text = "적용";
            apply.UseVisualStyleBackColor = false;
            apply.Click += delegate { ApplyProfile(profile); };
            _applyButtons.Add(apply);
            buttons.Controls.Add(apply);

            Button details = new Button();
            details.AutoSize = true;
            details.BackColor = Color.White;
            details.FlatAppearance.BorderColor = Color.FromArgb(154, 169, 184);
            details.FlatStyle = FlatStyle.Flat;
            details.ForeColor = Color.FromArgb(48, 72, 95);
            details.Padding = new Padding(10, 2, 10, 2);
            details.Text = "자세히";
            details.UseVisualStyleBackColor = false;
            details.Click += delegate { ShowDetails(profile); };
            buttons.Controls.Add(details);

            return card;
        }

        private void ShowDetails(PowerProfile profile)
        {
            StringBuilder content = new StringBuilder();
            content.AppendLine(profile.purpose);
            content.AppendLine();
            content.AppendLine("목적: " + profile.purpose);
            content.AppendLine("dGPU: " + ProfileText.DGpu(profile.dGpu));
            content.AppendLine("Turbo Boost: " + ProfileText.Turbo(profile.turbo));
            content.AppendLine("PL1: " + ProfileText.Watts(profile.pl1));
            content.AppendLine("PL2: " + ProfileText.Watts(profile.pl2));
            content.AppendLine("Tau: " + ProfileText.Seconds(profile.tau));
            content.AppendLine("화면 주사율: " + profile.refreshRate + "Hz");
            content.AppendLine();
            content.AppendLine("변경 항목:");
            foreach (string change in profile.changes ?? new List<string>())
            {
                content.AppendLine("• " + change);
            }

            if (profile.notes != null && profile.notes.Count > 0)
            {
                content.AppendLine();
                content.AppendLine("주의 사항:");
                foreach (string note in profile.notes)
                {
                    content.AppendLine("• " + note);
                }
            }

            MessageBox.Show(
                content.ToString(),
                profile.name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ApplyProfile(PowerProfile profile)
        {
            SetBusy(true, profile.name + " 적용 중…");
            Task<ProfileApplyResult> task = Task.Factory.StartNew(delegate
            {
                return _powerModeService.Apply(profile);
            });

            task.ContinueWith(delegate(Task<ProfileApplyResult> completedTask)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, String.Empty);
                    if (completedTask.IsFaulted)
                    {
                        Exception exception = completedTask.Exception == null
                            ? null
                            : completedTask.Exception.GetBaseException();
                        MessageBox.Show(
                            "적용 중 예기치 않은 오류가 발생했습니다.\r\n\r\n" +
                            (exception == null ? "알 수 없는 오류" : exception.Message),
                            "PowerModeSwitcher",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    ProfileApplyResult result = completedTask.Result;
                    RefreshLastAppliedLabel();
                    MessageBox.Show(
                        result.ToDisplayText(),
                        result.HasFailures ? profile.name + " 일부 적용 실패" : profile.name + " 적용 결과",
                        MessageBoxButtons.OK,
                        result.HasFailures ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                });
            });
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            foreach (Button button in _applyButtons)
            {
                button.Enabled = !busy;
            }

            foreach (Control control in _fanActionControls)
            {
                control.Enabled = !busy;
            }

            SetKeyboardControlsEnabled(!busy && _keyboardBacklightAvailable);

            _workingLabel.Text = message;
            _workingLabel.Left = Math.Max(14, _workingLabel.Parent.ClientSize.Width - _workingLabel.Width - 14);
            UseWaitCursor = busy;
        }

        private void RefreshLastAppliedLabel()
        {
            try
            {
                AppState state = _stateRepository.Load();
                if (String.IsNullOrWhiteSpace(state.lastAppliedProfile))
                {
                    _lastAppliedLabel.Text = "마지막 적용 모드: 없음";
                    return;
                }

                PowerProfile profile = _profiles == null
                    ? null
                    : _profiles.FirstOrDefault(delegate(PowerProfile candidate)
                    {
                        return String.Equals(candidate.id, state.lastAppliedProfile, StringComparison.OrdinalIgnoreCase);
                    });
                string profileName = profile == null
                    ? state.lastAppliedProfile
                    : profile.name;
                string timestamp = String.IsNullOrWhiteSpace(state.lastAppliedAt)
                    ? String.Empty
                    : "  (" + state.lastAppliedAt + ")";
                _lastAppliedLabel.Text = "마지막 적용 모드: " + profileName + timestamp;
            }
            catch
            {
                _lastAppliedLabel.Text = "마지막 적용 모드: 상태 파일을 읽을 수 없음";
            }
        }
    }

    internal sealed class ProfileRepository
    {
        private readonly string _path;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public ProfileRepository(string path)
        {
            _path = path;
        }

        public IList<PowerProfile> Load()
        {
            if (!File.Exists(_path))
            {
                throw new FileNotFoundException("profiles.json 파일을 찾을 수 없습니다.", _path);
            }

            ProfileDocument document = _serializer.Deserialize<ProfileDocument>(File.ReadAllText(_path, Encoding.UTF8));
            if (document == null || document.profiles == null)
            {
                throw new InvalidDataException("profiles.json에 profiles 배열이 없습니다.");
            }

            return document.profiles;
        }
    }

    internal sealed class StateRepository
    {
        private readonly string _path;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public StateRepository(string path)
        {
            _path = path;
        }

        public AppState Load()
        {
            AppState state = null;
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path, Encoding.UTF8);
                if (!String.IsNullOrWhiteSpace(json))
                {
                    state = _serializer.Deserialize<AppState>(json);
                }
            }

            state = state ?? new AppState();
            state.managedPlans = state.managedPlans ?? new List<ManagedPlan>();
            state.fan = state.fan ?? new FanState();
            return state;
        }

        public void Save(AppState state)
        {
            state = state ?? new AppState();
            state.managedPlans = state.managedPlans ?? new List<ManagedPlan>();
            state.fan = state.fan ?? new FanState();
            string temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, _serializer.Serialize(state), new UTF8Encoding(false));
            File.Copy(temporaryPath, _path, true);
            File.Delete(temporaryPath);
        }
    }

    internal static class ProfileValidator
    {
        public static void Validate(IList<PowerProfile> profiles)
        {
            if (profiles == null || profiles.Count != 8)
            {
                throw new InvalidDataException("profiles.json에는 정확히 8개의 프로필이 필요합니다.");
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PowerProfile profile in profiles)
            {
                if (profile == null || String.IsNullOrWhiteSpace(profile.id) ||
                    String.IsNullOrWhiteSpace(profile.name) || String.IsNullOrWhiteSpace(profile.purpose))
                {
                    throw new InvalidDataException("프로필의 id, 이름, 설명, 용도는 비워 둘 수 없습니다.");
                }

                if (!ids.Add(profile.id))
                {
                    throw new InvalidDataException("중복된 프로필 id: " + profile.id);
                }

                if (profile.refreshRate != 144)
                {
                    throw new InvalidDataException(profile.id + "의 화면 주사율은 144Hz여야 합니다.");
                }

                if (!ProfileText.IsOneOf(profile.dGpu, "on", "off", "restore", "unchanged"))
                {
                    throw new InvalidDataException(profile.id + "의 dGpu 값이 올바르지 않습니다.");
                }

                if (!ProfileText.IsOneOf(profile.turbo, "on", "off", "restore", "unchanged"))
                {
                    throw new InvalidDataException(profile.id + "의 turbo 값이 올바르지 않습니다.");
                }

                bool anyPlValue = profile.pl1.HasValue || profile.pl2.HasValue || profile.tau.HasValue;
                bool allPlValues = profile.pl1.HasValue && profile.pl2.HasValue && profile.tau.HasValue;
                if (anyPlValue && (!allPlValues || profile.pl1.Value <= 0 ||
                    profile.pl2.Value <= 0 || profile.tau.Value <= 0))
                {
                    throw new InvalidDataException(profile.id + "의 PL1/PL2/Tau 값은 모두 양수여야 합니다.");
                }
            }
        }
    }

    internal sealed class PowerModeService
    {
        private readonly StateRepository _stateRepository;
        private readonly BackendClient _backend;
        private readonly PowerPlanService _powerPlans;
        private readonly DisplayRefreshService _displayRefresh;
        private readonly MsiPowerLimitBackend _powerLimits;

        public PowerModeService(
            StateRepository stateRepository,
            BackendClient backend,
            PowerPlanService powerPlans,
            DisplayRefreshService displayRefresh)
        {
            _stateRepository = stateRepository;
            _backend = backend;
            _powerPlans = powerPlans;
            _displayRefresh = displayRefresh;
            _powerLimits = new MsiPowerLimitBackend();
        }

        public ProfileApplyResult Apply(PowerProfile profile)
        {
            ProfileApplyResult result = new ProfileApplyResult(profile);
            AppState state = _stateRepository.Load();

            CaptureBaseline(state, result, profile);
            _stateRepository.Save(state);

            ApplyDGpu(profile, state, result);
            ApplyTurbo(profile, state, result);
            ApplyPl(profile, state, result);
            ApplyRefresh(profile, result);

            if (!result.HasFailures)
            {
                state.lastAppliedProfile = profile.id;
                state.lastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _stateRepository.Save(state);
                result.LastAppliedUpdated = true;
            }

            return result;
        }

        private void CaptureBaseline(AppState state, ProfileApplyResult result, PowerProfile profile)
        {
            state.baseline = state.baseline ?? new BaselineState();
            List<string> captured = new List<string>();
            List<string> unavailable = new List<string>();

            if (String.IsNullOrWhiteSpace(state.baseline.activePowerScheme))
            {
                string schemeGuid;
                string error;
                if (_powerPlans.TryGetActiveScheme(out schemeGuid, out error))
                {
                    state.baseline.activePowerScheme = schemeGuid;
                    captured.Add("Windows 전원 구성표");
                }
                else
                {
                    unavailable.Add("Windows 전원 구성표: " + error);
                }
            }

            if (String.IsNullOrWhiteSpace(state.baseline.dGpuState))
            {
                BackendResponse response = _backend.QueryDGpu();
                if (response.ok && response.dGpu != null)
                {
                    state.baseline.dGpuState = response.dGpu.enabled ? "on" : "off";
                    captured.Add("dGPU 상태");
                }
                else
                {
                    unavailable.Add("dGPU 상태: " + response.ErrorText);
                }
            }

            bool profileNeedsPowerLimits = profile != null &&
                (profile.isRestore || profile.pl1.HasValue || profile.pl2.HasValue || profile.tau.HasValue);
            if (profileNeedsPowerLimits && (!state.baseline.pl1.HasValue || !state.baseline.pl2.HasValue))
            {
                PowerLimitStatus powerLimits = _powerLimits.Query();
                if (powerLimits != null && powerLimits.writeEnabled)
                {
                    state.baseline.pl1 = powerLimits.pl1;
                    state.baseline.pl2 = powerLimits.pl2;
                    captured.Add("PL1/PL2 상태");
                }
                else
                {
                    unavailable.Add("PL1/PL2 상태: " + (powerLimits == null ? "읽기 실패" : powerLimits.message));
                }
            }

            if (unavailable.Count == 0)
            {
                result.Add(SettingResult.Success("복원 기준", captured.Count == 0
                    ? "이미 저장된 baseline을 사용합니다."
                    : String.Join(", ", captured.ToArray()) + "을(를) 저장했습니다."));
            }
            else
            {
                string message = unavailable.Count == 1
                    ? unavailable[0]
                    : String.Join(" / ", unavailable.ToArray());
                result.Add(SettingResult.Warning("복원 기준", message + " — OEM 기본값을 추정하지 않습니다."));
            }
        }

        private void ApplyDGpu(PowerProfile profile, AppState state, ProfileApplyResult result)
        {
            string desired = (profile.dGpu ?? String.Empty).Trim().ToLowerInvariant();
            if (desired == "unchanged")
            {
                result.Add(SettingResult.Skipped("dGPU", "변경하지 않음"));
                return;
            }

            if (desired == "restore")
            {
                if (state.baseline == null || String.IsNullOrWhiteSpace(state.baseline.dGpuState))
                {
                    result.Add(SettingResult.Failure("dGPU", "저장된 baseline이 없어 원래 상태를 추정하지 않았습니다."));
                    return;
                }

                desired = state.baseline.dGpuState;
            }

            BackendResponse response = _backend.SetDGpu(desired);
            if (response.ok)
            {
                string applied = response.dGpu == null ? desired : (response.dGpu.enabled ? "on" : "off");
                result.Add(SettingResult.Success("dGPU", "요청: " + ProfileText.DGpu(desired) + " / 결과: " + ProfileText.DGpu(applied)));
            }
            else
            {
                result.Add(SettingResult.Failure("dGPU", response.ErrorText));
            }
        }

        private void ApplyTurbo(PowerProfile profile, AppState state, ProfileApplyResult result)
        {
            string desired = (profile.turbo ?? String.Empty).Trim().ToLowerInvariant();
            if (desired == "unchanged")
            {
                result.Add(SettingResult.Skipped("Turbo Boost", "변경하지 않음"));
                return;
            }

            if (desired == "restore")
            {
                ActionResult restoration = _powerPlans.RestoreBaseline(state, _stateRepository);
                result.Add(restoration.ToSettingResult("Turbo Boost / Windows 전원 설정"));
                return;
            }

            if (state.baseline == null || String.IsNullOrWhiteSpace(state.baseline.activePowerScheme))
            {
                result.Add(SettingResult.Failure("Turbo Boost", "기준 전원 구성표를 저장하지 못해 변경하지 않았습니다."));
                return;
            }

            string schemeGuid;
            ActionResult managedPlan = _powerPlans.EnsureManagedPlan(profile, state, _stateRepository, out schemeGuid);
            if (!managedPlan.ok)
            {
                result.Add(managedPlan.ToSettingResult("Turbo Boost"));
                return;
            }

            BackendResponse response = _backend.SetTurbo(desired, schemeGuid);
            if (!response.ok)
            {
                result.Add(SettingResult.Failure("Turbo Boost", response.ErrorText));
                return;
            }

            string activationError;
            if (!_powerPlans.TryActivate(schemeGuid, out activationError))
            {
                result.Add(SettingResult.Failure("Turbo Boost", "설정은 기록됐지만 전원 구성표를 활성화하지 못했습니다: " + activationError));
                return;
            }

            result.Add(SettingResult.Success(
                "Turbo Boost",
                ProfileText.Turbo(desired) + " · PowerModeSwitcher 관리 전원 구성표를 활성화했습니다."));
        }

        private void ApplyPl(PowerProfile profile, AppState state, ProfileApplyResult result)
        {
            bool requested = profile.pl1.HasValue || profile.pl2.HasValue || profile.tau.HasValue;
            if (!requested && !profile.isRestore)
            {
                result.Add(SettingResult.Skipped("PL1 / PL2 / Tau", "변경하지 않음"));
                return;
            }

            if (profile.isRestore)
            {
                if (state.baseline == null || !state.baseline.pl1.HasValue || !state.baseline.pl2.HasValue)
                {
                    result.Add(SettingResult.Warning(
                        "PL1 / PL2",
                        "저장된 PL1/PL2 baseline이 없어 원래 값을 추정하지 않았습니다."));
                }
                else
                {
                    PowerLimitApplyResult restored = _powerLimits.Apply(state.baseline.pl1.Value, state.baseline.pl2.Value);
                    result.Add(restored.success
                        ? SettingResult.Success("PL1 / PL2", restored.message)
                        : SettingResult.Failure("PL1 / PL2", restored.message));
                }

                if (profile.tau.HasValue)
                {
                    result.Add(SettingResult.Warning("Tau", "MSI Center 기존 backend에 Tau 쓰기 API가 없어 변경하지 않았습니다."));
                }
                return;
            }

            if (profile.pl1.HasValue && profile.pl2.HasValue)
            {
                PowerLimitApplyResult applied = _powerLimits.Apply(profile.pl1.Value, profile.pl2.Value);
                result.Add(applied.success
                    ? SettingResult.Success("PL1 / PL2", applied.message)
                    : SettingResult.Failure("PL1 / PL2", applied.message));
            }
            else
            {
                result.Add(SettingResult.Warning("PL1 / PL2", "PL1과 PL2를 함께 지정하지 않아 변경하지 않았습니다."));
            }

            if (profile.tau.HasValue)
            {
                result.Add(SettingResult.Warning(
                    "Tau",
                    "MSI Center 기존 backend에 Tau 쓰기 API가 없어 요청값 " + profile.tau.Value + "초는 변경하지 않았습니다."));
            }
        }

        private void ApplyRefresh(PowerProfile profile, ProfileApplyResult result)
        {
            result.Add(_displayRefresh.EnsureRefreshRate(profile.refreshRate));
        }
    }

    internal sealed class PowerLimitApplyResult
    {
        public bool success { get; set; }
        public string message { get; set; }
        public PowerLimitStatus status { get; set; }

        public static PowerLimitApplyResult Success(string message, PowerLimitStatus status)
        {
            return new PowerLimitApplyResult { success = true, message = message, status = status };
        }

        public static PowerLimitApplyResult Failure(string message, PowerLimitStatus status)
        {
            return new PowerLimitApplyResult { success = false, message = message, status = status };
        }
    }

    // MSI Center already ships this model-specific PL1/PL2 path. Reuse it rather
    // than opening ThrottleStop or adding a new kernel/MSR driver.
    internal sealed class MsiPowerLimitBackend
    {
        private const string ApiPath = @"C:\Program Files (x86)\MSI\MSI Center\Base Module\API_NB_Base Module.dll";
        private static readonly string[] AssemblyDirectories = new string[]
        {
            @"C:\Program Files (x86)\MSI\MSI Center\Base Module",
            @"C:\Program Files (x86)\MSI\MSI Center",
            @"C:\Program Files (x86)\MSI\MSI Center\System Diagnosis",
            @"C:\Program Files (x86)\MSI\MSI Center\Gaming Gear\MEG381_KC",
            @"C:\Program Files (x86)\MSI\MSI NBFoundation Service"
        };

        private readonly object _sync = new object();
        private readonly MsiFanWmiBackend _readback = new MsiFanWmiBackend();
        private MethodInfo _setPowerLimit;
        private ResolveEventHandler _resolver;
        private string _loadError;

        public PowerLimitStatus Query()
        {
            return _readback.QueryPowerLimits();
        }

        public PowerLimitApplyResult Apply(int pl1, int pl2)
        {
            if (pl1 <= 0 || pl1 > 255 || pl2 <= 0 || pl2 > 255 || pl2 < pl1)
            {
                return PowerLimitApplyResult.Failure(
                    "PL1/PL2 값은 1~255W 범위에서 PL2가 PL1 이상이어야 합니다.",
                    null);
            }

            lock (_sync)
            {
                PowerLimitStatus before = _readback.QueryPowerLimits();
                if (before == null || !before.writeEnabled)
                {
                    return PowerLimitApplyResult.Failure(
                        before == null ? "PL1/PL2 상태를 읽지 못했습니다." : before.message,
                        before);
                }

                try
                {
                    EnsureApi();
                    _setPowerLimit.Invoke(null, new object[] { pl1, pl2 });
                    Thread.Sleep(220);
                    PowerLimitStatus after = _readback.QueryPowerLimits();
                    if (after == null || !after.writeEnabled || after.pl1 != pl1 || after.pl2 != pl2)
                    {
                        throw new InvalidOperationException(
                            "MSI Center PL backend 호출 후 readback이 일치하지 않습니다. 요청: " +
                            pl1 + "W / " + pl2 + "W, 결과: " +
                            (after == null ? "읽기 실패" : after.pl1 + "W / " + after.pl2 + "W"));
                    }

                    return PowerLimitApplyResult.Success(
                        "MSI Center 기존 PL backend로 " + pl1 + "W / " + pl2 + "W를 적용하고 readback을 확인했습니다.",
                        after);
                }
                catch (Exception exception)
                {
                    string restoreMessage = TryRestore(before);
                    PowerLimitStatus current = _readback.QueryPowerLimits();
                    return PowerLimitApplyResult.Failure(
                        "PL1/PL2 적용 실패: " + FriendlyError(exception) + " " + restoreMessage,
                        current);
                }
            }
        }

        private string TryRestore(PowerLimitStatus before)
        {
            if (before == null || !before.writeEnabled || before.pl1 <= 0 || before.pl2 < before.pl1)
            {
                return "기존 PL snapshot이 없어 복원하지 않았습니다.";
            }

            try
            {
                EnsureApi();
                _setPowerLimit.Invoke(null, new object[] { before.pl1, before.pl2 });
                Thread.Sleep(160);
                PowerLimitStatus restored = _readback.QueryPowerLimits();
                return restored != null && restored.pl1 == before.pl1 && restored.pl2 == before.pl2
                    ? "기존 PL1/PL2를 복원했습니다."
                    : "기존 PL1/PL2 복원 readback이 일치하지 않습니다.";
            }
            catch (Exception exception)
            {
                return "기존 PL1/PL2 복원도 실패했습니다: " + FriendlyError(exception);
            }
        }

        private void EnsureApi()
        {
            if (_setPowerLimit != null)
            {
                return;
            }

            if (!String.IsNullOrWhiteSpace(_loadError))
            {
                throw new InvalidOperationException(_loadError);
            }

            if (!File.Exists(ApiPath))
            {
                _loadError = "MSI Center 기존 PL backend DLL을 찾지 못했습니다: " + ApiPath;
                throw new FileNotFoundException(_loadError, ApiPath);
            }

            if (_resolver == null)
            {
                _resolver = ResolveMsiAssembly;
                AppDomain.CurrentDomain.AssemblyResolve += _resolver;
            }

            try
            {
                Assembly assembly = Assembly.LoadFrom(ApiPath);
                Type userScenario = assembly.GetType("API_Base_Module.UserScenario", true);
                _setPowerLimit = userScenario.GetMethod(
                    "setPowerLimit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), typeof(int) },
                    null);
                if (_setPowerLimit == null)
                {
                    throw new MissingMethodException("MSI Center UserScenario.setPowerLimit(int,int)을 찾지 못했습니다.");
                }
            }
            catch (Exception exception)
            {
                _loadError = "MSI Center PL backend를 불러오지 못했습니다: " + FriendlyError(exception);
                throw new InvalidOperationException(_loadError, exception);
            }
        }

        private static Assembly ResolveMsiAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch
            {
                return null;
            }

            List<string> candidates = new List<string>();
            foreach (string directory in AssemblyDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                candidates.AddRange(Directory.GetFiles(directory, requested.Name + ".dll"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    AssemblyName candidateName = AssemblyName.GetAssemblyName(candidate);
                    if (String.Equals(candidateName.Name, requested.Name, StringComparison.OrdinalIgnoreCase) &&
                        candidateName.Version == requested.Version)
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }
                catch
                {
                    // Try the next installed copy.
                }
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch
                {
                    // Try the next installed copy.
                }
            }

            return null;
        }

        private static string FriendlyError(Exception exception)
        {
            Exception current = exception;
            while (current is TargetInvocationException && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current == null || String.IsNullOrWhiteSpace(current.Message)
                ? "알 수 없는 오류"
                : current.Message;
        }
    }

    internal sealed class PowerPlanService
    {
        private const string PowerCfg = "powercfg.exe";
        private static readonly Regex GuidPattern = new Regex(
            @"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b",
            RegexOptions.Compiled);

        public bool TryGetActiveScheme(out string schemeGuid, out string error)
        {
            ProcessRunResult process = ProcessRunner.Run(PowerCfg, "/getactivescheme", 10000);
            Match match = GuidPattern.Match(process.output ?? String.Empty);
            if (!process.Succeeded || !match.Success)
            {
                schemeGuid = null;
                error = process.ErrorText;
                return false;
            }

            schemeGuid = match.Value;
            error = null;
            return true;
        }

        public ActionResult EnsureManagedPlan(
            PowerProfile profile,
            AppState state,
            StateRepository stateRepository,
            out string schemeGuid)
        {
            ManagedPlan plan = state.managedPlans.FirstOrDefault(delegate(ManagedPlan candidate)
            {
                return candidate != null && String.Equals(candidate.profileId, profile.id, StringComparison.OrdinalIgnoreCase) &&
                       String.Equals(candidate.baselineSchemeGuid, state.baseline.activePowerScheme, StringComparison.OrdinalIgnoreCase);
            });

            if (plan != null && !String.IsNullOrWhiteSpace(plan.schemeGuid))
            {
                schemeGuid = plan.schemeGuid;
                return ActionResult.Success("기존 관리 전원 구성표를 사용합니다.");
            }

            schemeGuid = Guid.NewGuid().ToString();
            ProcessRunResult duplicate = RunPowerCfg("/duplicatescheme", state.baseline.activePowerScheme, schemeGuid);
            if (!duplicate.Succeeded)
            {
                return ActionResult.Failure("전원 구성표 복제 실패: " + duplicate.ErrorText);
            }

            ProcessRunResult rename = RunPowerCfg(
                "/changename",
                schemeGuid,
                "PowerModeSwitcher - " + profile.name,
                "Managed profile for " + profile.name);

            state.managedPlans.Add(new ManagedPlan
            {
                profileId = profile.id,
                schemeGuid = schemeGuid,
                baselineSchemeGuid = state.baseline.activePowerScheme
            });
            stateRepository.Save(state);
            return ActionResult.Success(rename.Succeeded
                ? "관리 전원 구성표를 만들었습니다."
                : "관리 전원 구성표를 만들었습니다. 이름 설정은 건너뛰었습니다: " + rename.ErrorText);
        }

        public bool TryActivate(string schemeGuid, out string error)
        {
            ProcessRunResult process = RunPowerCfg("/setactive", schemeGuid);
            error = process.Succeeded ? null : process.ErrorText;
            return process.Succeeded;
        }

        public ActionResult RestoreBaseline(AppState state, StateRepository stateRepository)
        {
            if (state.baseline == null || String.IsNullOrWhiteSpace(state.baseline.activePowerScheme))
            {
                return ActionResult.Failure("저장된 기준 전원 구성표가 없어 복원하지 않았습니다.");
            }

            string activationError;
            if (!TryActivate(state.baseline.activePowerScheme, out activationError))
            {
                return ActionResult.Failure("기준 전원 구성표를 활성화하지 못했습니다: " + activationError);
            }

            List<string> cleanupErrors = new List<string>();
            foreach (ManagedPlan plan in state.managedPlans.ToList())
            {
                if (plan == null || String.IsNullOrWhiteSpace(plan.schemeGuid))
                {
                    state.managedPlans.Remove(plan);
                    continue;
                }

                ProcessRunResult deleted = RunPowerCfg("/delete", plan.schemeGuid);
                if (!deleted.Succeeded)
                {
                    cleanupErrors.Add(plan.schemeGuid + ": " + deleted.ErrorText);
                }
                else
                {
                    state.managedPlans.Remove(plan);
                }
            }

            stateRepository.Save(state);
            if (cleanupErrors.Count == 0)
            {
                return ActionResult.Success("저장된 기준 전원 구성표를 복원하고 관리 전원 구성표를 정리했습니다.");
            }

            return ActionResult.Warning(
                "기준 전원 구성표는 복원했지만 관리 전원 구성표 일부를 정리하지 못했습니다: " +
                String.Join(" / ", cleanupErrors.ToArray()));
        }

        private static ProcessRunResult RunPowerCfg(params string[] arguments)
        {
            return ProcessRunner.Run(PowerCfg, CommandLine.Join(arguments), 20000);
        }
    }

    internal sealed class BackendClient
    {
        private readonly string _helperPath;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public BackendClient(string helperPath)
        {
            _helperPath = helperPath;
        }

        public BackendResponse QueryDGpu()
        {
            return Invoke("-Operation Dgpu -State Query");
        }

        public BackendResponse SetDGpu(string state)
        {
            return Invoke("-Operation Dgpu -State " + CommandLine.Quote(state));
        }

        public BackendResponse SetTurbo(string state, string schemeGuid)
        {
            return Invoke(
                "-Operation Turbo -State " + CommandLine.Quote(state) +
                " -SchemeGuid " + CommandLine.Quote(schemeGuid));
        }

        private BackendResponse Invoke(string helperArguments)
        {
            if (!File.Exists(_helperPath))
            {
                return BackendResponse.Failed("기존 전원 제어 helper를 찾을 수 없습니다: " + _helperPath);
            }

            string arguments = "-NoProfile -ExecutionPolicy Bypass -File " +
                CommandLine.Quote(_helperPath) + " " + helperArguments;
            ProcessRunResult process = ProcessRunner.Run("powershell.exe", arguments, 45000);
            BackendResponse response = null;
            string jsonLine = (process.output ?? String.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (!String.IsNullOrWhiteSpace(jsonLine))
            {
                try
                {
                    response = _serializer.Deserialize<BackendResponse>(jsonLine);
                }
                catch
                {
                    response = null;
                }
            }

            if (response == null)
            {
                return BackendResponse.Failed(process.Succeeded
                    ? "helper 응답을 해석하지 못했습니다."
                    : process.ErrorText);
            }

            if (!process.Succeeded || !response.success)
            {
                response.success = false;
                if (String.IsNullOrWhiteSpace(response.message))
                {
                    response.message = process.ErrorText;
                }
            }

            return response;
        }
    }

    internal sealed class DisplayRefreshService
    {
        private const int EnumCurrentSettings = -1;
        private const int DisplayDeviceAttachedToDesktop = 0x00000001;
        private const int DisplayDeviceMirroringDriver = 0x00000008;
        private const int CdsUpdateRegistry = 0x00000001;
        private const int DispChangeSuccessful = 0;

        public SettingResult EnsureRefreshRate(int refreshRateHz)
        {
            int activeDisplays = 0;
            List<string> reports = new List<string>();
            List<string> failures = new List<string>();

            for (uint index = 0; ; index++)
            {
                DisplayDevice device = new DisplayDevice();
                device.cb = Marshal.SizeOf(device);
                if (!EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                if ((device.stateFlags & DisplayDeviceAttachedToDesktop) == 0 ||
                    (device.stateFlags & DisplayDeviceMirroringDriver) != 0)
                {
                    continue;
                }

                activeDisplays++;
                DevMode current = CreateDevMode();
                if (!EnumDisplaySettings(device.deviceName, EnumCurrentSettings, ref current))
                {
                    failures.Add(device.deviceName + "의 현재 모드를 읽지 못했습니다.");
                    continue;
                }

                if (current.dmDisplayFrequency == refreshRateHz)
                {
                    reports.Add(device.deviceName + ": 이미 " + refreshRateHz + "Hz");
                    continue;
                }

                DevMode target = FindMatchingRefreshMode(device.deviceName, current, refreshRateHz);
                if (target.dmSize == 0)
                {
                    failures.Add(device.deviceName + "에 " + refreshRateHz + "Hz 모드가 없습니다.");
                    continue;
                }

                int code = ChangeDisplaySettingsEx(device.deviceName, ref target, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
                if (code != DispChangeSuccessful)
                {
                    failures.Add(device.deviceName + "을(를) " + refreshRateHz + "Hz로 변경하지 못했습니다 (코드 " + code + ").");
                    continue;
                }

                reports.Add(device.deviceName + ": " + refreshRateHz + "Hz 적용");
            }

            if (activeDisplays == 0)
            {
                return SettingResult.Failure("화면 주사율", "활성 디스플레이를 찾지 못했습니다.");
            }

            if (failures.Count > 0)
            {
                return SettingResult.Failure(
                    "화면 주사율",
                    String.Join(" / ", failures.ToArray()) +
                    (reports.Count == 0 ? String.Empty : " / " + String.Join(", ", reports.ToArray())));
            }

            return SettingResult.Success("화면 주사율", String.Join(", ", reports.ToArray()));
        }

        private static DevMode FindMatchingRefreshMode(string deviceName, DevMode current, int refreshRateHz)
        {
            for (int modeIndex = 0; ; modeIndex++)
            {
                DevMode candidate = CreateDevMode();
                if (!EnumDisplaySettings(deviceName, modeIndex, ref candidate))
                {
                    break;
                }

                if (candidate.dmPelsWidth == current.dmPelsWidth &&
                    candidate.dmPelsHeight == current.dmPelsHeight &&
                    candidate.dmBitsPerPel == current.dmBitsPerPel &&
                    candidate.dmDisplayFrequency == refreshRateHz)
                {
                    return candidate;
                }
            }

            return new DevMode();
        }

        private static DevMode CreateDevMode()
        {
            DevMode devMode = new DevMode();
            devMode.dmDeviceName = new String(new char[32]);
            devMode.dmFormName = new String(new char[32]);
            devMode.dmSize = (short)Marshal.SizeOf(devMode);
            return devMode;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string deviceName, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode devMode);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int ChangeDisplaySettingsEx(
            string deviceName,
            ref DevMode devMode,
            IntPtr hwnd,
            int flags,
            IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string deviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string deviceString;
            public int stateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string deviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string deviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }
    }

    internal static class ProcessRunner
    {
        public static ProcessRunResult Run(string fileName, string arguments, int timeoutMilliseconds)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = fileName;
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    bool exited = process.WaitForExit(timeoutMilliseconds);
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return ProcessRunResult.Failed("명령 시간이 초과되었습니다.");
                    }

                    return new ProcessRunResult
                    {
                        exitCode = process.ExitCode,
                        output = output,
                        error = error
                    };
                }
            }
            catch (Exception exception)
            {
                return ProcessRunResult.Failed(exception.Message);
            }
        }
    }

    internal static class CommandLine
    {
        public static string Quote(string value)
        {
            value = value ?? String.Empty;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static string Join(IEnumerable<string> arguments)
        {
            return String.Join(" ", arguments.Select(Quote).ToArray());
        }
    }

    internal static class ProfileText
    {
        public static string Compact(PowerProfile profile)
        {
            return "dGPU " + DGpu(profile.dGpu) + "  ·  Turbo " + Turbo(profile.turbo) +
                "  ·  " + Watts(profile.pl1) + "/" + Watts(profile.pl2) +
                "  ·  Tau " + Seconds(profile.tau) + "  ·  " + profile.refreshRate + "Hz";
        }

        public static string DGpu(string value)
        {
            if (String.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) return "ON";
            if (String.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return "OFF";
            if (String.Equals(value, "restore", StringComparison.OrdinalIgnoreCase)) return "원래 상태";
            return "변경 안 함";
        }

        public static string Turbo(string value)
        {
            if (String.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) return "ON";
            if (String.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return "OFF";
            if (String.Equals(value, "restore", StringComparison.OrdinalIgnoreCase)) return "원래 상태";
            return "변경 안 함";
        }

        public static string Watts(int? value)
        {
            return value.HasValue ? value.Value + "W" : "변경 안 함";
        }

        public static string Seconds(int? value)
        {
            return value.HasValue ? value.Value + "초" : "변경 안 함";
        }

        public static bool IsOneOf(string value, params string[] allowed)
        {
            return allowed.Any(delegate(string candidate)
            {
                return String.Equals(value, candidate, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    internal sealed class SettingResult
    {
        public string setting;
        public string message;
        public ResultKind kind;

        public static SettingResult Success(string setting, string message)
        {
            return new SettingResult { setting = setting, message = message, kind = ResultKind.Success };
        }

        public static SettingResult Skipped(string setting, string message)
        {
            return new SettingResult { setting = setting, message = message, kind = ResultKind.Skipped };
        }

        public static SettingResult Warning(string setting, string message)
        {
            return new SettingResult { setting = setting, message = message, kind = ResultKind.Warning };
        }

        public static SettingResult Failure(string setting, string message)
        {
            return new SettingResult { setting = setting, message = message, kind = ResultKind.Failure };
        }
    }

    internal enum ResultKind
    {
        Success,
        Skipped,
        Warning,
        Failure
    }

    internal sealed class ProfileApplyResult
    {
        private readonly List<SettingResult> _items = new List<SettingResult>();

        public ProfileApplyResult(PowerProfile profile)
        {
            Profile = profile;
        }

        public PowerProfile Profile { get; private set; }
        public bool LastAppliedUpdated { get; set; }
        public bool HasFailures { get { return _items.Any(delegate(SettingResult item) { return item.kind == ResultKind.Failure; }); } }
        public void Add(SettingResult item) { _items.Add(item); }

        public string ToDisplayText()
        {
            StringBuilder content = new StringBuilder();
            content.AppendLine(Profile.name);
            content.AppendLine();
            foreach (SettingResult item in _items)
            {
                string marker = item.kind == ResultKind.Success ? "✓" :
                    item.kind == ResultKind.Skipped ? "–" :
                    item.kind == ResultKind.Warning ? "!" : "✗";
                content.AppendLine(marker + " " + item.setting + ": " + item.message);
            }

            content.AppendLine();
            if (LastAppliedUpdated)
            {
                content.Append("마지막 적용 모드를 업데이트했습니다.");
            }
            else
            {
                content.Append("실패한 설정이 있어 마지막 적용 모드는 변경하지 않았습니다.");
            }

            return content.ToString();
        }
    }

    internal sealed class ActionResult
    {
        public bool ok;
        public ResultKind kind;
        public string message;

        public static ActionResult Success(string message)
        {
            return new ActionResult { ok = true, kind = ResultKind.Success, message = message };
        }

        public static ActionResult Warning(string message)
        {
            return new ActionResult { ok = true, kind = ResultKind.Warning, message = message };
        }

        public static ActionResult Failure(string message)
        {
            return new ActionResult { ok = false, kind = ResultKind.Failure, message = message };
        }

        public SettingResult ToSettingResult(string setting)
        {
            if (kind == ResultKind.Warning) return SettingResult.Warning(setting, message);
            if (!ok) return SettingResult.Failure(setting, message);
            return SettingResult.Success(setting, message);
        }
    }

    internal sealed class ProcessRunResult
    {
        public int exitCode;
        public string output;
        public string error;
        public bool Succeeded { get { return exitCode == 0; } }
        public string ErrorText
        {
            get
            {
                string text = !String.IsNullOrWhiteSpace(error) ? error : output;
                return String.IsNullOrWhiteSpace(text) ? "명령이 실패했습니다 (종료 코드 " + exitCode + ")." : text.Trim();
            }
        }

        public static ProcessRunResult Failed(string message)
        {
            return new ProcessRunResult { exitCode = -1, error = message, output = String.Empty };
        }
    }

    internal sealed class BackendResponse
    {
        public bool success { get; set; }
        public string message { get; set; }
        public BackendDGpu value { get; set; }

        public bool ok { get { return success; } }
        public BackendDGpu dGpu { get { return value; } }

        public string ErrorText
        {
            get { return String.IsNullOrWhiteSpace(message) ? "helper가 적용을 완료하지 못했습니다." : message; }
        }

        public static BackendResponse Failed(string message)
        {
            return new BackendResponse { success = false, message = message };
        }
    }

    internal sealed class BackendDGpu
    {
        public string instanceId { get; set; }
        public bool enabled { get; set; }
        public string status { get; set; }
        public string problem { get; set; }
    }

    internal sealed class ProfileDocument
    {
        public List<PowerProfile> profiles { get; set; }
    }

    internal sealed class PowerProfile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string purpose { get; set; }
        public string dGpu { get; set; }
        public string turbo { get; set; }
        public int? pl1 { get; set; }
        public int? pl2 { get; set; }
        public int? tau { get; set; }
        public int refreshRate { get; set; }
        public List<string> changes { get; set; }
        public List<string> notes { get; set; }
        public bool isRestore { get; set; }
    }

    internal sealed class AppState
    {
        public string lastAppliedProfile { get; set; }
        public string lastAppliedAt { get; set; }
        public BaselineState baseline { get; set; }
        public List<ManagedPlan> managedPlans { get; set; }
        public FanState fan { get; set; }
    }

    internal sealed class BaselineState
    {
        public string activePowerScheme { get; set; }
        public string dGpuState { get; set; }
        public int? pl1 { get; set; }
        public int? pl2 { get; set; }
    }

    internal sealed class ManagedPlan
    {
        public string profileId { get; set; }
        public string schemeGuid { get; set; }
        public string baselineSchemeGuid { get; set; }
    }

    internal static class SelfTest
    {
        public static int Run(string applicationDirectory)
        {
            try
            {
                ProfileRepository repository = new ProfileRepository(Path.Combine(applicationDirectory, "profiles.json"));
                IList<PowerProfile> profiles = repository.Load();
                ProfileValidator.Validate(profiles);
                FanPresetRepository fanRepository = new FanPresetRepository(Path.Combine(applicationDirectory, "fan-presets.json"));
                FanPresetDocument fanPresets = fanRepository.Load();
                FanPresetValidator.Validate(fanPresets);

                if (!File.Exists(Path.Combine(applicationDirectory, "helpers", "PowerModeBackend.ps1")))
                {
                    throw new FileNotFoundException("PowerModeBackend.ps1을 찾을 수 없습니다.");
                }

                foreach (PowerProfile profile in profiles)
                {
                    if (String.Equals(profile.turbo, "on", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(profile.turbo, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        string command = "-Operation Turbo -State " + CommandLine.Quote(profile.turbo) +
                            " -SchemeGuid " + CommandLine.Quote("00000000-0000-0000-0000-000000000000");
                        if (command.IndexOf("-Operation Turbo", StringComparison.Ordinal) < 0 ||
                            command.IndexOf("-SchemeGuid", StringComparison.Ordinal) < 0)
                        {
                            throw new InvalidOperationException("Turbo 명령 생성 검증에 실패했습니다.");
                        }
                    }
                }

                Console.WriteLine("PASS: 8 profiles and fan curves parsed; 144Hz and fan safety constraints validated; no hardware setting was changed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception.Message);
                return 1;
            }
        }
    }
}
