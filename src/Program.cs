using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenTimeoutScheduler
{
    public static class Startup
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "ScreenTimeoutScheduler";
        public static void Set(bool enabled, string dataDir)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (enabled)
                    key.SetValue(Name, "\"" + Application.ExecutablePath + "\" --background --data-dir \"" + dataDir.TrimEnd('\\') + "\"", RegistryValueKind.String);
                else key.DeleteValue(Name, false);
            }
        }
        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key))
                return key != null && key.GetValue(Name) != null;
        }
    }

    public class AppFiles
    {
        public readonly string DirectoryPath, SettingsPath, JournalPath;
        public AppFiles(string path)
        {
            DirectoryPath = Path.GetFullPath(path);
            SettingsPath = Path.Combine(DirectoryPath, "settings.xml");
            JournalPath = Path.Combine(DirectoryPath, "original-settings.xml");
        }
        public void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "activity.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    public static class Program
    {
        public const string Title = "屏幕省电时段";
        public static readonly int ShowMessage = RegisterWindowMessage("ScreenTimeoutScheduler.Show.v1");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTimeoutScheduler");
                string preview = null, report = null;
                double previewScale = 1;
                bool background = false, selfTest = false, powerTest = false, uiTest = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
                    else if (args[i] == "--preview" && i + 1 < args.Length) preview = args[++i];
                    else if (args[i] == "--report" && i + 1 < args.Length) report = args[++i];
                    else if (args[i] == "--preview-scale" && i + 1 < args.Length) previewScale = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    else if (args[i] == "--background") background = true;
                    else if (args[i] == "--self-test") selfTest = true;
                    else if (args[i] == "--power-test") powerTest = true;
                    else if (args[i] == "--ui-test") uiTest = true;
                    else throw new ArgumentException("无法识别的启动参数：" + args[i]);
                }
                if (selfTest || powerTest)
                {
                    if (report == null) throw new ArgumentException("测试模式需要 --report 文件路径。");
                    return Tests.Run(report, dataDir, powerTest);
                }
                if (previewScale < 0.5 || previewScale > 3) throw new ArgumentException("预览比例应为 0.5 到 3。");
                Application.EnableVisualStyles();
                System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
                if (preview != null || uiTest)
                {
                    MainWindow window = null;
                    try
                    {
                        window = new MainWindow(new AppFiles(dataDir), false, true);
                        if (uiTest)
                        {
                            if (report == null) throw new ArgumentException("界面测试需要 --report 文件路径。");
                            window.VerifyUi(report);
                        }
                        if (preview != null) window.RenderPreview(preview, previewScale);
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        if (report != null) { File.WriteAllText(report, ex.ToString()); return 1; }
                        throw;
                    }
                    finally { if (window != null) window.DisposeResources(); app.Shutdown(); }
                }
                bool first;
                string user = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                using (Mutex mutex = new Mutex(true, @"Local\ScreenTimeoutScheduler.v1." + user, out first))
                {
                    if (!first)
                    {
                        IntPtr result;
                        SendMessageTimeout(new IntPtr(0xffff), (uint)ShowMessage, IntPtr.Zero, IntPtr.Zero, 2, 1000, out result);
                        return 0;
                    }
                    try { app.Run(new MainWindow(new AppFiles(dataDir), background, false)); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
