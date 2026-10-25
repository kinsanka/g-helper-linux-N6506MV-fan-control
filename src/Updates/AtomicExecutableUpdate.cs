namespace GHelper.Linux.Updates;

public static class AtomicExecutableUpdate
{
    private static readonly object Gate = new();
    public static void Replace(string download, string target)
    {
        lock (Gate)
        {
            if (!File.Exists(download) || !File.Exists(target)) throw new IOException("安装包或当前程序不存在");
            string staged = target + ".update-" + Guid.NewGuid().ToString("N");
            string backup = target + ".bak";
            try
            {
                // Stage beside the target so the final rename stays atomic across /tmp and /home.
                File.Copy(download, staged);
#pragma warning disable CA1416
                File.SetUnixFileMode(staged, File.GetUnixFileMode(download));
#pragma warning restore CA1416
                using (var file = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.None)) file.Flush(true);
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(target, backup);
                try { File.Move(staged, target); }
                catch
                {
                    if (!File.Exists(target)) File.Move(backup, target);
                    throw;
                }
                try { File.Delete(download); } catch { }
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
    }
}
