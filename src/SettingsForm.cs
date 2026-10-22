using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MinePair {

public sealed class SettingsForm : Form {
    const int W = 470;
    readonly MainForm owner;
    Settings s;
    FlowLayoutPanel page;
    bool busy;

    public SettingsForm(MainForm owner) {
        this.owner = owner;
        Text = "MinePair settings";
        ClientSize = new Size(540, 660);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(10f, FontStyle.Regular);
        page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(28, 20, 28, 20), BackColor = Ui.Bg };
        Controls.Add(page);
        Build();
    }

    void Error(Exception e) { MessageBox.Show(this, Ui.Friendly(e), "MinePair", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    bool Ask(string q) { return MessageBox.Show(this, q, "MinePair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }
    void Info(string text) { MessageBox.Show(this, text, "MinePair", MessageBoxButtons.OK, MessageBoxIcon.Information); }

    void Section(string title) { Ui.Label(page, title, 13f, Ui.Text, FontStyle.Bold, 6, W).Margin = new Padding(0, 14, 0, 6); }
    Label Note(string text) { return Ui.Label(page, text, 9.5f, Ui.Muted, FontStyle.Regular, 8, W); }
    FlowLayoutPanel Row() { return Ui.Row(page, W); }
    Button Small(Control row, string text, Action a) { return Ui.Small(row, text, a, Error); }

    bool Running() { return Core.ServerRunning() || Core.TunnelServiceExists(s); }

    void RequireStopped() {
        if (Running()) throw new UserFacingException("Stop the server or disconnect first (button on the main screen).");
    }

    async void Run(Action task, Action done) {
        if (busy) return;
        busy = true;
        page.Enabled = false;
        UseWaitCursor = true;
        try { await Task.Run(task); if (done != null) done(); }
        catch (Exception e) { Error(e); }
        finally { busy = false; if (!IsDisposed) { page.Enabled = true; UseWaitCursor = false; } }
    }

    void Build() {
        s = Core.Load();
        foreach (var c in page.Controls.Cast<Control>().ToList()) c.Dispose();
        page.Controls.Clear();
        bool host = s.IsHost();

        if (host) {
            Section("Worlds");
            var list = new ListBox { Width = W, Height = 112, BackColor = Ui.Field, ForeColor = Ui.Text, BorderStyle = BorderStyle.None, Font = Ui.F(10.5f, FontStyle.Regular), Margin = new Padding(0, 0, 0, 8), IntegralHeight = false };
            foreach (var w in Core.Worlds()) {
                var item = new WorldItem(w);
                list.Items.Add(item);
                if (w.Id == s.ActiveWorld) list.SelectedItem = item;
            }
            page.Controls.Add(list);
            Func<World> selected = () => {
                var item = list.SelectedItem as WorldItem;
                if (item == null) throw new UserFacingException("Select a world in the list.");
                return item.World;
            };
            var row = Row();
            Small(row, "New world", () => {
                RequireStopped();
                var w = WorldDialog.Create(this);
                if (w == null) return;
                s.ActiveWorld = w.Id;
                Core.Save(s);
                Build();
                Info("World \"" + w.Name + "\" created and selected. It is downloaded and generated when you press \"Play\".");
            });
            Small(row, "Edit", () => {
                RequireStopped();
                if (WorldDialog.Edit(this, selected())) Build();
            });
            Small(row, "Play this world", () => {
                RequireStopped();
                s.ActiveWorld = selected().Id;
                Core.Save(s);
                Build();
            });
            Small(row, "World folder", () => Ui.OpenFolder(Core.WorldDir(selected().Id)));

            Section("Backups");
            Note("The current world is backed up automatically before every start (the last " + Core.AutoBackupsKept + " are kept). Manual backups are never deleted.");
            row = Row();
            Small(row, "Back up now", () => {
                RequireStopped();
                var w = Core.ActiveWorld(s);
                string path = null;
                Run(() => path = Core.Backup(w, "manual"), () => Info(path == null ? "Nothing to back up yet — the world is generated on the first start." : "Backup saved."));
            });
            Small(row, "Restore from backup…", () => {
                RequireStopped();
                var w = Core.ActiveWorld(s);
                using (var d = new OpenFileDialog { Filter = "World backup|*.zip", InitialDirectory = Core.WorldBackups(w) }) {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    if (!Ask("Replace world \"" + w.Name + "\" with the selected backup?\n\nThe current state is saved as a separate backup first.")) return;
                    string file = d.FileName;
                    Run(() => Core.Restore(w, file), () => Info("World restored."));
                }
            });
            Small(row, "Backups folder", () => Ui.OpenFolder(Core.WorldBackups(Core.ActiveWorld(s))));

            Section("Server");
            Ui.Label(page, "Server memory, MB", 9.5f, Ui.Text, FontStyle.Bold, 4, W);
            var memory = new NumericUpDown { Minimum = 2048, Maximum = 8192, Increment = 512, Value = s.MemoryMb, Width = 120, BackColor = Ui.Field, ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 8) };
            page.Controls.Add(memory);
            Ui.Label(page, "Your nickname", 9.5f, Ui.Text, FontStyle.Bold, 4, W);
            var me = Ui.Input(page, s.HostNick, 240);
            Ui.Label(page, "Friend's nickname", 9.5f, Ui.Text, FontStyle.Bold, 4, W);
            var friend = Ui.Input(page, s.FriendNick, 240);
            Note("The friend's nickname must match the name they use in the game.");
            row = Row();
            Small(row, "Save", () => {
                RequireStopped();
                string a = me.Text.Trim(), b = friend.Text.Trim();
                Core.Nick(a);
                Core.Nick(b);
                if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("The nicknames must be different.");
                s.HostNick = a;
                s.FriendNick = b;
                s.MemoryMb = (int)memory.Value;
                Core.Save(s);
                Info("Saved.");
            });
        }

        Section("Friend");
        if (s.Paired()) Note("Pair safety code: " + Core.SafetyCode(s) + " — your friend must see the same one.");
        var friendRow = Row();
        if (host) {
            if (s.Paired()) Small(friendRow, "New-address code for friend", () => {
                string code = Core.UpdateCode(s);
                Clipboard.SetText(code);
                Info("Code copied. Send it to your friend — they paste it with \"Paste code from friend\".\n\n" + code);
            });
            Small(friendRow, "Replace friend's device", () => {
                RequireStopped();
                if (!Ask("Unpair your friend's current computer? To play again you will need to exchange codes again.")) return;
                s.PeerKey = "";
                Core.Save(s);
                Core.DeleteTunnelFile(s);
                Close();
            });
        } else {
            Small(friendRow, "Unpair from host", () => {
                RequireStopped();
                if (!Ask("Unpair from " + s.HostNick + "? A new invitation code is needed to play again.")) return;
                s.PeerKey = "";
                s.Endpoint = "";
                s.ReplySent = false;
                Core.Save(s);
                Close();
            });
        }

        Section("Network");
        if (host) Note("Router forwarding: UDP " + s.Port + " → " + (s.LanIp == "" ? "this computer" : s.LanIp) + ". Public IP: " + (s.Endpoint == "" ? "not detected yet" : s.Endpoint) + ".");
        var netRow = Row();
        Small(netRow, "Check connection", Diagnose);
        if (host) Small(netRow, "Router instructions", () => { Close(); owner.ShowRouterInstructions(); });

        Section("Other");
        var other = Row();
        Small(other, "Data folder", () => Ui.OpenFolder(Core.Data));
        if (host) Small(other, "Server log", () => {
            string log = Path.Combine(Core.LogsDir, "server-console.log");
            if (!File.Exists(log)) throw new UserFacingException("No log yet — the server has not been started.");
            Ui.Open(log);
        });
        Ui.Label(page, " ", 4f, Ui.Muted, FontStyle.Regular, 6, W);
        var remove = Ui.Button("Remove MinePair from Windows…", Ui.Red, 0, 36, 9.75f, Uninstall, Error);
        remove.AutoSize = true;
        remove.Padding = new Padding(10, 0, 10, 0);
        page.Controls.Add(remove);
        Note("Removes the helper, the tunnel and the firewall rules (and WireGuard, if MinePair installed it). Worlds, backups and keys stay in the data folder." + (host ? " Delete the router forwarding rule by hand." : ""));
    }

    void Diagnose() {
        var report = new StringBuilder();
        Run(() => {
            bool tunnel = Core.TunnelServiceExists(s);
            report.AppendLine(tunnel ? "✓ Secure network is on" : "✗ Secure network is off — press \"Play\" / \"Connect\"");
            if (tunnel) {
                long hs = -1;
                try { hs = Convert.ToInt64(Helper.Call("status", 10000)["handshake"]); } catch (Exception e) { report.AppendLine("✗ Helper: " + Ui.Friendly(e)); }
                report.AppendLine(hs < 0 ? "✗ No contact with your friend yet (their side is off, or UDP port " + s.Port + ")" : "✓ Friend reachable: last handshake " + hs + " s ago");
                try {
                    var ping = Core.MinecraftPing(s.HostIp, 2500);
                    report.AppendLine("✓ Minecraft " + ping.Version + " responds · players " + ping.Online + "/" + ping.Max + " · ping " + ping.Milliseconds + " ms");
                } catch { report.AppendLine("✗ Minecraft server does not respond"); }
            }
            if (s.IsHost()) {
                try {
                    string ip = Core.DetectPublicIp();
                    report.AppendLine(ip == s.Endpoint ? "✓ Public IP unchanged: " + ip : "! Public IP changed: " + ip + " — send your friend a \"New-address code\"");
                } catch { report.AppendLine("✗ Could not detect the public IP"); }
                string lan = Core.LanIp();
                report.AppendLine(lan == null ? "✗ No connection to the router found" : lan == s.LanIp || s.LanIp == "" ? "✓ Internal IP: " + lan : "! Internal IP changed to " + lan + " — update the router rule");
            }
        }, () => Info(report.ToString() + "\nA TCP ping does not show packet loss or server TPS."));
    }

    void Uninstall() {
        if (busy) return;
        if (!Ask("Remove MinePair from Windows?\n\nThe server and network are stopped; the helper, tunnel and firewall rules are removed. WireGuard is removed only if MinePair installed it and there are no other tunnels.\n\nWorlds, backups and keys are kept.")) return;
        Run(() => {
            if (Core.SupervisorAlive()) Core.StopServer();
            if (Helper.Installed()) Helper.UninstallElevated();
        }, () => {
            Info("Done. MinePair's network components were removed. You can delete the data folder by hand if you no longer need the worlds:\n" + Core.Data);
            Close();
        });
    }
}

sealed class WorldDialog : Form {
    readonly World existing;
    TextBox name, seed;
    ComboBox version, mode, difficulty;
    CheckBox hardcore, pvp;
    public World Result;

    static readonly string[] ModeNames = { "Survival", "Creative", "Adventure" };
    static readonly string[] DifficultyNames = { "Peaceful", "Easy", "Normal", "Hard" };

    WorldDialog(World w) {
        existing = w;
        Text = w == null ? "New world" : "World \"" + w.Name + "\"";
        ClientSize = new Size(420, w == null ? 520 : 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(10f, FontStyle.Regular);
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22, 18, 22, 10) };
        Controls.Add(p);
        const int width = 370;

        Ui.Label(p, "Name", 9.5f, Ui.Text, FontStyle.Bold, 4, width);
        name = Ui.Input(p, w == null ? "New world" : w.Name, width);
        if (w == null) {
            Ui.Label(p, "Minecraft version", 9.5f, Ui.Text, FontStyle.Bold, 4, width);
            version = Combo(p, new[] { "Loading list…" }, 0, width);
            version.Enabled = false;
            LoadVersions();
        }
        Ui.Label(p, "Mode", 9.5f, Ui.Text, FontStyle.Bold, 4, width);
        mode = Combo(p, ModeNames, Array.IndexOf(Core.GameModes, w == null ? "survival" : w.GameMode), width);
        Ui.Label(p, "Difficulty", 9.5f, Ui.Text, FontStyle.Bold, 4, width);
        difficulty = Combo(p, DifficultyNames, Array.IndexOf(Core.Difficulties, w == null ? "normal" : w.Difficulty), width);
        pvp = Check(p, "PvP — players can hurt each other", w == null || w.Pvp);
        if (w == null) {
            hardcore = Check(p, "Hardcore — one life (cannot be changed later)", false);
            Ui.Label(p, "Seed (optional)", 9.5f, Ui.Text, FontStyle.Bold, 4, width);
            seed = Ui.Input(p, "", width);
        }
        var row = Ui.Row(p, width);
        var ok = Ui.Button(w == null ? "Create" : "Save", Ui.Green, 140, 38, 10f, Save, e => MessageBox.Show(this, Ui.Friendly(e), "MinePair"));
        row.Controls.Add(ok);
        Ui.Small(row, "Cancel", Close, e => { });
    }

    static ComboBox Combo(Control p, string[] items, int selected, int width) {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, BackColor = Ui.Field, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 0, 12) };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Max(0, selected);
        p.Controls.Add(c);
        return c;
    }

    static CheckBox Check(Control p, string text, bool value) {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 10) };
        p.Controls.Add(c);
        return c;
    }

    void LoadVersions() {
        Task.Run(() => Core.ReleaseVersions()).ContinueWith(t => {
            if (IsDisposed) return;
            version.Items.Clear();
            if (t.IsFaulted) {
                var installed = Directory.Exists(Core.VersionsDir) ? Directory.GetDirectories(Core.VersionsDir).Select(Path.GetFileName).OrderByDescending(v => v).ToArray() : new string[0];
                if (installed.Length == 0) { version.Items.Add("No internet"); version.SelectedIndex = 0; return; }
                version.Items.AddRange(installed);
            } else version.Items.AddRange(t.Result.ToArray());
            version.SelectedIndex = 0;
            version.Enabled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Save() {
        string gameMode = Core.GameModes[mode.SelectedIndex], diff = Core.Difficulties[difficulty.SelectedIndex];
        if (existing == null) {
            if (!version.Enabled) throw new UserFacingException("Wait until the version list has loaded.");
            Result = Core.CreateWorld(name.Text, (string)version.SelectedItem, gameMode, diff, hardcore.Checked, pvp.Checked, seed.Text);
        } else {
            existing.Name = name.Text.Trim();
            existing.GameMode = gameMode;
            existing.Difficulty = diff;
            existing.Pvp = pvp.Checked;
            Core.SaveWorld(existing);
            Result = existing;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    public static World Create(IWin32Window owner) {
        using (var d = new WorldDialog(null)) return d.ShowDialog(owner) == DialogResult.OK ? d.Result : null;
    }

    public static bool Edit(IWin32Window owner, World w) {
        using (var d = new WorldDialog(w)) return d.ShowDialog(owner) == DialogResult.OK;
    }
}
}
