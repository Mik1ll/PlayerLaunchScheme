// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

var supportedPlayers = new[] { "mpv", "vlc" };

switch (args[0])
{
    case "--install":
        if (args.Length != 2)
        {
            Console.WriteLine("Missing external player location");
            return 1;
        }

        var externalPlayerLocation = args[1];
        if (string.IsNullOrWhiteSpace(externalPlayerLocation))
        {
            Console.WriteLine("External player path/name empty");
            return 1;
        }

        var found = false;
        if (Path.IsPathFullyQualified(externalPlayerLocation))
        {
            if (File.Exists(externalPlayerLocation))
                found = true;
        }
        else if (externalPlayerLocation.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            var hasExt = Path.HasExtension(externalPlayerLocation) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var ext = hasExt ? string.Empty : ".exe";
            foreach (var dir in path)
            {
                var testpath = Path.Combine(dir, externalPlayerLocation + ext);
                if (File.Exists(testpath))
                {
                    found = true;
                    externalPlayerLocation = testpath;
                    break;
                }
            }
        }
        else
        {
            Console.WriteLine("File path must be fully qualified or a file name");
            return 1;
        }

        if (!found)
        {
            Console.WriteLine("Executable not found.");
            return 1;
        }

        var playerName = Path.GetFileNameWithoutExtension(externalPlayerLocation).ToLowerInvariant();
        if (!supportedPlayers.Contains(playerName))
        {
            Console.WriteLine("Player not supported");
            return 1;
        }

        var location = Process.GetCurrentProcess().MainModule?.FileName ?? throw new ArgumentException("No Main Module");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\shizou");
            key.SetValue("", "URL:Shizou Protocol");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{location}\",1");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{location}\" \"{externalPlayerLocation}\" \"%1\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var desktopContent =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Shizou External Player\n" +
                $"TryExec={location}\n" +
                $"Exec={location} {externalPlayerLocation.Replace(" ", @"\ ")} %u\n" +
                "Terminal=false\n" +
                "StartupNotify=false\n" +
                "MimeType=x-scheme-handler/shizou;\n";
            var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
            var desktopName = "shizou.desktop";
            var desktopPath = Path.Combine(desktopDir, desktopName);
            Directory.CreateDirectory(desktopDir);
            File.WriteAllText(desktopPath, desktopContent);
            Process.Start("desktop-file-install", $"-m 755 --dir={desktopDir.Replace(" ", @"\ ")} --rebuild-mime-info-cache {desktopPath.Replace(" ", @"\ ")}")
                .WaitForExit();
        }

        return 0;
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

        return 0;
    default:
        if (args.Length != 2)
            return 1;
        var uri = new Uri(args[1]);
        externalPlayerLocation = args[0];
        if (uri.Scheme != "shizou" || uri.Query.Length == 0)
            return 0;
        var innerUri = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Scheme, UriFormat.UriEscaped);
        playerName = Path.GetFileNameWithoutExtension(externalPlayerLocation).ToLowerInvariant();
        switch (playerName)
        {
            case "mpv":
                Process.Start(externalPlayerLocation, $"--no-terminal --no-ytdl {innerUri}");
                return 0;
            case "vlc":
                Process.Start(externalPlayerLocation, innerUri);
                return 0;
            default:
                return 1;
        }
}
