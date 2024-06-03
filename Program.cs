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

return rootCommand.Invoke(args);

void HandleInstall(string extPlayerCommand, string? extraPlayerArgs)
{
    extPlayerCommand = UnquoteString(extPlayerCommand);
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
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
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
            "Dim url\n" +
            "url = chr(34) & Unescape(Mid(WScript.Arguments(0), 8)) & chr(34)\n";
        vbScriptContent += playerName switch
        {
            "mpv" =>
                $"CreateObject(\"Wscript.Shell\").Run chr(34) & {extPlayerCommand} & chr(34) & \" --no-terminal --no-ytdl {extraPlayerArgs} -- \" & url, 0, False",
            "vlc" => $"CreateObject(\"Wscript.Shell\").Run chr(34) & {extPlayerCommand} & chr(34) & \" {extraPlayerArgs} \" & url, 0, False",
            _ => throw new ArgumentOutOfRangeException()
        };
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
        Process.Start("update-desktop-database", new[] { desktopDir! }).WaitForExit(2000);
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
