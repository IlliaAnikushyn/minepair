using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MinePair {

public sealed class Settings {
    public int Schema = 2;
    public string Id = Core.NewId(12);
    public string Role = "";              // "" until the wizard choice, then "host" or "client"
    public string HostNick = "";
    public string FriendNick = "";
    public string Endpoint = "";          // host's public IPv4
    public int Port = 51820;
    public int MemoryMb = 3072;
    public string HostIp = "";
    public string FriendIp = "";
    public string PublicKey = "";
    public string PeerKey = "";
    public string Java = "";              // java.exe the firewall rule is scoped to (host)
    public bool Eula;
    public string ActiveWorld = "";
    public bool RouterReady;
    public string LanIp = "";
    public bool ReplySent;
    public string TunnelName() { return "MinePair_" + Id; }
    public bool IsHost() { return Role == "host"; }
    public bool Paired() { return !string.IsNullOrEmpty(PeerKey); }
}

public sealed class World {
    public int Schema = 1;
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public string GameMode = "survival";
    public string Difficulty = "normal";
    public bool Hardcore;
    public bool Pvp = true;
    public string Seed = "";
    public string ServerSha1 = "";
    public int JavaMajor;
    public string Created = "";
}

public sealed class UserFacingException : Exception {
    public UserFacingException(string message) : base(message) {}
}

public static class Core {
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object JsonLock = new object();

    public static string ExePath = Assembly.GetExecutingAssembly().Location;
    public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinePair");
    public static string Config { get { return Path.Combine(Data, "settings.json"); } }
    public static string WorldsDir { get { return Path.Combine(Data, "worlds"); } }
    public static string VersionsDir { get { return Path.Combine(Data, "versions"); } }
    public static string JavaDir { get { return Path.Combine(Data, "java"); } }
    public static string BackupsDir { get { return Path.Combine(Data, "backups"); } }
    public static string LogsDir { get { return Path.Combine(Data, "logs"); } }
    public static string DownloadsDir { get { return Path.Combine(Data, "downloads"); } }
    public static string ProtectedDir { get { return Path.Combine(Data, "protected"); } }
    public static string PrivateKeyPath { get { return Path.Combine(Data, "private-key.dpapi"); } }
    public static string UncleanMarker { get { return Path.Combine(Data, "unclean-session"); } }
    static string SessionFile { get { return Path.Combine(Data, "session.json"); } }
    static string StopRequest { get { return Path.Combine(Data, "stop.request"); } }
    public static string HelperDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MinePair"); } }
    public static string StateDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MinePair"); } }
    public static string Wg { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"); } }
    public static string WgTool { get { return Path.Combine(Path.GetDirectoryName(Wg), "wg.exe"); } }

    // ---------- files ----------

    public static T Read<T>(string path) { lock (JsonLock) return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
    public static T Parse<T>(string text) { lock (JsonLock) return Json.Deserialize<T>(text); }
    public static string Serialize(object value) { lock (JsonLock) return Json.Serialize(value); }

    public static void Write(string path, object value) {
        Atomic(path, Encoding.UTF8.GetBytes(Serialize(value)));
    }

    public static void Atomic(string path, byte[] content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".new";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            f.Write(content, 0, content.Length);
            f.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    // Owner, SYSTEM and Administrators only; inherited entries removed.
    public static void PrivateDirectory(string path) {
        RestrictDirectory(path, WindowsIdentity.GetCurrent().User);
    }

    static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

    // SYSTEM and Administrators only: used for the helper's state in ProgramData.
    // Any user may create folders in ProgramData, so a folder planted before installation (or a junction
    // pointing elsewhere) is removed instead of reused: its owner could re-grant itself access later.
    public static void AdminDirectory(string path) {
        if (Directory.Exists(path)) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, false);
            else {
                var owner = Directory.GetAccessControl(path, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
                if (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid)) Directory.Delete(path, true);
            }
        }
        RestrictDirectory(path, null);
    }

    static void RestrictDirectory(string path, SecurityIdentifier user) {
        Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        var sids = new List<SecurityIdentifier> { SystemSid, AdminsSid };
        if (user != null) sids.Add(user);
        else sec.SetOwner(AdminsSid);
        foreach (var sid in sids)
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.SetAccessControl(path, sec);
    }

    public static string NewId(int hexChars) {
        var bytes = new byte[hexChars / 2];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Hex(bytes);
    }

    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

    static byte[] FromHex(string hex) {
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }

    // ---------- profile ----------

    public static Settings Load() {
        PrivateDirectory(Data);
        if (File.Exists(Config)) {
            var s = Read<Settings>(Config);
            Validate(s, false);
            return s;
        }
        var result = new Settings();
        AssignTunnelIps(result);
        Write(Config, result);
        return result;
    }

    public static void Save(Settings s) {
        Validate(s, false);
        Write(Config, s);
    }

    static void AssignTunnelIps(Settings s) {
        var b = new byte[2];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        int third = 16 + b[0] % 224, fourth = 4 * (b[1] % 62) + 1;
        s.HostIp = "10.203." + third + "." + fourth;
        s.FriendIp = "10.203." + third + "." + (fourth + 1);
    }

    public static void Nick(string value) {
        if (!Regex.IsMatch(value ?? "", @"\A[A-Za-z0-9_]{3,16}\z"))
            throw new UserFacingException("Nickname: 3–16 Latin letters, digits or _. Enter it exactly as in your launcher.");
    }

    public static bool IsKey(string value) {
        try {
            if (value == null || value.Length != 44) return false;
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 && bytes.Any(x => x != 0);
        } catch { return false; }
    }

    public static void Key(string value) {
        if (!IsKey(value)) throw new UserFacingException("Invalid encryption key.");
    }

    public static IPAddress Ip(string value) {
        IPAddress ip;
        if (!IPAddress.TryParse(value ?? "", out ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new UserFacingException("An IPv4 address is required.");
        return ip;
    }

    public static bool PublicIp(string value) {
        byte[] b = Ip(value).GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
            || (b[0] == 192 && b[1] == 0) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    public static void Validate(Settings s, bool paired) {
        if (s == null || s.Schema != 2 || !Regex.IsMatch(s.Id ?? "", @"\A[a-f0-9]{12}\z") || (s.Role != "" && s.Role != "host" && s.Role != "client"))
            throw new UserFacingException("The MinePair profile is damaged.");
        if (s.Port < 1024 || s.Port > 65535 || s.MemoryMb < 2048 || s.MemoryMb > 8192) throw new UserFacingException("Invalid port or memory size.");
        byte[] a = Ip(s.HostIp).GetAddressBytes(), b = Ip(s.FriendIp).GetAddressBytes();
        if (a[0] != 10 || a[1] != 203 || a[2] < 16 || a[2] > 239 || a[3] % 4 != 1 || b[0] != a[0] || b[1] != a[1] || b[2] != a[2] || b[3] != a[3] + 1)
            throw new UserFacingException("Invalid tunnel addressing.");
        if (!paired) return;
        if (s.Role == "") throw new UserFacingException("No mode selected.");
        Nick(s.HostNick);
        Nick(s.FriendNick);
        if (s.HostNick.Equals(s.FriendNick, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("The nicknames must be different.");
        Key(s.PublicKey);
        Key(s.PeerKey);
        if (s.PublicKey == s.PeerKey) throw new UserFacingException("Both sides cannot use the same key.");
        if (!PublicIp(s.Endpoint)) throw new UserFacingException("The host's public IPv4 is required.");
    }

    public static bool Elevated { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }

    public static string Run(string file, string args, string input, int timeout) {
        var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        using (var p = Process.Start(info)) {
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            // Raw ASCII bytes: the StreamWriter may add a UTF-8 BOM on close, which wg.exe rejects as stray characters.
            var stdin = p.StandardInput.BaseStream;
            if (input != null) { var bytes = Encoding.ASCII.GetBytes(input); stdin.Write(bytes, 0, bytes.Length); }
            stdin.Close();
            if (!p.WaitForExit(timeout)) throw new Exception("A component did not respond in time: " + Path.GetFileName(file));
            if (p.ExitCode != 0) {
                string detail = errors.Result.Trim();
                if (detail == "") detail = output.Result.Trim();
                throw new Exception("Error in " + Path.GetFileName(file) + ": " + detail);
            }
            return output.Result.Trim();
        }
    }

    public static string Quote(string s) {
        if (s.IndexOf('"') >= 0 || s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0) throw new Exception("Invalid path.");
        return "\"" + s + "\"";
    }

    // ---------- keys ----------

    public static void EnsureKey(Settings s) {
        if (File.Exists(PrivateKeyPath)) {
            if (IsKey(s.PublicKey)) return;
            // Generation was interrupted after the private key was stored: derive the public half again.
            s.PublicKey = PublicKeyFor(PrivateKey());
            Save(s);
            return;
        }
        string key = NewPrivateKey();
        string pub = PublicKeyFor(key);
        Key(pub);
        Atomic(PrivateKeyPath, ProtectedData.Protect(Encoding.ASCII.GetBytes(key), null, DataProtectionScope.CurrentUser));
        s.PublicKey = pub;
        Save(s);
    }

    // WireGuard keys are X25519 (RFC 7748). Computed here instead of piping secrets through wg.exe's stdin.
    static readonly BigInteger P25519 = BigInteger.Pow(2, 255) - 19;

    static BigInteger Mod(BigInteger x) { var r = x % P25519; return r.Sign < 0 ? r + P25519 : r; }

    public static byte[] X25519(byte[] scalar, byte[] u) {
        var k = (byte[])scalar.Clone();
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        var ub = (byte[])u.Clone();
        ub[31] &= 127;
        BigInteger x1 = new BigInteger(ub.Concat(new byte[] { 0 }).ToArray()), x2 = 1, z2 = 0, x3 = x1, z3 = 1, tmp;
        int swap = 0;
        for (int t = 254; t >= 0; t--) {
            int bit = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= bit;
            if (swap == 1) { tmp = x2; x2 = x3; x3 = tmp; tmp = z2; z2 = z3; z3 = tmp; }
            swap = bit;
            BigInteger a = x2 + z2, aa = Mod(a * a), b = x2 - z2, bb = Mod(b * b), e = aa - bb, c = x3 + z3, d = x3 - z3, da = Mod(d * a), cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + 121665 * e));
        }
        if (swap == 1) { x2 = x3; z2 = z3; }
        var result = Mod(x2 * BigInteger.ModPow(z2, P25519 - 2, P25519)).ToByteArray();
        var output = new byte[32];
        Buffer.BlockCopy(result, 0, output, 0, Math.Min(32, result.Length));
        return output;
    }

    public static string NewPrivateKey() {
        var k = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        return Convert.ToBase64String(k);
    }

    public static string PublicKeyFor(string privateKey) {
        var basePoint = new byte[32];
        basePoint[0] = 9;
        string pub = Convert.ToBase64String(X25519(Convert.FromBase64String(privateKey), basePoint));
        Key(pub);
        return pub;
    }

    public static bool HasKey(Settings s) { return File.Exists(PrivateKeyPath) && IsKey(s.PublicKey); }

    public static string PrivateKey() {
        return Encoding.ASCII.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PrivateKeyPath), null, DataProtectionScope.CurrentUser));
    }

    public static string TunnelText(Settings s, string privateKey) {
        Validate(s, true);
        Key(privateKey);
        bool host = s.IsHost();
        string text = "[Interface]\nPrivateKey = " + privateKey + "\nAddress = " + (host ? s.HostIp : s.FriendIp) + "/32\n";
        if (host) text += "ListenPort = " + s.Port + "\n";
        text += "\n[Peer]\nPublicKey = " + s.PeerKey + "\nAllowedIPs = " + (host ? s.FriendIp : s.HostIp) + "/32\n";
        if (!host) text += "Endpoint = " + s.Endpoint + ":" + s.Port + "\nPersistentKeepalive = 25\n";
        return text;
    }

    public static void TunnelFile(Settings s) {
        PrivateDirectory(ProtectedDir);
        Atomic(Path.Combine(ProtectedDir, s.TunnelName() + ".conf.dpapi"), Native.Protect(Encoding.UTF8.GetBytes(TunnelText(s, PrivateKey())), s.TunnelName()));
    }

    public static bool TunnelServiceExists(Settings s) {
        try {
            using (var sc = new ServiceController("WireGuardTunnel$" + s.TunnelName())) {
                var status = sc.Status;
                return status != ServiceControllerStatus.Stopped;
            }
        } catch { return false; }
    }

    // ---------- pairing codes ----------
    // A code is "MP-" + base64url(kind | body | first 4 bytes of SHA-256(kind | body)).
    // Codes carry only public data: the checksum catches copy/paste damage, the pinned keys provide security.

    const string CodePrefix = "MP-";
    const byte InviteKind = 1, ReplyKind = 2, UpdateKind = 3;

    sealed class CodeReader {
        readonly byte[] data;
        int pos;
        public CodeReader(byte[] data) { this.data = data; }
        public byte[] Take(int n) {
            if (pos + n > data.Length) throw Broken();
            var r = new byte[n];
            Buffer.BlockCopy(data, pos, r, 0, n);
            pos += n;
            return r;
        }
        public byte Byte() { return Take(1)[0]; }
        public string Text() { int n = Byte(); return Encoding.ASCII.GetString(Take(n)); }
        public void End() { if (pos != data.Length) throw Broken(); }
    }

    static Exception Broken() { return new UserFacingException("The code is damaged. Copy all of it again."); }

    static string EncodeCode(byte kind, List<byte> body) {
        var payload = new List<byte> { kind };
        payload.AddRange(body);
        byte[] sum;
        using (var sha = SHA256.Create()) sum = sha.ComputeHash(payload.ToArray());
        payload.AddRange(sum.Take(4));
        return CodePrefix + Convert.ToBase64String(payload.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static CodeReader DecodeCode(string code, out byte kind) {
        string text = Regex.Replace(code ?? "", @"\s", "");
        if (!text.StartsWith(CodePrefix, StringComparison.Ordinal)) throw new UserFacingException("This is not a MinePair code. Copy all of it; it starts with MP-.");
        text = text.Substring(CodePrefix.Length).Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        byte[] all;
        try { all = Convert.FromBase64String(text); } catch { throw Broken(); }
        if (all.Length < 6 || all.Length > 512) throw Broken();
        byte[] payload = all.Take(all.Length - 4).ToArray();
        byte[] sum;
        using (var sha = SHA256.Create()) sum = sha.ComputeHash(payload);
        if (!sum.Take(4).SequenceEqual(all.Skip(all.Length - 4))) throw Broken();
        kind = payload[0];
        return new CodeReader(payload.Skip(1).ToArray());
    }

    static void AddText(List<byte> b, string text) {
        var bytes = Encoding.ASCII.GetBytes(text);
        b.Add((byte)bytes.Length);
        b.AddRange(bytes);
    }

    static void AddEndpoint(List<byte> b, Settings s) {
        b.AddRange(Ip(s.Endpoint).GetAddressBytes());
        b.Add((byte)(s.Port >> 8));
        b.Add((byte)s.Port);
    }

    public static string InviteCode(Settings s) {
        if (!s.IsHost()) throw new UserFacingException("The invitation is created by whoever hosts the server.");
        Validate(s, false);
        Nick(s.HostNick);
        Nick(s.FriendNick);
        Key(s.PublicKey);
        if (!PublicIp(s.Endpoint)) throw new UserFacingException("Could not detect the public IP. Check your internet connection.");
        var b = new List<byte>(FromHex(s.Id));
        var ip = Ip(s.HostIp).GetAddressBytes();
        b.Add(ip[2]);
        b.Add(ip[3]);
        b.AddRange(Convert.FromBase64String(s.PublicKey));
        AddEndpoint(b, s);
        AddText(b, s.HostNick);
        AddText(b, s.FriendNick);
        return EncodeCode(InviteKind, b);
    }

    public static string ReplyCode(Settings s) {
        if (s.IsHost()) throw new UserFacingException("The reply code is created by the friend.");
        Validate(s, true);
        var b = new List<byte>(FromHex(s.Id));
        b.AddRange(Convert.FromBase64String(s.PublicKey));
        return EncodeCode(ReplyKind, b);
    }

    public static string UpdateCode(Settings s) {
        if (!s.IsHost()) throw new UserFacingException("The new-address code is created by the host.");
        Validate(s, true);
        var b = new List<byte>(FromHex(s.Id));
        AddEndpoint(b, s);
        return EncodeCode(UpdateKind, b);
    }

    // Same value on both computers once both public keys are known; read aloud to rule out a swapped code.
    public static string SafetyCode(string hostKey, string friendKey) {
        byte[] data = Encoding.ASCII.GetBytes("MinePair-v1").Concat(Convert.FromBase64String(hostKey)).Concat(Convert.FromBase64String(friendKey)).ToArray();
        byte[] h;
        using (var sha = SHA256.Create()) h = sha.ComputeHash(data);
        string hex = BitConverter.ToString(h, 0, 4).Replace("-", "");
        return hex.Substring(0, 4) + "-" + hex.Substring(4);
    }

    public static string SafetyCode(Settings s) {
        return s.IsHost() ? SafetyCode(s.PublicKey, s.PeerKey) : SafetyCode(s.PeerKey, s.PublicKey);
    }

    // Applies any code to the profile. Returns "invite", "reply" or "update".
    // confirm receives the safety code before a new friend key is accepted; returning false cancels.
    public static string ApplyCode(Settings s, string code, Func<string, bool> confirm) {
        byte kind;
        var r = DecodeCode(code, out kind);
        if (kind == InviteKind) {
            if (s.IsHost()) throw new UserFacingException("This is your own invitation. Send it to your friend and paste their reply code here.");
            string id = Hex(r.Take(6));
            byte third = r.Byte(), fourth = r.Byte();
            string hostKey = Convert.ToBase64String(r.Take(32));
            string endpoint = new IPAddress(r.Take(4)).ToString();
            var port = r.Take(2);
            string hostNick = r.Text(), friendNick = r.Text();
            r.End();
            if (s.Paired() && (s.Id != id || s.PeerKey != hostKey))
                throw new UserFacingException("You are already paired with another host. To accept a new invitation: Settings → Friend → Unpair.");
            var c = Clone(s);
            c.Id = id;
            c.Role = "client";
            c.HostIp = "10.203." + third + "." + fourth;
            c.FriendIp = "10.203." + third + "." + (fourth + 1);
            c.PeerKey = hostKey;
            c.Endpoint = endpoint;
            c.Port = port[0] << 8 | port[1];
            c.HostNick = hostNick;
            c.FriendNick = friendNick;
            Validate(c, true);
            if (confirm != null && !confirm(SafetyCode(c))) throw new UserFacingException("Pairing cancelled.");
            Save(c);
            CopyInto(c, s);
            return "invite";
        }
        if (kind == ReplyKind) {
            if (!s.IsHost()) throw new UserFacingException("This is a reply code. It is pasted by whoever hosts the server.");
            string id = Hex(r.Take(6));
            string friendKey = Convert.ToBase64String(r.Take(32));
            r.End();
            if (id != s.Id) throw new UserFacingException("This reply belongs to a different invitation.");
            Key(friendKey);
            if (friendKey == s.PublicKey) throw new UserFacingException("This is your own code.");
            if (s.Paired() && s.PeerKey != friendKey)
                throw new UserFacingException("A friend is already added. To replace their computer: Settings → Friend → Replace friend's device.");
            var c = Clone(s);
            c.PeerKey = friendKey;
            Validate(c, true);
            if (confirm != null && !confirm(SafetyCode(c))) throw new UserFacingException("Pairing cancelled. The friend was not added.");
            Save(c);
            CopyInto(c, s);
            return "reply";
        }
        if (kind == UpdateKind) {
            if (s.IsHost()) throw new UserFacingException("This is a new-address code. Your friend pastes it.");
            string id = Hex(r.Take(6));
            string endpoint = new IPAddress(r.Take(4)).ToString();
            var port = r.Take(2);
            r.End();
            if (!s.Paired() || id != s.Id) throw new UserFacingException("This code is from a different host. Paste an invitation first.");
            var c = Clone(s);
            c.Endpoint = endpoint;
            c.Port = port[0] << 8 | port[1];
            Validate(c, true);
            Save(c);
            CopyInto(c, s);
            return "update";
        }
        throw Broken();
    }

    static Settings Clone(Settings s) { return Parse<Settings>(Serialize(s)); }

    static void CopyInto(Settings from, Settings to) {
        foreach (var f in typeof(Settings).GetFields()) f.SetValue(to, f.GetValue(from));
    }

    // ---------- network discovery ----------

    public static string GetText(string url) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using (var c = new WebClient()) {
            c.Encoding = Encoding.UTF8;
            c.Headers["User-Agent"] = "MinePair/2.0";
            return c.DownloadString(url);
        }
    }

    public static string DetectPublicIp() {
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com" }) {
            try {
                string ip = GetText(url).Trim();
                if (PublicIp(ip)) return ip;
            } catch { }
        }
        throw new UserFacingException("Could not detect the public IP. Check your internet connection and try again.");
    }

    static readonly string[] VirtualAdapterWords = { "virtual", "vpn", "radmin", "wireguard", "hyper-v", "vmware", "virtualbox", "tap-", "zerotier", "hamachi", "tailscale", "loopback", "bluetooth" };

    // Local IPv4 of the physical adapter that carries the internet route; virtual VPN adapters are ignored.
    public static string LanIp() {
        string gateway;
        return LanIp(out gateway);
    }

    public static string LanIp(out string gateway) {
        gateway = null;
        var kinds = new[] { NetworkInterfaceType.Ethernet, NetworkInterfaceType.Wireless80211, NetworkInterfaceType.GigabitEthernet, NetworkInterfaceType.FastEthernetT };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)) {
            if (ni.OperationalStatus != OperationalStatus.Up || !kinds.Contains(ni.NetworkInterfaceType)) continue;
            string label = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (VirtualAdapterWords.Any(w => label.Contains(w))) continue;
            var props = ni.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw == null) continue;
            var addr = props.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !PublicIp(a.ToString()) && !a.ToString().StartsWith("169.254."));
            if (addr == null) continue;
            gateway = gw.ToString();
            return addr.ToString();
        }
        return null;
    }

    // ---------- downloads ----------

    public static string Hash(string path, bool sha256) {
        using (var f = File.OpenRead(path))
        using (HashAlgorithm h = sha256 ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
            return Hex(h.ComputeHash(f));
    }

    public static string HashString(string value) {
        using (var h = SHA256.Create()) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).Substring(0, 24);
    }

    public static void Get(string url, string target, string hash, bool sha256) {
        Uri uri = new Uri(url);
        if (uri.Scheme != "https") throw new Exception("Downloads are allowed over HTTPS only.");
        if (string.IsNullOrEmpty(hash)) throw new Exception("No checksum for the download.");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string tmp = target + ".download";
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try {
            using (var c = new WebClient()) {
                c.Headers["User-Agent"] = "MinePair/2.0";
                c.DownloadFile(uri, tmp);
            }
            if (!Hash(tmp, sha256).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new Exception("Checksum mismatch. The file will not be used.");
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        } finally {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static Dictionary<string, object> Obj(object o) { return (Dictionary<string, object>)o; }
    public static object[] Arr(object o) { return o as object[] ?? ((System.Collections.ArrayList)o).ToArray(); }

    static Dictionary<string, object> Manifest() {
        return Parse<Dictionary<string, object>>(GetText("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
    }

    // Official release versions, newest first.
    public static List<string> ReleaseVersions() {
        return Arr(Manifest()["versions"]).Select(Obj).Where(v => (string)v["type"] == "release").Select(v => (string)v["id"]).Take(40).ToList();
    }

    public static string VersionDir(string version) {
        if (!Regex.IsMatch(version ?? "", @"\A[0-9]+(\.[0-9]+){1,3}\z")) throw new UserFacingException("Invalid Minecraft version.");
        return Path.Combine(VersionsDir, version);
    }

    public static string ServerJar(World w) { return Path.Combine(VersionDir(w.Version), "server.jar"); }

    // Downloads (if needed) and verifies everything the world needs: server.jar for its version and the matching Java.
    public static void PrepareWorld(World w, Action<string> progress) {
        string dir = VersionDir(w.Version);
        string meta = Path.Combine(dir, "version.json");
        if (!File.Exists(meta)) {
            progress("Looking up Minecraft " + w.Version + " at Mojang…");
            var version = Arr(Manifest()["versions"]).Select(Obj).FirstOrDefault(v => (string)v["id"] == w.Version);
            if (version == null) throw new UserFacingException("Version " + w.Version + " was not found at Mojang.");
            Get((string)version["url"], meta, (string)version["sha1"], false);
        }
        var m = Read<Dictionary<string, object>>(meta);
        int major = m.ContainsKey("javaVersion") ? Convert.ToInt32(Obj(m["javaVersion"])["majorVersion"]) : 8;
        var jar = Obj(Obj(m["downloads"])["server"]);
        string sha = (string)jar["sha1"];
        string jarPath = Path.Combine(dir, "server.jar");
        if (!File.Exists(jarPath) || Hash(jarPath, false) != sha) {
            progress("Downloading Minecraft server " + w.Version + "…");
            Get((string)jar["url"], jarPath, sha, false);
        }
        EnsureJava(major, progress);
        if (w.ServerSha1 != sha || w.JavaMajor != major) {
            w.ServerSha1 = sha;
            w.JavaMajor = major;
            SaveWorld(w);
        }
    }

    public static string FindJava(int major) {
        string dir = Path.Combine(JavaDir, major.ToString());
        if (major < 8 || !Directory.Exists(dir)) return null;
        var found = Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories).Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin").ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    static void EnsureJava(int major, Action<string> progress) {
        if (FindJava(major) != null) return;
        progress("Downloading Java " + major + "…");
        object[] assets = Parse<object[]>(GetText("https://api.adoptium.net/v3/assets/latest/" + major + "/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse"));
        if (assets.Length == 0) assets = Parse<object[]>(GetText("https://api.adoptium.net/v3/assets/latest/" + major + "/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse"));
        if (assets.Length == 0) throw new UserFacingException("Java " + major + " was not found at Adoptium.");
        var package = Obj(Obj(Obj(assets[0])["binary"])["package"]);
        string zip = Path.Combine(DownloadsDir, "java-" + major + ".zip");
        Get((string)package["link"], zip, (string)package["checksum"], true);
        string target = Path.Combine(JavaDir, major.ToString()), staging = target + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        progress("Unpacking Java " + major + "…");
        try { Extract(zip, staging); } catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
        Directory.Move(staging, target);
        File.Delete(zip);
        string java = FindJava(major);
        if (java == null) throw new UserFacingException("Could not find java.exe in the archive.");
        Run(java, "-version", null, 20000);
    }

    public static void Extract(string archive, string target) {
        string root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        using (var zip = ZipFile.OpenRead(archive)) {
            long total = 0;
            foreach (var entry in zip.Entries) {
                string path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.IndexOf(':') >= 0 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new UserFacingException("The archive contains an unsafe path.");
                total += entry.Length;
                if (total > 4L * 1024 * 1024 * 1024) throw new UserFacingException("The archive is too large.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                entry.ExtractToFile(path, false);
            }
        }
    }

    // ---------- worlds ----------

    public static readonly string[] GameModes = { "survival", "creative", "adventure" };
    public static readonly string[] Difficulties = { "peaceful", "easy", "normal", "hard" };

    public static string WorldDir(string id) {
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{8}\z")) throw new UserFacingException("Invalid world.");
        return Path.Combine(WorldsDir, id);
    }

    public static void ValidateWorld(World w) {
        WorldDir(w.Id);
        VersionDir(w.Version);
        if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 32 || w.Name.Any(char.IsControl)) throw new UserFacingException("World name: 1–32 characters.");
        if (!GameModes.Contains(w.GameMode) || !Difficulties.Contains(w.Difficulty)) throw new UserFacingException("Invalid game mode or difficulty.");
        if (!Regex.IsMatch(w.Seed ?? "", @"\A[-\p{L}0-9_ ]{0,40}\z")) throw new UserFacingException("Seed: up to 40 letters, digits, spaces, - or _.");
    }

    public static void SaveWorld(World w) {
        ValidateWorld(w);
        Write(Path.Combine(WorldDir(w.Id), "minepair-world.json"), w);
    }

    public static List<World> Worlds() {
        var result = new List<World>();
        if (!Directory.Exists(WorldsDir)) return result;
        foreach (var dir in Directory.GetDirectories(WorldsDir)) {
            string file = Path.Combine(dir, "minepair-world.json");
            if (!File.Exists(file)) continue;
            try {
                var w = Read<World>(file);
                ValidateWorld(w);
                if (w.Id == Path.GetFileName(dir)) result.Add(w);
            } catch { }
        }
        return result.OrderBy(w => w.Created).ToList();
    }

    public static World GetWorld(string id) { return Worlds().FirstOrDefault(w => w.Id == id); }

    public static World CreateWorld(string name, string version, string gameMode, string difficulty, bool hardcore, bool pvp, string seed) {
        var w = new World {
            Id = NewId(8), Name = (name ?? "").Trim(), Version = version, GameMode = gameMode, Difficulty = difficulty,
            Hardcore = hardcore, Pvp = pvp, Seed = (seed ?? "").Trim(), Created = DateTime.UtcNow.ToString("o")
        };
        SaveWorld(w);
        return w;
    }

    public static World ActiveWorld(Settings s) {
        var w = GetWorld(s.ActiveWorld);
        if (w == null) throw new UserFacingException("Choose a world in Settings.");
        return w;
    }

    static string PropertyValue(string value) {
        var sb = new StringBuilder();
        foreach (char ch in value) {
            if (ch == '\\') sb.Append("\\\\");
            else if (ch < 0x20 || ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static void ServerProperties(Settings s, World w) {
        Validate(s, true);
        ValidateWorld(w);
        if (!s.Eula) throw new UserFacingException("The Minecraft EULA must be accepted.");
        string dir = WorldDir(w.Id);
        var p = new StringBuilder();
        Action<string, string> add = (k, v) => p.Append(k).Append('=').Append(v).Append('\n');
        add("server-ip", s.HostIp);
        add("server-port", "25565");
        add("online-mode", "false");
        add("enforce-secure-profile", "false");
        add("white-list", "true");
        add("enforce-whitelist", "true");
        add("max-players", "2");
        add("gamemode", w.GameMode);
        add("force-gamemode", "true");
        add("difficulty", w.Difficulty);
        add("hardcore", w.Hardcore ? "true" : "false");
        add("pvp", w.Pvp ? "true" : "false");
        add("level-name", "world");
        add("level-seed", PropertyValue(w.Seed));
        add("view-distance", "10");
        add("simulation-distance", "8");
        add("enable-rcon", "false");
        add("enable-query", "false");
        add("management-server-enabled", "false");
        add("management-server-host", "127.0.0.1");
        add("enable-jmx-monitoring", "false");
        add("accepts-transfers", "false");
        add("spawn-protection", "0");
        add("motd", PropertyValue("MinePair · " + w.Name));
        Atomic(Path.Combine(dir, "server.properties"), Encoding.ASCII.GetBytes(p.ToString()));
        Atomic(Path.Combine(dir, "eula.txt"), Encoding.ASCII.GetBytes("eula=true\n"));
        var list = new[] { s.HostNick, s.FriendNick }.Select(n => new Dictionary<string, string> { { "uuid", OfflineUuid(n) }, { "name", n } }).ToArray();
        Write(Path.Combine(dir, "whitelist.json"), list);
        Write(Path.Combine(dir, "ops.json"), new object[0]);
    }

    public static string OfflineUuid(string name) {
        byte[] h;
        using (var md5 = MD5.Create()) h = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        h[6] = (byte)((h[6] & 15) | 48);
        h[8] = (byte)((h[8] & 63) | 128);
        string hex = Hex(h);
        return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20);
    }

    // ---------- backups ----------

    public const int AutoBackupsKept = 10;

    public static string WorldBackups(World w) { return Path.Combine(BackupsDir, w.Id); }

    public static List<FileInfo> Backups(World w) {
        string dir = WorldBackups(w);
        if (!Directory.Exists(dir)) return new List<FileInfo>();
        return new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.Name).ToList();
    }

    // reason: "auto" (rotated), "manual", "before-restore", "after-crash".
    public static string Backup(World w, string reason) {
        if (ServerRunning()) throw new UserFacingException("Stop the server first: a backup of a live world may be incomplete.");
        string world = Path.Combine(WorldDir(w.Id), "world");
        if (!Directory.Exists(world)) return null;
        if ((File.GetAttributes(world) & FileAttributes.ReparsePoint) != 0 || Directory.GetFileSystemEntries(world, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new UserFacingException("The world contains a link to another directory.");
        string dir = WorldBackups(w);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + reason + ".zip");
        ZipFile.CreateFromDirectory(world, target + ".partial", CompressionLevel.Fastest, false);
        File.Move(target + ".partial", target);
        if (reason == "auto") {
            foreach (var old in Backups(w).Where(f => f.Name.EndsWith("_auto.zip")).Skip(AutoBackupsKept)) old.Delete();
        }
        return target;
    }

    public static void Restore(World w, string archive) {
        if (ServerRunning()) throw new UserFacingException("Stop the server first.");
        string stage = Path.Combine(Data, "restore-" + Guid.NewGuid().ToString("N"));
        try {
            Extract(archive, stage);
            if (!File.Exists(Path.Combine(stage, "level.dat"))) throw new UserFacingException("This is not a world backup.");
            Backup(w, "before-restore");
            string world = Path.Combine(WorldDir(w.Id), "world"), old = Path.Combine(Data, "previous-world-" + Guid.NewGuid().ToString("N"));
            bool moved = false;
            if (Directory.Exists(world)) { Directory.Move(world, old); moved = true; }
            try { Directory.Move(stage, world); } catch { if (moved) Directory.Move(old, world); throw; }
            if (moved) Directory.Delete(old, true);
        } finally {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    // ---------- server process ----------

    public static bool SupervisorAlive() {
        if (!File.Exists(SessionFile)) return false;
        try {
            var d = Read<Dictionary<string, object>>(SessionFile);
            using (var p = Process.GetProcessById(Convert.ToInt32(d["pid"])))
                return p.StartTime.ToUniversalTime().Ticks == Convert.ToInt64(d["started"])
                    && Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    // Java processes started from MinePair's own Java folders, with or without a supervisor.
    public static List<Process> ServerJavaProcesses() {
        string root = Path.GetFullPath(JavaDir).TrimEnd('\\') + "\\";
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("java")) {
            try {
                if (Path.GetFullPath(p.MainModule.FileName).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { result.Add(p); continue; }
            } catch { }
            p.Dispose();
        }
        return result;
    }

    public static bool ServerRunning() {
        if (SupervisorAlive()) return true;
        var orphans = ServerJavaProcesses();
        bool any = orphans.Count > 0;
        foreach (var p in orphans) p.Dispose();
        return any;
    }

    public static bool OrphanServer() { return !SupervisorAlive() && ServerRunning(); }

    public static void KillOrphanServer() {
        foreach (var p in ServerJavaProcesses()) using (p) { try { p.Kill(); p.WaitForExit(15000); } catch { } }
        if (File.Exists(SessionFile)) File.Delete(SessionFile);
    }

    public static bool UncleanSession() { return File.Exists(UncleanMarker) && !SupervisorAlive(); }

    public static void ClearUncleanSession(Settings s) {
        var w = GetWorld(s.ActiveWorld);
        if (w != null) Backup(w, "after-crash");
        if (File.Exists(UncleanMarker)) File.Delete(UncleanMarker);
    }

    public static void StartServer(Settings s, int owner) {
        if (Elevated) throw new UserFacingException("Start MinePair with a normal double-click, without administrator rights.");
        Validate(s, true);
        var w = ActiveWorld(s);
        if (SupervisorAlive()) throw new UserFacingException("The server is already running.");
        if (ServerRunning()) throw new UserFacingException("Found a Minecraft server that MinePair is not managing. Restart the program.");
        if (File.Exists(UncleanMarker)) throw new UserFacingException("The last session ended unexpectedly. Restart the program.");
        string java = FindJava(w.JavaMajor);
        if (java == null || !File.Exists(ServerJar(w)) || Hash(ServerJar(w), false) != w.ServerSha1) throw new UserFacingException("Server files are damaged. Press \"Play\" again to download them again.");
        s.Java = java;
        Save(s);
        Backup(w, "auto");
        ServerProperties(s, w);
        TunnelFile(s);
        Helper.Call("start", 180000);
        try {
            if (File.Exists(StopRequest)) File.Delete(StopRequest);
            Process.Start(new ProcessStartInfo(ExePath, "--server-worker " + owner + " " + w.Id) { UseShellExecute = false, CreateNoWindow = true });
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000) {
                if (SupervisorAlive()) return;
                Thread.Sleep(100);
            }
            throw new UserFacingException("The server did not start. See the server log (Settings → Server log).");
        } catch {
            try { Helper.Call("stop", 120000); } catch { }
            throw;
        }
    }

    public static void StopServer() {
        if (SupervisorAlive()) {
            Atomic(StopRequest, new byte[] { 1 });
            var sw = Stopwatch.StartNew();
            while (SupervisorAlive() && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(300);
            if (SupervisorAlive()) throw new UserFacingException("The server is still saving the world. Wait a minute and press \"Stop\" again.");
        }
        Helper.Call("stop", 120000);
    }

    public static void RequestStop() {
        if (SupervisorAlive()) Atomic(StopRequest, new byte[] { 1 });
    }

    public static void Supervisor(int owner, string worldId) {
        Settings s = Load();
        if (Elevated) throw new Exception("The server must not run with administrator rights.");
        var w = GetWorld(worldId);
        if (w == null) throw new Exception("World not found.");
        string java = FindJava(w.JavaMajor);
        if (java == null) throw new Exception("Java not found.");
        Process parent;
        try { parent = Process.GetProcessById(owner); } catch { parent = null; }
        var own = Process.GetCurrentProcess();
        using (var sessionLock = new FileStream(Path.Combine(Data, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            Atomic(UncleanMarker, Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")));
            Write(SessionFile, new { pid = own.Id, started = own.StartTime.ToUniversalTime().Ticks });
            Directory.CreateDirectory(LogsDir);
            string logPath = Path.Combine(LogsDir, "server-console.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 10 * 1024 * 1024) File.Copy(logPath, logPath + ".old", true);
            using (var log = new StreamWriter(logPath, new FileInfo(logPath).Exists && new FileInfo(logPath).Length <= 10 * 1024 * 1024, Encoding.UTF8)) {
                log.AutoFlush = true;
                object gate = new object();
                log.WriteLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + w.Name + " · " + w.Version + " ===");
                var info = new ProcessStartInfo(java, "-Xms1G -Xmx" + s.MemoryMb + "M -jar " + Quote(ServerJar(w)) + " nogui") {
                    WorkingDirectory = WorldDir(w.Id), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = new Process { StartInfo = info }) {
                    p.OutputDataReceived += (o, e) => { if (e.Data != null) lock (gate) log.WriteLine(e.Data); };
                    p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (gate) log.WriteLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    bool stopSent = false;
                    while (!p.WaitForExit(500)) {
                        bool ownerGone;
                        try { ownerGone = parent == null || parent.HasExited; } catch { ownerGone = true; }
                        if (!stopSent && (ownerGone || File.Exists(StopRequest))) {
                            p.StandardInput.WriteLine("stop");
                            p.StandardInput.Flush();
                            stopSent = true;
                        }
                    }
                    p.WaitForExit();
                    if (p.ExitCode == 0 && stopSent) File.Delete(UncleanMarker);
                }
            }
            File.Delete(SessionFile);
            if (File.Exists(StopRequest)) File.Delete(StopRequest);
        }
        if (parent != null) parent.Dispose();
    }

    // ---------- diagnostics ----------

    static byte[] VarInt(int value) {
        uint v = unchecked((uint)value);
        var bytes = new List<byte>();
        do { byte b = (byte)(v & 127); v >>= 7; if (v != 0) b |= 128; bytes.Add(b); } while (v != 0);
        return bytes.ToArray();
    }

    static int ReadVarInt(Stream stream) {
        int v = 0;
        for (int i = 0; i < 5; i++) {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            v |= (b & 127) << (7 * i);
            if ((b & 128) == 0) return v;
        }
        throw new Exception("Invalid Minecraft response.");
    }

    static byte[] ReadPacket(Stream stream) {
        int length = ReadVarInt(stream);
        if (length < 1 || length > 1024 * 1024) throw new Exception("Invalid response size.");
        var data = new byte[length];
        int got = 0;
        while (got < length) {
            int n = stream.Read(data, got, length - got);
            if (n == 0) throw new EndOfStreamException();
            got += n;
        }
        return data;
    }

    static void Packet(Stream stream, byte[] data) {
        byte[] length = VarInt(data.Length);
        stream.Write(length, 0, length.Length);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    public sealed class ServerPing {
        public string Version;
        public int Online, Max;
        public long Milliseconds;
    }

    // Real Minecraft status + ping/pong over the game port.
    public static ServerPing MinecraftPing(string address, int connectTimeout) {
        using (var c = new TcpClient()) {
            var pending = c.ConnectAsync(Ip(address), 25565);
            if (!pending.Wait(connectTimeout) || !c.Connected) throw new Exception("The Minecraft server does not respond.");
            c.ReceiveTimeout = 3000;
            c.SendTimeout = 3000;
            c.NoDelay = true;
            using (var stream = c.GetStream()) {
                var handshake = new List<byte> { 0 };
                handshake.AddRange(VarInt(-1));
                byte[] host = Encoding.UTF8.GetBytes(address);
                handshake.AddRange(VarInt(host.Length));
                handshake.AddRange(host);
                handshake.Add(99); handshake.Add(221);
                handshake.Add(1);
                Packet(stream, handshake.ToArray());
                Packet(stream, new byte[] { 0 });
                byte[] status = ReadPacket(stream);
                Dictionary<string, object> metadata;
                using (var input = new MemoryStream(status)) {
                    if (ReadVarInt(input) != 0) throw new Exception("Expected a Minecraft status response.");
                    int len = ReadVarInt(input);
                    if (len < 1 || len > status.Length - input.Position) throw new Exception("Invalid status.");
                    byte[] json = new byte[len];
                    input.Read(json, 0, len);
                    metadata = Parse<Dictionary<string, object>>(Encoding.UTF8.GetString(json));
                }
                byte[] ping = new byte[9];
                ping[0] = 1;
                byte[] nonce = new byte[8];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                Buffer.BlockCopy(nonce, 0, ping, 1, 8);
                var watch = Stopwatch.StartNew();
                Packet(stream, ping);
                byte[] pong = ReadPacket(stream);
                watch.Stop();
                if (!pong.SequenceEqual(ping)) throw new Exception("The game ping reply did not match.");
                var version = Obj(metadata["version"]);
                var players = Obj(metadata["players"]);
                return new ServerPing { Version = Convert.ToString(version["name"]), Online = Convert.ToInt32(players["online"]), Max = Convert.ToInt32(players["max"]), Milliseconds = watch.ElapsedMilliseconds };
            }
        }
    }

    // ---------- migration from the first version (.local next to the exe) ----------

    public static bool MigrateLegacy(string legacy) {
        if (!File.Exists(Path.Combine(legacy, "settings.json")) || File.Exists(Config)) return false;
        if (Directory.Exists(Data)) {
            if (Directory.EnumerateFileSystemEntries(Data).Any()) return false;
            Directory.Delete(Data);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Data));
        Directory.Move(legacy, Data);
        PrivateDirectory(Data);
        foreach (var stale in new[] { StopRequest, Path.Combine(Data, "server.lock"), SessionFile, Path.Combine(Data, "admin-result.json") })
            if (File.Exists(stale)) File.Delete(stale);

        var old = Read<Dictionary<string, object>>(Config);
        Func<string, string> str = k => old.ContainsKey(k) && old[k] != null ? Convert.ToString(old[k]) : "";
        var s = new Settings {
            Id = str("Id"), Role = str("Role"), HostNick = str("HostNick"), FriendNick = str("FriendNick"), Endpoint = str("Endpoint"),
            Port = old.ContainsKey("Port") ? Convert.ToInt32(old["Port"]) : 51820,
            MemoryMb = old.ContainsKey("MemoryMb") ? Convert.ToInt32(old["MemoryMb"]) : 3072,
            HostIp = str("HostIp"), FriendIp = str("FriendIp"), PublicKey = str("PublicKey"), PeerKey = str("PeerKey"),
            Eula = old.ContainsKey("Eula") && Convert.ToBoolean(old["Eula"])
        };
        s.ReplySent = s.Role == "client" && s.Paired();

        string manual = Path.Combine(Data, "router-manual.json");
        if (File.Exists(manual)) {
            var r = Read<Dictionary<string, object>>(manual);
            s.RouterReady = true;
            if (r.ContainsKey("internalIp")) s.LanIp = Convert.ToString(r["internalIp"]);
        }

        // Java: java\<folder> → java\<major>\<folder>
        if (Directory.Exists(JavaDir)) {
            foreach (var dir in Directory.GetDirectories(JavaDir)) {
                if (Regex.IsMatch(Path.GetFileName(dir), @"\A[0-9]+\z")) continue;
                string release = Path.Combine(dir, "release");
                if (!File.Exists(release)) continue;
                var m = Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"([0-9]+)");
                if (!m.Success) continue;
                string target = Path.Combine(JavaDir, m.Groups[1].Value);
                Directory.CreateDirectory(target);
                Directory.Move(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        // Server folder → worlds\<id>, server.jar → versions\<version>
        string server = Path.Combine(Data, "server");
        if (Directory.Exists(server)) {
            string version = str("Version");
            if (version == "") version = "26.3";
            var w = new World { Id = NewId(8), Name = "My world", Version = version, ServerSha1 = str("ServerSha1"), Created = DateTime.UtcNow.ToString("o") };
            string vdir = VersionDir(version);
            Directory.CreateDirectory(vdir);
            string jar = Path.Combine(server, "server.jar");
            if (File.Exists(jar)) File.Move(jar, Path.Combine(vdir, "server.jar"));
            string meta = Path.Combine(DownloadsDir, "version.json");
            if (File.Exists(meta)) {
                File.Move(meta, Path.Combine(vdir, "version.json"));
                var m = Read<Dictionary<string, object>>(Path.Combine(vdir, "version.json"));
                if (m.ContainsKey("javaVersion")) w.JavaMajor = Convert.ToInt32(Obj(m["javaVersion"])["majorVersion"]);
            }
            Directory.CreateDirectory(WorldsDir);
            Directory.Move(server, WorldDir(w.Id));
            SaveWorld(w);
            s.ActiveWorld = w.Id;
            if (Directory.Exists(BackupsDir)) {
                var target = WorldBackups(w);
                Directory.CreateDirectory(target);
                foreach (var zip in Directory.GetFiles(BackupsDir, "*.zip")) {
                    string name = Path.GetFileName(zip).Replace("_before-start.zip", "_auto.zip");
                    File.Move(zip, Path.Combine(target, name));
                }
            }
        }
        foreach (var leftover in new[] { Path.Combine(DownloadsDir, "java.zip"), Path.Combine(DownloadsDir, "wireguard.msi") })
            if (File.Exists(leftover)) File.Delete(leftover);
        Save(s);
        return true;
    }
}

internal static class Native {
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    // LocalMachine scope (flag 4) so the SYSTEM WireGuard service can read it; the description must equal the tunnel name.
    public static byte[] Protect(byte[] data, string name) {
        Blob b = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) }, output;
        try {
            Marshal.Copy(data, 0, b.Data, data.Length);
            if (!CryptProtectData(ref b, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out output)) throw new System.ComponentModel.Win32Exception();
            try {
                var a = new byte[output.Length];
                Marshal.Copy(output.Data, a, 0, a.Length);
                return a;
            } finally { LocalFree(output.Data); }
        } finally { Marshal.FreeHGlobal(b.Data); }
    }
}
}
