using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace MinePair {
public static class SelfTests {
    static List<string> output = new List<string>();
    static void Check(bool yes, string label) { if (!yes) throw new Exception("FAIL: " + label); output.Add("PASS: " + label); }
    static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, label); }
    static void CreateHandles(Control c) { var handle = c.Handle; foreach (Control child in c.Controls) CreateHandles(child); c.PerformLayout(); }
    static string Key(int start) { return Convert.ToBase64String(Enumerable.Range(start, 32).Select(x => (byte)x).ToArray()); }

    // Builds a host and a client profile in separate folders; returns to the host folder.
    static Settings Profile(string dir, string role, string publicKey) {
        Core.Data = dir;
        var s = Core.Load();
        s.Role = role;
        s.PublicKey = publicKey;
        if (role == "host") { s.HostNick = "Host_One"; s.FriendNick = "Friend_Two"; s.Endpoint = "8.8.4.4"; s.Eula = true; }
        Core.Save(s);
        return s;
    }

    public static int Run() {
        string original = Core.Data;
        string artifacts = Path.Combine(Path.GetDirectoryName(Core.ExePath), "artifacts");
        Directory.CreateDirectory(artifacts);
        string temp = Path.Combine(artifacts, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try {
            string hostDir = Path.Combine(temp, "host"), clientDir = Path.Combine(temp, "client");
            var host = Profile(hostDir, "host", Key(1));
            var client = Profile(clientDir, "client", Key(33));
            Check(Core.IsPrivateDirectory(hostDir) && Core.IsPrivateDirectory(clientDir), "data folder restricted to the user, SYSTEM and Administrators");
            File.WriteAllText(Path.Combine(hostDir, "probe.txt"), "x");
            var probeAcl = File.GetAccessControl(Path.Combine(hostDir, "probe.txt")).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            Core.Load();
            Check(Core.IsPrivateDirectory(hostDir) && File.GetAccessControl(Path.Combine(hostDir, "probe.txt")).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access) == probeAcl, "loading settings again leaves an already private folder alone");
            File.Delete(Path.Combine(hostDir, "probe.txt"));

            // validation
            foreach (var ip in new[] { "192.168.1.1", "100.64.0.1", "10.0.0.2", "127.0.0.1", "169.254.5.1", "203.0.113.4", "0.0.0.0", "224.0.0.1" }) Check(!Core.PublicIp(ip), "reject non-public endpoint " + ip);
            foreach (var nick in new[] { "ab", "has space", "nick\nstop", "../../../world", "@everyone" }) Reject(() => Core.Nick(nick), "reject malformed nickname");
            Reject(() => Core.Key(new string('A', 44)), "reject all-zero key");
            Func<string, byte[]> hex = h => Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(i * 2, 2), 16)).ToArray();
            Check(Core.X25519(hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"), hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")).SequenceEqual(hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")), "X25519 matches RFC 7748 §5.2 vector");
            Check(Core.PublicKeyFor(Convert.ToBase64String(hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"))) == Convert.ToBase64String(hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")), "public key matches RFC 7748 §6.1 vector");
            string generated = Core.NewPrivateKey();
            Check(Core.IsKey(generated) && Core.IsKey(Core.PublicKeyFor(generated)) && Core.NewPrivateKey() != generated, "fresh key pairs generated without wg.exe");

            // pairing codes: invite → client, reply → host, address update → client
            Core.Data = hostDir;
            string invite = Core.InviteCode(host);
            Check(invite.StartsWith("MP-") && invite.Length < 200, "invite code is short text (" + invite.Length + " chars)");
            Reject(() => Core.ApplyCode(host, invite, null), "host cannot paste its own invite");
            char[] broken = invite.ToCharArray(); broken[20] = broken[20] == 'A' ? 'B' : 'A';
            Core.Data = clientDir;
            Reject(() => Core.ApplyCode(client, new string(broken), null), "damaged code rejected by checksum");
            Check(Core.ApplyCode(client, "  " + invite.Substring(0, 30) + "\r\n" + invite.Substring(30) + " ", null) == "invite", "invite accepted with whitespace/line breaks from chat");
            Check(client.Id == host.Id && client.PeerKey == host.PublicKey && client.HostIp == host.HostIp && client.FriendIp == host.FriendIp && client.Endpoint == "8.8.4.4" && client.FriendNick == "Friend_Two", "client profile matches host invite");
            Check(Core.Load().PeerKey == host.PublicKey, "invite saved to disk");
            string reply = Core.ReplyCode(client);

            Core.Data = hostDir;
            Reject(() => Core.ApplyCode(host, reply, safety => false), "host can refuse when safety codes differ");
            Check(!host.Paired() && !Core.Load().Paired(), "refused reply leaves host unpaired");
            string seen = null;
            Check(Core.ApplyCode(host, reply, safety => { seen = safety; return true; }) == "reply", "host accepts reply");
            Check(host.PeerKey == client.PublicKey && seen == Core.SafetyCode(host) && seen == Core.SafetyCode(client) && System.Text.RegularExpressions.Regex.IsMatch(seen, @"\A[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}\z"), "48-bit safety code identical on both computers (" + seen + ")");
            Reject(() => Core.ApplyCode(host, Core.ReplyCode(new Settings { Id = host.Id, Role = "client", HostNick = "Host_One", FriendNick = "Friend_Two", HostIp = host.HostIp, FriendIp = host.FriendIp, Endpoint = "8.8.4.4", PublicKey = Key(70), PeerKey = host.PublicKey }), null), "second friend device rejected while paired");

            host.Endpoint = "1.1.1.1";
            Core.Save(host);
            string update = Core.UpdateCode(host);
            Core.Data = clientDir;
            Check(Core.ApplyCode(client, update, null) == "update" && client.Endpoint == "1.1.1.1" && client.PeerKey == host.PublicKey, "address update keeps pairing");
            Check(Core.ApplyCode(client, Core.InviteCode(host), null) == "invite" && client.PeerKey == host.PublicKey, "same host re-invite accepted");
            var stranger = new Settings { Role = "host", HostNick = "Other_Host", FriendNick = "Friend_Two", Endpoint = "9.9.9.9", PublicKey = Key(100), HostIp = "10.203.20.5", FriendIp = "10.203.20.6" };
            Reject(() => Core.ApplyCode(client, Core.InviteCode(stranger), null), "invite from another host rejected while paired");

            // tunnel configuration
            Core.Data = hostDir;
            string config = Core.TunnelText(host, host.PublicKey);
            Check(config.Contains("AllowedIPs = " + host.FriendIp + "/32") && config.Contains("ListenPort = 51820"), "host restricts peer to one /32");
            Check(!config.Contains("DNS") && !config.Contains("0.0.0.0/0") && !config.Contains("::/0") && !config.Contains("PostUp"), "no default route, DNS or scripts");
            string clientConfig = Core.TunnelText(client, client.PublicKey);
            Check(clientConfig.Contains("AllowedIPs = " + host.HostIp + "/32") && clientConfig.Contains("Endpoint = 1.1.1.1:51820") && !clientConfig.Contains("ListenPort"), "client has one host route and outbound endpoint");
            byte[] secret = Encoding.UTF8.GetBytes("test secret");
            byte[] encrypted = Native.Protect(secret, host.TunnelName());
            Check(Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine)) == "test secret", "tunnel configuration protected with DPAPI");
            Check(Core.OfflineUuid("Notch") == "b50ad385-829d-3141-a216-7e7d7539ba7f", "offline UUID matches Java UUID.nameUUIDFromBytes");
            string pipeName = "MinePair-test-" + Guid.NewGuid().ToString("N");
            using (var server = new System.IO.Pipes.NamedPipeServerStream(pipeName))
            using (var probe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName)) {
                probe.Connect(5000);
                Check(!Native.PipeServerIsService(probe), "pipe served by a user process is not trusted as the helper");
            }

            // worlds
            var world = Core.CreateWorld("Test world", "26.3", "creative", "hard", true, false, "Grüße seed");
            host.ActiveWorld = world.Id;
            Core.Save(host);
            Core.ServerProperties(host, world);
            string props = File.ReadAllText(Path.Combine(Core.WorldDir(world.Id), "server.properties"));
            Check(props.Contains("server-ip=" + host.HostIp + "\n") && props.Contains("enable-rcon=false") && props.Contains("management-server-enabled=false") && props.Contains("online-mode=false"), "server bound to tunnel; remote management off");
            Check(props.Contains("gamemode=creative\n") && props.Contains("difficulty=hard\n") && props.Contains("hardcore=true\n") && props.Contains("pvp=false\n"), "world settings written");
            Check(props.Contains("level-seed=Gr\\u00fc\\u00dfe seed\n"), "non-ASCII seed escaped for server.properties");
            Check(Core.Read<object[]>(Path.Combine(Core.WorldDir(world.Id), "ops.json")).Length == 0, "players receive no OP privileges");
            Reject(() => Core.CreateWorld("x", "26.3", "survival", "normal", false, true, "bad\nseed"), "seed with line break rejected");
            Reject(() => Core.CreateWorld("x", "../26", "survival", "normal", false, true, ""), "path-like version rejected");
            Check(Core.Worlds().Count == 1 && Core.GetWorld(world.Id).Name == "Test world", "world list reads saved world");

            // backups
            string worldFolder = Path.Combine(Core.WorldDir(world.Id), "world");
            Directory.CreateDirectory(Path.Combine(worldFolder, "region"));
            File.WriteAllText(Path.Combine(worldFolder, "level.dat"), "original");
            File.WriteAllText(Path.Combine(worldFolder, "region", "r.0.0.mca"), "region");
            string manual = Core.Backup(world, "manual");
            for (int i = 0; i < Core.AutoBackupsKept + 3; i++) { Core.Backup(world, "auto"); System.Threading.Thread.Sleep(5); }
            var all = Core.Backups(world);
            Check(all.Count(f => f.Name.EndsWith("_auto.zip")) == Core.AutoBackupsKept && File.Exists(manual), "automatic copies rotated, manual copy kept");
            File.WriteAllText(Path.Combine(worldFolder, "level.dat"), "changed");
            Core.Restore(world, manual);
            Check(File.ReadAllText(Path.Combine(worldFolder, "level.dat")) == "original", "backup / staged restore roundtrip");
            Check(Core.Backups(world).Any(f => f.Name.EndsWith("_before-restore.zip")), "restore keeps a safety copy");
            string malicious = Path.Combine(temp, "malicious.zip");
            using (var z = ZipFile.Open(malicious, ZipArchiveMode.Create)) using (var w = new StreamWriter(z.CreateEntry("../escaped.txt").Open())) w.Write("bad");
            Reject(() => Core.Extract(malicious, Path.Combine(temp, "stage")), "archive traversal rejected");
            Check(!File.Exists(Path.Combine(temp, "escaped.txt")), "archive cannot write outside destination");
            string ads = Path.Combine(temp, "ads.zip");
            using (var z = ZipFile.Open(ads, ZipArchiveMode.Create)) using (var w = new StreamWriter(z.CreateEntry("file:stream").Open())) w.Write("bad");
            Reject(() => Core.Extract(ads, Path.Combine(temp, "stage2")), "NTFS alternate stream rejected");

            // migration from version 1 (.local next to the exe)
            string legacy = Path.Combine(temp, "old", ".local");
            Directory.CreateDirectory(Path.Combine(legacy, "server", "world"));
            Directory.CreateDirectory(Path.Combine(legacy, "java", "jdk-25.0.4.1+1-jre", "bin"));
            Directory.CreateDirectory(Path.Combine(legacy, "backups"));
            Directory.CreateDirectory(Path.Combine(legacy, "downloads"));
            File.WriteAllText(Path.Combine(legacy, "java", "jdk-25.0.4.1+1-jre", "release"), "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"25.0.4\"\n");
            File.WriteAllText(Path.Combine(legacy, "java", "jdk-25.0.4.1+1-jre", "bin", "java.exe"), "");
            File.WriteAllText(Path.Combine(legacy, "server", "server.jar"), "jar");
            File.WriteAllText(Path.Combine(legacy, "server", "world", "level.dat"), "legacy world");
            File.WriteAllText(Path.Combine(legacy, "backups", "2026-10-22_23-21-22-156_before-start.zip"), "zip");
            File.WriteAllText(Path.Combine(legacy, "downloads", "version.json"), "{\"javaVersion\":{\"majorVersion\":25},\"downloads\":{\"server\":{\"sha1\":\"x\",\"url\":\"https://example.invalid\"}}}");
            File.WriteAllText(Path.Combine(legacy, "router-manual.json"), "{\"internalIp\":\"192.168.1.20\"}");
            File.WriteAllText(Path.Combine(legacy, "stop.request"), "1");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Schema\":1,\"Id\":\"a1b2c3d4e5f6\",\"Role\":\"host\",\"Version\":\"26.3\",\"HostNick\":\"Host_One\",\"FriendNick\":\"Friend_Two\",\"Endpoint\":\"1.0.0.1\",\"Port\":51820,\"MemoryMb\":4096,\"HostIp\":\"10.203.40.5\",\"FriendIp\":\"10.203.40.6\",\"PublicKey\":\"" + Key(1) + "\",\"PeerKey\":\"\",\"Java\":\"C:\\\\old\\\\java.exe\",\"ServerSha1\":\"abc\",\"Eula\":true,\"Tunnel\":\"MinePair_a1b2c3d4e5f6\"}");
            Core.Data = Path.Combine(temp, "migrated");
            Check(Core.MigrateLegacy(legacy), "legacy profile migrated");
            var m = Core.Load();
            var mw = Core.GetWorld(m.ActiveWorld);
            Check(m.Id == "a1b2c3d4e5f6" && m.IsHost() && m.MemoryMb == 4096 && m.RouterReady && m.LanIp == "192.168.1.20" && m.PublicKey == Key(1), "profile, tunnel id and router state preserved");
            Check(mw != null && mw.Version == "26.3" && mw.JavaMajor == 25 && mw.ServerSha1 == "abc" && File.ReadAllText(Path.Combine(Core.WorldDir(mw.Id), "world", "level.dat")) == "legacy world", "world moved to worlds folder");
            Check(File.Exists(Path.Combine(Core.VersionsDir, "26.3", "server.jar")) && Core.FindJava(25) != null, "server.jar and Java moved to shared folders");
            Check(Core.Backups(mw).Count == 1 && Core.Backups(mw)[0].Name.EndsWith("_auto.zip"), "old backups moved under the world");
            Check(!Directory.Exists(legacy) && !File.Exists(Path.Combine(Core.Data, "stop.request")), "legacy folder emptied, stale files dropped");
            Check(!Core.MigrateLegacy(legacy), "migration runs only once");
            string held = Path.Combine(temp, "held"), heldFile = Path.Combine(held, "java.exe");
            Directory.CreateDirectory(held);
            File.WriteAllText(heldFile, "x");
            var scanner = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { System.Threading.Thread.Sleep(700); scanner.Dispose(); });
            Core.MoveDirectory(held, held + "-moved");
            Check(File.Exists(Path.Combine(held + "-moved", "java.exe")) && !Directory.Exists(held), "folder move waits for a briefly locked file (antivirus scan)");

            // UI: render every screen offscreen without touching the network or the helper
            Core.Data = hostDir;
            Application.EnableVisualStyles();
            var screens = new[] { "welcome", "profile", "setup", "invite", "router", "main" };
            foreach (var name in screens) RenderScreen(name, artifacts);
            Core.Data = clientDir;
            foreach (var name in new[] { "join", "reply", "main" }) RenderScreen(name, artifacts, "client-");
            Check(File.Exists(Path.Combine(artifacts, "screen-main.png")) && File.Exists(Path.Combine(artifacts, "screen-client-main.png")), "all screens rendered");
            Check(Helper.EmbeddedScript().Length > 1000 && Encoding.UTF8.GetString(Helper.EmbeddedScript()).Contains("SERIALNUMBER=4227913"), "network script embedded with pinned WireGuard signer");
            string scriptCopy = Path.Combine(temp, "Network.ps1");
            File.WriteAllBytes(scriptCopy, Helper.EmbeddedScript());
            string parse = Core.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -Command \"$e=$null; [void][Management.Automation.Language.Parser]::ParseFile('" + scriptCopy + "',[ref]$null,[ref]$e); $e.Count\"", null, 60000);
            Check(parse.Trim() == "0", "installed network script parses in Windows PowerShell 5.1");

            output.Add("All checks passed. No router, firewall, service or installed software changed.");
            return 0;
        } catch (Exception e) {
            output.Add(e.ToString());
            return 1;
        } finally {
            Core.Data = original;
            File.WriteAllLines(Path.Combine(artifacts, "tests.txt"), output, Encoding.UTF8);
        }
    }

    static void RenderScreen(string name, string artifacts, string prefix = "") {
        using (var form = new MainForm()) {
            form.ShowInTaskbar = false;
            CreateHandles(form);
            form.ShowForTest(name);
            CreateHandles(form);
            form.PerformLayout();
            using (var bmp = new Bitmap(form.Width, form.Height)) {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(artifacts, "screen-" + prefix + name + ".png"));
            }
        }
    }
}
}
