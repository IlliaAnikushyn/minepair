using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MinePair {

static class Program {
    [STAThread]
    static int Main(string[] args) {
        string mode = args.Length > 0 ? args[0] : "";
        try {
            switch (mode) {
                case "--self-test":
                    return SelfTests.Run();
                case "--service":
                    ServiceBase.Run(new HelperService());
                    return 0;
                case "--server-worker":
                    if (args.Length != 3) return 2;
                    Core.Supervisor(int.Parse(args[1]), args[2]);
                    return 0;
                case "--install-helper":
                    if (args.Length != 3) return 2;
                    Helper.Install(args[1], args[2]);
                    return 0;
                case "--uninstall-helper":
                    string warning = Helper.Uninstall();
                    if (warning != null) MessageBox.Show(warning, "MinePair");
                    return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (var mutex = new Mutex(true, "Local\\MinePair-" + Core.HashString(Core.Data), out first)) {
                if (!first) { MessageBox.Show("MinePair is already open.", "MinePair"); return 1; }
                if (Core.Elevated) {
                    MessageBox.Show("Open MinePair with a normal double-click, not \"Run as administrator\". The program asks Windows for permission itself when it needs it.", "MinePair");
                    return 1;
                }
                Core.MigrateLegacy(Path.Combine(Path.GetDirectoryName(Core.ExePath), ".local"));
                Application.Run(new MainForm());
            }
            return 0;
        } catch (Exception e) {
            if (mode == "--service") return 1;
            try {
                Directory.CreateDirectory(Core.Data);
                File.AppendAllText(Path.Combine(Core.Data, "application-errors.log"), DateTime.UtcNow.ToString("o") + " " + mode + " " + e + Environment.NewLine);
            } catch { }
            if (mode == "" || mode == "--install-helper" || mode == "--uninstall-helper") MessageBox.Show(e.Message, "MinePair");
            return 1;
        }
    }
}

// Shared look: dark theme, one column of content.
static class Ui {
    public static readonly Color Bg = Color.FromArgb(16, 20, 27);
    public static readonly Color Card = Color.FromArgb(26, 32, 42);
    public static readonly Color Field = Color.FromArgb(37, 45, 58);
    public static readonly Color Text = Color.FromArgb(234, 238, 245);
    public static readonly Color Muted = Color.FromArgb(150, 162, 182);
    public static readonly Color Green = Color.FromArgb(46, 160, 108);
    public static readonly Color Yellow = Color.FromArgb(226, 174, 72);
    public static readonly Color Red = Color.FromArgb(200, 76, 76);
    public static readonly Color Blue = Color.FromArgb(112, 166, 245);
    public static readonly Color BannerBg = Color.FromArgb(54, 46, 26);

    public static Font F(float size, FontStyle style) { return new Font("Segoe UI", size, style); }

    public static FlowLayoutPanel Column(int width) {
        return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
    }

    public static FlowLayoutPanel Row(Control parent, int width) {
        var r = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 6), BackColor = Color.Transparent };
        parent.Controls.Add(r);
        return r;
    }

    public static Label Label(Control parent, string text, float size, Color color, FontStyle style, int bottom, int width) {
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Font = F(size, style), ForeColor = color, Margin = new Padding(0, 0, 0, bottom), BackColor = Color.Transparent };
        parent.Controls.Add(l);
        return l;
    }

    public static Button Button(string text, Color back, int width, int height, float fontSize, Action action, Action<Exception> onError) {
        var b = new Button { Text = text, Width = width, Height = height, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Font = F(fontSize, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 10), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.2f);
        if (action != null) b.Click += (o, e) => { try { action(); } catch (Exception x) { onError(x); } };
        return b;
    }

    public static Button Small(Control parent, string text, Action action, Action<Exception> onError) {
        var b = Button(text, Field, 0, 36, 9.75f, action, onError);
        b.AutoSize = true;
        b.Padding = new Padding(10, 0, 10, 0);
        b.Font = F(9.75f, FontStyle.Regular);
        parent.Controls.Add(b);
        return b;
    }

    public static TextBox Input(Control parent, string text, int width) {
        var t = new TextBox { Text = text ?? "", Width = width, BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = F(11.5f, FontStyle.Regular), Margin = new Padding(0, 0, 0, 12) };
        parent.Controls.Add(t);
        return t;
    }

    public static TextBox CodeBox(Control parent, string text, bool readOnly, int width) {
        var t = new TextBox { Text = text ?? "", Multiline = true, WordWrap = true, ReadOnly = readOnly, Width = width, Height = 66, BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5f), Margin = new Padding(0, 0, 0, 8), ScrollBars = ScrollBars.None };
        parent.Controls.Add(t);
        return t;
    }

    public static string Friendly(Exception e) {
        if (e is AggregateException && e.InnerException != null) e = e.InnerException;
        if (e is UserFacingException) return e.Message;
        if (e is WebException) return "No internet connection, or the download site is unavailable. Check your connection and try again.\n\n(" + e.Message + ")";
        return e.Message;
    }

    public static void Copy(string text, Button button) {
        Clipboard.SetText(text);
        string old = button.Text;
        button.Text = "Copied ✓";
        var t = new System.Windows.Forms.Timer { Interval = 1600 };
        t.Tick += (o, e) => { t.Stop(); t.Dispose(); if (!button.IsDisposed) button.Text = old; };
        t.Start();
    }

    public static void Open(string target) {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public static void OpenFolder(string path) {
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", Core.Quote(path));
    }
}

sealed class WorldItem {
    public readonly World World;
    public WorldItem(World w) { World = w; }
    public override string ToString() { return World.Name + "  ·  " + World.Version; }
}

public sealed class MainForm : Form {
    const int Width0 = 520;
    Settings settings;
    readonly Panel content;
    bool busy, closing, polling, serverWasRunning;
    DateTime tunnelSince = DateTime.MinValue;
    readonly System.Windows.Forms.Timer timer;
    string screen = "";
    internal bool Offline;   // self-tests: no network or helper calls

    Label stateLabel, detailLabel, worldLabel, bannerText;
    Button playButton;
    FlowLayoutPanel banner, bannerButtons;
    TextBox bannerCode;
    Button worldPicker;

    public MainForm() {
        settings = Core.Load();
        Text = "MinePair";
        ClientSize = new Size(620, 720);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(10f, FontStyle.Regular);
        try { Icon = Icon.ExtractAssociatedIcon(Core.ExePath); } catch { }
        content = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        Controls.Add(content);
        timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (o, e) => Poll();
        FormClosing += HandleClosing;
        Shown += (o, e) => { Route(); timer.Start(); };
    }

    // ---------- plumbing ----------

    void Error(Exception e) {
        if (!(e is UserFacingException)) {
            try { File.AppendAllText(Path.Combine(Core.Data, "application-errors.log"), DateTime.UtcNow.ToString("o") + " " + e + Environment.NewLine); } catch { }
        }
        MessageBox.Show(this, Ui.Friendly(e), "MinePair", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    bool Ask(string question) {
        return MessageBox.Show(this, question, "MinePair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void OnUi(Action a) {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    async void Run(Action task, Action done, Action failed, bool lockUi) {
        if (busy) return;
        busy = true;
        if (lockUi) content.Enabled = false;
        UseWaitCursor = true;
        try {
            await Task.Run(task);
            if (done != null) done();
        } catch (Exception e) {
            if (failed != null) failed();
            Error(e);
        } finally {
            busy = false;
            if (!IsDisposed) { content.Enabled = true; UseWaitCursor = false; }
        }
    }

    void Run(Action task, Action done) { Run(task, done, null, true); }

    FlowLayoutPanel Page(string name) {
        screen = name;
        foreach (var c in content.Controls.Cast<Control>().ToList()) c.Dispose();
        content.Controls.Clear();
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(40, 30, 40, 24), BackColor = Ui.Bg };
        content.Controls.Add(page);
        return page;
    }

    Label Title(Control p, string text) { return Ui.Label(p, text, 20f, Ui.Text, FontStyle.Bold, 8, Width0); }
    Label Step(Control p, string text) { return Ui.Label(p, text.ToUpperInvariant(), 9f, Ui.Blue, FontStyle.Bold, 4, Width0); }
    Label Para(Control p, string text) { return Ui.Label(p, text, 10.5f, Ui.Muted, FontStyle.Regular, 14, Width0); }
    Label FieldName(Control p, string text) { return Ui.Label(p, text, 9.5f, Ui.Text, FontStyle.Bold, 4, Width0); }

    Button Primary(Control p, string text, Action action) {
        var b = Ui.Button(text, Ui.Green, Width0, 48, 11.5f, action, Error);
        p.Controls.Add(b);
        return b;
    }

    Button Small(Control p, string text, Action action) { return Ui.Small(p, text, action, Error); }

    void Gap(Control p, int height) { p.Controls.Add(new Panel { Width = 1, Height = height, Margin = new Padding(0), BackColor = Color.Transparent }); }

    void BackLink(Control p, Action back) {
        var l = new LinkLabel { Text = "← Back", AutoSize = true, LinkColor = Ui.Muted, ActiveLinkColor = Ui.Text, Font = Ui.F(9.75f, FontStyle.Regular), Margin = new Padding(0, 8, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline };
        l.LinkClicked += (o, e) => { try { back(); } catch (Exception x) { Error(x); } };
        p.Controls.Add(l);
    }

    Panel Card(Control p, int height) {
        var c = new Panel { Width = Width0, Height = height, BackColor = Ui.Card, Margin = new Padding(0, 0, 0, 14) };
        p.Controls.Add(c);
        return c;
    }

    static bool IsPublic(string ip) { try { return Core.PublicIp(ip); } catch { return false; } }

    // ---------- routing ----------

    void Route() {
        settings = Core.Load();
        if (settings.Role == "") ShowWelcome();
        else if (settings.IsHost() && (!NicksOk() || !settings.Eula)) ShowHostProfile();
        else if (!Ready()) ShowSetup();
        else if (!settings.Paired()) { if (settings.IsHost()) ShowInvite(); else ShowJoin(); }
        else if (!settings.IsHost() && !settings.ReplySent) ShowReply();
        else if (settings.IsHost() && !settings.RouterReady) ShowRouter();
        else ShowMain();
    }

    bool NicksOk() {
        try {
            Core.Nick(settings.HostNick);
            Core.Nick(settings.FriendNick);
            return !settings.HostNick.Equals(settings.FriendNick, StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    bool Ready() {
        if (!Helper.Installed() || !File.Exists(Core.Wg) || !Core.HasKey(settings)) return false;
        if (!settings.IsHost()) return true;
        var w = Core.GetWorld(settings.ActiveWorld);
        return w != null && w.JavaMajor > 0 && Core.FindJava(w.JavaMajor) != null && File.Exists(Core.ServerJar(w)) && IsPublic(settings.Endpoint);
    }

    void ResetRole() {
        if (settings.Paired()) return;
        settings.Role = "";
        Core.Save(settings);
        Route();
    }

    // ---------- wizard ----------

    void ShowWelcome() {
        var p = Page("welcome");
        Gap(p, 20);
        Ui.Label(p, "MinePair", 28f, Ui.Text, FontStyle.Bold, 6, Width0);
        Para(p, "Your own Minecraft server for you and a friend. A private encrypted network between just your two computers — no Radmin, Hamachi or Aternos.");
        Gap(p, 14);
        Choice(p, "I host the server", "The world lives on this computer. Your friend connects to you.", () => ChooseRole("host"));
        Choice(p, "I join a friend", "Your friend already runs MinePair and sent you a code.", () => ChooseRole("client"));
    }

    void Choice(Control p, string title, string text, Action action) {
        var card = Card(p, 92);
        card.Cursor = Cursors.Hand;
        var t = new Label { Text = title, Font = Ui.F(13f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = true, Location = new Point(22, 18), BackColor = Color.Transparent };
        var d = new Label { Text = text, Font = Ui.F(10f, FontStyle.Regular), ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(Width0 - 60, 0), Location = new Point(22, 50), BackColor = Color.Transparent };
        var arrow = new Label { Text = "→", Font = Ui.F(16f, FontStyle.Bold), ForeColor = Ui.Green, AutoSize = true, Location = new Point(Width0 - 44, 28), BackColor = Color.Transparent };
        card.Controls.AddRange(new Control[] { t, d, arrow });
        EventHandler click = (o, e) => { try { action(); } catch (Exception x) { Error(x); } };
        EventHandler enter = (o, e) => card.BackColor = Ui.Field;
        EventHandler leave = (o, e) => card.BackColor = Ui.Card;
        foreach (Control c in new Control[] { card, t, d, arrow }) { c.Click += click; c.MouseEnter += enter; c.MouseLeave += leave; c.Cursor = Cursors.Hand; }
    }

    void ChooseRole(string role) {
        settings.Role = role;
        Core.Save(settings);
        Route();
    }

    void ShowHostProfile() {
        var p = Page("profile");
        Step(p, "Step 1 of 3");
        Title(p, "Your nicknames");
        Para(p, "Enter the nicknames exactly as in your launcher. The server lets in only these two names.");
        FieldName(p, "Your nickname");
        var me = Ui.Input(p, settings.HostNick, Width0);
        FieldName(p, "Friend's nickname");
        var friend = Ui.Input(p, settings.FriendNick, Width0);
        var eula = new CheckBox { Text = "I accept the Minecraft End User License Agreement (EULA)", AutoSize = true, Checked = settings.Eula, ForeColor = Ui.Text, Font = Ui.F(10f, FontStyle.Regular), Margin = new Padding(0, 4, 0, 2) };
        p.Controls.Add(eula);
        var link = new LinkLabel { Text = "Read the EULA", AutoSize = true, LinkColor = Ui.Blue, Font = Ui.F(9.5f, FontStyle.Regular), Margin = new Padding(20, 0, 0, 18) };
        link.LinkClicked += (o, e) => Ui.Open("https://www.minecraft.net/eula");
        p.Controls.Add(link);
        Primary(p, "Next", () => {
            string a = me.Text.Trim(), b = friend.Text.Trim();
            Core.Nick(a);
            Core.Nick(b);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("The nicknames must be different.");
            if (!eula.Checked) throw new UserFacingException("You need to accept the Minecraft EULA to run your own server.");
            settings.HostNick = a;
            settings.FriendNick = b;
            settings.Eula = true;
            Core.Save(settings);
            Route();
        });
        BackLink(p, ResetRole);
    }

    void ShowSetup() {
        var p = Page("setup");
        bool host = settings.IsHost();
        Step(p, host ? "Step 2 of 3" : "Step 1 of 2");
        Title(p, "Setup");
        Para(p, host
            ? "MinePair downloads and configures everything it needs: the WireGuard secure network, Java and the Minecraft server. Windows asks for permission once — click \"Yes\"."
            : "MinePair installs the WireGuard secure network. Windows asks for permission once — click \"Yes\".");
        var names = new List<string> { "MinePair helper", "WireGuard secure network", "Encryption keys" };
        if (host) { names.Add("Java and Minecraft server"); names.Add("Public IP address"); }
        var steps = names.Select(n => Ui.Label(p, "○   " + n, 11.5f, Ui.Muted, FontStyle.Regular, 8, Width0)).ToList();
        Gap(p, 6);
        var status = Para(p, " ");
        Button start = null;
        start = Primary(p, "Start setup", () => {
            start.Enabled = false;
            start.Text = "Installing…";
            Action<int, bool> mark = (i, done) => OnUi(() => {
                steps[i].Text = (done ? "✓   " : "●   ") + names[i];
                steps[i].ForeColor = done ? Ui.Green : Ui.Text;
            });
            Action<string> progress = text => OnUi(() => status.Text = text);
            Run(() => {
                mark(0, false);
                if (!Helper.UpToDate()) { progress("Waiting for Windows permission…"); Helper.InstallElevated(); }
                mark(0, true);
                mark(1, false);
                if (!File.Exists(Core.Wg)) { progress("Downloading and installing WireGuard (1–2 minutes)…"); Helper.Call("install-wireguard", 600000); }
                mark(1, true);
                mark(2, false);
                progress("Creating keys…");
                Core.EnsureKey(settings);
                mark(2, true);
                if (host) {
                    mark(3, false);
                    var w = EnsureWorld(progress);
                    Core.PrepareWorld(w, progress);
                    mark(3, true);
                    mark(4, false);
                    progress("Detecting the public IP…");
                    settings.Endpoint = Core.DetectPublicIp();
                    if (settings.LanIp == "") settings.LanIp = Core.LanIp() ?? "";
                    Core.Save(settings);
                    mark(4, true);
                }
                progress("Done!");
            }, Route, () => { start.Enabled = true; start.Text = "Retry"; status.Text = "That did not work. You can retry — finished steps are not repeated."; }, false);
        });
        if (!settings.Paired()) BackLink(p, ResetRole);
    }

    World EnsureWorld(Action<string> progress) {
        var w = Core.GetWorld(settings.ActiveWorld) ?? Core.Worlds().FirstOrDefault();
        if (w == null) {
            progress("Looking up the latest Minecraft release…");
            w = Core.CreateWorld("My world", Core.ReleaseVersions().First(), "survival", "normal", false, true, "");
        }
        if (settings.ActiveWorld != w.Id) {
            settings.ActiveWorld = w.Id;
            Core.Save(settings);
        }
        return w;
    }

    void ShowInvite() {
        var p = Page("invite");
        Step(p, "Step 3 of 3");
        Title(p, "Invite your friend");
        Para(p, "1. Copy the code and send it to your friend in any messenger. It contains no passwords or secret keys.");
        string code = Core.InviteCode(settings);
        Ui.CodeBox(p, code, true, Width0);
        var row = Ui.Row(p, Width0);
        Button copy = null;
        copy = Small(row, "Copy code", () => Ui.Copy(code, copy));
        Small(row, "Show the program file", () => Process.Start("explorer.exe", "/select," + Core.Quote(Core.ExePath)));
        Gap(p, 8);
        Para(p, "2. Your friend starts MinePair (a single MinePair.exe file you can simply send), chooses \"I join a friend\" and pastes the code.");
        Para(p, "3. Your friend sends back a reply code — paste it here:");
        var reply = Ui.CodeBox(p, "", false, Width0);
        Gap(p, 4);
        Primary(p, "Add friend", () => {
            Core.ApplyCode(settings, reply.Text, safety => Ask("Safety code: " + safety + "\n\nAsk your friend to read the code from their screen. If it matches, click \"Yes\"."));
            Route();
        });
    }

    void ShowJoin() {
        var p = Page("join");
        Step(p, "Step 2 of 2");
        Title(p, "Paste your friend's code");
        Para(p, "The friend who hosts the server sends a code starting with MP-. Copy all of it and paste it here:");
        var box = Ui.CodeBox(p, "", false, Width0);
        Gap(p, 4);
        Primary(p, "Continue", () => {
            Core.ApplyCode(settings, box.Text, null);
            settings.ReplySent = false;
            Core.Save(settings);
            Route();
        });
        BackLink(p, ResetRole);
    }

    void ShowReply() {
        var p = Page("reply");
        Step(p, "Last step");
        Title(p, "Send the reply to your friend");
        Para(p, "Copy this reply code and send it to your friend. They paste it on their side.");
        string code = Core.ReplyCode(settings);
        Ui.CodeBox(p, code, true, Width0);
        var row = Ui.Row(p, Width0);
        Button copy = null;
        copy = Small(row, "Copy code", () => Ui.Copy(code, copy));
        Gap(p, 8);
        var card = Card(p, 96);
        card.Controls.Add(new Label { Text = "Safety code", Font = Ui.F(9.5f, FontStyle.Regular), ForeColor = Ui.Muted, AutoSize = true, Location = new Point(20, 14) });
        card.Controls.Add(new Label { Text = Core.SafetyCode(settings), Font = new Font("Consolas", 20f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = true, Location = new Point(18, 36) });
        card.Controls.Add(new Label { Text = "Your friend sees the same one.\nCompare it by voice or in chat.", Font = Ui.F(9.5f, FontStyle.Regular), ForeColor = Ui.Muted, AutoSize = true, Location = new Point(300, 34) });
        Para(p, "Your Minecraft nickname: " + settings.FriendNick + ". Join the game with exactly this name.");
        Primary(p, "My friend pasted the code — next", () => {
            settings.ReplySent = true;
            Core.Save(settings);
            Route();
        });
    }

    void ShowRouter() {
        var p = Page("router");
        string gateway;
        string lan = Core.LanIp(out gateway) ?? "this computer's IP (see ipconfig)";
        Step(p, "Almost done");
        Title(p, "Open one port on your router");
        Para(p, "For your friend to connect, the router has to forward one port to you. This is safe: only WireGuard answers on it, and only to your friend's key — to everyone else the port looks closed.");
        var card = Card(p, 104);
        card.Controls.Add(new Label { Text = "Protocol:  UDP\nPort (external and internal):  " + settings.Port + "\nComputer (internal IP):  " + lan, Font = Ui.F(11f, FontStyle.Regular), ForeColor = Ui.Text, AutoSize = true, Location = new Point(20, 16) });
        Para(p, "Open your router settings, find \"Port forwarding\" (sometimes \"Virtual server\" or \"NAT\") and add a rule with these values. Do not open TCP 25565 and do not enable DMZ. It also helps to reserve this computer's IP in the DHCP settings.");
        var row = Ui.Row(p, Width0);
        if (gateway != null) Small(row, "Open router", () => Ui.Open("http://" + gateway + "/"));
        Small(row, "Try automatically (UPnP)", () => Run(() => Helper.Call("router-add", 120000), () => {
            MessageBox.Show(this, "Done: the router opened the port.", "MinePair");
            MarkRouterReady();
        }));
        Gap(p, 8);
        Primary(p, "Done, the port is open", MarkRouterReady);
    }

    void MarkRouterReady() {
        settings.RouterReady = true;
        settings.LanIp = Core.LanIp() ?? settings.LanIp;
        Core.Save(settings);
        Route();
    }

    // ---------- main screen ----------

    void ShowMain() {
        var p = Page("main");
        bool host = settings.IsHost();
        Ui.Label(p, "MinePair", 20f, Ui.Text, FontStyle.Bold, 2, Width0);
        worldLabel = Ui.Label(p, host ? "" : "Connecting to the server of " + settings.HostNick, 10f, Ui.Muted, FontStyle.Regular, 16, Width0);

        var status = Card(p, 124);
        stateLabel = new Label { Font = Ui.F(15f, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16), ForeColor = Ui.Muted };
        detailLabel = new Label { Font = Ui.F(10.25f, FontStyle.Regular), AutoSize = false, Location = new Point(20, 52), Size = new Size(Width0 - 40, 64), ForeColor = Ui.Muted };
        status.Controls.Add(stateLabel);
        status.Controls.Add(detailLabel);

        playButton = Ui.Button("", Ui.Green, Width0, 60, 14f, PlayClicked, Error);
        p.Controls.Add(playButton);

        var address = Card(p, 112);
        address.Controls.Add(new Label { Text = "Address in Minecraft: Multiplayer → Direct Connection", Font = Ui.F(9.5f, FontStyle.Regular), ForeColor = Ui.Muted, AutoSize = true, Location = new Point(20, 14) });
        address.Controls.Add(new Label { Text = settings.HostIp, Font = new Font("Consolas", 19f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = true, Location = new Point(17, 36) });
        address.Controls.Add(new Label { Text = "Your nickname: " + (host ? settings.HostNick : settings.FriendNick), Font = Ui.F(9.5f, FontStyle.Regular), ForeColor = Ui.Muted, AutoSize = true, Location = new Point(20, 80) });
        var copy = Ui.Button("Copy", Ui.Field, 140, 36, 9.75f, null, Error);
        copy.Click += (o, e) => Ui.Copy(settings.HostIp, copy);
        copy.Location = new Point(Width0 - 160, 38);
        address.Controls.Add(copy);

        banner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = Width0, BackColor = Ui.BannerBg, Padding = new Padding(16, 14, 16, 8), Margin = new Padding(0, 0, 0, 14), Visible = false };
        bannerText = Ui.Label(banner, "", 10f, Ui.Text, FontStyle.Regular, 8, Width0 - 32);
        bannerCode = Ui.CodeBox(banner, "", true, Width0 - 32);
        bannerButtons = Ui.Row(banner, Width0 - 32);
        p.Controls.Add(banner);

        var bottom = Ui.Row(p, Width0);
        if (host) {
            worldPicker = Small(bottom, "Switch world ▾", () => {
                if (Core.SupervisorAlive()) throw new UserFacingException("Stop the server first.");
                var menu = new ContextMenuStrip { ShowImageMargin = false, Font = Ui.F(10f, FontStyle.Regular) };
                foreach (var w in Core.Worlds()) {
                    var world = w;
                    var item = new ToolStripMenuItem(new WorldItem(w).ToString()) { Checked = w.Id == settings.ActiveWorld };
                    item.Click += (o, e) => { settings.ActiveWorld = world.Id; Core.Save(settings); UpdateWorldLabel(); };
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("New world or world settings…", null, (o, e) => OpenSettings());
                menu.Show(worldPicker, new Point(0, worldPicker.Height));
            });
        } else {
            Small(bottom, "Paste code from friend", PasteCode);
        }
        Small(bottom, "Settings", OpenSettings);

        UpdateWorldLabel();
        Apply(new Status { Server = Core.SupervisorAlive(), Tunnel = Core.TunnelServiceExists(settings) });
        if (Offline) return;
        if (host) { CheckEndpoint(); CheckLan(); }
        Poll();
    }

    void UpdateWorldLabel() {
        if (!settings.IsHost() || worldLabel == null) return;
        var w = Core.GetWorld(settings.ActiveWorld);
        worldLabel.Text = w == null ? "No world selected" : "World \"" + w.Name + "\" · Minecraft " + w.Version + " · " + ModeName(w.GameMode) + (w.Hardcore ? " · hardcore" : "");
    }

    public static string ModeName(string mode) {
        return mode == "creative" ? "creative" : mode == "adventure" ? "adventure" : "survival";
    }

    void ShowBanner(string text, string code, params KeyValuePair<string, Action>[] buttons) {
        if (banner == null || banner.IsDisposed) return;
        bannerText.Text = text;
        bannerCode.Text = code ?? "";
        bannerCode.Visible = code != null;
        foreach (var c in bannerButtons.Controls.Cast<Control>().ToList()) c.Dispose();
        bannerButtons.Controls.Clear();
        if (code != null) {
            Button copy = null;
            copy = Small(bannerButtons, "Copy code", () => Ui.Copy(code, copy));
        }
        foreach (var b in buttons) Small(bannerButtons, b.Key, b.Value);
        Small(bannerButtons, "Hide", () => banner.Visible = false);
        banner.Visible = true;
    }

    static KeyValuePair<string, Action> B(string text, Action a) { return new KeyValuePair<string, Action>(text, a); }

    // Host: notice a changed public IP and give the friend a one-line update code.
    void CheckEndpoint() {
        if (!settings.Paired()) return;
        Task.Run(() => Core.DetectPublicIp()).ContinueWith(t => {
            if (t.IsFaulted || IsDisposed || screen != "main") return;
            if (t.Result == settings.Endpoint) return;
            settings.Endpoint = t.Result;
            Core.Save(settings);
            ShowEndpointBanner();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ShowEndpointBanner() {
        ShowBanner("Your public IP address changed (common with home internet). Send this code to your friend — they paste it with \"Paste code from friend\". There is no need to pair again.", Core.UpdateCode(settings));
    }

    void CheckLan() {
        string lan = Core.LanIp();
        if (lan == null) return;
        if (settings.LanIp == "") { settings.LanIp = lan; Core.Save(settings); return; }
        if (lan == settings.LanIp) return;
        string gateway;
        Core.LanIp(out gateway);
        ShowBanner("This computer's internal IP changed: it was " + settings.LanIp + ", now it is " + lan + ". Update the UDP " + settings.Port + " forwarding rule on the router to the new address, otherwise your friend cannot connect.", null,
            B("Open router", () => { if (gateway != null) Ui.Open("http://" + gateway + "/"); }),
            B("I updated the rule", () => { settings.LanIp = lan; Core.Save(settings); banner.Visible = false; }));
    }

    sealed class Status {
        public bool Server, Tunnel;
        public long Handshake = -2;
        public Core.ServerPing Ping;
    }

    Status Collect() {
        var st = new Status { Server = Core.SupervisorAlive(), Tunnel = Core.TunnelServiceExists(settings) };
        if (st.Tunnel) {
            try { st.Handshake = Convert.ToInt64(Helper.Call("status", 8000)["handshake"]); } catch { }
            try { st.Ping = Core.MinecraftPing(settings.HostIp, 1500); } catch { }
        }
        return st;
    }

    async void Poll() {
        if (Offline || polling || busy || screen != "main") return;
        polling = true;
        try {
            var st = await Task.Run(() => Collect());
            if (!busy && screen == "main" && !IsDisposed) Apply(st);
        } catch { } finally { polling = false; }
    }

    static string FriendText(long handshake) {
        if (handshake == -2) return "";
        if (handshake < 0) return "friend has not connected yet";
        return handshake <= 180 ? "friend online" : "friend offline";
    }

    void SetState(string state, Color color, string detail, string button, Color buttonColor) {
        stateLabel.Text = state;
        stateLabel.ForeColor = color;
        detailLabel.Text = detail;
        playButton.Text = button;
        playButton.BackColor = buttonColor;
        playButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(buttonColor, 0.2f);
    }

    void Apply(Status st) {
        if (stateLabel == null || stateLabel.IsDisposed) return;
        if (settings.IsHost()) {
            if (worldPicker != null) worldPicker.Enabled = !st.Server;
            if (serverWasRunning && !st.Server && st.Tunnel) {
                serverWasRunning = false;
                SetState("Server stopped", Ui.Yellow, "The server exited on its own. Turning off the secure network… Details are in the log (Settings → Server log).", "▶   Play", Ui.Green);
                Run(() => Helper.Call("stop", 120000), null, null, false);
                return;
            }
            serverWasRunning = st.Server;
            if (!st.Server)
                SetState("Server off", Ui.Muted, "Press \"Play\" — the world starts and your friend can press \"Connect\" on their side.", "▶   Play", Ui.Green);
            else if (st.Ping == null)
                SetState("Starting…", Ui.Yellow, "Loading the world. The first start of a new world takes 1–3 minutes.", "■   Stop", Ui.Red);
            else
                SetState("Server running", Ui.Green, "Players: " + st.Ping.Online + " of " + st.Ping.Max + "  ·  " + FriendText(st.Handshake) + "\nJoin in Minecraft using the address below.", "■   Stop", Ui.Red);
            return;
        }
        if (!st.Tunnel) {
            tunnelSince = DateTime.MinValue;
            SetState("Not connected", Ui.Muted, "When your friend presses \"Play\", press \"Connect\".", "Connect", Ui.Green);
            return;
        }
        if (tunnelSince == DateTime.MinValue) tunnelSince = DateTime.Now;
        if (st.Ping != null)
            SetState("Connected", Ui.Green, "Friend's server is running  ·  ping " + st.Ping.Milliseconds + " ms  ·  players " + st.Ping.Online + " of " + st.Ping.Max + "\nJoin in Minecraft using the address below.", "Disconnect", Ui.Red);
        else if (st.Handshake >= 0 && st.Handshake <= 180)
            SetState("Connected to friend", Ui.Yellow, "But the friend's server is not started yet or still loading. Wait, or ask them to press \"Play\".", "Disconnect", Ui.Red);
        else
            SetState("Waiting for friend…", Ui.Yellow, "Your friend needs to press \"Play\"." + (DateTime.Now - tunnelSince > TimeSpan.FromSeconds(60)
                ? " No connection for a while? Your friend's address may have changed — ask for a new code and paste it with the button below." : ""), "Disconnect", Ui.Red);
    }

    // Runs on a worker thread: refreshes the helper after an update and installs WireGuard if it disappeared.
    void EnsureHelper() {
        if (!Helper.UpToDate()) {
            OnUi(() => detailLabel.Text = "The program was updated — Windows asks for permission once to update the helper.");
            Helper.InstallElevated();
        }
        if (!File.Exists(Core.Wg)) {
            OnUi(() => detailLabel.Text = "Installing WireGuard…");
            Helper.Call("install-wireguard", 600000);
        }
    }

    void PlayClicked() {
        if (settings.IsHost()) {
            if (Core.SupervisorAlive()) {
                SetState("Stopping…", Ui.Yellow, "Saving the world. This takes a few seconds.", "■   Stop", Ui.Red);
                Run(Core.StopServer, Poll);
                return;
            }
            if (Core.OrphanServer()) {
                if (!Ask("A Minecraft server is still running after the program crashed. End it?\n\nChanges from the last few minutes may be lost.")) return;
                Core.KillOrphanServer();
            }
            bool unclean = Core.UncleanSession();
            if (unclean && !Ask("The last session ended unexpectedly (for example, the computer turned off).\n\nThe world is usually fine — Minecraft saves it every few minutes. A backup is made before starting. Continue?")) return;
            int pid = Process.GetCurrentProcess().Id;
            bool endpointChanged = false;
            SetState("Starting…", Ui.Yellow, "Checking files and turning on the secure network…", "■   Stop", Ui.Red);
            Run(() => {
                if (unclean) Core.ClearUncleanSession(settings);
                EnsureHelper();
                var w = Core.ActiveWorld(settings);
                Core.PrepareWorld(w, text => OnUi(() => detailLabel.Text = text));
                try {
                    string ip = Core.DetectPublicIp();
                    if (ip != settings.Endpoint) { settings.Endpoint = ip; Core.Save(settings); endpointChanged = true; }
                } catch { }
                OnUi(() => detailLabel.Text = "Turning on the secure network and starting the server…");
                Core.StartServer(settings, pid);
            }, () => {
                serverWasRunning = true;
                if (endpointChanged) ShowEndpointBanner();
                CheckLan();
                Poll();
            }, () => Apply(new Status { Server = Core.SupervisorAlive(), Tunnel = Core.TunnelServiceExists(settings) }), true);
            return;
        }
        if (Core.TunnelServiceExists(settings)) {
            Run(() => Helper.Call("stop", 120000), Poll);
            return;
        }
        SetState("Connecting…", Ui.Yellow, "Turning on the secure network…", "Disconnect", Ui.Red);
        Run(() => {
            EnsureHelper();
            Core.StartTunnel(settings, 180000);
        }, () => { tunnelSince = DateTime.Now; Poll(); }, () => Apply(new Status { Tunnel = Core.TunnelServiceExists(settings) }), true);
    }

    void PasteCode() {
        string code = CodeDialog.Ask(this, "Code from friend", "Paste the code your friend sent (it starts with MP-):");
        if (code == null) return;
        string kind = Core.ApplyCode(settings, code, null);
        bool reconnect = Core.TunnelServiceExists(settings);
        if (!reconnect) { MessageBox.Show(this, "Friend's address updated.", "MinePair"); return; }
        Run(() => {
            Helper.Call("stop", 120000);
            Core.StartTunnel(settings, 180000);
        }, () => { tunnelSince = DateTime.Now; MessageBox.Show(this, kind == "update" ? "Friend's address updated, reconnecting." : "Code accepted, reconnecting.", "MinePair"); Poll(); });
    }

    void OpenSettings() {
        using (var f = new SettingsForm(this)) f.ShowDialog(this);
        settings = Core.Load();
        if (screen == "router") return;
        if (screen == "main" && settings.Paired() && (settings.IsHost() ? settings.RouterReady : settings.ReplySent)) {
            UpdateWorldLabel();
            Poll();
        } else Route();
    }

    internal Settings CurrentSettings { get { return settings; } }
    internal bool Busy { get { return busy; } }
    internal void ShowRouterInstructions() { ShowRouter(); }

    void HandleClosing(object sender, FormClosingEventArgs e) {
        if (closing || Offline) return;
        if (e.CloseReason == CloseReason.WindowsShutDown) {
            // Windows is shutting down: ask the server to save, give it a little time, drop the tunnel.
            Core.RequestStop();
            var sw = Stopwatch.StartNew();
            while (Core.SupervisorAlive() && sw.Elapsed.TotalSeconds < 20) Thread.Sleep(250);
            try { Helper.Call("stop", 15000); } catch { }
            return;
        }
        if (busy) { e.Cancel = true; MessageBox.Show(this, "Wait until the current action finishes.", "MinePair"); return; }
        bool server = Core.SupervisorAlive(), tunnel = Core.TunnelServiceExists(settings);
        if (!server && !tunnel) return;
        e.Cancel = true;
        if (!Ask(server ? "The server is running. Save the world, stop the server and exit?" : "Disconnect from your friend and exit?")) return;
        if (screen == "main") SetState("Stopping…", Ui.Yellow, "Saving the world and turning off the secure network…", playButton.Text, Ui.Red);
        Run(() => { if (server) Core.StopServer(); else Helper.Call("stop", 120000); }, () => { closing = true; timer.Stop(); Close(); });
    }

    // ---------- self-test hooks ----------

    internal void ShowForTest(string name) {
        Offline = true;
        settings = Core.Load();
        switch (name) {
            case "welcome": ShowWelcome(); break;
            case "profile": ShowHostProfile(); break;
            case "setup": ShowSetup(); break;
            case "invite": ShowInvite(); break;
            case "join": ShowJoin(); break;
            case "reply": ShowReply(); break;
            case "router": ShowRouter(); break;
            default: ShowMain(); break;
        }
    }
}

// Small modal asking for a pasted code.
sealed class CodeDialog : Form {
    TextBox box;

    CodeDialog(string title, string prompt) {
        Text = title;
        ClientSize = new Size(500, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(10f, FontStyle.Regular);
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 18, 20, 10) };
        Controls.Add(p);
        Ui.Label(p, prompt, 10f, Ui.Text, FontStyle.Regular, 8, 460);
        box = Ui.CodeBox(p, "", false, 460);
        var row = Ui.Row(p, 460);
        var ok = Ui.Button("OK", Ui.Green, 120, 36, 10f, () => { DialogResult = DialogResult.OK; Close(); }, e => { });
        row.Controls.Add(ok);
        Ui.Small(row, "Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, e => { });
        AcceptButton = ok;
    }

    public static string Ask(IWin32Window owner, string title, string prompt) {
        using (var d = new CodeDialog(title, prompt)) {
            if (d.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(d.box.Text)) return null;
            return d.box.Text;
        }
    }
}
}
