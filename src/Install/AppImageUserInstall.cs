using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Install;

public static class AppImageUserInstall
{
    public static bool Handle(ref string[] args)
    {
        if (args.FirstOrDefault() != "--install-appimage") return false;
        args = args.Skip(1).ToArray();
        string? source = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return false;
        if (Environment.UserName == "root") throw new InvalidOperationException("请以普通用户运行安装包");
        string home = CanonicalPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string target = Path.Combine(home, "Applications", Updates.ForkRelease.AppImageName);
        if (IsInstalledLocation(source, target)) return false;
        using var desktopStream = typeof(AppImageUserInstall).Assembly.GetManifestResourceStream("ghelper.desktop")
            ?? throw new IOException("缺少菜单资源");
        using var reader = new StreamReader(desktopStream);
        Install(source, target, home, reader.ReadToEnd(), AppConfig.IsNotFalse("autostart"));
        using var icon = typeof(AppImageUserInstall).Assembly.GetManifestResourceStream("ghelper.png");
        if (icon != null)
        {
            string path = Path.Combine(home, ".local/share/icons/hicolor/256x256/apps/ghelper.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path); icon.CopyTo(file);
        }
        Logger.WriteLine("AppImage installed in the user's Applications directory; launching installed copy.");
        var start = new ProcessStartInfo(target) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        Process.Start(start);
        return true;
    }

    [DllImport("libc", EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr buffer);

    private static string CanonicalPath(string path)
    {
        IntPtr value = RealPath(path, IntPtr.Zero);
        if (value == IntPtr.Zero) return Path.GetFullPath(path);
        try { return Marshal.PtrToStringUTF8(value) ?? Path.GetFullPath(path); }
        finally { Free(value); }
    }

    public static bool IsInstalledLocation(string source, string target)
    {
        // Bazzite's /home is a link to /var/home; compare physical paths to avoid reinstall loops.
        return File.Exists(source) && File.Exists(target) && CanonicalPath(source) == CanonicalPath(target);
    }

    public static string QuoteDesktopExec(string path)
    {
        string escaped = path.Replace("%", "%%");
        if (!path.Any(c => char.IsWhiteSpace(c) || "\"\\`$".Contains(c))) return escaped;
        return "\"" + escaped.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
    }

    public static void Install(string source, string target, string home, string desktop, bool autostart)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
#pragma warning disable CA1416
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
            if (File.Exists(target)) File.Copy(target, target + ".previous", true);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Desktop Exec quoting must preserve spaces, quotes and literal shell characters.
        string quoted = QuoteDesktopExec(target);
        string entry = string.Join('\n', desktop.Split('\n').Select(x => x.StartsWith("Exec=", StringComparison.Ordinal) ? "Exec=" + quoted : x));
        string menu = Path.Combine(home, ".local/share/applications/ghelper.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(menu)!); File.WriteAllText(menu, entry);
        if (autostart)
        {
            string login = Path.Combine(home, ".config/autostart/ghelper.desktop");
            Directory.CreateDirectory(Path.GetDirectoryName(login)!); File.WriteAllText(login, entry);
        }
    }
}
