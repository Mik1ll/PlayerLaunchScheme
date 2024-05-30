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

    var executableLocation = Environment.ProcessPath ?? throw new ArgumentException("No process path?");
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var vbScriptLocation =
            Path.Combine(Path.GetDirectoryName(executableLocation) ?? throw new InvalidOperationException("Parent directory returned null for executable"),
                "start.vbs");
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
            "url = chr(34) & Mid(WScript.Arguments(0), 8) & chr(34)\n";
        vbScriptContent += playerName switch
        {
            "mpv" =>
                $"CreateObject(\"Wscript.Shell\").Run \"\"\"{extPlayerCommand}\"\" --no-terminal --no-ytdl {extraPlayerArgs} -- \" & url, 0, False",
            "vlc" => $"CreateObject(\"Wscript.Shell\").Run \"\"\"{extPlayerCommand}\"\" {extraPlayerArgs} \" & url, 0, False",
            _ => throw new ArgumentOutOfRangeException()
        };
        File.WriteAllText(vbScriptLocation, vbScriptContent);
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        void ValidateChars(string str)
        {
            var invalidChars = new HashSet<char> { '`', '$', '\\', ' ', '\t', '\n', '\"', '\'', '<', '>', '~', '|', '&', ';', '*', '?', '#', '(', ')' };
            string StrRepr(char s) => s switch { '\t' => "<tab>", ' ' => "<space>", '\n' => "<newline>", _ => s.ToString() };
            if (str.Intersect(invalidChars).FirstOrDefault() is var invalidChar and not '\0')
                throw new ArgumentException($"String \"{str}\" contains an invalid desktop entry character: {StrRepr(invalidChar)}");
        }

        ValidateChars(extPlayerCommand);
        extraPlayerArgs = extraPlayerArgs?
            .Replace("\\", "\\\\")
            .Replace("$", "\\$")
            .Replace("`", "\\`")
            .Replace("\\", "\\\\");

        var desktopContent =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Shizou External Player\n" +
            $"TryExec={extPlayerCommand}\n" +
            playerName switch
            {
                "mpv" => $"Exec={extPlayerCommand} --no-terminal --no-ytdl {extraPlayerArgs} -- %u\n",
                "vlc" => $"Exec={extPlayerCommand} {extraPlayerArgs} %u\n",
                _ => throw new ArgumentOutOfRangeException()
            } +
            "Terminal=false\n" +
            "StartupNotify=false\n" +
            "MimeType=x-scheme-handler/shizou;\n";
        var desktopPath = GetDesktopEntryPath();
        var desktopDir = Path.GetDirectoryName(desktopPath);
        File.WriteAllText(desktopPath, desktopContent);
        Process.Start("desktop-file-install", $"\"--dir={desktopDir}\" --rebuild-mime-info-cache \"{desktopPath}\"")
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
        var desktopPath = GetDesktopEntryPath();
        var desktopDir = Path.GetDirectoryName(desktopPath);
        File.Delete(desktopPath);
        Process.Start("update-desktop-database", new[] { desktopDir! }).WaitForExit(2000);
    }
}

string UnquoteString(string str)
{
    if (str.Length >= 2 && str.StartsWith('"') && str.EndsWith('"'))
        return str[1..^1];
    return str;
}

string GetDesktopEntryPath()
{
    var desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications");
    Directory.CreateDirectory(desktopDir);
    var desktopName = "shizou.desktop";
    var desktopPath = Path.Combine(desktopDir, desktopName);
    return desktopPath;
}
