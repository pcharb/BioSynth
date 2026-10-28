using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BioSynth.App;

/// <summary>
/// Ouvre l'interface dans une fenêtre d'application : un navigateur Chromium (Edge, Chrome,
/// Chromium, Brave) lancé en mode « --app », sans onglets ni barre d'adresse, avec un profil
/// dédié dans le dossier de données de BioSynth. Edge est présent sur tout Windows 10/11 ;
/// sous Linux et macOS, Chrome ou Chromium sont cherchés aux emplacements usuels.
/// À défaut, l'URL est ouverte dans le navigateur par défaut.
/// </summary>
internal static class AppWindow
{
    /// <summary>Lance la fenêtre. Retourne le processus du navigateur dédié (null si navigateur par défaut ou échec).</summary>
    public static Process? Open(string url, string profileDir, bool appMode)
    {
        if (appMode && FindChromium() is string exe)
        {
            try
            {
                Directory.CreateDirectory(profileDir);
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[]
                {
                    $"--app={url}",
                    $"--user-data-dir={profileDir}",
                    "--window-size=1440,900",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-features=Translate",
                })
                    psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                if (p != null)
                {
                    // Vider les sorties du navigateur pour ne pas encombrer la console
                    p.OutputDataReceived += (_, _) => { };
                    p.ErrorDataReceived  += (_, _) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    return p;
                }
            }
            catch { /* repli sur le navigateur par défaut */ }
        }
        OpenDefault(url);
        return null;
    }

    public static void OpenDefault(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (HasDisplay())
                Process.Start(new ProcessStartInfo("xdg-open", url) { RedirectStandardOutput = true, RedirectStandardError = true });
        }
        catch { /* pas de navigateur : l'URL est affichée dans la console */ }
    }

    private static bool HasDisplay()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
           !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string? FindChromium()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
            candidates.Add("/Applications/Brave Browser.app/Contents/MacOS/Brave Browser");
        }
        else
        {
            if (!HasDisplay()) return null;
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable", "brave-browser" })
                foreach (var dir in path)
                    candidates.Add(Path.Combine(dir, name));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Vrai si le port est déjà occupé (par exemple une autre instance de BioSynth).</summary>
    public static bool PortInUse(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return false;
        }
        catch (SocketException) { return true; }
    }
}
