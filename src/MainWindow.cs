using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenTimeoutScheduler
{
    public sealed class PeriodEditor
    {
        public TextBox Start, End;
        public Border Container;
        public TextBlock Index;
    }

    public sealed class MainWindow : Window
    {
        private readonly AppFiles files;
        private readonly bool preview, background;
        private readonly Border surface;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly List<PeriodEditor> editors = new List<PeriodEditor>();
        private readonly ToggleButton[] weekdays = new ToggleButton[7];
        private readonly TextBox workInput, otherInput;
        private readonly CheckBox autoStart;
        private readonly StackPanel rows;
        private readonly Canvas timeline;
        private Settings settings;
        private RestoreJournal journal;
        private PowerController controller;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem toggle;
        private System.Drawing.Icon trayIcon;
        private bool exiting, sessionEnding, loading = true, dirty, disposed, initialized;
        private string lastError, loadError;
        private static readonly Brush Accent = BrushOf("#286448");
        private static readonly Brush Muted = BrushOf("#69776E");
        private static readonly Brush Error = BrushOf("#A54630");
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public MainWindow(AppFiles files, bool background, bool preview)
        {
            this.files = files; this.background = background; this.preview = preview;
            Title = Program.Title; Width = 920; Height = 734; MinWidth = 900; MinHeight = 710;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BrushOf("#F5F7F5"); FontFamily = new FontFamily("Microsoft YaHei UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14), UseAeroCaptionButtons = false });
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTimeoutScheduler.AppWindow.xaml"))
            {
                if (stream == null) throw new FileNotFoundException("界面资源缺失，请重新构建工具。");
                surface = (Border)XamlReader.Load(stream);
            }
            Content = surface;
            workInput = Named<TextBox>("WorkMinutesInput"); otherInput = Named<TextBox>("OtherMinutesInput");
            autoStart = Named<CheckBox>("AutoStartSwitch"); rows = Named<StackPanel>("PeriodRows"); timeline = Named<Canvas>("Timeline");
            AutomationProperties.SetName(workInput, "工作时段等待分钟数");
            AutomationProperties.SetName(otherInput, "其余时间等待分钟数");
            AutomationProperties.SetName(autoStart, "登录 Windows 后自动启动");
            Named<Button>("SaveButton").Click += delegate { SaveAndEnable(); };
            Named<Button>("PauseButton").Click += delegate { Pause(); };
            Named<Button>("AddPeriodButton").Click += delegate { AddPeriod("18:00", "19:00"); MarkDirty(); Named<ScrollViewer>("PeriodScroll").ScrollToEnd(); };
            Named<Button>("CloseButton").Click += delegate { HideToTray(); };
            Named<Button>("MinimizeButton").Click += delegate { HideToTray(); };
            PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                { SaveAndEnable(); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Escape) { HideToTray(); e.Handled = true; }
            };
            workInput.TextChanged += delegate { MarkDirty(); }; otherInput.TextChanged += delegate { MarkDirty(); };
            autoStart.Checked += delegate { MarkDirty(); }; autoStart.Unchecked += delegate { MarkDirty(); };
            timeline.SizeChanged += delegate { DrawTimeline(); };
            CreateWeekdays(); LoadSettings();
            controller = new PowerController(new NativePower(), journal, delegate { FileStore.Write(files.JournalPath, journal); });
            LoadFields();
            UpdateDisplay(false); DrawTimeline();
            if (!preview) CreateTray();
            timer.Tick += delegate { RefreshState(); };
            Loaded += WindowLoaded;
            SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle).AddHook(WindowMessage);
                // Windows 11 applies native rounded corners; earlier Windows use WindowChrome.
                try { int round = 2; DwmSetWindowAttribute(handle, 33, ref round, 4); }
                catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            };
            StateChanged += delegate { if (!preview && WindowState == WindowState.Minimized) HideToTray(); };
            Closing += WindowClosing;
            Closed += delegate { DisposeResources(); };
        }

        private static Brush BrushOf(string hex)
        { Brush result = (Brush)new BrushConverter().ConvertFromInvariantString(hex); result.Freeze(); return result; }
        private T Named<T>(string name) where T : FrameworkElement
        {
            T item = surface.FindName(name) as T;
            if (item == null) throw new InvalidDataException("缺少界面控件：" + name);
            return item;
        }
        private void SetText(string name, string text) { Named<TextBlock>(name).Text = text; }

        private void LoadSettings()
        {
            settings = new Settings(); journal = new RestoreJournal();
            if (preview) return;
            try
            {
                settings = FileStore.Read<Settings>(files.SettingsPath); settings.Validate();
                settings.AutoStart = Startup.IsEnabled();
                journal = FileStore.Read<RestoreJournal>(files.JournalPath);
                if (journal.Items == null) throw new InvalidDataException("原设置备份不完整。");
            }
            catch (Exception ex)
            {
                loadError = "配置或备份读取失败，未修改电源设置。" + ex.Message;
                settings = new Settings(); files.Log(loadError);
            }
        }

        private void CreateWeekdays()
        {
            string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            UniformGrid panel = Named<UniformGrid>("Weekdays");
            for (int i = 0; i < 7; i++)
            {
                int day = (i + 1) % 7;
                ToggleButton button = new ToggleButton { Content = names[i], Style = (Style)surface.Resources["Day"] };
                button.Checked += delegate { MarkDirty(); }; button.Unchecked += delegate { MarkDirty(); };
                AutomationProperties.SetName(button, names[i] + "适用工作时段");
                weekdays[day] = button; panel.Children.Add(button);
            }
        }

        private void LoadFields()
        {
            workInput.Text = settings.WorkMinutes.ToString(CultureInfo.InvariantCulture);
            otherInput.Text = settings.OtherMinutes.ToString(CultureInfo.InvariantCulture);
            autoStart.IsChecked = settings.AutoStart;
            for (int i = 0; i < 7; i++) weekdays[i].IsChecked = settings.Days.Contains(i);
            rows.Children.Clear(); editors.Clear();
            foreach (WorkPeriod period in settings.Periods) AddPeriod(period.Start, period.End);
            Named<Button>("PauseButton").IsEnabled = settings.Enabled || journal.Items.Count > 0;
        }

        private void AddPeriod(string start, string end)
        {
            if (editors.Count >= 100) { Feedback("最多可添加 100 个时段。", true); return; }
            PeriodEditor editor = new PeriodEditor();
            Grid grid = new Grid();
            foreach (double width in new double[] { 27, -1, 28, -1, 46 })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            editor.Index = new TextBlock { Text = (editors.Count + 1).ToString("00"), Foreground = Muted,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(editor.Index);
            editor.Start = new TextBox { Text = start, Height = 43, MaxLength = 5, ToolTip = "开始时间，24 小时制 HH:mm", TextAlignment = TextAlignment.Center };
            editor.End = new TextBox { Text = end, Height = 43, MaxLength = 5, ToolTip = "结束时间，24 小时制 HH:mm；早于开始时跨到次日", TextAlignment = TextAlignment.Center };
            Grid.SetColumn(editor.Start, 1); Grid.SetColumn(editor.End, 3);
            grid.Children.Add(editor.Start); grid.Children.Add(editor.End);
            TextBlock dash = new TextBlock { Text = "—", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dash, 2); grid.Children.Add(dash);
            Button remove = new Button { Content = "\uE74D", Style = (Style)surface.Resources["IconButton"],
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "删除这个时段", Background = Brushes.Transparent, Foreground = Muted };
            AutomationProperties.SetName(remove, "删除时段");
            AutomationProperties.SetName(editor.Start, "时段开始时间"); AutomationProperties.SetName(editor.End, "时段结束时间");
            Grid.SetColumn(remove, 4); grid.Children.Add(remove);
            editor.Container = new Border { Child = grid, Margin = new Thickness(0, 0, 0, 10) };
            remove.Click += delegate { RemovePeriod(editor); };
            editor.Start.TextChanged += delegate { MarkDirty(); }; editor.End.TextChanged += delegate { MarkDirty(); };
            editors.Add(editor); rows.Children.Add(editor.Container);
        }

        private void RemovePeriod(PeriodEditor editor)
        {
            editors.Remove(editor); rows.Children.Remove(editor.Container);
            for (int i = 0; i < editors.Count; i++) editors[i].Index.Text = (i + 1).ToString("00");
            MarkDirty();
        }

        private Settings ReadFields()
        {
            int work, other;
            if (!int.TryParse(workInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out work) ||
                !int.TryParse(otherInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out other))
                throw new ArgumentException("等待时间请填写 1 到 1440 之间的整数分钟。");
            Settings result = new Settings { WorkMinutes = work, OtherMinutes = other, Enabled = true,
                AutoStart = autoStart.IsChecked == true, Days = new List<int>(), Periods = new List<WorkPeriod>() };
            for (int i = 0; i < 7; i++) if (weekdays[i].IsChecked == true) result.Days.Add(i);
            foreach (PeriodEditor editor in editors) result.Periods.Add(new WorkPeriod(editor.Start.Text.Trim(), editor.End.Text.Trim()));
            result.Validate(); return result;
        }

        private void MarkDirty()
        {
            if (loading) return;
            dirty = true;
            Feedback("有未保存的修改", false);
            SetText("FooterNote", "点击保存后，才会应用新规则");
            DrawTimeline();
            if (!settings.Enabled && !File.Exists(files.SettingsPath)) UpdateDisplay(false);
        }

        private void Feedback(string text, bool error)
        {
            SetText("Feedback", text); Named<TextBlock>("Feedback").Foreground = error ? Error : Muted;
            Named<TextBlock>("Feedback").ToolTip = error ? text : null;
        }

        private void DrawTimeline()
        {
            if (timeline == null || loading) return;
            timeline.Children.Clear(); double width = timeline.ActualWidth;
            if (width < 1) return;
            try
            {
                Settings draft = ReadFields();
                SetText("TimelineCaption", dirty ? "未保存的时段预览" : "已选时段预览");
                SetText("WorkLegend", "工作 " + draft.WorkMinutes + " 分钟"); SetText("OtherLegend", "其余 " + draft.OtherMinutes + " 分钟");
                Rectangle baseLine = new Rectangle { Width = width, Height = 14, RadiusX = 5, RadiusY = 5, Fill = BrushOf("#DCE5DE") };
                Canvas.SetTop(baseLine, 9); timeline.Children.Add(baseLine);
                DateTime day = DateTime.Now.Date; int segmentStart = -1;
                for (int minute = 0; minute <= 1440; minute++)
                {
                    bool working = minute < 1440 && Schedule.IsWork(draft, day.AddMinutes(minute));
                    if (working && segmentStart < 0) segmentStart = minute;
                    if (!working && segmentStart >= 0)
                    {
                        Rectangle segment = new Rectangle { Width = (minute - segmentStart) * width / 1440,
                            Height = 14, RadiusX = 2, RadiusY = 2, Fill = BrushOf("#448F63") };
                        Canvas.SetLeft(segment, segmentStart * width / 1440); Canvas.SetTop(segment, 9);
                        timeline.Children.Add(segment); segmentStart = -1;
                    }
                }
                double position = Math.Min(width - 2, DateTime.Now.TimeOfDay.TotalMinutes * width / 1440);
                Rectangle pointer = new Rectangle { Width = 2, Height = 24, Fill = BrushOf("#183F30"), ToolTip = "当前时间 " + DateTime.Now.ToString("HH:mm") };
                Canvas.SetLeft(pointer, position); Canvas.SetTop(pointer, 4); timeline.Children.Add(pointer);
            }
            catch (ArgumentException ex) { SetText("TimelineCaption", ex.Message); }
        }

        private void UpdateDisplay(bool effective)
        {
            DateTime now = DateTime.Now;
            Settings display = settings;
            if (!effective && !File.Exists(files.SettingsPath))
                try { display = ReadFields(); } catch (ArgumentException) { }
            int minutes = Schedule.Minutes(display, now);
            bool working = Schedule.IsWork(display, now);
            SetText("CurrentMinutes", minutes.ToString()); SetText("CurrentUnit", "分钟");
            SetText("StatusText", effective ? "运行中" : "待启用");
            SetText("ModeText", (working ? "工作时段" : "其余时间") + (effective ? "" : " · 规则预览"));
            SetText("StateDescription", effective ? "无操作后，自动关闭屏幕" : "保存并启用后，按此规则自动关屏");
            DateTime? change = Schedule.NextChange(display, now);
            SetText("NextCaption", effective ? "下一次切换" : "下一时段 · 规则预览");
            SetText("NextText", change.HasValue ? (change.Value.Date == now.Date ? "" : change.Value.ToString("MM-dd ")) +
                change.Value.ToString("HH:mm") + " → " + Schedule.Minutes(display, change.Value) + " 分钟" : "当前规则下保持不变");
        }

        private void RefreshState()
        {
            if (preview || loadError != null || disposed) return;
            try
            {
                if (settings.Enabled)
                {
                    int minutes = Schedule.Minutes(settings, DateTime.Now);
                    if (controller.Apply(minutes)) files.Log("已设置：" + minutes + " 分钟（插电 / 电池）。");
                    UpdateDisplay(true); toggle.Text = "暂停并恢复原设置";
                }
                else
                {
                    controller.Restore(); UpdateDisplay(false);
                    if (File.Exists(files.SettingsPath))
                    {
                        PowerValues values = controller.Current();
                        bool onBattery = Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline;
                        uint seconds = onBattery ? values.Dc : values.Ac;
                        SetText("StatusText", "已暂停"); SetText("ModeText", "Windows 当前设置");
                        SetText("CurrentMinutes", seconds == 0 ? "∞" : (seconds % 60 == 0 ? (seconds / 60).ToString() : seconds.ToString()));
                        SetText("CurrentUnit", seconds == 0 ? "" : seconds % 60 == 0 ? "分钟" : "秒");
                        SetText("StateDescription", "插电 " + FormatSeconds(values.Ac) + " · 电池 " + FormatSeconds(values.Dc));
                        SetText("NextCaption", "自动切换已暂停"); SetText("NextText", "保存并启用，即可继续");
                    }
                    toggle.Text = "启用自动切换";
                }
                tray.Text = Program.Title + " · " + (settings.Enabled ? Schedule.Minutes(settings, DateTime.Now) + " 分钟" : "已暂停");
                Named<Button>("PauseButton").IsEnabled = settings.Enabled || journal.Items.Count > 0;
                if (lastError != null && !dirty) Feedback("设置已恢复正常", false);
                lastError = null;
                if (IsVisible) DrawTimeline();
            }
            catch (Exception ex) { SetError(ex); }
        }

        private static string FormatSeconds(uint value)
        { return value == 0 ? "从不关闭" : value % 60 == 0 ? (value / 60) + " 分钟" : value + " 秒"; }

        private void SaveAndEnable()
        {
            if (preview) return;
            if (loadError != null) { Feedback(loadError, true); return; }
            try
            {
                Settings updated = ReadFields(); bool oldStartup = Startup.IsEnabled();
                Startup.Set(updated.AutoStart, files.DirectoryPath);
                try { FileStore.Write(files.SettingsPath, updated); }
                catch { Startup.Set(oldStartup, files.DirectoryPath); throw; }
                settings = updated; dirty = false; lastError = null;
                RefreshState();
                if (lastError == null) { Feedback("已保存，正在按时间表运行", false); files.Log("规则已保存并启用。"); }
                SetText("FooterNote", "关闭窗口后，仍在托盘运行"); DrawTimeline();
            }
            catch (Exception ex) { Feedback(ex.Message, true); }
        }

        private void Pause()
        {
            if (preview || loadError != null) return;
            try
            {
                Settings updated = File.Exists(files.SettingsPath) ? FileStore.Read<Settings>(files.SettingsPath) : settings;
                updated.Enabled = false; FileStore.Write(files.SettingsPath, updated); settings = updated;
                controller.Restore(); lastError = null; RefreshState();
                Feedback(dirty ? "已暂停；界面修改尚未保存" : "已暂停，原设置已恢复", false);
                files.Log("已暂停，原设置已恢复。");
            }
            catch (Exception ex) { SetError(ex); }
        }

        private void SetError(Exception ex)
        {
            if (lastError != ex.Message) files.Log(ex.ToString()); lastError = ex.Message;
            SetText("StatusText", "设置失败"); SetText("StateDescription", "暂未生效，将自动重试");
            Feedback(ex.Message.Replace(Environment.NewLine, " "), true);
            if (tray != null) tray.Text = Program.Title + " · 设置失败";
        }

        private void CreateTray()
        {
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath);
            using (MemoryStream stream = new MemoryStream())
            {
                trayIcon.Save(stream); stream.Position = 0;
                BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); Icon = image;
            }
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            menu.Items.Add("打开设置", null, delegate { ShowSettings(); });
            toggle = new Forms.ToolStripMenuItem("启用自动切换");
            toggle.Click += delegate { if (settings.Enabled) Pause(); else SaveAndEnable(); }; menu.Items.Add(toggle);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出并恢复原设置", null, delegate { exiting = true; Close(); });
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Program.Title, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowSettings(); };
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            if (initialized) return;
            initialized = true;
            loading = false; dirty = false;
            Feedback("插电和电池都适用", false); SetText("FooterNote", "关闭窗口后，仍在托盘运行");
            DrawTimeline();
            if (preview) return;
            SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.TimeChanged += ClockChanged;
            Application.Current.SessionEnding += SessionEnding;
            if (loadError != null) Feedback(loadError + " 请保留原设置备份。", true);
            else RefreshState();
            timer.Start();
            if (background && loadError == null && lastError == null) Dispatcher.BeginInvoke((Action)delegate { Hide(); });
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            if (preview) { DisposeResources(); return; }
            if (!exiting && !sessionEnding) { e.Cancel = true; HideToTray(); return; }
            timer.Stop();
            if (loadError == null)
            {
                try { controller.Restore(); files.Log("程序退出，原设置已恢复。"); }
                catch (Exception ex)
                {
                    files.Log(ex.ToString());
                    if (!sessionEnding) { e.Cancel = true; exiting = false; timer.Start(); ShowSettings(); SetError(ex); }
                }
            }
        }

        private void ShowSettings() { Show(); WindowState = WindowState.Normal; Activate(); DrawTimeline(); }
        private void HideToTray()
        {
            if (preview) return; Hide();
            tray.ShowBalloonTip(1800, Program.Title, "程序继续在托盘运行。双击图标打开设置，右键可暂停或退出。", Forms.ToolTipIcon.Info);
        }
        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        { if (msg == Program.ShowMessage) { ShowSettings(); handled = true; } return IntPtr.Zero; }
        private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode != PowerModes.Suspend) QueueRefresh(); }
        private void ClockChanged(object sender, EventArgs e) { QueueRefresh(); }
        private void QueueRefresh()
        { if (!disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)delegate { if (!disposed) RefreshState(); }); }
        private void SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            sessionEnding = true; timer.Stop();
            if (loadError == null) try { controller.Restore(); } catch (Exception ex) { files.Log(ex.ToString()); }
        }

        public void RenderPreview(string path, double scale)
        {
            ShowInTaskbar = false; ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000;
            Show();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, (Action)delegate { });
            surface.UpdateLayout();
            DrawTimeline(); surface.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)(Width * scale), (int)(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(surface); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            Hide();
        }

        public void VerifyUi(string report)
        {
            StringBuilder result = new StringBuilder(); int checks = 0;
            Action<bool, string> verify = delegate(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; result.AppendLine("PASS: " + name); };
            verify(ReadFields().Periods.Count == 2 && ReadFields().Days.Count == 7, "default controls retain the exact daily schedule");
            AddPeriod("22:00", "02:00");
            verify(ReadFields().Periods.Count == 3 && Schedule.IsWork(ReadFields(), new DateTime(2026, 10, 7, 23, 0, 0)), "adding editable overnight period");
            RemovePeriod(editors.Last()); verify(editors.Count == 2 && rows.Children.Count == 2, "remove period updates visible rows and data");
            weekdays[0].IsChecked = false; weekdays[6].IsChecked = false;
            verify(ReadFields().Days.Count == 5 && !Schedule.IsWork(ReadFields(), new DateTime(2026, 10, 10, 9, 0, 0)), "weekday pills update effective dates");
            workInput.Text = "27"; otherInput.Text = "2";
            verify(ReadFields().WorkMinutes == 27 && ReadFields().OtherMinutes == 2, "custom minute inputs read correctly");
            autoStart.IsChecked = true; verify(ReadFields().AutoStart, "startup switch reads correctly without modifying Windows startup");
            bool invalid = false; editors[0].Start.Text = "99:00";
            try { ReadFields(); } catch (ArgumentException) { invalid = true; }
            verify(invalid, "invalid time cannot be saved"); editors[0].Start.Text = "08:00";
            invalid = false; workInput.Text = "0";
            try { ReadFields(); } catch (ArgumentException) { invalid = true; }
            verify(invalid, "invalid minute count cannot be saved");
            loading = true; LoadFields(); loading = false; dirty = false;
            RenderPreview(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(report), "ui-test-preview.png"), 1);
            ScrollViewer textHost = editors[0].Start.Template.FindName("PART_ContentHost", editors[0].Start) as ScrollViewer;
            Rect character = editors[0].Start.GetRectFromCharacterIndex(0);
            verify(!character.IsEmpty && character.Height > 0 && character.Bottom <= editors[0].Start.ActualHeight &&
                textHost != null && textHost.ViewportHeight >= character.Height, "time editor characters fit inside the visible viewport");
            verify(surface.ActualWidth == 920 && timeline.Children.Count >= 4, "complete WPF layout and schedule timeline render");
            verify(Named<CheckBox>("AutoStartSwitch").Template != null && Named<Button>("SaveButton").Template != null, "switch and primary button templates load");
            CreateTray(); verify(tray.Visible && toggle != null && tray.ContextMenuStrip.Items.Count == 4, "WPF window integrates with native tray icon and actions");
            result.AppendLine("RESULT: PASS (" + checks + " UI checks)");
            File.WriteAllText(report, result.ToString(), new UTF8Encoding(true));
        }

        public void DisposeResources()
        {
            if (disposed) return; disposed = true; timer.Stop();
            if (!preview)
            {
                SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.TimeChanged -= ClockChanged;
                if (Application.Current != null) Application.Current.SessionEnding -= SessionEnding;
            }
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
        }
    }
}
