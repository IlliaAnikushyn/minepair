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
    static List<string> output=new List<string>();
    static void Check(bool yes,string label) {if(!yes)throw new Exception("FAIL: "+label);output.Add("PASS: "+label);}
    static void Reject(Action action,string label) {bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,label);}
    static void CreateHandles(Control c) {var handle=c.Handle;foreach(Control child in c.Controls)CreateHandles(child);c.PerformLayout();}
    public static int Run() {
        string original=Core.Root; string artifacts=Path.Combine(original,"artifacts");Directory.CreateDirectory(artifacts);
        string temp=Path.Combine(artifacts,"test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);Core.Root=temp;
        try {
            var s=Core.Load();s.HostNick="Host_One";s.FriendNick="Friend_Two";s.Endpoint="8.8.4.4";
            s.PublicKey=Convert.ToBase64String(Enumerable.Range(1,32).Select(x=>(byte)x).ToArray());s.PeerKey=Convert.ToBase64String(Enumerable.Range(33,32).Select(x=>(byte)x).ToArray());
            Core.Validate(s,true);Check(s.HostIp!=s.FriendIp,"two distinct /32 tunnel addresses");
            foreach(var ip in new[]{"192.168.1.1","100.64.0.1","10.0.0.2","127.0.0.1","169.254.5.1","203.0.113.4","0.0.0.0","224.0.0.1"})Check(!Core.PublicIp(ip),"reject non-public endpoint "+ip);
            foreach(var nick in new[]{"ab","has space","nick\nstop","../../../world","@everyone"})Reject(()=>Core.Nick(nick),"reject malformed nickname");
            Reject(()=>Core.Key(new string('A',44)),"reject malformed key");
            string config=Core.TunnelText(s,s.PublicKey);Check(config.Contains("AllowedIPs = "+s.FriendIp+"/32"),"host restricts peer source IP");Check(!config.Contains("DNS")&&!config.Contains("0.0.0.0/0")&&!config.Contains("::/0")&&!config.Contains("PostUp"),"no default route, DNS or scripts");
            s.Role="client";config=Core.TunnelText(s,s.PublicKey);Check(config.Contains("AllowedIPs = "+s.HostIp+"/32")&&config.Contains("Endpoint = 8.8.4.4:51820")&&!config.Contains("ListenPort"),"client has one host route and outbound endpoint");s.Role="host";
            byte[] secret=Encoding.UTF8.GetBytes("test secret");byte[] encrypted=Native.Protect(secret,s.Tunnel);Check(encrypted.Length>secret.Length&&Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted,null,DataProtectionScope.LocalMachine))=="test secret","WireGuard configuration protected with DPAPI");
            byte[] privateEncrypted=ProtectedData.Protect(secret,null,DataProtectionScope.CurrentUser);Check(ProtectedData.Unprotect(privateEncrypted,null,DataProtectionScope.CurrentUser).SequenceEqual(secret),"private key current-user DPAPI roundtrip");
            Check(Core.OfflineUuid("Notch")=="b50ad385-829d-3141-a216-7e7d7539ba7f","offline UUID matches Java UUID.nameUUIDFromBytes");
            s.Eula=false;Reject(()=>Core.ServerProperties(s),"EULA required before creating runnable server");s.Eula=true;Core.ServerProperties(s);
            string props=File.ReadAllText(Path.Combine(Core.ServerDir,"server.properties"));Check(props.Contains("server-ip="+s.HostIp+"\n")&&props.Contains("enable-rcon=false")&&props.Contains("management-server-enabled=false"),"server bound to tunnel; remote management disabled");
            Check(Core.Read<object[]>(Path.Combine(Core.ServerDir,"ops.json")).Length==0,"players receive no OP privileges");
            var response=Core.Reply(new Settings {Id=s.Id,Role="client",HostNick=s.HostNick,FriendNick=s.FriendNick,HostIp=s.HostIp,FriendIp=s.FriendIp,Endpoint=s.Endpoint,PublicKey=s.PeerKey,PeerKey=s.PublicKey});
            var pairTarget=Core.Read<Settings>(Core.Config);pairTarget.HostNick=s.HostNick;pairTarget.FriendNick=s.FriendNick;pairTarget.Endpoint=s.Endpoint;pairTarget.PublicKey=s.PublicKey;
            Core.AcceptReply(pairTarget,response);Check(pairTarget.PeerKey==s.PeerKey,"pairing accepts matching public response");
            response.Id="ffffffffffff";Reject(()=>Core.AcceptReply(pairTarget,response),"pairing rejects unrelated invitation ID");
            string world=Path.Combine(Core.ServerDir,"world");Directory.CreateDirectory(world);File.WriteAllText(Path.Combine(world,"level.dat"),"original");Directory.CreateDirectory(Path.Combine(world,"region"));File.WriteAllText(Path.Combine(world,"region","r.0.0.mca"),"region");Core.Backup("test");
            string archive=Directory.GetFiles(Path.Combine(Core.Data,"backups"),"*.zip").Single();File.WriteAllText(Path.Combine(world,"level.dat"),"changed");Core.Restore(archive);Check(File.ReadAllText(Path.Combine(world,"level.dat"))=="original","backup / staged restore roundtrip");Check(Directory.GetFiles(Path.Combine(Core.Data,"backups"),"*.zip").Length==2,"restore retains pre-restore backup");
            string malicious=Path.Combine(temp,"malicious.zip");using(var z=ZipFile.Open(malicious,ZipArchiveMode.Create)){using(var w=new StreamWriter(z.CreateEntry("../escaped.txt").Open()))w.Write("bad");}Reject(()=>Core.Extract(malicious,Path.Combine(temp,"stage")),"archive traversal rejected");Check(!File.Exists(Path.Combine(temp,"escaped.txt")),"archive cannot write outside destination");
            string ads=Path.Combine(temp,"ads.zip");using(var z=ZipFile.Open(ads,ZipArchiveMode.Create)){using(var w=new StreamWriter(z.CreateEntry("file:stream").Open()))w.Write("bad");}Reject(()=>Core.Extract(ads,Path.Combine(temp,"stage2")),"NTFS alternate stream rejected");
            // Render the actual WinForms layout without operating the user's desktop.
            Application.EnableVisualStyles();using(var form=new MainForm()){form.ShowInTaskbar=false;CreateHandles(form);form.PerformLayout();using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(artifacts,"preview.png"));}}Check(File.Exists(Path.Combine(artifacts,"preview.png")),"WinForms offscreen preview rendered");
            output.Add("All checks passed. No router, firewall, service or installed software changed.");return 0;
        } catch(Exception e){output.Add(e.ToString());return 1;}
        finally {Core.Root=original;File.WriteAllLines(Path.Combine(artifacts,"tests.txt"),output,Encoding.UTF8);}
    }
}
}
