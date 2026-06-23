using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace ScreenBuddyTray;

/// <summary>Represents a single virtual display created by the VDD driver.</summary>
public record VirtualDisplayInfo(int Id, int Width, int Height, int RefreshRate, string Name);

/// <summary>
/// Manages the Virtual Display Driver (VDD) lifecycle:
/// detection, installation, and adding/removing virtual monitors
/// via the VDD named pipe and settings XML file.
/// </summary>
public static class VddManager
{
    // VDD named-pipe name (matches VDD 25.x IPC interface)
    private const string VddPipeName = "MTTVirtualDisplayPipe";
    private const string VddSettingsFolder = "C:\\VirtualDisplayDriver";
    private const string VddSettingsFile = "C:\\VirtualDisplayDriver\\vdd_settings.xml";

    // Where we cache extracted driver files on first run
    private static readonly string DriverCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ScreenBuddy", "Drivers");

    // ── Driver presence ────────────────────────────────────────────────────────

    /// <summary>Returns true if the VDD kernel driver is installed on this machine.</summary>
    public static bool IsDriverInstalled()
    {
        // 1. Check the Setup Class Registry Key (robust, no elevation, no WMI)
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey != null)
            {
                foreach (var subkeyName in classKey.GetSubKeyNames())
                {
                    if (subkeyName.Length != 4 || !int.TryParse(subkeyName, out _))
                        continue;

                    using var subKey = classKey.OpenSubKey(subkeyName);
                    if (subKey != null)
                    {
                        var desc = subKey.GetValue("DriverDesc") as string;
                        if (desc != null && desc.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: check SCM or UMDF keys for MttVDD service
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MttVDD");
            if (key != null) return true;
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\MttVDD");
            if (key != null) return true;
        }
        catch { }

        // 3. Fallback: WMI check
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Virtual Display%' OR DeviceID LIKE '%ROOT\\MTTVDD%'");
            if (searcher.Get().Count > 0) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Ensures the VDD driver is installed.
    /// If not, prompts the user and installs it silently via pnputil.
    /// Returns true when the driver is ready.
    /// </summary>
    public static async Task<bool> EnsureDriverInstalledAsync()
    {
        // Ensure settings folder and file exist first
        EnsureSettingsFolderAndFile(0);

        if (IsDriverInstalled()) return true;

        var result = MessageBox.Show(
            "ScreenBuddy needs to install a virtual display driver so your phone can act as a second monitor.\n\n" +
            "This is a one-time installation and requires administrator permission (one UAC click).\n\n" +
            "Click OK to install now.",
            "ScreenBuddy — Install Display Driver",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (result != DialogResult.OK) return false;

        return await InstallDriverSilentlyAsync();
    }

    // ── Installation ───────────────────────────────────────────────────────────

    private static async Task<bool> InstallDriverSilentlyAsync()
    {
        try
        {
            string? infPath = await LocateOrExtractDriverInfAsync();
            if (infPath == null) return false;

            // Ensure the settings file exists before installing, so the driver loads successfully on first launch
            EnsureSettingsFolderAndFile(0);

            // 1. Register the driver package in the driver store using pnputil.exe
            var psiPnp = new ProcessStartInfo
            {
                FileName        = "pnputil.exe",
                Arguments       = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
                CreateNoWindow  = true,
                WorkingDirectory = Path.GetDirectoryName(infPath)
            };

            using var procPnp = Process.Start(psiPnp);
            if (procPnp != null)
            {
                await procPnp.WaitForExitAsync();
            }

            // 2. Instantiate the virtual display device node using devcon.exe
            string? devconPath = FindDevconPath(infPath);
            if (devconPath != null)
            {
                var psiDevcon = new ProcessStartInfo
                {
                    FileName        = devconPath,
                    Arguments       = $"install \"{infPath}\" Root\\MttVDD",
                    UseShellExecute = true,
                    Verb            = "runas",
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    CreateNoWindow  = true,
                    WorkingDirectory = Path.GetDirectoryName(infPath)
                };

                using var procDevcon = Process.Start(psiDevcon);
                if (procDevcon == null)
                {
                    ShowError("Failed to launch devcon.exe for device node installation.");
                    return false;
                }

                await procDevcon.WaitForExitAsync();
                if (procDevcon.ExitCode != 0)
                {
                    ShowError($"devcon.exe exited with code {procDevcon.ExitCode}.\n" +
                              "Device node creation failed. Try running the app as Administrator.");
                    return false;
                }
            }
            else
            {
                Debug.WriteLine("[VddManager] devcon.exe not found. Relying solely on pnputil.");
            }

            // Verify if the driver is successfully installed & active now
            if (IsDriverInstalled())
            {
                MessageBox.Show(
                    "Virtual display driver installed successfully!\n\n" +
                    "You can now create virtual displays from the Displays tab.",
                    "ScreenBuddy — Driver Installed",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            else
            {
                ShowError("Driver registration completed, but the Virtual Display device could not be found or started.");
                return false;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User clicked "No" on the UAC dialog
            MessageBox.Show("Installation cancelled — UAC was denied.",
                "ScreenBuddy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            ShowError($"Driver installation failed: {ex.Message}");
            return false;
        }
    }

    private static string? FindDevconPath(string infPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(infPath);
            while (dir != null)
            {
                var direct = Path.Combine(dir, "devcon.exe");
                if (File.Exists(direct)) return direct;

                var dep = Path.Combine(dir, "Dependencies", "devcon.exe");
                if (File.Exists(dep)) return dep;

                var parentName = Path.GetFileName(dir);
                if (parentName == "VirtualDrivers.Virtual-Display-Driver_Microsoft.Winget.Source_8wekyb3d8bbwe" || 
                    parentName == "Packages")
                {
                    // Scan recursively in subfolders if we are in the Winget package parent dir
                    var files = Directory.GetFiles(dir, "devcon.exe", SearchOption.AllDirectories);
                    if (files.Length > 0) return files[0];
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Looks for a bundled .inf in the DriverCacheDir, or guides the user
    /// to download VDD and cache it there.
    /// </summary>
    private static async Task<string?> LocateOrExtractDriverInfAsync()
    {
        Directory.CreateDirectory(DriverCacheDir);

        // 1. Check cache directory first
        var infFiles = Directory.GetFiles(DriverCacheDir, "*.inf");
        foreach (var f in infFiles)
        {
            if (IsCorrectArchitecturePath(f))
            {
                return f;
            }
        }

        // 2. Check if VDD was already installed via winget/setup
        var knownPaths = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Virtual Display Driver"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Virtual Display Driver")
        };

        // Check WinGet portable package directory
        var wingetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetDir))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(wingetDir, "*Virtual-Display-Driver*", SearchOption.TopDirectoryOnly))
                {
                    knownPaths.Add(sub);
                }
            }
            catch { /* ignore directory enumeration errors */ }
        }

        foreach (var dir in knownPaths)
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories);
            foreach (var f in found)
            {
                if (IsCorrectArchitecturePath(f))
                {
                    return f;
                }
            }
        }

        // 3. Ask the user to download and show them where to put it
        var dlgResult = MessageBox.Show(
            "The Virtual Display Driver files were not found.\n\n" +
            "Please download the latest release from GitHub:\n" +
            "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases\n\n" +
            $"Then extract the .inf file into this folder and click Retry:\n{DriverCacheDir}\n\n" +
            "Click Yes to open the download page now.",
            "ScreenBuddy — Driver Files Missing",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);

        if (dlgResult == DialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo(
                "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases")
                { UseShellExecute = true });

            // Open the cache folder so the user can paste files there
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DriverCacheDir}\"")
                { UseShellExecute = true });
        }

        await Task.CompletedTask; // keep async signature for future embedded-resource support
        return null;
    }

    private static bool IsCorrectArchitecturePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        bool isArm64System = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

        if (isArm64System)
        {
            return normalized.Contains(@"\ARM64\", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return !normalized.Contains(@"\ARM64\", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── XML Settings Management ────────────────────────────────────────────────

    private static void EnsureSettingsFolderAndFile(int count = 0)
    {
        try
        {
            Directory.CreateDirectory(VddSettingsFolder);
            if (!File.Exists(VddSettingsFile))
            {
                WriteDefaultXml(VddSettingsFile, count);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VddManager] EnsureSettingsFolderAndFile error: {ex.Message}");
        }
    }

    private static int GetXmlDisplayCount()
    {
        EnsureSettingsFolderAndFile(0);
        if (!File.Exists(VddSettingsFile)) return 0;
        try
        {
            var doc = new XmlDocument();
            doc.Load(VddSettingsFile);
            var node = doc.SelectSingleNode("/vdd_settings/monitors/count");
            if (node != null && int.TryParse(node.InnerText, out int count))
            {
                return count;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VddManager] GetXmlDisplayCount error: {ex.Message}");
        }
        return 0;
    }

    private static void UpdateXmlDisplayCount(int count)
    {
        EnsureSettingsFolderAndFile(count);
        try
        {
            var doc = new XmlDocument();
            doc.Load(VddSettingsFile);
            var node = doc.SelectSingleNode("/vdd_settings/monitors/count");
            if (node != null)
            {
                node.InnerText = count.ToString();
                doc.Save(VddSettingsFile);
            }
            else
            {
                WriteDefaultXml(VddSettingsFile, count);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VddManager] UpdateXmlDisplayCount error: {ex.Message}");
            WriteDefaultXml(VddSettingsFile, count);
        }
    }

    private static void WriteDefaultXml(string path, int count)
    {
        var xml = $@"<?xml version='1.0' encoding='utf-8'?>
<vdd_settings>
    <monitors>
        <count>{count}</count>
    </monitors>
    <gpu>
        <friendlyname>default</friendlyname>
    </gpu>
    <global>
        <g_refresh_rate>60</g_refresh_rate>
        <g_refresh_rate>90</g_refresh_rate>
        <g_refresh_rate>120</g_refresh_rate>
    </global>
    <resolutions>
        <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>1280</width><height>720</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>2560</width><height>1440</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>1080</width><height>1920</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>720</width><height>1280</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>1080</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
    </resolutions>
    <options>
        <CustomEdid>false</CustomEdid>
        <PreventSpoof>false</PreventSpoof>
        <EdidCeaOverride>false</EdidCeaOverride>
        <HardwareCursor>true</HardwareCursor>
        <SDR10bit>false</SDR10bit>
        <HDRPlus>false</HDRPlus>
        <logging>false</logging>
        <debuglogging>false</debuglogging>
    </options>
</vdd_settings>";
        File.WriteAllText(path, xml, Encoding.UTF8);
    }

    private static void EnsureXmlResolutionExists(int width, int height)
    {
        EnsureSettingsFolderAndFile(0);
        try
        {
            var doc = new XmlDocument();
            doc.Load(VddSettingsFile);
            var resolutionsNode = doc.SelectSingleNode("/vdd_settings/resolutions");
            if (resolutionsNode == null) return;

            // Check if resolution already exists
            bool exists = false;
            foreach (XmlNode resNode in resolutionsNode.ChildNodes)
            {
                var wNode = resNode.SelectSingleNode("width");
                var hNode = resNode.SelectSingleNode("height");
                if (wNode != null && hNode != null)
                {
                    if (int.TryParse(wNode.InnerText, out int w) && int.TryParse(hNode.InnerText, out int h) && w == width && h == height)
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                var newRes = doc.CreateElement("resolution");
                var wEl = doc.CreateElement("width");
                wEl.InnerText = width.ToString();
                var hEl = doc.CreateElement("height");
                hEl.InnerText = height.ToString();
                var rEl = doc.CreateElement("refresh_rate");
                rEl.InnerText = "60";

                newRes.AppendChild(wEl);
                newRes.AppendChild(hEl);
                newRes.AppendChild(rEl);
                resolutionsNode.AppendChild(newRes);
                doc.Save(VddSettingsFile);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VddManager] EnsureXmlResolutionExists error: {ex.Message}");
        }
    }

    // ── Virtual display management via VDD named pipe ──────────────────────────

    /// <summary>
    /// Returns all virtual displays currently managed by VDD.
    /// </summary>
    public static List<VirtualDisplayInfo> GetVirtualDisplays()
    {
        var list = new List<VirtualDisplayInfo>();
        if (!IsDriverInstalled()) return list;

        int count = GetXmlDisplayCount();
        for (int i = 0; i < count; i++)
        {
            list.Add(new VirtualDisplayInfo(i, 1920, 1080, 60, $"ScreenBuddy Virtual Display {i + 1}"));
        }
        return list;
    }

    /// <summary>Sends an "add" command to VDD to create a new virtual monitor.</summary>
    public static bool AddVirtualDisplay(int width, int height, int refreshRate = 60)
    {
        if (!IsDriverInstalled())
        {
            ShowVddNotRunning();
            return false;
        }

        // Ensure resolution exists in settings XML
        EnsureXmlResolutionExists(width, height);

        int count = GetXmlDisplayCount();
        int newCount = count + 1;

        // Update display count in XML
        UpdateXmlDisplayCount(newCount);

        // Tell the driver to reload settings via the pipe
        string response = SendPipeCommand($"SETDISPLAYCOUNT {newCount}");
        if (response == "ERROR")
        {
            ShowVddNotRunning();
            return false;
        }

        return true;
    }

    /// <summary>Removes the virtual display with the given VDD id.</summary>
    public static bool RemoveVirtualDisplay(int displayId)
    {
        if (!IsDriverInstalled()) return false;

        int count = GetXmlDisplayCount();
        if (count <= 0) return false;

        int newCount = count - 1;
        UpdateXmlDisplayCount(newCount);

        string response = SendPipeCommand($"SETDISPLAYCOUNT {newCount}");
        return response != "ERROR";
    }

    // ── Startup-with-Windows registry ──────────────────────────────────────────

    public static void SetStartupWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true)!;

        if (enable)
            key.SetValue("ScreenBuddy", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("ScreenBuddy", throwOnMissingValue: false);
    }

    public static bool IsStartupWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("ScreenBuddy") != null;
    }

    // ── Pipe helpers ───────────────────────────────────────────────────────────

    private static string SendPipeCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", VddPipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(1500); // 1.5 seconds timeout

            // Write command in UTF-16LE
            byte[] cmdBytes = Encoding.Unicode.GetBytes(command);
            pipe.Write(cmdBytes, 0, cmdBytes.Length);
            pipe.Flush();

            // Read response until disconnected (UTF-8)
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            string response = reader.ReadToEnd();
            return response;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VddManager] SendPipeCommand '{command}' failed: {ex.Message}");
            return "ERROR";
        }
    }

    // ── UI helpers ─────────────────────────────────────────────────────────────

    private static void ShowVddNotRunning()
    {
        MessageBox.Show(
            "The Virtual Display Driver is not responding.\n\n" +
            "Make sure the driver is installed (go to the Displays tab and click Install Driver).",
            "ScreenBuddy — Driver Not Responding",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void ShowError(string msg) =>
        MessageBox.Show(msg, "ScreenBuddy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
