using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipShelf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ClipboardTrayApp());
    }
}

internal sealed class ClipboardTrayApp : ApplicationContext
{
    private readonly ClipboardWatcher _watcher;
    private readonly HistoryStore _store = new();
    private readonly NotifyIcon _tray;
    private HistoryForm? _historyForm;
    private SettingsForm? _settingsForm;
    private bool _paused;

    public ClipboardTrayApp()
    {
        _watcher = new ClipboardWatcher(CaptureClipboard, ShowHistory);
        _tray = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "ClipShelf",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _tray.DoubleClick += (_, _) => ShowHistory();
        _watcher.Show();
        RegisterShortcut();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open history", null, (_, _) => ShowHistory());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Pause capture", null, (_, _) => TogglePause());
        menu.Items.Add("Clear history", null, (_, _) => ClearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _tray.ContextMenuStrip!.Items[2].Text = _paused ? "Resume capture" : "Pause capture";
        _tray.Text = _paused ? "ClipShelf - paused" : "ClipShelf";
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_store);
            _settingsForm.FormClosed += (_, _) =>
            {
                _settingsForm = null;
                _historyForm?.RefreshItems();
                RegisterShortcut();
            };
            _settingsForm.Show(_watcher);
        }
        else if (!_settingsForm.Visible)
        {
            _settingsForm.Show();
        }

        if (_historyForm is not null && !_historyForm.IsDisposed && _historyForm.Visible)
        {
            _settingsForm.CenterOver(_historyForm);
        }

        _settingsForm.HideFromAltTab();
        _settingsForm.Activate();
    }

    private void ClearHistory()
    {
        if (MessageBox.Show("Clear all saved clipboard items?", "ClipShelf", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _store.Clear();
        _historyForm?.RefreshItems();
    }

    private void CaptureClipboard()
    {
        if (_paused)
        {
            return;
        }

        var entry = ClipboardEntryReader.TryRead();
        if (entry is null || _store.IsDuplicate(entry))
        {
            return;
        }

        _store.Add(entry);
        _historyForm?.RefreshItems();
    }

    private void ShowHistory()
    {
        if (_historyForm is null || _historyForm.IsDisposed)
        {
            _historyForm = new HistoryForm(_store, ShowSettings);
            _historyForm.FormClosed += (_, _) => _historyForm = null;
            _historyForm.Show(_watcher);
        }
        else if (!_historyForm.Visible)
        {
            _historyForm.Show();
        }

        _historyForm.HideFromAltTab();
        _historyForm.WindowState = FormWindowState.Normal;
        _historyForm.Activate();
    }

    private void Exit()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _watcher.Dispose();
        _settingsForm?.Close();
        _historyForm?.Close();
        ExitThread();
    }

    private void RegisterShortcut()
    {
        if (!_watcher.RegisterShortcut(_store.OpenShortcut))
        {
            _tray.ShowBalloonTip(2500, "ClipShelf", $"Shortcut {_store.OpenShortcut} is already in use.", ToolTipIcon.Info);
        }
    }
}

internal sealed class ClipboardWatcher : Form
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4353;
    private readonly Action _onChanged;
    private readonly Action _onHotkey;
    private bool _hotkeyRegistered;

    public ClipboardWatcher(Action onChanged, Action onHotkey)
    {
        _onChanged = onChanged;
        _onHotkey = onHotkey;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = new Size(0, 0);
        AddClipboardFormatListener(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            var createParams = base.CreateParams;
            createParams.ExStyle |= wsExToolWindow;
            return createParams;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            BeginInvoke(_onChanged);
        }
        else if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            BeginInvoke(_onHotkey);
        }

        base.WndProc(ref m);
    }

    public bool RegisterShortcut(string shortcut)
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (!HotkeyParser.TryParse(shortcut, out var modifiers, out var key))
        {
            return false;
        }

        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, modifiers, (uint)key);
        return _hotkeyRegistered;
    }

    protected override void Dispose(bool disposing)
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
        }

        RemoveClipboardFormatListener(Handle);
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

internal static class ClipboardEntryReader
{
    public static ClipboardEntry? TryRead()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                return ClipboardEntry.FromText(text);
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                return files.Count == 0 ? null : ClipboardEntry.FromFiles(files);
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null)
                {
                    return null;
                }

                var path = HistoryStore.CreateImagePath();
                image.Save(path, ImageFormat.Png);
                image.Dispose();
                return ClipboardEntry.FromImage(path);
            }
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }
}

internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _storeDir;
    private readonly string _historyPath;
    private readonly string _settingsPath;
    private readonly List<ClipboardEntry> _entries;
    private AppSettings _settings;

    public HistoryStore()
    {
        _storeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipShelf");
        _historyPath = Path.Combine(_storeDir, "history.json");
        _settingsPath = Path.Combine(_storeDir, "settings.json");
        Directory.CreateDirectory(_storeDir);
        Directory.CreateDirectory(Path.Combine(_storeDir, "images"));
        _settings = LoadSettings();
        _entries = Load();
        Trim();
        Save();
    }

    public IReadOnlyList<ClipboardEntry> Entries => _entries;

    public int MaxEntries => _settings.MaxEntries;

    public string OpenShortcut => _settings.OpenShortcut;

    public bool StartWithWindows => _settings.StartWithWindows;

    public static string CreateImagePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipShelf", "images");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.png");
    }

    public bool IsDuplicate(ClipboardEntry entry)
    {
        var latest = _entries.FirstOrDefault();
        return latest is not null && latest.Kind == entry.Kind && latest.Fingerprint == entry.Fingerprint;
    }

    public void Add(ClipboardEntry entry)
    {
        _entries.Insert(0, entry);
        Trim();
        Save();
    }

    public void Delete(ClipboardEntry entry)
    {
        _entries.RemoveAll(item => item.Id == entry.Id);
        DeleteImage(entry);
        Save();
    }

    public void Clear()
    {
        foreach (var entry in _entries)
        {
            DeleteImage(entry);
        }

        _entries.Clear();
        Save();
    }

    public void SetMaxEntries(int maxEntries)
    {
        _settings = _settings with { MaxEntries = Math.Clamp(maxEntries, 10, 5000) };
        SaveSettings();
        Trim();
        Save();
    }

    public void SetOpenShortcut(string shortcut)
    {
        _settings = _settings with { OpenShortcut = shortcut };
        SaveSettings();
    }

    public void SetStartWithWindows(bool enabled)
    {
        _settings = _settings with { StartWithWindows = enabled };
        StartupManager.SetEnabled(enabled);
        SaveSettings();
    }

    private List<ClipboardEntry> Load()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(_historyPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        File.WriteAllText(_historyPath, JsonSerializer.Serialize(_entries, JsonOptions));
    }

    private AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveSettings()
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonOptions));
    }

    private void Trim()
    {
        while (_entries.Count > _settings.MaxEntries)
        {
            var last = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            DeleteImage(last);
        }
    }

    private static void DeleteImage(ClipboardEntry entry)
    {
        if (entry.Kind == ClipboardKind.Image && entry.ImagePath is not null && File.Exists(entry.ImagePath))
        {
            try
            {
                File.Delete(entry.ImagePath);
            }
            catch
            {
                // A locked image cache should not block clipboard capture.
            }
        }
    }
}

internal sealed record AppSettings
{
    public int MaxEntries { get; init; } = 5000;
    public string OpenShortcut { get; init; } = "Ctrl + Alt + V";
    public bool StartWithWindows { get; init; }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ClipShelf";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}

internal static class HotkeyParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static bool TryParse(string shortcut, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;

        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
            }
            else if (Enum.TryParse(part, true, out Keys parsedKey))
            {
                key = parsedKey;
            }
        }

        return modifiers != 0 && IsUsableKey(key);
    }

    public static string FromKeyEvent(KeyEventArgs e)
    {
        var key = e.KeyCode;
        if (!IsUsableKey(key))
        {
            return "";
        }

        var parts = new List<string>();
        if (e.Control)
        {
            parts.Add("Ctrl");
        }

        if (e.Alt)
        {
            parts.Add("Alt");
        }

        if (e.Shift)
        {
            parts.Add("Shift");
        }

        if ((e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin)
        {
            parts.Add("Win");
        }

        if (parts.Count == 0)
        {
            return "";
        }

        parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }

    private static bool IsUsableKey(Keys key)
    {
        return key is not Keys.None
            and not Keys.ControlKey
            and not Keys.Menu
            and not Keys.ShiftKey
            and not Keys.LWin
            and not Keys.RWin
            and not Keys.Control
            and not Keys.Shift
            and not Keys.Alt;
    }
}

internal sealed class SettingsForm : Form
{
    private readonly HistoryStore _store;
    private readonly TextBox _maxEntries = new();
    private readonly TextBox _shortcut = new();
    private readonly CheckBox _startup = new();
    private readonly ModernButton _save = new();
    private readonly ModernButton _cancel = new();
    private readonly ModernButton _close = new();
    private readonly ModernButton _minimize = new();

    public SettingsForm(HistoryStore store)
    {
        _store = store;
        Text = "ClipShelf Settings";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(460, 350);
        MinimumSize = new Size(420, 330);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = UiFonts.Create(9.5f);
        Padding = new Padding(1);

        BuildLayout();
        ApplyRoundedCorners();
    }

    public void CenterOver(Form owner)
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(
            owner.Left + (owner.Width - Width) / 2,
            owner.Top + (owner.Height - Height) / 2);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExAppWindow = 0x00040000;
            var createParams = base.CreateParams;
            createParams.ExStyle |= wsExToolWindow;
            createParams.ExStyle &= ~wsExAppWindow;
            return createParams;
        }
    }

    public void HideFromAltTab()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        const int gwlExStyle = -20;
        const int wsExToolWindow = 0x00000080;
        const int wsExAppWindow = 0x00040000;
        var style = GetWindowLong(Handle, gwlExStyle);
        style &= ~wsExAppWindow;
        style |= wsExToolWindow;
        SetWindowLong(Handle, gwlExStyle, style);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        HideFromAltTab();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        HideFromAltTab();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedCorners();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 0, 14, 14),
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Theme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var header = BuildHeader();

        var panel = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            BorderColor = Theme.Border,
            CornerRadius = 12,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 2, 0, 8)
        };

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Theme.Panel
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));

        var label = new Label
        {
            Text = "Saved items",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = UiFonts.Create(10f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var inputHost = BuildNumberInput();
        var shortcutHost = BuildShortcutInput();

        _maxEntries.Text = Math.Clamp(_store.MaxEntries, 10, 5000).ToString();
        _maxEntries.BackColor = Theme.Panel;
        _maxEntries.ForeColor = Theme.Text;
        _maxEntries.BorderStyle = BorderStyle.None;
        _maxEntries.Font = UiFonts.Create(10f);
        _maxEntries.Margin = Padding.Empty;
        _maxEntries.TextAlign = HorizontalAlignment.Right;
        _maxEntries.KeyPress += (_, e) =>
        {
            e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
        };

        var shortcutLabel = new Label
        {
            Text = "Open shortcut",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = UiFonts.Create(10f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _shortcut.Text = _store.OpenShortcut;
        _shortcut.ReadOnly = true;
        _shortcut.BackColor = Theme.Panel;
        _shortcut.ForeColor = Theme.Text;
        _shortcut.BorderStyle = BorderStyle.None;
        _shortcut.Font = UiFonts.Create(10f);
        _shortcut.Margin = Padding.Empty;
        _shortcut.TextAlign = HorizontalAlignment.Right;
        _shortcut.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            var shortcut = HotkeyParser.FromKeyEvent(e);
            if (!string.IsNullOrWhiteSpace(shortcut))
            {
                _shortcut.Text = shortcut;
            }
        };

        var startupLabel = new Label
        {
            Text = "Start with Windows",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = UiFonts.Create(10f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _startup.Checked = _store.StartWithWindows;
        _startup.Dock = DockStyle.Fill;
        _startup.CheckAlign = ContentAlignment.MiddleRight;
        _startup.BackColor = Theme.Panel;
        _startup.ForeColor = Theme.Text;
        _startup.FlatStyle = FlatStyle.Flat;

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(inputHost, 1, 0);
        row.Controls.Add(shortcutLabel, 0, 1);
        row.Controls.Add(shortcutHost, 1, 1);
        row.Controls.Add(startupLabel, 0, 2);
        row.Controls.Add(_startup, 1, 2);
        panel.Controls.Add(row);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Background,
            WrapContents = false
        };
        StyleSettingsButton(_save, "Save");
        StyleSettingsButton(_cancel, "Cancel");
        _save.Click += (_, _) =>
        {
            if (!int.TryParse(_maxEntries.Text.Trim(), out var value))
            {
                MessageBox.Show("Enter a number between 10 and 5000.", "ClipShelf", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _store.SetMaxEntries(value);
            if (!HotkeyParser.TryParse(_shortcut.Text.Trim(), out _, out _))
            {
                MessageBox.Show("Press a shortcut like Ctrl + Alt + V.", "ClipShelf", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _store.SetOpenShortcut(_shortcut.Text.Trim());
            _store.SetStartWithWindows(_startup.Checked);
            Close();
        };
        _cancel.Click += (_, _) => Close();
        actions.Controls.AddRange([_save, _cancel]);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(panel, 0, 1);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background }, 0, 2);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
    }

    private Control BuildNumberInput()
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            BorderColor = Theme.Border,
            CornerRadius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 8, 0, 8)
        };
        host.Controls.Add(_maxEntries);
        void PositionInput()
        {
            _maxEntries.Width = Math.Max(0, host.ClientSize.Width - host.Padding.Horizontal);
            _maxEntries.Location = new Point(host.Padding.Left, Math.Max(0, (host.ClientSize.Height - _maxEntries.Height) / 2));
        }

        host.Resize += (_, _) => PositionInput();
        host.HandleCreated += (_, _) => PositionInput();
        return host;
    }

    private Control BuildShortcutInput()
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            BorderColor = Theme.Border,
            CornerRadius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 8, 0, 8)
        };
        host.Controls.Add(_shortcut);
        host.Click += (_, _) => _shortcut.Focus();

        void PositionInput()
        {
            _shortcut.Width = Math.Max(0, host.ClientSize.Width - host.Padding.Horizontal);
            _shortcut.Location = new Point(host.Padding.Left, Math.Max(0, (host.ClientSize.Height - _shortcut.Height) / 2));
        }

        host.Resize += (_, _) => PositionInput();
        host.HandleCreated += (_, _) => PositionInput();
        return host;
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Cursor = Cursors.SizeAll
        };
        header.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
            }
        };

        var title = new Label
        {
            Text = "Settings",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = UiFonts.Create(11f, FontStyle.Bold),
            Location = new Point(0, 14)
        };

        StyleChromeButton(_close, Color.FromArgb(239, 68, 68));
        StyleChromeButton(_minimize, Color.FromArgb(245, 158, 11));
        _close.Click += (_, _) => Close();
        _minimize.Click += (_, _) => Hide();

        header.Controls.Add(title);
        header.Controls.Add(_close);
        header.Controls.Add(_minimize);
        header.Resize += (_, _) =>
        {
            _close.Location = new Point(header.Width - _close.Width, 14);
            _minimize.Location = new Point(_close.Left - _minimize.Width - 9, 14);
        };

        return header;
    }

    private static void StyleSettingsButton(ModernButton button, string text)
    {
        button.Text = text;
        button.Width = 92;
        button.Height = 34;
        button.BackColor = Theme.Button;
        button.ForeColor = Theme.Text;
        button.Font = UiFonts.Create(9.5f);
        button.Margin = new Padding(8, 8, 0, 0);
    }

    private static void StyleChromeButton(ModernButton button, Color color)
    {
        button.Text = "";
        button.Width = 14;
        button.Height = 14;
        button.BackColor = color;
        button.ForeColor = Theme.Background;
        button.Font = UiFonts.Create(9.5f, FontStyle.Bold);
        button.Margin = Padding.Empty;
        button.Borderless = true;
        button.CornerRadius = 7;
        button.Circle = true;
        button.AccentColor = ControlPaint.Light(color);
    }

    private void ApplyRoundedCorners()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var handle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
        Region = Region.FromHrgn(handle);
        DeleteObject(handle);
    }

    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

internal sealed class HistoryForm : Form
{
    private readonly HistoryStore _store;
    private readonly Action _openSettings;
    private readonly TextBox _search = new();
    private readonly SlimHistoryList _list = new();
    private readonly ModernButton _copy = new();
    private readonly ModernButton _delete = new();
    private readonly ModernButton _clear = new();
    private readonly ModernButton _settings = new();
    private readonly ModernButton _minimize = new();
    private readonly ModernButton _close = new();
    private bool _layoutReady;

    public HistoryForm(HistoryStore store, Action openSettings)
    {
        _store = store;
        _openSettings = openSettings;
        Text = "ClipShelf";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        MinimumSize = new Size(500, 260);
        MaximumSize = new Size(820, 760);
        Size = new Size(680, 420);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(1);

        BuildLayout();
        _layoutReady = true;
        ApplyRoundedCorners();
        RefreshItems();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExAppWindow = 0x00040000;
            var createParams = base.CreateParams;
            createParams.ExStyle |= wsExToolWindow;
            createParams.ExStyle &= ~wsExAppWindow;
            return createParams;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        HideFromAltTab();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        HideFromAltTab();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedCorners();
    }

    public void RefreshItems()
    {
        var filter = _search.Text.Trim();
        var items = _store.Entries
            .Where(item => filter.Length == 0 || item.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _list.SetEntries(items);

        UpdateButtons();
        AdjustSizeToContent(items.Length);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 0, 14, 14),
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Theme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var header = BuildHeader();

        var searchHost = BuildSearchBox();

        _search.PlaceholderText = "Search clipboard history";
        _search.BackColor = Theme.Panel;
        _search.ForeColor = Theme.Text;
        _search.BorderStyle = BorderStyle.None;
        _search.Font = UiFonts.Create(10.5f);
        _search.Margin = Padding.Empty;
        _search.TextChanged += (_, _) => RefreshItems();

        _list.Dock = DockStyle.Fill;
        _list.EntryActivated += (_, _) => RestoreSelected();
        _list.SelectionChanged += (_, _) => UpdateButtons();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Background,
            WrapContents = false
        };

        StyleButton(_copy, "Copy");
        StyleButton(_delete, "Delete");
        StyleButton(_clear, "Clear");
        _copy.Click += (_, _) => RestoreSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _clear.Click += (_, _) => ClearAll();

        actions.Controls.AddRange([_copy, _delete, _clear]);
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(searchHost, 0, 1);
        root.Controls.Add(_list, 0, 2);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
    }

    private Control BuildSearchBox()
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            BorderColor = Theme.Border,
            CornerRadius = 12,
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 2, 0, 8)
        };
        host.Controls.Add(_search);
        host.Resize += (_, _) =>
        {
            _search.Width = Math.Max(0, host.ClientSize.Width - host.Padding.Horizontal);
            _search.Location = new Point(host.Padding.Left, Math.Max(0, (host.ClientSize.Height - _search.Height) / 2));
        };
        return host;
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Cursor = Cursors.SizeAll
        };
        header.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
            }
        };

        var title = new Label
        {
            Text = "ClipShelf",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = UiFonts.Create(11f, FontStyle.Bold),
            Location = new Point(42, 14)
        };

        StyleChromeButton(_close, Color.FromArgb(239, 68, 68));
        StyleChromeButton(_minimize, Color.FromArgb(245, 158, 11));
        StyleHeaderIconButton(_settings, "⚙");
        _close.Click += (_, _) => HideToTray();
        _minimize.Click += (_, _) => HideToTray();
        _settings.Click += (_, _) => _openSettings();

        header.Controls.Add(title);
        header.Controls.Add(_close);
        header.Controls.Add(_minimize);
        header.Controls.Add(_settings);
        header.Resize += (_, _) =>
        {
            _settings.Location = new Point(0, 8);
            _close.Location = new Point(header.Width - _close.Width, 14);
            _minimize.Location = new Point(_close.Left - _minimize.Width - 9, 14);
        };

        return header;
    }

    private static void StyleButton(ModernButton button, string text)
    {
        button.Text = text;
        button.Width = 88;
        button.Height = 34;
        button.BackColor = Theme.Button;
        button.ForeColor = Theme.Text;
        button.Font = UiFonts.Create(9.5f);
        button.Margin = new Padding(8, 8, 0, 0);
    }

    private static void StyleChromeButton(ModernButton button, Color color)
    {
        button.Text = "";
        button.Width = 14;
        button.Height = 14;
        button.BackColor = color;
        button.ForeColor = Theme.Background;
        button.Font = UiFonts.Create(9.5f, FontStyle.Bold);
        button.Margin = Padding.Empty;
        button.Borderless = true;
        button.CornerRadius = 7;
        button.Circle = true;
        button.AccentColor = ControlPaint.Light(color);
    }

    private static void StyleHeaderIconButton(ModernButton button, string text)
    {
        button.Text = text;
        button.Width = 30;
        button.Height = 30;
        button.BackColor = Theme.Background;
        button.ForeColor = Theme.Muted;
        button.Font = UiFonts.Create(12f);
        button.Margin = Padding.Empty;
        button.Borderless = true;
        button.CornerRadius = 15;
        button.AccentColor = Theme.ButtonHover;
    }

    private ClipboardEntry? SelectedEntry => _list.SelectedEntry;

    private void RestoreSelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        try
        {
            switch (entry.Kind)
            {
                case ClipboardKind.Text:
                    Clipboard.SetText(entry.Text ?? string.Empty);
                    break;
                case ClipboardKind.Files:
                    var files = new StringCollection();
                    files.AddRange((entry.Files ?? []).ToArray());
                    Clipboard.SetFileDropList(files);
                    break;
                case ClipboardKind.Image:
                    if (entry.ImagePath is not null && File.Exists(entry.ImagePath))
                    {
                        using var image = Image.FromFile(entry.ImagePath);
                        Clipboard.SetImage(new Bitmap(image));
                    }
                    break;
            }

            HideToTray();
        }
        catch (ExternalException)
        {
            MessageBox.Show("The clipboard is busy. Try again in a moment.", "ClipShelf", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void DeleteSelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        _store.Delete(entry);
        RefreshItems();
    }

    private void ClearAll()
    {
        if (MessageBox.Show("Clear all saved clipboard items?", "ClipShelf", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _store.Clear();
        RefreshItems();
    }

    private void UpdateButtons()
    {
        var hasSelection = SelectedEntry is not null;
        _copy.Enabled = hasSelection;
        _delete.Enabled = hasSelection;
        _clear.Enabled = _store.Entries.Count > 0;
    }

    public void HideFromAltTab()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        const int gwlExStyle = -20;
        const int wsExToolWindow = 0x00000080;
        const int wsExAppWindow = 0x00040000;
        var style = GetWindowLong(Handle, gwlExStyle);
        style &= ~wsExAppWindow;
        style |= wsExToolWindow;
        SetWindowLong(Handle, gwlExStyle, style);
    }

    private void HideToTray()
    {
        WindowState = FormWindowState.Normal;
        HideFromAltTab();
        Hide();
    }

    private void AdjustSizeToContent(int visibleItemCount)
    {
        if (!_layoutReady || WindowState != FormWindowState.Normal)
        {
            return;
        }

        var shownRows = Math.Clamp(visibleItemCount, 2, 8);
        var wantedHeight = 46 + 46 + 48 + 28 + shownRows * 64;
        var wantedWidth = Math.Clamp(Width, MinimumSize.Width, MaximumSize.Width);
        var wantedSize = new Size(wantedWidth, Math.Clamp(wantedHeight, MinimumSize.Height, MaximumSize.Height));

        if (Size != wantedSize)
        {
            Size = wantedSize;
        }
    }

    private void ApplyRoundedCorners()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var handle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
        Region = Region.FromHrgn(handle);
        DeleteObject(handle);
    }

    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

internal sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(96, 165, 250);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Borderless { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 8;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Circle { get; set; }

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Parent?.BackColor ?? Theme.Background);

        var fill = !Enabled
            ? Theme.Disabled
            : _pressed
                ? AccentColor
                : _hovered
                    ? Theme.ButtonHover
                    : BackColor;
        if (Circle && Enabled)
        {
            fill = _hovered || _pressed ? AccentColor : BackColor;
        }

        using var brush = new SolidBrush(fill);
        using var border = new Pen(_hovered && Enabled ? AccentColor : Theme.Border);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        if (Circle)
        {
            graphics.FillEllipse(brush, bounds);
        }
        else
        {
            graphics.FillRoundedRectangle(brush, bounds, CornerRadius);
        }
        if (!Borderless)
        {
            graphics.DrawRoundedRectangle(border, bounds, CornerRadius);
        }

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            bounds,
            Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SlimHistoryList : Control
{
    private const int ItemHeight = 64;
    private const int ScrollbarWidth = 6;
    private readonly List<ClipboardEntry> _entries = [];
    private int _selectedIndex = -1;
    private int _scrollOffset;

    public event EventHandler? SelectionChanged;
    public event EventHandler? EntryActivated;

    public SlimHistoryList()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = UiFonts.Create(9.5f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    public ClipboardEntry? SelectedEntry => _selectedIndex >= 0 && _selectedIndex < _entries.Count ? _entries[_selectedIndex] : null;

    public void SetEntries(IEnumerable<ClipboardEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        if (_selectedIndex >= _entries.Count)
        {
            _selectedIndex = _entries.Count - 1;
        }

        if (_entries.Count == 0)
        {
            _selectedIndex = -1;
        }

        ClampScroll();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(Theme.Panel);

        using var kindBrush = new SolidBrush(Theme.Muted);
        using var textBrush = new SolidBrush(Theme.Text);
        using var selectedBrush = new SolidBrush(Theme.Selection);
        using var panelBrush = new SolidBrush(Theme.Panel);
        using var previewFont = UiFonts.Create(10.2f);
        using var metaFont = UiFonts.Create(8.5f);
        using var divider = new Pen(Theme.Border);

        var contentWidth = Width - (NeedsScrollbar() ? ScrollbarWidth + 8 : 0);
        var firstIndex = Math.Max(0, _scrollOffset / ItemHeight);
        var y = firstIndex * ItemHeight - _scrollOffset;

        for (var index = firstIndex; index < _entries.Count && y < Height; index++, y += ItemHeight)
        {
            var bounds = new Rectangle(0, y, contentWidth, ItemHeight);
            e.Graphics.FillRectangle(index == _selectedIndex ? selectedBrush : panelBrush, bounds);

            var inset = new Rectangle(bounds.X + 12, bounds.Y + 8, bounds.Width - 24, bounds.Height - 16);
            e.Graphics.DrawString(_entries[index].Title, metaFont, kindBrush, inset.X, inset.Y);
            e.Graphics.DrawString(_entries[index].Preview, previewFont, textBrush, new RectangleF(inset.X, inset.Y + 22, inset.Width, 28));
            e.Graphics.DrawLine(divider, bounds.Left + 12, bounds.Bottom - 1, bounds.Right - 12, bounds.Bottom - 1);
        }

        DrawScrollbar(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var index = (e.Y + _scrollOffset) / ItemHeight;
        if (index >= 0 && index < _entries.Count)
        {
            _selectedIndex = index;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        base.OnMouseDown(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        if (SelectedEntry is not null)
        {
            EntryActivated?.Invoke(this, EventArgs.Empty);
        }

        base.OnDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scrollOffset -= Math.Sign(e.Delta) * ItemHeight;
        ClampScroll();
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnResize(EventArgs e)
    {
        ClampScroll();
        Invalidate();
        base.OnResize(e);
    }

    private void DrawScrollbar(Graphics graphics)
    {
        if (!NeedsScrollbar())
        {
            return;
        }

        var track = new Rectangle(Width - ScrollbarWidth - 2, 8, ScrollbarWidth, Height - 16);
        var totalHeight = _entries.Count * ItemHeight;
        var thumbHeight = Math.Max(28, (int)(track.Height * (Height / (float)totalHeight)));
        var maxScroll = Math.Max(1, totalHeight - Height);
        var thumbTop = track.Top + (int)((track.Height - thumbHeight) * (_scrollOffset / (float)maxScroll));

        using var trackBrush = new SolidBrush(Theme.Panel);
        using var thumbBrush = new SolidBrush(Theme.ScrollThumb);
        graphics.FillRoundedRectangle(trackBrush, track, 3);
        graphics.FillRoundedRectangle(thumbBrush, new Rectangle(track.Left, thumbTop, track.Width, thumbHeight), 3);
    }

    private bool NeedsScrollbar() => _entries.Count * ItemHeight > Height;

    private void ClampScroll()
    {
        var maxScroll = Math.Max(0, _entries.Count * ItemHeight - Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Theme.Border;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(BorderColor);
        e.Graphics.FillRoundedRectangle(fill, bounds, CornerRadius);
        e.Graphics.DrawRoundedRectangle(border, bounds, CornerRadius);
    }
}

internal enum ClipboardKind
{
    Text,
    Image,
    Files
}

internal sealed record ClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ClipboardKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public List<string>? Files { get; init; }
    public string Fingerprint { get; init; } = string.Empty;

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipboardKind.Text => Compact(Text),
        ClipboardKind.Image => Path.GetFileName(ImagePath) ?? "Copied image",
        ClipboardKind.Files => Compact(string.Join(", ", Files ?? [])),
        _ => string.Empty
    };

    [JsonIgnore]
    public string Title => $"{Kind}  {CreatedAt:g}";

    [JsonIgnore]
    public string SearchText => $"{Kind} {Text} {ImagePath} {string.Join(' ', Files ?? [])}";

    public static ClipboardEntry FromText(string text)
    {
        return new ClipboardEntry
        {
            Kind = ClipboardKind.Text,
            Text = text,
            Fingerprint = $"text:{text}"
        };
    }

    public static ClipboardEntry FromFiles(List<string> files)
    {
        return new ClipboardEntry
        {
            Kind = ClipboardKind.Files,
            Files = files,
            Fingerprint = $"files:{string.Join('|', files)}"
        };
    }

    public static ClipboardEntry FromImage(string imagePath)
    {
        return new ClipboardEntry
        {
            Kind = ClipboardKind.Image,
            ImagePath = imagePath,
            Fingerprint = $"image:{new FileInfo(imagePath).Length}:{File.GetLastWriteTimeUtc(imagePath).Ticks}"
        };
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var compact = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 18, 20);
    public static readonly Color Panel = Color.FromArgb(28, 29, 33);
    public static readonly Color Button = Color.FromArgb(42, 44, 50);
    public static readonly Color ButtonHover = Color.FromArgb(52, 56, 64);
    public static readonly Color Disabled = Color.FromArgb(35, 36, 40);
    public static readonly Color Border = Color.FromArgb(58, 61, 69);
    public static readonly Color ScrollThumb = Color.FromArgb(86, 91, 103);
    public static readonly Color Selection = Color.FromArgb(45, 61, 76);
    public static readonly Color Text = Color.FromArgb(238, 239, 242);
    public static readonly Color Muted = Color.FromArgb(157, 163, 175);
}

internal static class UiFonts
{
    private static readonly string FamilyName = ResolveFamilyName();

    public static Font Create(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(FamilyName, size, style, GraphicsUnit.Point);
    }

    private static string ResolveFamilyName()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(family => string.Equals(family.Name, "Space Mono", StringComparison.OrdinalIgnoreCase))
            ? "Space Mono"
            : "Consolas";
    }
}

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var back = new SolidBrush(Color.FromArgb(28, 29, 33));
        using var accent = new SolidBrush(Color.FromArgb(96, 165, 250));
        using var paper = new SolidBrush(Color.FromArgb(238, 239, 242));
        graphics.FillEllipse(back, 1, 1, 30, 30);
        graphics.FillRectangle(accent, 10, 6, 12, 4);
        graphics.FillRoundedRectangle(paper, new Rectangle(8, 10, 16, 15), 3);
        graphics.FillRectangle(back, 11, 14, 10, 2);
        graphics.FillRectangle(back, 11, 18, 10, 2);

        return Icon.FromHandle(bitmap.GetHicon());
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}
