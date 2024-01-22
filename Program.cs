using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

var rootCommand = new RootCommand
{
    TreatUnmatchedTokensAsErrors = true
};
var installCommand = new Command("install", "Install the scheme handler");
var extPlayerArg = new Argument<string>("player-command", "Name of the external player if it is in PATH or the path to the player");
var extPlayerExtraArgsOpt = new Option<string?>("--extra-args", "Extra arguments to send to the player");
installCommand.AddArgument(extPlayerArg);
installCommand.AddOption(extPlayerExtraArgsOpt);
installCommand.SetHandler(HandleInstall, extPlayerArg, extPlayerExtraArgsOpt);
rootCommand.AddCommand(installCommand);
var uninstallCommand = new Command("uninstall", "Uninstall the scheme handler");
uninstallCommand.SetHandler(HandleUninstall);
rootCommand.AddCommand(uninstallCommand);
var runCommand = new Command("run", "Run the scheme handler");
var playUriArg = new Argument<string>("uri", "Target URI to send to player");
runCommand.AddArgument(extPlayerArg);
runCommand.AddOption(extPlayerExtraArgsOpt);
runCommand.AddArgument(playUriArg);
runCommand.SetHandler(HandleRun, extPlayerArg, extPlayerExtraArgsOpt, playUriArg);
rootCommand.AddCommand(runCommand);

return rootCommand.Invoke(args);

void HandleInstall(string extPlayerCommand, string? extraPlayerArgs)
{
    var supportedPlayers = new[] { "mpv", "vlc" };
    if (string.IsNullOrWhiteSpace(extPlayerCommand))
    {
        Console.WriteLine("External player path/name empty");
        return;
    }

    var found = false;
    if (Path.IsPathFullyQualified(extPlayerCommand))
    {
        if (File.Exists(extPlayerCommand))
            found = true;
    }
    else if (extPlayerCommand.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        var hasExt = Path.HasExtension(extPlayerCommand) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var ext = hasExt ? string.Empty : ".exe";
        foreach (var dir in path)
        {
            var testpath = Path.Combine(dir, extPlayerCommand + ext);
            if (File.Exists(testpath))
            {
                found = true;
                extPlayerCommand = testpath;
                break;
            }
        }
    }
    else
    {
        Console.WriteLine("File path must be fully qualified or a file name");
        return;
    }

    if (!found)
    {
        Console.WriteLine("Executable not found.");
        return;
    }

    var playerName = Path.GetFileNameWithoutExtension(extPlayerCommand).ToLowerInvariant();
    if (!supportedPlayers.Contains(playerName))
    {
        Console.WriteLine("Player not supported");
        return;
    }

    var location = Process.GetCurrentProcess().MainModule?.FileName ?? throw new ArgumentException("No Main Module");
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        extPlayerCommand = extPlayerCommand.Replace("%", "%%");
        extraPlayerArgs = extraPlayerArgs?.Replace("\"", "\\\"")
            .Replace("%", "%%");
        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\shizou");
        key.SetValue("", "URL:Shizou Protocol");
        key.SetValue("URL Protocol", "");
        using var icon = key.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{location}\",1");
        using var command = key.CreateSubKey(@"shell\open\command");
        location = location.Replace("%", "%%");
        command.SetValue("", $"\"{location}\" run \"{extPlayerCommand}\" \"%1\" --extra-args \"{extraPlayerArgs}\"");
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        extPlayerCommand = extPlayerCommand.Replace(" ", @"\s");
        extraPlayerArgs = extraPlayerArgs?
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$");
        var desktopContent =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Shizou External Player\n" +
            $"TryExec=\"{location}\"\n" +
            $"Exec=\"{location}\" run {extPlayerCommand} \"--extra-args {extraPlayerArgs}\" %u\n" +
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
}

void HandleUninstall()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\Classes\shizou", false);
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
        var desktopName = "shizou.desktop";
        var desktopPath = Path.Combine(desktopDir, desktopName);
        File.Delete(desktopPath);
        Process.Start("update-desktop-database", desktopDir.Replace(" ", @"\ "));
    }
}

void HandleRun(string extPlayerCommand, string? extraPlayerArgs, string playUrl)
{
    var uri = new Uri(playUrl);
    var externalPlayerLocation = extPlayerCommand;
    if (uri.Scheme != "shizou")
    {
        Console.WriteLine("Bad URI scheme");
        return;
    }

    var innerUri = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Scheme, UriFormat.UriEscaped);
    var playerName = Path.GetFileNameWithoutExtension(externalPlayerLocation).ToLowerInvariant();
    switch (playerName)
    {
        case "mpv":
            Process.Start(externalPlayerLocation, $"--no-terminal --no-ytdl {extraPlayerArgs} {innerUri}");
            return;
        case "vlc":
            Process.Start(externalPlayerLocation, $"{extraPlayerArgs} {innerUri}");
            return;
        default:
            return;
    }
}
