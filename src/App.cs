using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MinePair {
static class Program {
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length>0&&args[0]=="--self-test") return SelfTests.Run();
            if(args.Length==2&&args[0]=="--server-worker") {Core.Supervisor(int.Parse(args[1]));return 0;}
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool first; using(var mutex=new Mutex(true,"Local\\MinePair-"+Core.HashString(Core.Root),out first)) {
                if(!first) {MessageBox.Show("MinePair is already open for this folder.");return 1;}
                if(Core.Elevated) {MessageBox.Show("Start MinePair with a normal double-click, not \"Run as administrator\". Elevation is requested separately for network changes.");return 1;}
                Application.Run(new MainForm());
            }
            return 0;
        } catch(Exception e) { try {Directory.CreateDirectory(Core.Data);File.AppendAllText(Path.Combine(Core.Data,"application-errors.log"),DateTime.UtcNow.ToString("o")+" "+e+Environment.NewLine);}catch{} if(args.Length==0) MessageBox.Show(e.Message,"MinePair");return 1; }
    }
}
public sealed class MainForm : Form {
    Settings settings;
    ComboBox role;
    TextBox hostNick,friendNick,endpoint;
    NumericUpDown memory;
    Label state,details,address;
    CheckBox eula;
    FlowLayoutPanel buttons;
    TextBox log;
    bool busy,closing,networkOn;
    System.Windows.Forms.Timer timer;
    public MainForm() {
        settings=Core.Load();
        Text="MinePair"; ClientSize=new Size(820,790); MinimumSize=new Size(800,780); StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10); BackColor=Color.FromArgb(19,23,30); ForeColor=Color.FromArgb(230,235,243);
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(28),ColumnCount=1,RowCount=9};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,67));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,57));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,130));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,144));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
        var header=new Label {Text="MinePair",Font=new Font("Segoe UI",27,FontStyle.Bold),Dock=DockStyle.Fill};layout.Controls.Add(header,0,0);
        state=new Label {Text="A private world for the two of you",Dock=DockStyle.Fill,Font=new Font("Segoe UI",12),ForeColor=Color.FromArgb(126,211,183)};layout.Controls.Add(state,0,1);
        var fields=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=4,RowCount=3};
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,116));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        role=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};role.Items.AddRange(new object[]{"My server","I am the friend"});role.SelectedIndex=settings.Role=="host"?0:1;
        hostNick=Input(settings.HostNick);friendNick=Input(settings.FriendNick);endpoint=Input(settings.Endpoint);
        memory=new NumericUpDown {Minimum=2048,Maximum=6144,Increment=512,Value=settings.MemoryMb,Dock=DockStyle.Fill,BackColor=Color.FromArgb(33,40,51),ForeColor=ForeColor};
        Field(fields,"Mode",role,0,0);Field(fields,"Memory, MB",memory,2,0);Field(fields,"Your nickname",hostNick,0,1);Field(fields,"Friend's nickname",friendNick,2,1);Field(fields,"Host IPv4",endpoint,0,2);
        var autoIp=Button("Detect IPv4",()=>Work(()=>{string ip=Core.GetText("https://api.ipify.org").Trim();if(!Core.PublicIp(ip)) throw new Exception("No public IPv4 address received.");Ui(()=>endpoint.Text=ip);},"Public IPv4 detected. Compare it with the WAN address on your router."));fields.Controls.Add(autoIp,3,2);layout.Controls.Add(fields,0,2);
        eula=new CheckBox {Text="I accept the Minecraft EULA to run my own server",Checked=settings.Eula,Dock=DockStyle.Fill};layout.Controls.Add(eula,0,3);
        address=new Label {Dock=DockStyle.Fill,Text="The game address appears once connected",ForeColor=Color.FromArgb(163,182,210)};layout.Controls.Add(address,0,4);
        buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true,AutoScroll=true,Margin=Padding.Empty};
        buttons.Controls.Add(Button("1. Prepare",Prepare));
        buttons.Controls.Add(Button("2. Invitation",ExportInvite));
        buttons.Controls.Add(Button("Import file",Import));
        buttons.Controls.Add(Button("Reply to host",ExportReply));
        buttons.Controls.Add(Button("Start",StartNetwork,true));
        buttons.Controls.Add(Button("Stop / remove network",StopNetwork));
        buttons.Controls.Add(Button("Router",Router));
        buttons.Controls.Add(Button("Check connection",Probe));
        buttons.Controls.Add(Button("Back up world",Backup));
        buttons.Controls.Add(Button("Restore",Restore));
        buttons.Controls.Add(Button("Remove setup",Uninstall));
        buttons.Controls.Add(Button("Open data folder",()=>Process.Start("explorer.exe",Core.Quote(Core.Data))));
        buttons.Controls.Add(Button("Revoke friend",Revoke));
        layout.Controls.Add(buttons,0,5);
        details=new Label {Dock=DockStyle.Fill,Text="Server and VPN are off. Components are installed in a separate step.",ForeColor=Color.FromArgb(164,174,191)};layout.Controls.Add(details,0,6);
        log=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(13,17,23),ForeColor=Color.FromArgb(175,191,208),BorderStyle=BorderStyle.FixedSingle};layout.Controls.Add(log,0,7);
        var footer=new LinkLabel {Text="Minecraft EULA",LinkColor=Color.FromArgb(141,175,224),Dock=DockStyle.Fill};footer.LinkClicked+=(o,e)=>Process.Start("https://www.minecraft.net/eula");layout.Controls.Add(footer,0,8);
        Controls.Add(layout); role.SelectedIndexChanged+=(o,e)=>RoleChanged();FormClosing+=HandleClosing;RoleChanged();
        timer=new System.Windows.Forms.Timer {Interval=2500};timer.Tick+=(o,e)=>RefreshStatus();timer.Start();
        Shown+=(o,e)=> {Log("All data stays local; private keys are never exported. Minecraft is reachable only through the encrypted tunnel.");if(HasTunnel()) {networkOn=true;Log("A leftover tunnel was found. Press \"Stop / remove network\" before starting.");} if(File.Exists(Path.Combine(Core.Data,"unclean-session"))&&!Core.SupervisorAlive()) Log("The previous session ended unexpectedly. See README: recovering after a crash.");RefreshStatus();};
    }
    TextBox Input(string text) {return new TextBox {Text=text,Dock=DockStyle.Fill,BackColor=Color.FromArgb(33,40,51),ForeColor=ForeColor,BorderStyle=BorderStyle.FixedSingle};}
    void Field(TableLayoutPanel panel,string title,Control c,int col,int row) {panel.Controls.Add(new Label {Text=title,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},col,row);panel.Controls.Add(c,col+1,row);}
    Button Button(string title,Action action,bool primary=false) {var b=new Button {Text=title,AutoSize=true,Height=38,Padding=new Padding(9,5,9,5),Margin=new Padding(0,0,8,8),FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(42,123,101):Color.FromArgb(33,40,51),ForeColor=ForeColor};b.FlatAppearance.BorderSize=0;b.Click+=(o,e)=>{try{action();}catch(Exception x){Error(x);}};return b;}
    void RoleChanged() {bool host=role.SelectedIndex==0;hostNick.Enabled=host;friendNick.Enabled=host;endpoint.Enabled=host;memory.Enabled=host;eula.Enabled=host;}
    void SaveFields() {
        if(Core.SupervisorAlive()||HasTunnel()) throw new Exception("Stop the server and the network before changing the profile.");
        string newRole=role.SelectedIndex==0?"host":"client";
        if(newRole!=settings.Role && (!string.IsNullOrEmpty(settings.PeerKey)||File.Exists(Path.Combine(Core.Data,"network-journal.json")))) throw new Exception("Use a separate application folder for the other mode. Roll back the current network setup first.");
        settings.Role=newRole;settings.HostNick=hostNick.Text.Trim();settings.FriendNick=friendNick.Text.Trim();settings.Endpoint=endpoint.Text.Trim();settings.MemoryMb=(int)memory.Value;settings.Eula=eula.Checked;Core.Validate(settings,false);Core.Write(Core.Config,settings);
    }
    void Reload() {settings=Core.Load();role.SelectedIndex=settings.Role=="host"?0:1;hostNick.Text=settings.HostNick;friendNick.Text=settings.FriendNick;endpoint.Text=settings.Endpoint;eula.Checked=settings.Eula;memory.Value=settings.MemoryMb;RoleChanged();}
    void Ui(Action a) {if(IsDisposed||Disposing)return;if(InvokeRequired)BeginInvoke(a);else a();}
    void Log(string text) {Ui(()=>log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+text+Environment.NewLine));}
    void Error(Exception x) {Log(x.Message);MessageBox.Show(this,x.Message,"MinePair",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    async void Work(Action task,string success) {
        if(busy)return;busy=true;buttons.Enabled=false;role.Enabled=false;state.Text="Working…";
        try {await Task.Run(task);Log(success);Reload();}
        catch(Exception e){Error(e);}
        finally {busy=false;buttons.Enabled=true;role.Enabled=true;RefreshStatus();}
    }
    void Prepare() {SaveFields();if(!Environment.Is64BitOperatingSystem)throw new Exception("Windows x64 is required.");if(MessageBox.Show(this,"This downloads the official WireGuard and (for the host) Minecraft plus a portable Java. Installing WireGuard asks for UAC. No other network settings change in this step. Continue?","Preparation",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;Work(()=>Core.Prepare(settings,Log),"Preparation finished.");}
    void ExportInvite() {SaveFields();if(settings.Role!="host")throw new Exception("The server owner creates the invitation.");PairFile invite=Core.Invite(settings);using(var d=new SaveFileDialog {Filter="MinePair invitation|*.invite.json",FileName="MinePair.invite.json"}) if(d.ShowDialog(this)==DialogResult.OK){Core.Write(d.FileName,invite);Log("Send the invitation file to your friend. Compare the public key by voice: "+settings.PublicKey);}}
    void Import() {
        if(Core.SupervisorAlive()||HasTunnel())throw new Exception("Stop the server and the network first.");
        using(var d=new OpenFileDialog {Filter="MinePair file|*.json"})if(d.ShowDialog(this)==DialogResult.OK) {
            if(new FileInfo(d.FileName).Length>16384)throw new Exception("The file is too large.");var p=Core.Read<PairFile>(d.FileName);
            if(p.Kind=="invite") {SaveFields();if(settings.Role!="client")throw new Exception("Choose the \"I am the friend\" mode to accept an invitation.");Core.EnsureKey(settings);if(MessageBox.Show(this,"Invitation from "+p.HostNick+" to "+p.FriendNick+".\nCompare the host's public key by voice or in a trusted chat:\n"+p.PublicKey+"\n\nAccept?","Verify friend",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Core.ImportInvite(settings,p);Reload();Log("Invitation accepted. Create a \"Reply to host\" file and send it to the owner.");}
            else {if(MessageBox.Show(this,"Compare your friend's public key with them:\n"+p.PublicKey+"\n\nAdd this device?","Verify friend",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Core.AcceptReply(settings,p);Reload();Log("Friend added. Only one device key is allowed.");}
        }
    }
    void ExportReply() {if(settings.Role!="client")throw new Exception("The friend creates the reply after importing the invitation.");using(var d=new SaveFileDialog {Filter="MinePair reply|*.reply.json",FileName="MinePair.reply.json"})if(d.ShowDialog(this)==DialogResult.OK){Core.Write(d.FileName,Core.Reply(settings));Log("Send the reply to the owner and compare the public key: "+settings.PublicKey);}}
    void StartNetwork() {SaveFields();Core.Validate(settings,true);Work(()=>{if(settings.Role=="host")Core.StartServer(settings,Process.GetCurrentProcess().Id);else{Core.TunnelFile(settings);Core.Admin("start");}networkOn=true;},"The secure network is on. If this is the first start, configure the host's router.");}
    void StopNetwork() {Work(()=>{Core.StopServer();networkOn=false;},"Server stopped; our own tunnel and rules removed. Data kept.");}
    void Backup(){Work(()=>Core.Backup("manual"),"World backup saved (if the world exists yet).");}
    void Restore(){if(settings.Role!="host")throw new Exception("The world is stored on the owner's computer.");using(var d=new OpenFileDialog{Filter="Backup|*.zip",InitialDirectory=Path.Combine(Core.Data,"backups")})if(d.ShowDialog(this)==DialogResult.OK&&MessageBox.Show(this,"Replace the current world with the selected backup? A safety backup is made first.","Restore",MessageBoxButtons.YesNo)==DialogResult.Yes){string path=d.FileName;Work(()=>Core.Restore(path),"World restored. The previous state is kept in a backup.");}}
    void Uninstall(){if(MessageBox.Show(this,"This stops the server and our own tunnel and removes only MinePair's rules and its UPnP mapping. WireGuard is removed only if this program installed it and no other tunnels use it. The world and keys are kept. A manual router rule has to be removed by hand. Continue?","Remove network setup",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Work(()=>{Core.StopServer();Core.Admin("uninstall");networkOn=false;},"Network setup removed. Saves are kept.");}
    void Router() {
        if(settings.Role!="host")throw new Exception("Only the server owner configures the router.");
        string text="The host's router needs ONE rule:\n\nName: "+settings.Tunnel+"\nProtocol: UDP\nExternal and internal port: "+settings.Port+"\nInternal IP: this computer's local IPv4 (ipconfig).\n\nDo not open TCP 25565, do not enable DMZ and do not change WAN/DNS/Wi-Fi. For a manual rule, reserve this computer's local IP and note the previous DHCP setting. To undo, delete only this rule.\n\n\"Yes\" - try UPnP, only if it is already enabled. Other rules are never overwritten. Our own UPnP mapping is removed on stop.\n\"No\" - open the instructions/data.\n\"Cancel\" - change nothing.";
        var choice=MessageBox.Show(this,text,"One UDP port for the secure network",MessageBoxButtons.YesNoCancel);
        if(choice==DialogResult.Yes)Work(()=>Core.Admin("router-add"),"Own UDP mapping added and recorded for rollback.");else if(choice==DialogResult.No)Process.Start("explorer.exe",Core.Quote(Core.Root));
    }
    void Revoke(){if(settings.Role!="host")throw new Exception("Only the server owner can revoke the friend.");if(Core.SupervisorAlive()||HasTunnel())throw new Exception("Stop the server and the network first.");if(MessageBox.Show(this,"Remove the friend's allowed public key? The next start will require accepting a device reply again.","Revoke access",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;settings.PeerKey="";Core.Write(Core.Config,settings);string path=Path.Combine(Core.Data,"protected",settings.Tunnel+".conf.dpapi");if(File.Exists(path))File.Delete(path);Log("Friend key revoked. Their old tunnel can no longer reach the running server.");}
    void Probe(){Work(()=>{Core.Admin("status");var result=Core.Read<System.Collections.Generic.Dictionary<string,object>>(Path.Combine(Core.Data,"admin-result.json"));Log(Convert.ToString(result["handshake"]));Log(Core.MinecraftPing(settings.HostIp));},"Diagnostics finished.");}
    bool HasTunnel(){try{using(var s=new ServiceController("WireGuardTunnel$"+settings.Tunnel)){return s.Status==ServiceControllerStatus.Running||s.Status==ServiceControllerStatus.Stopped;}}catch{return false;}}
    void RefreshStatus(){if(busy)return;bool server=Core.SupervisorAlive();bool tunnel=HasTunnel();networkOn=tunnel;state.Text=server?"Server running · starting up or in game":tunnel?"Secure network on":"Server and secure network off";address.Text="Address in Minecraft: "+settings.HostIp+":25565  ·  "+(settings.Role=="host"?"nickname: "+settings.HostNick:"nickname: "+settings.FriendNick);details.Text=tunnel?"The tunnel exists. A running service does not prove the friend is connected.":"New world · vanilla 26.3 · device-key access";}
    async void HandleClosing(object sender,FormClosingEventArgs e){
        if(closing)return;
        if(busy){e.Cancel=true;MessageBox.Show(this,"Wait for the current operation to finish.");return;}
        if(!Core.SupervisorAlive()&&!HasTunnel())return;
        e.Cancel=true;if(MessageBox.Show(this,"Save the world, stop the server and remove our network before exiting?","Exit",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        busy=true;buttons.Enabled=false;
        try{await Task.Run(()=>Core.StopServer());closing=true;timer.Stop();Close();}catch(Exception x){Error(x);busy=false;buttons.Enabled=true;}
    }
}
}
