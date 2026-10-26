using System;
using System.Drawing;
using System.Windows.Forms;

// 切换显示模式后的确认框: 倒计时结束前没点"保留"就自动恢复, 防止切到显示器不支持的模式后没信号
class ConfirmModeForm : Form
{
    const int Seconds = 15;
    readonly Timer timer = new Timer();
    readonly Label countdown;
    int left = Seconds;
    public bool Keep;

    static int S(int v) { return Ui.S(v); }

    public ConfirmModeForm(string modeName, Icon icon)
    {
        // 分辨率/缩放刚变过, 按主显示器当前的缩放来排版
        var b = Screen.PrimaryScreen.Bounds;
        Ui.SetDpi(Ui.DpiAt(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2)));

        Text = "保留此显示模式？";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Font = Theme.Font;
        KeyPreview = true;

        int x = S(24), w = S(400), y = S(20);
        var title = Ui.Text("已切换到「" + modeName + "」", Theme.Header, Theme.Text, Theme.Bg);
        title.Bounds = new Rectangle(x, y, w, S(30));
        Controls.Add(title);
        y += S(38);

        countdown = Ui.Text("", Theme.Font, Theme.SubText, Theme.Bg);
        countdown.AutoEllipsis = false;
        countdown.TextAlign = ContentAlignment.TopLeft;
        countdown.Bounds = new Rectangle(x, y, w, S(48));
        Controls.Add(countdown);
        y += S(60);

        var revert = new RoundButton("恢复");
        revert.FitWidth(S(96));
        revert.Location = new Point(x + w - revert.Width, y);
        revert.Click += delegate { Finish(false); };
        var keep = new RoundButton("保留") { Primary = true };
        keep.FitWidth(S(96));
        keep.Location = new Point(revert.Left - S(8) - keep.Width, y);
        keep.Click += delegate { Finish(true); };
        Controls.Add(keep);
        Controls.Add(revert);
        ClientSize = new Size(w + x * 2, y + keep.Height + S(20));

        UpdateText();
        timer.Interval = 1000;
        timer.Tick += delegate
        {
            left--;
            if (left <= 0) Finish(false);
            else UpdateText();
        };
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) Finish(true);
            else if (e.KeyCode == Keys.Escape) Finish(false);
        };
    }

    void UpdateText()
    {
        countdown.Text = "画面显示正常的话点「保留」（回车）。\n" + left + " 秒后自动恢复为之前的显示模式。";
    }

    void Finish(bool keep)
    {
        timer.Stop();
        Keep = keep;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }
}

// 输入一个名字: 回车确定, Esc 取消
class NameForm : Form
{
    readonly TextField field;

    public string Result { get { return field.Box.Text.Trim(); } }

    static int S(int v) { return Ui.S(v); }

    public NameForm(string title, string prompt, string text, Icon icon)
    {
        Text = title;
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Font = Theme.Font;
        KeyPreview = true;

        int x = S(24), w = S(360), y = S(20);
        var label = Ui.Text(prompt, Theme.Font, Theme.Text, Theme.Bg);
        label.Bounds = new Rectangle(x, y, w, S(24));
        Controls.Add(label);
        y += S(32);

        field = new TextField(text, w) { Location = new Point(x, y) };
        Controls.Add(field);
        y += field.Height + S(20);

        var cancel = new RoundButton("取消");
        cancel.FitWidth(S(96));
        cancel.Location = new Point(x + w - cancel.Width, y);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        var ok = new RoundButton("保存") { Primary = true };
        ok.FitWidth(S(96));
        ok.Location = new Point(cancel.Left - S(8) - ok.Width, y);
        ok.Click += delegate { if (Result.Length > 0) DialogResult = DialogResult.OK; };
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(w + x * 2, y + ok.Height + S(20));

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (Result.Length > 0) DialogResult = DialogResult.OK; }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        field.Box.Focus();
        field.Box.SelectAll();
    }
}
