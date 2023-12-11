// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

if (args.Length != 1)
    return;
switch (args[0])
{
    case "--install":
        var location = Process.GetCurrentProcess().MainModule?.FileName ?? throw new ArgumentException("No Main Module");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\shizou");
            key.SetValue("", "URL:Shizou Protocol");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{location}\",1");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{location}\" \"%1\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var desktopContent =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Shizou External Player\n" +
                $"TryExec={location}\n" +
                $"Exec={location} %u\n" +
                "Terminal=false\n" +
                "StartupNotify=false\n" +
                "MimeType=x-scheme-handler/shizou;\n";
            var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
            var desktopName = "shizou.desktop";
            var desktopPath = Path.Combine(desktopDir, desktopName);
            Directory.CreateDirectory(desktopDir);
            File.WriteAllText(desktopPath, desktopContent);
            Process.Start("desktop-file-install", $"-m 755 --dir={desktopDir.Replace(" ", @"\ ")} --rebuild-mime-info-cache {desktopName}").WaitForExit();
        }

        break;
    case "--uninstall":
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\Classes\shizou");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
            var desktopName = "shizou.desktop";
            var desktopPath = Path.Combine(desktopDir, desktopName);
            File.Delete(desktopPath);
            Process.Start("update-desktop-database", desktopDir.Replace(" ", @"\ "));
        }

        break;
    default:
        var uri = new Uri(args[0]);
        if (uri.Scheme != "shizou" || uri.Query.Length == 0)
            return;
        Process.Start("mpv", $"--no-terminal --no-ytdl \"--http-header-fields=Cookie: {uri.Query[1..]}\" {uri.AbsolutePath}");
        break;
}
