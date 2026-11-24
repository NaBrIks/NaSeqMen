using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NaSeqMen.Engine;

namespace NaSeqMen.Host;

internal sealed class TrayHost : IDisposable
{
    private readonly string _editorUrl;
    private readonly FlowEngine _engine;
    private readonly Action _requestExit;
    private Thread? _uiThread;
    private ApplicationContext? _appContext;
    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _pauseItem;
    private Icon? _activeIcon;
    private Icon? _pausedIcon;
    private SynchronizationContext? _syncContext;

    public TrayHost(string editorUrl, FlowEngine engine, Action requestExit)
    {
        _editorUrl = editorUrl;
        _engine = engine;
        _requestExit = requestExit;
    }

    public void Start()
    {
        _uiThread = new Thread(RunTrayLoop)
        {
            IsBackground = true,
            Name = "NaSeqMen.TrayIcon",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    private void RunTrayLoop()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _activeIcon = CreateNeonNTrayIcon(paused: false);
        _pausedIcon = CreateNeonNTrayIcon(paused: true);

        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem($"Open Editor ({_editorUrl})");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => OpenEditor();

        _pauseItem = new ToolStripMenuItem("Pause Engine");
        _pauseItem.Click += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                if (_engine.IsPaused) await _engine.ResumeAsync();
                else await _engine.PauseAsync();
            });
        };

        var exitItem = new ToolStripMenuItem("Exit NaSeqMen");
        exitItem.Click += (_, _) => _requestExit();

        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _engine.IsPaused ? _pausedIcon : _activeIcon,
            Text = "NaSeqMen — Running",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenEditor();

        _syncContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        _engine.PausedChanged += OnEnginePausedChanged;

        _appContext = new ApplicationContext();
        Application.Run(_appContext);

        _engine.PausedChanged -= OnEnginePausedChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private void OnEnginePausedChanged(bool paused)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon is null || _pauseItem is null)
                return;

            _notifyIcon.Icon = paused ? _pausedIcon : _activeIcon;
            _notifyIcon.Text = paused ? "NaSeqMen — Paused" : "NaSeqMen — Running";
            _pauseItem.Text = paused ? "Resume Engine" : "Pause Engine";
        }, null);
    }

    private void OpenEditor()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _editorUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore shell open errors
        }
    }

    private static Icon CreateNeonNTrayIcon(bool paused)
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using var rectPath = CreateRoundedRect(1f, 1f, 30f, 30f, 7f);
            using var bgBrush = new LinearGradientBrush(
                new PointF(0, 0),
                new PointF(32, 32),
                Color.FromArgb(255, 30, 35, 45),
                Color.FromArgb(255, 18, 22, 30));
            g.FillPath(bgBrush, rectPath);

            var borderColor = paused
                ? Color.FromArgb(255, 217, 119, 6)
                : Color.FromArgb(255, 62, 74, 96);
            using var borderPen = new Pen(borderColor, 1.4f);
            g.DrawPath(borderPen, rectPath);

            var mint = paused ? Color.FromArgb(255, 251, 191, 36) : Color.FromArgb(255, 110, 231, 183);
            var sky = paused ? Color.FromArgb(255, 245, 158, 11) : Color.FromArgb(255, 125, 211, 252);
            var lav = paused ? Color.FromArgb(255, 253, 224, 71) : Color.FromArgb(255, 196, 181, 253);
            var peach = paused ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 253, 186, 116);

            const float x1 = 9.5f, x2 = 22.5f, y1 = 8.5f, y2 = 23.5f, sw = 2.8f;

            using (var bLeft = new LinearGradientBrush(new PointF(x1, y1), new PointF(x1, y2), mint, sky))
            using (var pLeft = new Pen(bLeft, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pLeft, x1, y2, x1, y1);

            using (var bDiag = new LinearGradientBrush(new PointF(x1, y1), new PointF(x2, y2), mint, peach))
            {
                bDiag.InterpolationColors = new ColorBlend(3)
                {
                    Colors = [mint, lav, peach],
                    Positions = [0f, 0.5f, 1f],
                };
                using var pDiag = new Pen(bDiag, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawBezier(pDiag, x1, y1, 15.5f, y1, 16.5f, y2, x2, y2);
            }

            using (var bRight = new LinearGradientBrush(new PointF(x2, y1), new PointF(x2, y2), lav, peach))
            using (var pRight = new Pen(bRight, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pRight, x2, y2, x2, y1);

            using var coreBrush = new SolidBrush(Color.FromArgb(255, 22, 26, 34));
            foreach (var (cx, cy, col) in new[] { (x1, y1, mint), (x1, y2, sky), (x2, y2, peach), (x2, y1, lav) })
            {
                g.FillEllipse(coreBrush, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
                using var sp = new Pen(col, 1.6f);
                g.DrawEllipse(sp, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
            }
        }

        var hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        var cloned = (Icon)temp.Clone();
        DestroyIcon(hIcon);
        return cloned;
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        var d = r * 2f;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    public void Dispose()
    {
        _syncContext?.Post(_ => _appContext?.ExitThread(), null);
    }
}
