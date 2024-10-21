using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

var supportedPlayers = new[] { "mpv", "vlc" };
var rootCommand = new RootCommand
{
    TreatUnmatchedTokensAsErrors = true
};
var installCommand = new Command("install", "Install the scheme handler");
var extPlayerArg = new Argument<string>("player-command",
    $"Name of the external player if it is in PATH or the full path to the player. Supported players: {string.Join(", ", supportedPlayers)}");
extPlayerArg.AddValidator(result =>
{
    var value = result.GetValueForArgument(extPlayerArg);
    if (string.IsNullOrWhiteSpace(value))
        result.ErrorMessage = "External player path/name empty";
    else if (!supportedPlayers.Any(p => Path.GetFileName(value).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        result.ErrorMessage = $"Player not supported. Supported players: {string.Join(", ", supportedPlayers)}";
    else
    {
        List<string> pathext = [string.Empty, ..Environment.GetEnvironmentVariable("PATHEXT")?.Split(Path.PathSeparator) ?? []];
        if (Path.IsPathFullyQualified(value))
        {
            if (!pathext.Select(pext => value + pext).Any(File.Exists))
                result.ErrorMessage = $"Player not found at \"{value}\"";
        }
        else if (value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
            if (!path.SelectMany(p => pathext.Select(pext => Path.Combine(p, value + pext))).Any(File.Exists))
                result.ErrorMessage = "Player not found in path";
        }
        else
            result.ErrorMessage = "Player needs to be a full path or file name in PATH";
    }
});
var extPlayerExtraArgsOpt = new Option<string?>("--extra-args", "Extra arguments to send to the player");
installCommand.AddArgument(extPlayerArg);
installCommand.AddOption(extPlayerExtraArgsOpt);
installCommand.SetHandler(HandleInstall, extPlayerArg, extPlayerExtraArgsOpt);
rootCommand.AddCommand(installCommand);
var uninstallCommand = new Command("uninstall", "Uninstall the scheme handler");
uninstallCommand.SetHandler(HandleUninstall);
rootCommand.AddCommand(uninstallCommand);

return rootCommand.Invoke(args);

void HandleInstall(string extPlayerCommand, string? extraPlayerArgs)
{
    var playerName = supportedPlayers.First(p => Path.GetFileName(extPlayerCommand).StartsWith(p, StringComparison.OrdinalIgnoreCase));
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var vbScriptLocation = GetVbScriptLocation();
        if (extraPlayerArgs is not null)
            extraPlayerArgs = UnquoteString(extraPlayerArgs).Replace("\"", "\"\"");
        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\shizou");
        key.SetValue("", "URL:Shizou Protocol");
        key.SetValue("URL Protocol", "");
        using var icon = key.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{extPlayerCommand}\",1");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"wscript.exe \"{vbScriptLocation}\" \"%1\"");
        var vbScriptContent =
            "If InStr(1, WScript.Arguments(0), \"shizou:\") <> 1 Then\n" +
            "   MsgBox \"Error: protocol needs to be shizou:, started with \" & WScript.Arguments(0)\n" +
            "   WScript.Quit 1\n" +
            "End If\n" +
            "Dim url, player_path\n" +
            "url = chr(34) & Unescape(Mid(WScript.Arguments(0), 8)) & chr(34)\n" +
            $"player_path = chr(34) & \"{extPlayerCommand}\" & chr(34)\n" +
            "CreateObject(\"Wscript.Shell\").Run player_path & " + playerName switch
            {
                "mpv" => $"\" --no-terminal --no-ytdl {extraPlayerArgs} -- \" & url",
                "vlc" => $"\" {extraPlayerArgs} \" & url",
                _ => throw new ArgumentOutOfRangeException()
            } + ", 0, False\n";
        File.WriteAllText(vbScriptLocation, vbScriptContent);
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var scriptContent = "#!/bin/bash\n" +
                            "function urldecode() { echo -e \"${1//%/\\\\x}\"; }\n" +
                            "url=\"$(urldecode \"${1:7}\")\"\n" +
                            playerName switch
                            {
                                "mpv" => $"{extPlayerCommand.Replace(" ", "\\ ")} --no-terminal --no-ytdl {extraPlayerArgs} -- \"${{url}}\"\n",
                                "vlc" => $"{extPlayerCommand.Replace(" ", "\\ ")} {extraPlayerArgs} \"${{url}}\"\n",
                                _ => throw new ArgumentOutOfRangeException()
                            };
        var scriptPath = GetShellScriptPath();
        File.WriteAllText(scriptPath, scriptContent);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute | File.GetUnixFileMode(scriptPath));


        var desktopContent =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Shizou External Player\n" +
            $"TryExec={scriptPath.Replace(" ", "\\ ")}\n" +
            $"Exec={scriptPath.Replace(" ", "\\ ")} %u\n" +
            "Terminal=false\n" +
            "StartupNotify=false\n" +
            "MimeType=x-scheme-handler/shizou;\n";
        var desktopPath = GetDesktopEntryPath();
        var desktopDir = Path.GetDirectoryName(desktopPath);
        File.WriteAllText(desktopPath, desktopContent);
        Process.Start("desktop-file-install", $"\"--dir={desktopDir}\" --rebuild-mime-info-cache \"{desktopPath}\"").WaitForExit(2000);
        File.SetUnixFileMode(desktopPath, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute | File.GetUnixFileMode(desktopPath));
    }
}

void HandleUninstall()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\Classes\shizou", false);
        File.Delete(GetVbScriptLocation());
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var desktopPath = GetDesktopEntryPath();
        var desktopDir = Path.GetDirectoryName(desktopPath);
        File.Delete(desktopPath);
        File.Delete(GetShellScriptPath());
        Process.Start("update-desktop-database", [desktopDir!]).WaitForExit(2000);
    }
}

static string UnquoteString(string str)
{
    if (str.Length >= 2 && str.StartsWith('"') && str.EndsWith('"'))
        return str[1..^1];
    return str;
}

static string GetDesktopEntryPath()
{
    var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
    Directory.CreateDirectory(desktopDir);
    var desktopName = "shizou.desktop";
    return Path.Combine(desktopDir, desktopName);
}

static string GetShellScriptPath()
{
    var shellScriptDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin");
    Directory.CreateDirectory(shellScriptDir);
    var scriptName = "shizou-ext-player-start.sh";
    return Path.Combine(shellScriptDir, scriptName);
}

static string GetVbScriptLocation()
{
    var vbScriptDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shizou");
    Directory.CreateDirectory(vbScriptDir);
    var vbScriptName = "shizou-ext-player-start.vbs";
    return Path.Combine(vbScriptDir, vbScriptName);
}
