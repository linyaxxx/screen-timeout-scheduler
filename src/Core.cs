using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Serialization;

namespace ScreenTimeoutScheduler
{
    public class WorkPeriod
    {
        public string Start = "08:00";
        public string End = "12:00";
        public WorkPeriod() { }
        public WorkPeriod(string start, string end) { Start = start; End = end; }
    }

    public class Settings
    {
        public int Version = 1;
        public int WorkMinutes = 15;
        public int OtherMinutes = 1;
        public bool Enabled = false;
        public bool AutoStart = false;
        [XmlIgnore] public List<int> Days = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
        [XmlIgnore] public List<WorkPeriod> Periods = new List<WorkPeriod> {
            new WorkPeriod("08:00", "12:00"), new WorkPeriod("13:30", "17:30") };
        // Arrays are replaced on deserialize; initialized Lists would be appended to.
        [XmlArray("Days")]
        public int[] SerializedDays
        {
            get { return Days == null ? null : Days.ToArray(); }
            set { Days = value == null ? null : value.ToList(); }
        }
        [XmlArray("Periods")]
        public WorkPeriod[] SerializedPeriods
        {
            get { return Periods == null ? null : Periods.ToArray(); }
            set { Periods = value == null ? null : value.ToList(); }
        }

        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("配置文件版本不受支持。");
            if (WorkMinutes < 1 || WorkMinutes > 1440 || OtherMinutes < 1 || OtherMinutes > 1440)
                throw new ArgumentException("关闭屏幕的等待时间应为 1 到 1440 分钟。");
            if (Days == null || Days.Count == 0 || Days.Any(d => d < 0 || d > 6))
                throw new ArgumentException("请至少选择一个工作日。");
            if (Periods == null || Periods.Count == 0 || Periods.Count > 100)
                throw new ArgumentException("请设置 1 到 100 个工作时段。");
            foreach (WorkPeriod p in Periods)
            {
                if (p == null || Schedule.ParseTime(p.Start) == Schedule.ParseTime(p.End))
                    throw new ArgumentException("开始时间和结束时间不能相同。");
            }
        }
    }

    public static class Schedule
    {
        public static TimeSpan ParseTime(string text)
        {
            DateTime time;
            if (text == null || !DateTime.TryParseExact(text.Trim(), "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                throw new ArgumentException("时间请使用 24 小时制，例如 08:00 或 13:30。");
            return time.TimeOfDay;
        }

        // Overnight periods belong to their starting weekday; end is exclusive.
        public static bool IsWork(Settings s, DateTime now)
        {
            foreach (WorkPeriod p in s.Periods)
            {
                TimeSpan start = ParseTime(p.Start), end = ParseTime(p.End);
                for (int offset = -1; offset <= 0; offset++)
                {
                    DateTime day = now.Date.AddDays(offset);
                    if (!s.Days.Contains((int)day.DayOfWeek)) continue;
                    DateTime from = day.Add(start);
                    DateTime to = day.Add(end).AddDays(end < start ? 1 : 0);
                    if (now >= from && now < to) return true;
                }
            }
            return false;
        }

        public static int Minutes(Settings s, DateTime now)
        { return IsWork(s, now) ? s.WorkMinutes : s.OtherMinutes; }

        public static DateTime? NextChange(Settings s, DateTime now)
        {
            if (s.WorkMinutes == s.OtherMinutes) return null;
            bool current = IsWork(s, now);
            SortedSet<DateTime> candidates = new SortedSet<DateTime>();
            for (int offset = -1; offset <= 8; offset++)
            {
                DateTime day = now.Date.AddDays(offset);
                if (!s.Days.Contains((int)day.DayOfWeek)) continue;
                foreach (WorkPeriod p in s.Periods)
                {
                    TimeSpan start = ParseTime(p.Start), end = ParseTime(p.End);
                    candidates.Add(day.Add(start));
                    candidates.Add(day.Add(end).AddDays(end < start ? 1 : 0));
                }
            }
            foreach (DateTime candidate in candidates)
                if (candidate > now && IsWork(s, candidate) != current) return candidate;
            return null;
        }
    }

    public static class FileStore
    {
        public static T Read<T>(string path) where T : new()
        {
            if (!File.Exists(path)) return new T();
            XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, options))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new XmlSerializer(typeof(T)).Serialize(stream, value);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public class PowerValues
    {
        public uint Ac, Dc;
        public PowerValues(uint ac, uint dc) { Ac = ac; Dc = dc; }
    }

    public interface IPowerBackend
    {
        Guid ActiveScheme();
        PowerValues Read(Guid scheme);
        void Write(Guid scheme, uint? ac, uint? dc);
    }

    public class NativePower : IPowerBackend
    {
        private static Guid video = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static Guid timeout = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        private static void Check(uint code, string action)
        {
            if (code != 0) throw new Win32Exception((int)code, action + "：" + new Win32Exception((int)code).Message);
        }

        public Guid ActiveScheme()
        {
            IntPtr memory;
            Check(PowerGetActiveScheme(IntPtr.Zero, out memory), "读取电源计划失败");
            try { return (Guid)Marshal.PtrToStructure(memory, typeof(Guid)); }
            finally { LocalFree(memory); }
        }

        public PowerValues Read(Guid scheme)
        {
            uint ac, dc;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref video, ref timeout, out ac), "读取插电设置失败");
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref video, ref timeout, out dc), "读取电池设置失败");
            return new PowerValues(ac, dc);
        }

        public void Write(Guid scheme, uint? ac, uint? dc)
        {
            if (ac.HasValue) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref video, ref timeout, ac.Value), "修改插电设置失败");
            if (dc.HasValue) Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref video, ref timeout, dc.Value), "修改电池设置失败");
            // Never activate an old plan if the user switched plans while we were writing.
            if (ActiveScheme() == scheme)
                Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "使电源设置生效失败");
            PowerValues actual = Read(scheme);
            if ((ac.HasValue && actual.Ac != ac.Value) || (dc.HasValue && actual.Dc != dc.Value))
                throw new InvalidOperationException("Windows 未接受设置，请检查设备的电源管理策略。");
        }
    }

    public class OriginalSetting
    {
        public string Scheme;
        public uint OriginalAc, OriginalDc, LastAc, LastDc;
        public bool OwnAc, OwnDc;
    }
    public class RestoreJournal
    {
        public List<OriginalSetting> Items = new List<OriginalSetting>();
    }

    public class PowerController
    {
        private readonly IPowerBackend power;
        private readonly RestoreJournal journal;
        private readonly Action persist;
        public PowerController(IPowerBackend power, RestoreJournal journal, Action persist)
        { this.power = power; this.journal = journal; this.persist = persist; }

        public PowerValues Current() { return power.Read(power.ActiveScheme()); }

        public bool Apply(int minutes)
        {
            if (minutes < 1 || minutes > 1440) throw new ArgumentOutOfRangeException("minutes");
            uint seconds = checked((uint)minutes * 60);
            Guid scheme = power.ActiveScheme();
            PowerValues current = power.Read(scheme);
            if (current.Ac == seconds && current.Dc == seconds) return false;
            OriginalSetting entry = journal.Items.Find(e => e.Scheme == scheme.ToString());
            if (entry == null)
            {
                entry = new OriginalSetting { Scheme = scheme.ToString(), OriginalAc = current.Ac, OriginalDc = current.Dc };
                journal.Items.Add(entry);
            }
            // Respect later manual changes when deciding what to restore.
            if (entry.OwnAc && current.Ac != entry.LastAc) { entry.OriginalAc = current.Ac; entry.OwnAc = false; }
            if (entry.OwnDc && current.Dc != entry.LastDc) { entry.OriginalDc = current.Dc; entry.OwnDc = false; }
            uint? ac = current.Ac == seconds ? (uint?)null : seconds;
            uint? dc = current.Dc == seconds ? (uint?)null : seconds;
            if (ac.HasValue) { entry.LastAc = seconds; entry.OwnAc = true; }
            if (dc.HasValue) { entry.LastDc = seconds; entry.OwnDc = true; }
            // Persist original values BEFORE touching Windows, including partial-failure recovery.
            persist();
            power.Write(scheme, ac, dc);
            return true;
        }

        public void Restore()
        {
            List<string> errors = new List<string>();
            foreach (OriginalSetting entry in journal.Items.ToArray())
            {
                try
                {
                    Guid scheme = new Guid(entry.Scheme);
                    PowerValues current = power.Read(scheme);
                    uint? ac = entry.OwnAc && current.Ac == entry.LastAc ? (uint?)entry.OriginalAc : null;
                    uint? dc = entry.OwnDc && current.Dc == entry.LastDc ? (uint?)entry.OriginalDc : null;
                    if (ac.HasValue || dc.HasValue) power.Write(scheme, ac, dc);
                    journal.Items.Remove(entry);
                    persist();
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
            if (errors.Count > 0) throw new InvalidOperationException("部分原设置未能恢复，备份仍保留。" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
    }
}
