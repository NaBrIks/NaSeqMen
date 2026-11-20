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

        _activeIcon = CreateTrayIcon(Color.FromArgb(37, 99, 235), Color.FromArgb(52, 211, 153));
        _pausedIcon = CreateTrayIcon(Color.FromArgb(120, 113, 108), Color.FromArgb(251, 191, 36));

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

    private static Icon CreateTrayIcon(Color bgColor, Color dotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(bgColor);
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            using var pen = new Pen(Color.White, 2.2f);
            g.DrawLine(pen, 9, 16, 21, 10);
            g.DrawLine(pen, 9, 16, 21, 22);

            using var whiteBrush = new SolidBrush(Color.White);
            using var accentBrush = new SolidBrush(dotColor);
            g.FillEllipse(whiteBrush, 6, 13, 6, 6);
            g.FillEllipse(accentBrush, 19, 7, 6, 6);
            g.FillEllipse(accentBrush, 19, 19, 6, 6);
        }

        var hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        var cloned = (Icon)temp.Clone();
        DestroyIcon(hIcon);
        return cloned;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    public void Dispose()
    {
        _syncContext?.Post(_ => _appContext?.ExitThread(), null);
    }
}
