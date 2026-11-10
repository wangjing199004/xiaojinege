using System;
using System.IO;
using TitleAttributeBatch.Core;

internal static class SafeDwgSaveTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TkattrSaveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "target.dwg"), temp = Path.Combine(directory, "temporary.dwg"), backup = Path.Combine(directory, "original.bak");
        try
        {
            File.WriteAllText(target, "original content"); File.WriteAllText(temp, "updated content");
            SafeDwgSave.Replace(temp, target, backup, SafeDwgSave.Digest(target));
            Check(File.ReadAllText(target) == "updated content", "Updated content missing");
            Check(File.ReadAllText(backup) == "original content", "Backup does not preserve original");
            Check(!File.Exists(temp), "Temporary file not consumed");
            Console.WriteLine("PASS: atomic replacement preserves original backup");

            string stale = SafeDwgSave.Digest(target);
            File.WriteAllText(target, "externally changed"); File.WriteAllText(temp, "must not overwrite");
            bool rejected = false;
            try { SafeDwgSave.Replace(temp, target, Path.Combine(directory, "conflict.bak"), stale); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(target) == "externally changed" && File.Exists(temp), "Concurrent change was overwritten");
            Check(!File.Exists(Path.Combine(directory, "conflict.bak")), "Conflict created a misleading backup");
            Console.WriteLine("PASS: externally modified original is not overwritten");

            using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                rejected = false;
                try { SafeDwgSave.Replace(temp, target, Path.Combine(directory, "locked.bak"), stale); } catch (IOException) { rejected = true; }
                Check(rejected && File.Exists(temp), "Locked file was not protected");
            }
            Check(File.ReadAllText(target) == "externally changed", "Locked original changed");
            Check(File.ReadAllText(backup) == "original content", "Previous backup changed");
            Console.WriteLine("PASS: locked file and previous backup are preserved");

            var store = new TempBackupStore(Path.Combine(directory, "session"));
            File.WriteAllText(target, "revision0");
            for (int i = 1; i <= 5; i++)
            {
                File.WriteAllText(temp, "revision" + i);
                string slot = store.NextBackup(target);
                SafeDwgSave.Replace(temp, target, slot, SafeDwgSave.Digest(target));
            }
            string[] slots = Directory.GetFiles(store.Root, "*.bak?", SearchOption.AllDirectories);
            Check(slots.Length == 3, "Backup limit is not three");
            var contents = new System.Collections.Generic.HashSet<string>();
            foreach (string slot in slots) contents.Add(File.ReadAllText(slot));
            Check(contents.SetEquals(new[] { "revision2", "revision3", "revision4" }), "Rotation did not preserve the latest three originals");
            Check(File.ReadAllText(target) == "revision5", "Final target content incorrect");
            string other = store.NextBackup(Path.Combine(directory, "another-folder", "target.dwg"));
            Check(Path.GetDirectoryName(other) != Path.GetDirectoryName(slots[0]), "Identically named drawings collided");
            Check(store.HasBackups(), "Backups not detected");
            Console.WriteLine("PASS: five updates retain only the latest three backups; duplicate filenames are isolated");
            store.Clear();
            Check(!store.HasBackups() && !Directory.Exists(store.Root), "Session cleanup did not remove backups");
            Console.WriteLine("PASS: closing-session cleanup removes backups and empty directories");
            string noBackupFolder = Path.Combine(directory, "no-backup");
            Directory.CreateDirectory(noBackupFolder);
            string original = Path.Combine(noBackupFolder, "drawing.dwg");
            string replacement = Path.Combine(noBackupFolder, "new.dwg");
            File.WriteAllText(original, "before signing");
            File.WriteAllText(replacement, "after signing");
            SafeDwgSave.ReplaceWithoutBackup(replacement, original, SafeDwgSave.Digest(original));
            Check(File.ReadAllText(original) == "after signing", "No-backup save failed");
            Check(Directory.GetFiles(noBackupFolder).Length == 1, "No-backup save left extra files");
            string oldHash = SafeDwgSave.Digest(original);
            File.WriteAllText(original, "changed outside plugin"); File.WriteAllText(replacement, "must not save");
            rejected = false;
            try { SafeDwgSave.ReplaceWithoutBackup(replacement, original, oldHash); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(original) == "changed outside plugin", "No-backup save overwrote an external change");
            File.Delete(original); File.Delete(replacement); Directory.Delete(noBackupFolder);
            Console.WriteLine("PASS: signing save creates no backup and rejects concurrent changes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
