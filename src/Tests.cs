using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenTimeoutScheduler
{
    public class FakePower : IPowerBackend
    {
        public Guid Active = Guid.NewGuid();
        public Dictionary<Guid, PowerValues> Values = new Dictionary<Guid, PowerValues>();
        public int Writes;
        public bool FailDc;
        public FakePower() { Values[Active] = new PowerValues(180, 300); }
        public Guid ActiveScheme() { return Active; }
        public PowerValues Read(Guid scheme) { PowerValues v = Values[scheme]; return new PowerValues(v.Ac, v.Dc); }
        public void Write(Guid scheme, uint? ac, uint? dc)
        {
            Writes++;
            if (ac.HasValue) Values[scheme].Ac = ac.Value;
            if (FailDc && dc.HasValue) throw new InvalidOperationException("simulated DC write failure");
            if (dc.HasValue) Values[scheme].Dc = dc.Value;
        }
    }

    public static class Tests
    {
        private static int count;
        private static StringBuilder output;
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            count++; output.AppendLine("PASS: " + name);
        }
        private static void Invalid(Action action, string name)
        {
            bool invalid = false;
            try { action(); } catch (ArgumentException) { invalid = true; }
            Assert(invalid, name);
        }

        public static int Run(string report, string dataDir, bool live)
        {
            count = 0; output = new StringBuilder();
            output.AppendLine("ScreenTimeoutScheduler verification " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                ScheduleTests(); ControllerTests(); PersistenceTests(dataDir);
                if (live) LivePowerTests();
                output.AppendLine("RESULT: PASS (" + count + " checks)");
                File.WriteAllText(report, output.ToString(), new UTF8Encoding(true)); return 0;
            }
            catch (Exception ex)
            {
                output.AppendLine(ex.ToString()); output.AppendLine("RESULT: FAIL");
                File.WriteAllText(report, output.ToString(), new UTF8Encoding(true)); return 1;
            }
        }

        private static void ScheduleTests()
        {
            Settings s = new Settings(); s.Validate();
            string[] times = { "07:59:59", "08:00:00", "11:59:59", "12:00:00", "13:29:59", "13:30:00", "17:29:59", "17:30:00", "23:59:59" };
            int[] expected = { 1, 15, 15, 1, 1, 15, 15, 1, 1 };
            for (int i = 0; i < times.Length; i++)
                Assert(Schedule.Minutes(s, DateTime.Parse("2026-10-07 " + times[i], System.Globalization.CultureInfo.InvariantCulture)) == expected[i], "daily boundary " + times[i]);
            Assert(Schedule.Minutes(s, new DateTime(2026, 10, 10, 9, 0, 0)) == 15, "default includes Saturday");
            Assert(Schedule.Minutes(s, new DateTime(2026, 10, 11, 9, 0, 0)) == 15, "default includes Sunday");
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 7, 10, 0, 0)) == new DateTime(2026, 10, 7, 12, 0, 0), "next change at noon");
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 7, 12, 0, 0)) == new DateTime(2026, 10, 7, 13, 30, 0), "boundary selects next afternoon");
            s.Days = new List<int> { 1, 2, 3, 4, 5 };
            Assert(Schedule.Minutes(s, new DateTime(2026, 10, 10, 9, 0, 0)) == 1, "excluded weekend");
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 9, 18, 0, 0)) == new DateTime(2026, 10, 12, 8, 0, 0), "Friday to Monday");
            s.Days = new List<int> { 5 };
            s.Periods = new List<WorkPeriod> { new WorkPeriod("22:00", "02:00") }; s.Validate();
            Assert(Schedule.IsWork(s, new DateTime(2026, 10, 9, 22, 0, 0)), "overnight Friday starts inclusive");
            Assert(Schedule.IsWork(s, new DateTime(2026, 10, 10, 1, 59, 59)), "overnight Saturday uses Friday selection");
            Assert(!Schedule.IsWork(s, new DateTime(2026, 10, 10, 2, 0, 0)), "overnight end exclusive");
            Assert(!Schedule.IsWork(s, new DateTime(2026, 10, 9, 1, 0, 0)), "overnight does not use next day's selection");
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 10, 1, 0, 0)) == new DateTime(2026, 10, 10, 2, 0, 0), "overnight next boundary");
            s = new Settings { Periods = new List<WorkPeriod> { new WorkPeriod("08:00", "12:00"), new WorkPeriod("11:00", "13:00") } };
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 7, 10, 0, 0)) == new DateTime(2026, 10, 7, 13, 0, 0), "overlap merged without false transition");
            s.Periods = new List<WorkPeriod> { new WorkPeriod("08:00", "12:00"), new WorkPeriod("12:00", "17:00") };
            Assert(Schedule.NextChange(s, new DateTime(2026, 10, 7, 10, 0, 0)) == new DateTime(2026, 10, 7, 17, 0, 0), "adjacent ranges merged");
            s.WorkMinutes = 1;
            Assert(!Schedule.NextChange(s, DateTime.Now).HasValue, "identical timeouts have no transition");
            Invalid(delegate { Schedule.ParseTime("24:00"); }, "reject invalid hour");
            Invalid(delegate { Schedule.ParseTime("8:00"); }, "require unambiguous HH:mm format");
            Invalid(delegate { new Settings { Periods = new List<WorkPeriod> { new WorkPeriod("09:00", "09:00") } }.Validate(); }, "reject zero-length period");
            Invalid(delegate { new Settings { Days = new List<int>() }.Validate(); }, "reject empty weekdays");
            Invalid(delegate { new Settings { WorkMinutes = 0 }.Validate(); }, "reject zero timeout");
            Invalid(delegate { new Settings { OtherMinutes = 1441 }.Validate(); }, "reject excessive timeout");
            s = new Settings { WorkMinutes = 20, OtherMinutes = 2, Days = new List<int> { 3 }, Periods = new List<WorkPeriod> { new WorkPeriod("09:45", "10:20") } };
            Assert(Schedule.Minutes(s, new DateTime(2026, 10, 7, 10, 0, 0)) == 20, "custom period and duration");
            Assert(Schedule.Minutes(s, new DateTime(2026, 10, 7, 10, 20, 0)) == 2, "custom outside duration");
        }

        private static void ControllerTests()
        {
            FakePower fake = new FakePower(); RestoreJournal journal = new RestoreJournal();
            int persisted = 0;
            PowerController controller = new PowerController(fake, journal, delegate { persisted++; });
            Assert(controller.Apply(15), "first apply updates native settings");
            Assert(fake.Values[fake.Active].Ac == 900 && fake.Values[fake.Active].Dc == 900, "AC and DC applied");
            int writes = fake.Writes;
            Assert(!controller.Apply(15) && fake.Writes == writes, "unchanged polling performs no writes");
            controller.Apply(1);
            Assert(journal.Items[0].OriginalAc == 180 && journal.Items[0].OriginalDc == 300, "transition preserves first originals");
            controller.Restore();
            Assert(fake.Values[fake.Active].Ac == 180 && fake.Values[fake.Active].Dc == 300 && journal.Items.Count == 0, "pause restores distinct AC and DC originals");
            controller.Apply(15); fake.Values[fake.Active].Ac = 1200;
            controller.Restore();
            Assert(fake.Values[fake.Active].Ac == 1200 && fake.Values[fake.Active].Dc == 300, "restore preserves external manual edit");
            controller.Apply(15); fake.Values[fake.Active].Ac = 420; controller.Apply(1); controller.Restore();
            Assert(fake.Values[fake.Active].Ac == 420, "reapply remembers later manual setting");
            Guid first = fake.Active;
            controller.Apply(15); fake.Active = Guid.NewGuid(); fake.Values[fake.Active] = new PowerValues(600, 120);
            Guid second = fake.Active; controller.Apply(1); controller.Restore();
            Assert(fake.Values[first].Ac == 420 && fake.Values[second].Ac == 600 && fake.Values[second].Dc == 120, "all modified power schemes restored");
            Assert(fake.Active == second, "restoration retains user's active scheme");
            fake.FailDc = true;
            bool failed = false;
            try { controller.Apply(15); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && journal.Items.Count == 1, "partial native failure retains recovery journal");
            fake.FailDc = false; controller.Restore();
            Assert(fake.Values[second].Ac == 600 && fake.Values[second].Dc == 120, "partial write recovered");
            fake = new FakePower(); journal = new RestoreJournal();
            controller = new PowerController(fake, journal, delegate { throw new IOException("simulated persistence failure"); });
            failed = false;
            try { controller.Apply(15); } catch (IOException) { failed = true; }
            Assert(failed && fake.Writes == 0, "backup failure prevents power modification");
            Assert(persisted > 0, "restore journal updated");
        }

        private static void PersistenceTests(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "test-settings.xml");
            Settings s = new Settings { WorkMinutes = 27, AutoStart = true, Enabled = true };
            FileStore.Write(path, s);
            Settings loaded = FileStore.Read<Settings>(path); loaded.Validate();
            Assert(loaded.WorkMinutes == 27 && loaded.Enabled && loaded.AutoStart && loaded.Periods.Count == 2, "configuration persists and validates");
            loaded.OtherMinutes = 3; FileStore.Write(path, loaded);
            Assert(FileStore.Read<Settings>(path).OtherMinutes == 3, "atomic replace updates existing configuration");
            path = Path.Combine(directory, "test-journal.xml");
            FakePower fake = new FakePower(); RestoreJournal journal = new RestoreJournal();
            new PowerController(fake, journal, delegate { FileStore.Write(path, journal); }).Apply(15);
            RestoreJournal recovered = FileStore.Read<RestoreJournal>(path);
            new PowerController(fake, recovered, delegate { FileStore.Write(path, recovered); }).Restore();
            Assert(fake.Values[fake.Active].Ac == 180 && fake.Values[fake.Active].Dc == 300, "recovery after simulated process restart");
        }

        private static void LivePowerTests()
        {
            NativePower native = new NativePower(); Guid scheme = native.ActiveScheme(); PowerValues original = native.Read(scheme);
            output.AppendLine("Original scheme: " + scheme + "; AC=" + original.Ac + "s; DC=" + original.Dc + "s");
            try
            {
                native.Write(scheme, 900, 900); PowerValues actual = native.Read(scheme);
                Assert(actual.Ac == 900 && actual.Dc == 900, "LIVE Windows accepted 15-minute AC and DC timeout");
                native.Write(scheme, 60, 60); actual = native.Read(scheme);
                Assert(actual.Ac == 60 && actual.Dc == 60, "LIVE Windows accepted 1-minute AC and DC timeout");
            }
            finally
            {
                native.Write(scheme, original.Ac, original.Dc);
                PowerValues restored = native.Read(scheme);
                Assert(restored.Ac == original.Ac && restored.Dc == original.Dc, "LIVE exact original settings restored in finally");
            }
            Assert(native.ActiveScheme() == scheme, "LIVE original active scheme unchanged");
        }
    }
}
