using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MinePair {
public sealed class Settings {
    public int Schema = 1;
    public string Id = Guid.NewGuid().ToString("N").Substring(0, 12);
    public string Role = "host";
    public string Version = "26.3";
    public string HostNick = "";
    public string FriendNick = "";
    public string Endpoint = "";
    public int Port = 51820;
    public int MemoryMb = 3072;
    public string HostIp = "";
    public string FriendIp = "";
    public string PublicKey = "";
    public string PeerKey = "";
    public string Java = "";
    public string ServerSha1 = "";
    public bool Eula = false;
    public string Tunnel { get { return "MinePair_" + Id; } }
}
public sealed class PairFile {
    public int Schema = 1;
    public string Kind;
    public string Id;
    public string Version;
    public string HostNick;
    public string FriendNick;
    public string HostIp;
    public string FriendIp;
    public string PublicKey;
    public string Endpoint;
    public int Port;
}
public static class Core {
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    public static string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    public static string Data { get { return Path.Combine(Root, ".local"); } }
    public static string Config { get { return Path.Combine(Data, "settings.json"); } }
    public static string ServerDir { get { return Path.Combine(Data, "server"); } }
    public static string Wg { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"); } }
    public static string WgTool { get { return Path.Combine(Path.GetDirectoryName(Wg), "wg.exe"); } }
    static readonly object JsonLock = new object();
    public static T Read<T>(string path) { lock(JsonLock) return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
    public static void Write(string path, object value) {
        string content; lock(JsonLock) content = Json.Serialize(value);
        Atomic(path, Encoding.UTF8.GetBytes(content));
    }
    public static void Atomic(string path, byte[] content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".new";
        using(var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(content,0,content.Length); f.Flush(true); }
        if(File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path);
    }
    public static void PrivateDirectory(string path) {
        Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true,false);
        foreach(var sid in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null) })
            sec.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        Directory.SetAccessControl(path,sec);
    }
    public static Settings Load() {
        PrivateDirectory(Data);
        if(File.Exists(Config)) { var s=Read<Settings>(Config); Validate(s,false); return s; }
        var result = new Settings();
        byte[] b=new byte[2]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(b);
        result.HostIp="10.203." + (16 + b[0]%224) + "." + (4*(b[1]%62)+1);
        var parts=result.HostIp.Split('.'); parts[3]=(int.Parse(parts[3])+1).ToString(); result.FriendIp=string.Join(".",parts);
        Write(Config,result); return result;
    }
    public static void Nick(string value) { if(!Regex.IsMatch(value??"",@"\A[A-Za-z0-9_]{3,16}\z")) throw new Exception("Nickname: 3–16 Latin letters, digits or _. Use the exact in-game nickname."); }
    public static void Key(string value) {
        try { if(Convert.FromBase64String(value??"").Length != 32 || value.Length!=44) throw new Exception(); }
        catch { throw new Exception("Invalid public key."); }
        if(Convert.FromBase64String(value).All(x=>x==0)) throw new Exception("An empty key is not allowed.");
    }
    public static IPAddress Ip(string value) { IPAddress ip; if(!IPAddress.TryParse(value,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork) throw new Exception("An IPv4 address is required."); return ip; }
    public static bool PublicIp(string value) {
        byte[] b=Ip(value).GetAddressBytes();
        return !(b[0]==0 || b[0]==10 || b[0]==127 || b[0]>=224 || (b[0]==169&&b[1]==254) || (b[0]==172&&b[1]>=16&&b[1]<=31) || (b[0]==192&&b[1]==168) || (b[0]==100&&b[1]>=64&&b[1]<=127) || (b[0]==198&&(b[1]==18||b[1]==19)) || (b[0]==192&&b[1]==0) || (b[0]==198&&b[1]==51&&b[2]==100) || (b[0]==203&&b[1]==0&&b[2]==113));
    }
    public static void Validate(Settings s, bool paired) {
        if(s==null || s.Schema!=1 || !Regex.IsMatch(s.Id??"",@"\A[a-f0-9]{12}\z") || s.Version!="26.3" || (s.Role!="host"&&s.Role!="client")) throw new Exception("Unsupported profile.");
        if(s.Port<1024||s.Port>65535||s.MemoryMb<2048||s.MemoryMb>6144) throw new Exception("Invalid port or memory size.");
        byte[] a=Ip(s.HostIp).GetAddressBytes(), b=Ip(s.FriendIp).GetAddressBytes();
        if(a[0]!=10||a[1]!=203||a[2]<16||a[2]>239||a[3]%4!=1||b[0]!=a[0]||b[1]!=a[1]||b[2]!=a[2]||b[3]!=a[3]+1) throw new Exception("Invalid tunnel addressing.");
        if(paired) { Nick(s.HostNick); Nick(s.FriendNick); if(s.HostNick.Equals(s.FriendNick,StringComparison.OrdinalIgnoreCase)) throw new Exception("The nicknames must be different."); Key(s.PublicKey); Key(s.PeerKey); if(s.PublicKey==s.PeerKey) throw new Exception("Both sides cannot use the same key."); if(!PublicIp(s.Endpoint)) throw new Exception("The host's public IPv4 is required."); }
    }
    public static bool Elevated { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
    public static string Run(string file, string args, string input, int timeout) {
        var info=new ProcessStartInfo(file,args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true };
        using(var p=Process.Start(info)) {
            var output=p.StandardOutput.ReadToEndAsync(); var errors=p.StandardError.ReadToEndAsync();
            if(input!=null) p.StandardInput.Write(input); p.StandardInput.Close();
            if(!p.WaitForExit(timeout)) throw new Exception("A component did not respond in time. Check its state before retrying.");
            if(p.ExitCode!=0) throw new Exception("Component error: " + errors.Result.Trim()); return output.Result.Trim();
        }
    }
    public static string Quote(string s) { if(s.IndexOf('"')>=0 || s.IndexOf('\r')>=0 || s.IndexOf('\n')>=0) throw new Exception("Invalid path."); return "\""+s+"\""; }
    public static void Admin(string action) {
        if(!new[]{"install","start","stop","status","uninstall","router-add","router-remove"}.Contains(action)) throw new Exception("Unknown action.");
        string script=Path.Combine(Root,"Network.ps1");
        string result=Path.Combine(Data,"admin-result.json"); if(File.Exists(result)) File.Delete(result);
        var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "+Quote(script)+" -Root "+Quote(Root)+" -Action "+action) { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden });
        p.WaitForExit();
        if(!File.Exists(result)) throw new Exception("The action did not finish. The administrator prompt may have been cancelled.");
        var r=Read<Dictionary<string,object>>(result); if(!(bool)r["ok"]) throw new Exception(Convert.ToString(r["error"]));
    }
    public static void EnsureKey(Settings s) {
        string path=Path.Combine(Data,"private-key.dpapi");
        if(File.Exists(path)) { Key(s.PublicKey); return; }
        if(!File.Exists(WgTool)) throw new Exception("Install WireGuard first with the \"Prepare\" button.");
        string key=Run(WgTool,"genkey",null,10000); Key(key);
        string pub=Run(WgTool,"pubkey",key+"\n",10000); Key(pub);
        Atomic(path,ProtectedData.Protect(Encoding.ASCII.GetBytes(key),null,DataProtectionScope.CurrentUser));
        s.PublicKey=pub; Write(Config,s);
    }
    public static string PrivateKey() { return Encoding.ASCII.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Data,"private-key.dpapi")),null,DataProtectionScope.CurrentUser)); }
    public static PairFile Invite(Settings s) {
        Validate(s,false); Nick(s.HostNick); Nick(s.FriendNick); if(s.HostNick.Equals(s.FriendNick,StringComparison.OrdinalIgnoreCase)) throw new Exception("The nicknames must be different."); if(!PublicIp(s.Endpoint)) throw new Exception("Enter your router's public IPv4 address."); EnsureKey(s);
        return new PairFile { Kind="invite",Id=s.Id,Version=s.Version,HostNick=s.HostNick,FriendNick=s.FriendNick,HostIp=s.HostIp,FriendIp=s.FriendIp,PublicKey=s.PublicKey,Endpoint=s.Endpoint,Port=s.Port };
    }
    public static void ImportInvite(Settings s, PairFile p) {
        if(p==null||p.Schema!=1||p.Kind!="invite") throw new Exception("This is not a MinePair invitation.");
        if(!string.IsNullOrEmpty(s.PeerKey)) throw new Exception("This profile is already in use. Remove the network setup first and use a separate program folder for a new profile.");
        var c=new Settings { Id=p.Id,Role="client",Version=p.Version,HostNick=p.HostNick,FriendNick=p.FriendNick,HostIp=p.HostIp,FriendIp=p.FriendIp,PeerKey=p.PublicKey,PublicKey=s.PublicKey,Endpoint=p.Endpoint,Port=p.Port };
        Validate(c,true);
        if(c.PublicKey==c.PeerKey) throw new Exception("An invitation cannot be accepted on the same device.");
        string journal=Path.Combine(Data,"network-journal.json");
        if(File.Exists(journal)) {
            var j=Read<Dictionary<string,object>>(journal);
            if((bool)j["active"]||(bool)j["routerOwned"]) throw new Exception("Remove our active network first.");
            j["id"]=c.Id; Write(journal,j);
        }
        Write(Config,c);
    }
    public static PairFile Reply(Settings s) { Validate(s,true); return new PairFile { Kind="reply",Id=s.Id,Version=s.Version,PublicKey=s.PublicKey,HostIp=s.HostIp,FriendIp=s.FriendIp }; }
    public static void AcceptReply(Settings s, PairFile p) {
        if(p==null||p.Schema!=1||p.Kind!="reply"||p.Id!=s.Id||p.Version!=s.Version||p.HostIp!=s.HostIp||p.FriendIp!=s.FriendIp||s.Role!="host") throw new Exception("The reply does not match this invitation.");
        Key(p.PublicKey); if(p.PublicKey==s.PublicKey) throw new Exception("This is your own key."); if(!string.IsNullOrEmpty(s.PeerKey)&&s.PeerKey!=p.PublicKey) throw new Exception("A friend is already added. Revoke access first to replace the key.");
        s.PeerKey=p.PublicKey; Validate(s,true); Write(Config,s);
    }
    public static string TunnelText(Settings s, string privateKey) {
        Validate(s,true); Key(privateKey);
        string text="[Interface]\nPrivateKey = "+privateKey+"\nAddress = "+(s.Role=="host"?s.HostIp:s.FriendIp)+"/32\n";
        if(s.Role=="host") text+="ListenPort = "+s.Port+"\n";
        text+="\n[Peer]\nPublicKey = "+s.PeerKey+"\nAllowedIPs = "+(s.Role=="host"?s.FriendIp:s.HostIp)+"/32\n";
        if(s.Role=="client") text+="Endpoint = "+s.Endpoint+":"+s.Port+"\nPersistentKeepalive = 25\n";
        return text;
    }
    public static void TunnelFile(Settings s) {
        PrivateDirectory(Path.Combine(Data,"protected"));
        Atomic(Path.Combine(Data,"protected",s.Tunnel+".conf.dpapi"), Native.Protect(Encoding.UTF8.GetBytes(TunnelText(s,PrivateKey())),s.Tunnel));
    }
    public static string Hash(string path, bool sha256) { using(var f=File.OpenRead(path)) using(HashAlgorithm h=sha256?(HashAlgorithm)SHA256.Create():SHA1.Create()) return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
    public static string HashString(string value) { using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).Replace("-","").Substring(0,24); }
    public static void Get(string url,string target,string hash,bool sha256) {
        Uri uri=new Uri(url); if(uri.Scheme!="https") throw new Exception("Downloads are allowed over HTTPS only.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)); string tmp=target+".download";
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        try { using(var c=new WebClient()) { c.Headers["User-Agent"]="MinePair/1.0"; c.DownloadFile(uri,tmp); } if(hash!=null && !Hash(tmp,sha256).Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new Exception("Checksum mismatch. The file will not be used."); if(File.Exists(target)) File.Delete(target); File.Move(tmp,target); }
        finally { if(File.Exists(tmp)) File.Delete(tmp); }
    }
    public static string GetText(string url) { ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12; using(var c=new WebClient()) { c.Encoding=Encoding.UTF8; c.Headers["User-Agent"]="MinePair/1.0"; return c.DownloadString(url); } }
    public static Dictionary<string,object> Obj(object o) { return (Dictionary<string,object>)o; }
    public static object[] Arr(object o) { return o as object[] ?? ((System.Collections.ArrayList)o).ToArray(); }
    public static void Prepare(Settings s, Action<string> progress) {
        Directory.CreateDirectory(Path.Combine(Data,"downloads"));
        if(!File.Exists(Wg)) {
            progress("Downloading official WireGuard…");
            string index=GetText("https://download.wireguard.com/windows-client/");
            var matches=Regex.Matches(index,@"wireguard-amd64-([0-9]+\.[0-9]+\.[0-9]+)\.msi");
            if(matches.Count==0) throw new Exception("Official WireGuard installer not found.");
            string file=matches.Cast<Match>().OrderByDescending(m=>new Version(m.Groups[1].Value)).First().Value;
            Get("https://download.wireguard.com/windows-client/"+file,Path.Combine(Data,"downloads","wireguard.msi"),null,true);
            progress("Verifying the signature and installing WireGuard (UAC)…"); Admin("install");
        }
        EnsureKey(s);
        if(s.Role=="client") { progress("The network component is ready. Import the invitation."); return; }
        progress("Checking the official Minecraft 26.3 release…");
        Dictionary<string,object> manifest; lock(JsonLock) manifest=Json.Deserialize<Dictionary<string,object>>(GetText("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
        var version=Arr(manifest["versions"]).Select(Obj).FirstOrDefault(v=>(string)v["id"]==s.Version);
        if(version==null) throw new Exception("Version not found in the official manifest.");
        string metadata=Path.Combine(Data,"downloads","version.json"); Get((string)version["url"],metadata,(string)version["sha1"],false);
        var meta=Read<Dictionary<string,object>>(metadata);
        int major=Convert.ToInt32(Obj(meta["javaVersion"])["majorVersion"]);
        var jar=Obj(Obj(meta["downloads"])["server"]); string jarPath=Path.Combine(ServerDir,"server.jar");
        if(!File.Exists(jarPath)||Hash(jarPath,false)!=(string)jar["sha1"]) { progress("Downloading vanilla server.jar…"); Get((string)jar["url"],jarPath,(string)jar["sha1"],false); }
        s.ServerSha1=(string)jar["sha1"];
        string javaDir=Path.Combine(Data,"java");
        if(string.IsNullOrEmpty(s.Java)||!File.Exists(s.Java)) {
            progress("Downloading portable Java "+major+"…");
            object[] assets; lock(JsonLock) assets=Json.Deserialize<object[]>(GetText("https://api.adoptium.net/v3/assets/latest/"+major+"/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse"));
            if(assets.Length==0) throw new Exception("Official Temurin JRE not found.");
            var package=Obj(Obj(Obj(assets[0])["binary"])["package"]);
            string zip=Path.Combine(Data,"downloads","java.zip"); Get((string)package["link"],zip,(string)package["checksum"],true);
            if(Directory.Exists(javaDir)) throw new Exception("The Java folder already has content. Check it before preparing again.");
            try { Extract(zip,javaDir); } catch { if(Directory.Exists(javaDir)) Directory.Delete(javaDir,true); throw; }
            var found=Directory.GetFiles(javaDir,"java.exe",SearchOption.AllDirectories).Where(p=>Path.GetFileName(Path.GetDirectoryName(p))=="bin").ToArray();
            if(found.Length!=1) throw new Exception("Could not locate java.exe."); s.Java=found[0];
        }
        string javaVersion=Run(s.Java,"-XshowSettings:properties -version",null,20000); // exit code verifies executable; version recorded by runtime metadata
        Write(Config,s); progress("Components ready. An invitation and the friend's reply are still needed.");
    }
    public static void Extract(string archive,string target) {
        string root=Path.GetFullPath(target).TrimEnd('\\')+"\\";
        using(var zip=ZipFile.OpenRead(archive)) {
            long total=0;
            foreach(var entry in zip.Entries) {
                string path=Path.GetFullPath(Path.Combine(root,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
                if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase) || entry.FullName.IndexOf(':')>=0 || (entry.ExternalAttributes>>16 & 0xF000)==0xA000) throw new Exception("The archive contains an unsafe path.");
                total+=entry.Length; if(total>4L*1024*1024*1024) throw new Exception("The archive is too large.");
                if(string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path,false);
            }
        }
    }
    public static void ServerProperties(Settings s) {
        Validate(s,true); if(!s.Eula) throw new Exception("The Minecraft EULA must be accepted.");
        Directory.CreateDirectory(ServerDir);
        string p="server-ip="+s.HostIp+"\nserver-port=25565\nonline-mode=false\nenforce-secure-profile=false\nwhite-list=true\nenforce-whitelist=true\nmax-players=2\ngamemode=survival\ndifficulty=normal\nhardcore=false\npvp=true\nlevel-name=world\nview-distance=10\nsimulation-distance=6\nenable-rcon=false\nenable-query=false\nmanagement-server-enabled=false\nmanagement-server-host=127.0.0.1\nenable-jmx-monitoring=false\naccepts-transfers=false\nspawn-protection=0\nmotd=MinePair - private survival\n";
        Atomic(Path.Combine(ServerDir,"server.properties"),Encoding.ASCII.GetBytes(p));
        Atomic(Path.Combine(ServerDir,"eula.txt"),Encoding.ASCII.GetBytes("eula=true\n"));
        var names=new[]{s.HostNick,s.FriendNick}; var list=names.Select(n=>new Dictionary<string,string>{{"uuid",OfflineUuid(n)},{"name",n}}).ToArray();
        Write(Path.Combine(ServerDir,"whitelist.json"),list);
        Write(Path.Combine(ServerDir,"ops.json"),new object[0]);
    }
    public static string OfflineUuid(string name) {
        byte[] h; using(var md5=MD5.Create()) h=md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:"+name)); h[6]=(byte)((h[6]&15)|48); h[8]=(byte)((h[8]&63)|128);
        string hex=BitConverter.ToString(h).Replace("-","").ToLowerInvariant(); return hex.Substring(0,8)+"-"+hex.Substring(8,4)+"-"+hex.Substring(12,4)+"-"+hex.Substring(16,4)+"-"+hex.Substring(20);
    }
    public static bool SupervisorAlive() {
        string f=Path.Combine(Data,"session.json"); if(!File.Exists(f)) return false;
        try { var d=Read<Dictionary<string,object>>(f); using(var p=Process.GetProcessById(Convert.ToInt32(d["pid"]))) return p.StartTime.ToUniversalTime().Ticks==Convert.ToInt64(d["started"]) && Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(Path.Combine(Root,"MinePair.exe")),StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    public static void Backup(string reason) {
        if(SupervisorAlive()) throw new Exception("Stop the server first: a backup of a live world may be incomplete.");
        string world=Path.Combine(ServerDir,"world"); if(!Directory.Exists(world)) return;
        if((File.GetAttributes(world)&FileAttributes.ReparsePoint)!=0 || Directory.GetFileSystemEntries(world,"*",SearchOption.AllDirectories).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)) throw new Exception("The world contains a link to another directory.");
        string dir=Path.Combine(Data,"backups"); Directory.CreateDirectory(dir);
        string target=Path.Combine(dir,DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff")+"_"+reason+".zip");
        ZipFile.CreateFromDirectory(world,target+".partial",CompressionLevel.Fastest,false); File.Move(target+".partial",target);
    }
    public static void Restore(string archive) {
        if(SupervisorAlive()) throw new Exception("Stop the server first.");
        string stage=Path.Combine(Data,"restore-"+Guid.NewGuid().ToString("N"));
        try { Extract(archive,stage); if(!File.Exists(Path.Combine(stage,"level.dat"))) throw new Exception("This is not a MinePair world backup."); Backup("before-restore"); string world=Path.Combine(ServerDir,"world"), old=Path.Combine(Data,"previous-world-"+Guid.NewGuid().ToString("N")); bool moved=false;
            if(Directory.Exists(world)) { Directory.Move(world,old); moved=true; }
            try { Directory.Move(stage,world); } catch { if(moved) Directory.Move(old,world); throw; }
            if(moved) Directory.Delete(old,true);
        } finally { if(Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    public static void StartServer(Settings s,int owner) {
        if(Elevated) throw new Exception("Start MinePair with a normal double-click, without administrator rights. The server must not run elevated.");
        Validate(s,true); if(!File.Exists(s.Java)||!File.Exists(Path.Combine(ServerDir,"server.jar"))) throw new Exception("Prepare the components first.");
        if(Hash(Path.Combine(ServerDir,"server.jar"),false)!=s.ServerSha1) throw new Exception("server.jar was modified. Run the preparation again.");
        if(SupervisorAlive()) throw new Exception("The server is already running.");
        if(File.Exists(Path.Combine(Data,"unclean-session"))) throw new Exception("The previous session ended unexpectedly. Check the world and the log; do not overwrite backups. See the README.");
        Backup("before-start"); ServerProperties(s); TunnelFile(s);
        Admin("start");
        try {
            string stop=Path.Combine(Data,"stop.request"); if(File.Exists(stop)) File.Delete(stop);
            Process.Start(new ProcessStartInfo(Path.Combine(Root,"MinePair.exe"),"--server-worker "+owner) { UseShellExecute=false,CreateNoWindow=true });
            var sw=Stopwatch.StartNew(); while(sw.ElapsedMilliseconds<15000) { if(SupervisorAlive()) return; Thread.Sleep(100); } throw new Exception("The server supervisor did not start. See the log.");
        } catch { Admin("stop"); throw; }
    }
    public static void StopServer() {
        if(SupervisorAlive()) {
            Atomic(Path.Combine(Data,"stop.request"),new byte[]{1}); var sw=Stopwatch.StartNew();
            while(SupervisorAlive()&&sw.Elapsed.TotalSeconds<120) Thread.Sleep(300);
            if(SupervisorAlive()) throw new Exception("The server is still saving the world or is stuck. It was not force-killed. Wait and try stopping again.");
        }
        Admin("stop");
    }
    public static void Supervisor(int owner) {
        Settings s=Load(); if(Elevated) throw new Exception("The server must not run elevated.");
        var own=Process.GetCurrentProcess();
        using(var sessionLock=new FileStream(Path.Combine(Data,"server.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            Atomic(Path.Combine(Data,"unclean-session"),Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")));
            Write(Path.Combine(Data,"session.json"),new {pid=own.Id,started=own.StartTime.ToUniversalTime().Ticks});
            Directory.CreateDirectory(Path.Combine(Data,"logs"));
            using(var log=new StreamWriter(Path.Combine(Data,"logs","server-console.log"),true,Encoding.UTF8)) {
                log.AutoFlush=true; object gate=new object();
                var info=new ProcessStartInfo(s.Java,"-Xms1G -Xmx"+s.MemoryMb+"M -jar server.jar nogui") { WorkingDirectory=ServerDir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
                using(var p=new Process {StartInfo=info}) {
                    p.OutputDataReceived+=(o,e)=>{if(e.Data!=null) lock(gate) log.WriteLine(e.Data);}; p.ErrorDataReceived+=(o,e)=>{if(e.Data!=null) lock(gate) log.WriteLine(e.Data);};
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); bool stopSent=false;
                    DateTime ownerStarted; try { ownerStarted=Process.GetProcessById(owner).StartTime; } catch { ownerStarted=DateTime.MinValue; }
                    while(!p.WaitForExit(500)) {
                        bool ownerGone=false; try { using(var parent=Process.GetProcessById(owner)) ownerGone=parent.StartTime!=ownerStarted; } catch {ownerGone=true;}
                        if(!stopSent&&(ownerGone||File.Exists(Path.Combine(Data,"stop.request")))) { p.StandardInput.WriteLine("stop"); p.StandardInput.Flush(); stopSent=true; }
                    }
                    p.WaitForExit(); if(p.ExitCode==0 && stopSent) File.Delete(Path.Combine(Data,"unclean-session"));
                }
            }
            File.Delete(Path.Combine(Data,"session.json"));
        }
    }
    public static string TcpProbe(string address) {
        var sw=Stopwatch.StartNew(); using(var c=new TcpClient()) { var t=c.ConnectAsync(Ip(address),25565); if(!t.Wait(1800)) return "TCP: no response (this is not a packet-loss measurement)"; if(!c.Connected) return "TCP: unreachable"; }
        return "TCP connect: "+sw.ElapsedMilliseconds+" ms. This is neither the in-game ping nor TPS.";
    }
    static byte[] VarInt(int value) {uint v=unchecked((uint)value);var bytes=new List<byte>();do{byte b=(byte)(v&127);v>>=7;if(v!=0)b|=128;bytes.Add(b);}while(v!=0);return bytes.ToArray();}
    static int ReadVarInt(Stream stream) {int v=0;for(int i=0;i<5;i++){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();v|=(b&127)<<(7*i);if((b&128)==0)return v;}throw new Exception("Invalid Minecraft response.");}
    static byte[] ReadPacket(Stream stream) {int length=ReadVarInt(stream);if(length<1||length>1024*1024)throw new Exception("Invalid response size.");var data=new byte[length];int got=0;while(got<length){int n=stream.Read(data,got,length-got);if(n==0)throw new EndOfStreamException();got+=n;}return data;}
    static void Packet(Stream stream,byte[] data){byte[] length=VarInt(data.Length);stream.Write(length,0,length.Length);stream.Write(data,0,data.Length);stream.Flush();}
    public static string MinecraftPing(string address) {
        using(var c=new TcpClient()) {
            var pending=c.ConnectAsync(Ip(address),25565);if(!pending.Wait(2500))throw new Exception("Minecraft did not respond. Check that the server is running, the UDP forwarding and the friend's connection.");
            c.ReceiveTimeout=3000;c.SendTimeout=3000;c.NoDelay=true;
            using(var stream=c.GetStream()) {
                var handshake=new List<byte>();handshake.Add(0);handshake.AddRange(VarInt(-1));byte[] host=Encoding.UTF8.GetBytes(address);handshake.AddRange(VarInt(host.Length));handshake.AddRange(host);handshake.Add(99);handshake.Add(221);handshake.Add(1);Packet(stream,handshake.ToArray());Packet(stream,new byte[]{0});
                byte[] status=ReadPacket(stream);Dictionary<string,object> metadata;
                using(var input=new MemoryStream(status)){if(ReadVarInt(input)!=0)throw new Exception("Expected a Minecraft status response.");int len=ReadVarInt(input);if(len<1||len>status.Length-input.Position)throw new Exception("Invalid status.");byte[] json=new byte[len];input.Read(json,0,len);lock(JsonLock)metadata=Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(json));}
                byte[] ping=new byte[9];ping[0]=1;byte[] nonce=new byte[8];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(nonce);Buffer.BlockCopy(nonce,0,ping,1,8);var watch=Stopwatch.StartNew();Packet(stream,ping);byte[] pong=ReadPacket(stream);watch.Stop();
                if(!pong.SequenceEqual(ping))throw new Exception("The game ping reply did not match.");
                var version=Obj(metadata["version"]);var players=Obj(metadata["players"]);
                return "Minecraft "+version["name"]+" · players "+players["online"]+"/"+players["max"]+" · ping "+watch.ElapsedMilliseconds+" ms. This test does not measure packet loss or TPS.";
            }
        }
    }
}
internal static class Native {
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    public static byte[] Protect(byte[] data,string name) { Blob b=new Blob {Length=data.Length,Data=Marshal.AllocHGlobal(data.Length)}, output; try {Marshal.Copy(data,0,b.Data,data.Length); if(!CryptProtectData(ref b,name,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,5,out output)) throw new System.ComponentModel.Win32Exception(); try {var a=new byte[output.Length]; Marshal.Copy(output.Data,a,0,a.Length); return a;} finally {LocalFree(output.Data);} } finally {Marshal.FreeHGlobal(b.Data);} }
}
}
