using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MinePair {

// The helper is a small Windows service installed once (one UAC prompt) into Program Files.
// It runs as SYSTEM and only performs a fixed list of actions on MinePair's own tunnel and firewall rules.
// The GUI talks to it over a named pipe that only the installing user can open.
public static class Helper {
    public const string ServiceName = "MinePairHelper";
    public const string PipeName = "MinePairHelper";
    static readonly string[] Actions = { "version", "status", "install-wireguard", "start", "stop", "router-add", "router-remove" };

    public static string InstalledExe { get { return Path.Combine(Core.HelperDir, "MinePair.exe"); } }
    static string ScriptPath { get { return Path.Combine(Core.HelperDir, "Network.ps1"); } }
    static string ConfigPath { get { return Path.Combine(Core.StateDir, "helper.json"); } }

    static string ownHash;
    public static string OwnHash() {
        if (ownHash == null) ownHash = Core.Hash(Core.ExePath, true);
        return ownHash;
    }

    public static bool Installed() {
        try {
            using (var sc = new ServiceController(ServiceName)) { var status = sc.Status; return true; }
        } catch { return false; }
    }

    // True when the installed helper is this exact build.
    public static bool UpToDate() {
        if (!Installed()) return false;
        try { return Convert.ToString(Call("version", 30000)["hash"]) == OwnHash(); } catch { return false; }
    }

    // ---------- client ----------

    public static Dictionary<string, object> Call(string action, int timeout) {
        try { return CallOnce(action, timeout); }
        catch (IOException) { return CallOnce(action, timeout); }
        catch (System.TimeoutException) { return CallOnce(action, timeout); }
        catch (System.ServiceProcess.TimeoutException) { return CallOnce(action, timeout); }
    }

    static Dictionary<string, object> CallOnce(string action, int timeout) {
        EnsureRunning();
        // Ask only for read/write data: the default InOut access would also request "create pipe instance", which the helper never grants.
        var access = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize;
        using (var pipe = new NamedPipeClientStream(".", PipeName, access, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None)) {
            pipe.Connect(15000);
            // While the helper is stopped any program could create a pipe with this name; only talk to a service.
            if (!Native.PipeServerIsService(pipe)) throw new UserFacingException("Another program is pretending to be the MinePair helper. Restart Windows and try again.");
            var request = Encoding.UTF8.GetBytes(Core.Serialize(new Dictionary<string, object> { { "action", action } }) + "\n");
            pipe.Write(request, 0, request.Length);
            pipe.Flush();
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var line = reader.ReadLineAsync();
            if (!line.Wait(timeout)) throw new UserFacingException("The MinePair helper did not respond in time.");
            if (line.Result == null) throw new IOException("The helper closed the connection.");
            var response = Core.Parse<Dictionary<string, object>>(line.Result);
            if (!Convert.ToBoolean(response["ok"])) throw new UserFacingException(Convert.ToString(response["error"]));
            return response;
        }
    }

    static void EnsureRunning() {
        if (!Installed()) throw new UserFacingException("The MinePair helper is not installed. Run the setup again.");
        using (var sc = new ServiceController(ServiceName)) {
            if (sc.Status == ServiceControllerStatus.StopPending) sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
    }

    // ---------- elevation from the GUI ----------

    public static void InstallElevated() {
        string sid = WindowsIdentity.GetCurrent().User.Value;
        RunElevated("--install-helper " + sid + " " + Core.Quote(Core.Data));
        if (!Installed()) throw new UserFacingException("The helper was not installed.");
    }

    public static void UninstallElevated() {
        RunElevated("--uninstall-helper");
    }

    static void RunElevated(string args) {
        var info = new ProcessStartInfo(Core.ExePath, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        Process p;
        try { p = Process.Start(info); }
        catch (Win32Exception e) {
            if (e.NativeErrorCode == 1223) throw new UserFacingException("The administrator prompt was cancelled. Without it MinePair cannot set up the secure network — try again and choose \"Yes\".");
            throw;
        }
        using (p) {
            p.WaitForExit();
            if (p.ExitCode != 0) throw new UserFacingException("The elevated action did not finish. Details were shown in a separate window.");
        }
    }

    // ---------- elevated install / uninstall ----------

    public static void Install(string sid, string data) {
        if (!Core.Elevated) throw new Exception("Administrator rights are required.");
        var user = new SecurityIdentifier(sid);
        if (!Path.IsPathRooted(data) || data.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new Exception("Invalid data folder.");
        data = Path.GetFullPath(data);

        StopService();
        Directory.CreateDirectory(Core.HelperDir);
        if (!Path.GetFullPath(Core.ExePath).Equals(Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(Core.ExePath, InstalledExe);
        // The network script comes from this process's own embedded resource, never from a user-writable file.
        Core.Atomic(ScriptPath, EmbeddedScript());

        Core.AdminDirectory(Core.StateDir);
        Core.Write(ConfigPath, new Dictionary<string, object> { { "sid", user.Value }, { "data", data } });
        string userJournal = Path.Combine(data, "network-journal.json"), stateJournal = Path.Combine(Core.StateDir, "network-journal.json");
        if (File.Exists(userJournal)) {
            if (!File.Exists(stateJournal)) File.Copy(userJournal, stateJournal);
            File.Delete(userJournal);
        }

        string bin = "\"\\\"" + InstalledExe + "\\\" --service\"";
        if (Installed()) Core.Run("sc.exe", "config " + ServiceName + " binPath= " + bin + " start= demand obj= LocalSystem", null, 30000);
        else Core.Run("sc.exe", "create " + ServiceName + " binPath= " + bin + " start= demand obj= LocalSystem DisplayName= \"MinePair Helper\"", null, 30000);
        Core.Run("sc.exe", "description " + ServiceName + " \"Turns the MinePair secure network on and off. Installed and removed from the MinePair app.\"", null, 30000);
        // Default service rights plus start/query for the installing user only.
        Core.Run("sc.exe", "sdset " + ServiceName + " \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWRPLOCRRC;;;" + user.Value + ")\"", null, 30000);
    }

    public static string Uninstall() {
        if (!Core.Elevated) throw new Exception("Administrator rights are required.");
        string warning = null;
        if (File.Exists(ScriptPath) && File.Exists(ConfigPath)) {
            try { RunScript("uninstall", ReadConfig()["data"]); }
            catch (Exception e) { warning = e.Message; }
        }
        StopService();
        if (Installed()) Core.Run("sc.exe", "delete " + ServiceName, null, 30000);
        for (int i = 0; i < 20 && Directory.Exists(Core.HelperDir); i++) {
            try { Directory.Delete(Core.HelperDir, true); } catch { Thread.Sleep(500); }
        }
        if (Directory.Exists(Core.StateDir)) Directory.Delete(Core.StateDir, true);
        return warning;
    }

    static void CopyWithRetry(string from, string to) {
        for (int i = 0; ; i++) {
            try { File.Copy(from, to, true); return; }
            catch (IOException) { if (i >= 20) throw; Thread.Sleep(500); }
        }
    }

    static void StopService() {
        if (!Installed()) return;
        using (var sc = new ServiceController(ServiceName)) {
            if (sc.Status != ServiceControllerStatus.Stopped) {
                try { sc.Stop(); } catch { }
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
    }

    // Windows PowerShell 5.1 reads BOM-less files as ANSI, which would garble the Russian messages: always write a BOM.
    public static byte[] EmbeddedScript() {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MinePair.Network.ps1"))
        using (var m = new MemoryStream()) {
            s.CopyTo(m);
            var bytes = m.ToArray();
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            return bytes.Take(3).SequenceEqual(bom) ? bytes : bom.Concat(bytes).ToArray();
        }
    }

    static Dictionary<string, string> ReadConfig() {
        var c = Core.Read<Dictionary<string, object>>(ConfigPath);
        return new Dictionary<string, string> { { "sid", Convert.ToString(c["sid"]) }, { "data", Convert.ToString(c["data"]) } };
    }

    // ---------- service side ----------

    public static Dictionary<string, object> RunScript(string action, string data) {
        string result = Path.Combine(Core.StateDir, "result.json");
        if (File.Exists(result)) File.Delete(result);
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Core.Quote(ScriptPath) + " -Data " + Core.Quote(data) + " -State " + Core.Quote(Core.StateDir) + " -Action " + action;
        using (var p = Process.Start(new ProcessStartInfo(ps, args) { UseShellExecute = false, CreateNoWindow = true })) {
            if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } throw new Exception("The network operation did not finish within 10 minutes."); }
        }
        if (!File.Exists(result)) throw new Exception("The network operation returned no result.");
        var r = Core.Read<Dictionary<string, object>>(result);
        if (!Convert.ToBoolean(r["ok"])) throw new UserFacingException(Convert.ToString(r["error"]));
        return r;
    }

    internal static Dictionary<string, object> Execute(string action, string data) {
        if (!Actions.Contains(action)) throw new Exception("Unknown action.");
        if (action == "version") return new Dictionary<string, object> { { "hash", OwnHash() } };
        if (action == "status") return Status(data);
        string mapped = action == "install-wireguard" ? "install" : action;
        RunScript(mapped, data);
        return new Dictionary<string, object>();
    }

    // Seconds since the last WireGuard handshake with the paired key; -1 when there has been none yet.
    static Dictionary<string, object> Status(string data) {
        var s = Core.Read<Settings>(Path.Combine(data, "settings.json"));
        Core.Validate(s, false);
        var result = new Dictionary<string, object> { { "tunnel", false }, { "handshake", -1L } };
        if (!File.Exists(Core.WgTool)) return result;
        string output;
        try { output = Core.Run(Core.WgTool, "show " + s.TunnelName() + " latest-handshakes", null, 10000); }
        catch { return result; }
        result["tunnel"] = true;
        foreach (var line in output.Split('\n')) {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            long stamp;
            if (parts.Length == 2 && parts[0] == s.PeerKey && long.TryParse(parts[1], out stamp) && stamp > 0)
                result["handshake"] = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp);
        }
        return result;
    }

    internal static NamedPipeServerStream CreatePipe(string sid) {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
    }

    internal static Dictionary<string, string> Config() { return ReadConfig(); }
}

public sealed class HelperService : ServiceBase {
    volatile bool stopping, busy;
    DateTime lastUse = DateTime.UtcNow;
    string sid, data;

    public HelperService() {
        ServiceName = Helper.ServiceName;
        CanStop = true;
    }

    protected override void OnStart(string[] args) {
        var config = Helper.Config();
        sid = config["sid"];
        data = config["data"];
        new Thread(Loop) { IsBackground = true }.Start();
        new Thread(IdleWatch) { IsBackground = true }.Start();
    }

    protected override void OnStop() {
        stopping = true;
        try { using (var c = new NamedPipeClientStream(".", Helper.PipeName)) c.Connect(500); } catch { }
    }

    // The helper is only needed for a moment; it stops itself after ten idle minutes.
    void IdleWatch() {
        while (!stopping) {
            Thread.Sleep(30000);
            if (!busy && DateTime.UtcNow - lastUse > TimeSpan.FromMinutes(10)) { try { Stop(); } catch { } return; }
        }
    }

    void Loop() {
        while (!stopping) {
            try {
                using (var pipe = Helper.CreatePipe(sid)) {
                    pipe.WaitForConnection();
                    if (stopping) return;
                    Handle(pipe);
                }
            } catch (Exception e) {
                Log(e.ToString());
                Thread.Sleep(500);
            }
        }
    }

    void Handle(NamedPipeServerStream pipe) {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (line.Length < 4096) {
            if (pipe.Read(buffer, 0, 1) != 1 || buffer[0] == '\n') break;
            line.Append((char)buffer[0]);
        }
        Dictionary<string, object> response;
        busy = true;
        try {
            var request = Core.Parse<Dictionary<string, object>>(line.ToString());
            response = Helper.Execute(Convert.ToString(request["action"]), data);
            response["ok"] = true;
        } catch (Exception e) {
            response = new Dictionary<string, object> { { "ok", false }, { "error", e is UserFacingException ? e.Message : "Helper error: " + e.Message } };
            if (!(e is UserFacingException)) Log(e.ToString());
        } finally {
            busy = false;
            lastUse = DateTime.UtcNow;
        }
        var bytes = Encoding.UTF8.GetBytes(Core.Serialize(response) + "\n");
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    static void Log(string text) {
        try { File.AppendAllText(Path.Combine(Core.StateDir, "helper.log"), DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine); } catch { }
    }
}
}
