using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Valve.VR;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new OverlayForm());
    }
}

internal enum PlacementMode
{
    World,
    LeftController,
    RightController
}

internal enum CaptureMode
{
    PrintWindow,
    ScreenRegion
}

internal sealed class OverlaySettings
{
    public PlacementMode Placement { get; set; } = PlacementMode.World;
    public CaptureMode Capture { get; set; } = CaptureMode.PrintWindow;

    public string WindowTitle { get; set; } = "";

    public float Width { get; set; } = 0.60f;
    public float Opacity { get; set; } = 1.0f;

    public float WorldX { get; set; }
    public float WorldY { get; set; } = 1.5f;
    public float WorldZ { get; set; } = -1.2f;

    public float WorldPitch { get; set; }
    public float WorldYaw { get; set; }
    public float WorldRoll { get; set; }

    public float ControllerX { get; set; }
    public float ControllerY { get; set; } = 0.15f;
    public float ControllerZ { get; set; } = -0.10f;

    public float ControllerPitch { get; set; }
    public float ControllerYaw { get; set; }
    public float ControllerRoll { get; set; }

    public bool ShowOnlyWhenLooking { get; set; }
    public float LookAngle { get; set; } = 30.0f;
    public int HideDelayMs { get; set; } = 400;

    // Проценты от исходной ширины/высоты: положительное значение обрезает,
    // отрицательное расширяет область за границы окна.
    public int CaptureLeft { get; set; }
    public int CaptureRight { get; set; }
    public int CaptureTop { get; set; }
    public int CaptureBottom { get; set; }

    public int FrameIntervalMs { get; set; } = 100;
}

internal sealed class WindowItem
{
    public IntPtr Handle { get; }
    public string Title { get; }

    public WindowItem(IntPtr handle, string title)
    {
        Handle = handle;
        Title = title;
    }

    public override string ToString()
    {
        return Title;
    }
}

internal sealed class OverlayException : Exception
{
    public EVROverlayError Error { get; }

    public OverlayException(
        EVROverlayError error,
        string operation
    ) : base($"{operation}: {error}")
    {
        Error = error;
    }
}

internal sealed class OverlayForm : Form
{
    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        ),
        "SteamVrWindowOverlay",
        "settings.json"
    );

    private readonly OverlaySettings settings;
    private readonly FlowLayoutPanel controlsPanel;
    private readonly ComboBox windowCombo;
    private readonly Label statusLabel;
    private readonly TextBox logBox;
    private readonly System.Windows.Forms.Timer renderTimer;

    private readonly Dictionary<string, NumericUpDown> numberControls =
        new Dictionary<string, NumericUpDown>();

    private readonly TrackedDevicePose_t[] poses =
        new TrackedDevicePose_t[
            (int)OpenVR.k_unMaxTrackedDeviceCount
        ];

    private SharpDX.Direct3D11.Device? graphicsDevice;
    private SharpDX.Direct3D11.Texture2D? overlayTexture;

    private Bitmap? captureBitmap;
    private Bitmap? adjustedCaptureBitmap;
    private byte[] pixelBuffer = Array.Empty<byte>();

    private int textureWidth;
    private int textureHeight;

    private ulong overlayHandle;
    private bool initialized;
    private bool panelVisible;
    private bool hasUploadedFrame;
    private bool resettingTexture;
    private bool ticking;
    private bool shuttingDown;
    private bool loadingWindowList;

    private WindowItem? selectedWindow;
    private long lastLookTime = -1;

    private string lastCaptureError = "";
    private string lastVrError = "";
    private long nextVrErrorLog;
    private string startupSettingsError = "";

    public OverlayForm()
    {
        settings = LoadSettings();

        Text = "SteamVR Window Overlay — настройки";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1120, 850);
        MinimumSize = new Size(1050, 650);
        Font = new Font("Segoe UI", 9.0f);

        Panel rightPanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 320,
            Padding = new Padding(10)
        };

        controlsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10)
        };

        Controls.Add(controlsPanel);
        Controls.Add(rightPanel);

        TableLayoutPanel rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5
        };

        rightLayout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100)
        );

        rightLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 100)
        );
        rightLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 45)
        );
        rightLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 45)
        );
        rightLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 100)
        );
        rightLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100)
        );

        statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Подключение к SteamVR...",
            Padding = new Padding(3),
            AutoEllipsis = true
        };

        Button reconnectButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Подключиться к SteamVR"
        };

        reconnectButton.Click += (_, _) => InitializeSteamVr();

        Button saveButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Сохранить настройки"
        };

        saveButton.Click += (_, _) => SaveSettings();

        Label informationLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text =
                "Настройки применяются сразу.\r\n" +
                "При закрытии они сохраняются.\r\n\r\n" +
                "Управления захваченным окном из VR нет."
        };

        logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = SystemColors.Window
        };

        rightLayout.Controls.Add(statusLabel, 0, 0);
        rightLayout.Controls.Add(reconnectButton, 0, 1);
        rightLayout.Controls.Add(saveButton, 0, 2);
        rightLayout.Controls.Add(informationLabel, 0, 3);
        rightLayout.Controls.Add(logBox, 0, 4);

        rightPanel.Controls.Add(rightLayout);

        TableLayoutPanel sourceGroup = CreateGroup("Захватываемое окно");

        FlowLayoutPanel sourceButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false
        };

        Button refreshButton = new Button
        {
            Text = "Обновить список",
            AutoSize = true
        };

        refreshButton.Click += (_, _) => RefreshWindowList();

        Button hideButton = new Button
        {
            Text = "Отключить захват",
            AutoSize = true
        };

        hideButton.Click += (_, _) => windowCombo.SelectedIndex = -1;

        sourceButtons.Controls.Add(refreshButton);
        sourceButtons.Controls.Add(hideButton);
        AddRow(sourceGroup, sourceButtons, 38);

        windowCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DropDownWidth = 650
        };

        windowCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loadingWindowList)
            {
                return;
            }

            ChangeSelectedWindow(
                windowCombo.SelectedItem as WindowItem
            );
        };

        AddRow(sourceGroup, windowCombo, 36);

        ComboBox captureCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        captureCombo.Items.Add("PrintWindow — захват окна");
        captureCombo.Items.Add("Область экрана — окно должно быть видно");
        captureCombo.SelectedIndex = (int)settings.Capture;

        captureCombo.SelectedIndexChanged += (_, _) =>
        {
            settings.Capture = (CaptureMode)captureCombo.SelectedIndex;
            lastCaptureError = "";
            Log($"Способ захвата: {captureCombo.SelectedItem}");
        };

        AddRow(
            sourceGroup,
            CreateLabeledControl("Способ захвата", captureCombo),
            36
        );

        AddNote(
            sourceGroup,
            "PrintWindow может давать чёрный кадр в некоторых приложениях. " +
            "В режиме области экрана перекрывающие окна попадают в захват."
        );

        TableLayoutPanel cropGroup = CreateGroup("Обрезка области захвата");
        AddNumber(
            cropGroup, "CaptureLeft", "Слева: обрезать (− расширить), %",
            -50, 50, settings.CaptureLeft, 0,
            value => settings.CaptureLeft = (int)value
        );
        AddNumber(
            cropGroup, "CaptureRight", "Справа: обрезать (− расширить), %",
            -50, 50, settings.CaptureRight, 0,
            value => settings.CaptureRight = (int)value
        );
        AddNumber(
            cropGroup, "CaptureTop", "Сверху: обрезать (− расширить), %",
            -50, 50, settings.CaptureTop, 0,
            value => settings.CaptureTop = (int)value
        );
        AddNumber(
            cropGroup, "CaptureBottom", "Снизу: обрезать (− расширить), %",
            -50, 50, settings.CaptureBottom, 0,
            value => settings.CaptureBottom = (int)value
        );
        AddNote(
            cropGroup,
            "Положительные значения обрезают край, отрицательные расширяют область. " +
            "Расширение в режиме PrintWindow заполняется чёрным; для захвата " +
            "содержимого за пределами окна используй режим «Область экрана»."
        );

        TableLayoutPanel placementGroup = CreateGroup("Привязка");

        ComboBox placementCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        placementCombo.Items.Add("В пространстве SteamVR");
        placementCombo.Items.Add("Левый контроллер");
        placementCombo.Items.Add("Правый контроллер");
        placementCombo.SelectedIndex = (int)settings.Placement;

        placementCombo.SelectedIndexChanged += (_, _) =>
        {
            settings.Placement =
                (PlacementMode)placementCombo.SelectedIndex;

            lastLookTime = -1;
        };

        AddRow(
            placementGroup,
            CreateLabeledControl("Режим", placementCombo),
            36
        );

        Button placeButton = new Button
        {
            Text = "Разместить передо мной на расстоянии 1,2 м",
            Dock = DockStyle.Fill
        };

        placeButton.Click += (_, _) =>
        {
            if (PlaceInFrontOfHead())
            {
                placementCombo.SelectedIndex =
                    (int)PlacementMode.World;
            }
        };

        AddRow(placementGroup, placeButton, 38);

        TableLayoutPanel appearanceGroup = CreateGroup("Внешний вид");

        AddNumber(
            appearanceGroup,
            "Width",
            "Ширина, м",
            0.10f,
            2.00f,
            settings.Width,
            2,
            value => settings.Width = value
        );

        AddNumber(
            appearanceGroup,
            "Opacity",
            "Прозрачность: 1 = непрозрачно",
            0.05f,
            1.00f,
            settings.Opacity,
            2,
            value => settings.Opacity = value
        );

        TableLayoutPanel worldGroup = CreateGroup(
            "Положение в пространстве — действует в режиме пространства"
        );

        AddNumber(
            worldGroup, "WorldX", "X, м",
            -20, 20, settings.WorldX, 2,
            value => settings.WorldX = value
        );

        AddNumber(
            worldGroup, "WorldY", "Y, м",
            -20, 20, settings.WorldY, 2,
            value => settings.WorldY = value
        );

        AddNumber(
            worldGroup, "WorldZ", "Z, м",
            -20, 20, settings.WorldZ, 2,
            value => settings.WorldZ = value
        );

        AddNumber(
            worldGroup, "WorldPitch", "Наклон X, градусы",
            -180, 180, settings.WorldPitch, 1,
            value => settings.WorldPitch = value
        );

        AddNumber(
            worldGroup, "WorldYaw", "Поворот Y, градусы",
            -180, 180, settings.WorldYaw, 1,
            value => settings.WorldYaw = value
        );

        AddNumber(
            worldGroup, "WorldRoll", "Крен Z, градусы",
            -180, 180, settings.WorldRoll, 1,
            value => settings.WorldRoll = value
        );

        AddNote(
            worldGroup,
            "Координаты относятся к Standing-пространству SteamVR. " +
            "Y направлен вверх. После перенастройки игровой зоны " +
            "может потребоваться снова нажать «Разместить передо мной»."
        );

        TableLayoutPanel controllerGroup = CreateGroup(
            "Положение относительно контроллера — для обеих рук"
        );

        AddNumber(
            controllerGroup, "ControllerX", "Смещение X, м",
            -1, 1, settings.ControllerX, 2,
            value => settings.ControllerX = value
        );

        AddNumber(
            controllerGroup, "ControllerY", "Смещение Y, м",
            -1, 1, settings.ControllerY, 2,
            value => settings.ControllerY = value
        );

        AddNumber(
            controllerGroup, "ControllerZ", "Смещение Z, м",
            -1, 1, settings.ControllerZ, 2,
            value => settings.ControllerZ = value
        );

        AddNumber(
            controllerGroup, "ControllerPitch", "Наклон X, градусы",
            -180, 180, settings.ControllerPitch, 1,
            value => settings.ControllerPitch = value
        );

        AddNumber(
            controllerGroup, "ControllerYaw", "Поворот Y, градусы",
            -180, 180, settings.ControllerYaw, 1,
            value => settings.ControllerYaw = value
        );

        AddNumber(
            controllerGroup, "ControllerRoll", "Крен Z, градусы",
            -180, 180, settings.ControllerRoll, 1,
            value => settings.ControllerRoll = value
        );

        AddNote(
            controllerGroup,
            "Оси локальные и зависят от модели контроллера. " +
            "Если видна обратная сторона панели, попробуй поворот Y на 180°. " +
            "Настройки смещения и поворота сейчас общие для обеих рук."
        );

        TableLayoutPanel gazeGroup = CreateGroup(
            "Показ при взгляде — только в режиме контроллера"
        );

        CheckBox gazeCheck = new CheckBox
        {
            Text = "Показывать только при взгляде на панель",
            Checked = settings.ShowOnlyWhenLooking,
            Dock = DockStyle.Fill
        };

        gazeCheck.CheckedChanged += (_, _) =>
        {
            settings.ShowOnlyWhenLooking = gazeCheck.Checked;
            lastLookTime = -1;
        };

        AddRow(gazeGroup, gazeCheck, 34);

        AddNumber(
            gazeGroup, "LookAngle", "Допустимый угол, градусы",
            5, 80, settings.LookAngle, 0,
            value => settings.LookAngle = value
        );

        AddNumber(
            gazeGroup, "HideDelayMs", "Задержка скрытия, мс",
            0, 2000, settings.HideDelayMs, 0,
            value => settings.HideDelayMs = (int)value
        );

        AddNote(
            gazeGroup,
            "Используется направление шлема, а не отслеживание глаз. " +
            "Для появления панель должна быть перед тобой " +
            "и обращена лицевой стороной к голове."
        );

        TableLayoutPanel performanceGroup = CreateGroup("Обновление");

        AddNumber(
            performanceGroup, "FrameIntervalMs", "Интервал кадров, мс",
            33, 500, settings.FrameIntervalMs, 0,
            value =>
            {
                settings.FrameIntervalMs = (int)value;

                if (renderTimer != null)
                {
                    renderTimer.Interval = settings.FrameIntervalMs;
                }
            }
        );

        AddNote(
            performanceGroup,
            "100 мс — примерно 10 обновлений в секунду. " +
            "Меньший интервал увеличивает нагрузку. " +
            "PrintWindow может задерживать интерфейс, " +
            "если захватываемое приложение отвечает медленно."
        );

        controlsPanel.SizeChanged += (_, _) => ResizeGroups();
        ResizeGroups();

        renderTimer = new System.Windows.Forms.Timer
        {
            Interval = settings.FrameIntervalMs
        };

        renderTimer.Tick += (_, _) => RenderTick();

        Shown += (_, _) =>
        {
            ResizeGroups();

            if (startupSettingsError.Length > 0)
            {
                Log(startupSettingsError);
            }

            RefreshWindowList();
            InitializeSteamVr();
        };
    }

    private OverlaySettings LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new OverlaySettings();
            }

            string json = File.ReadAllText(settingsPath);

            OverlaySettings loaded =
                JsonSerializer.Deserialize<OverlaySettings>(json)
                ?? new OverlaySettings();

            if (!Enum.IsDefined(typeof(PlacementMode), loaded.Placement))
            {
                loaded.Placement = PlacementMode.World;
            }

            if (!Enum.IsDefined(typeof(CaptureMode), loaded.Capture))
            {
                loaded.Capture = CaptureMode.PrintWindow;
            }

            loaded.WindowTitle ??= "";
            loaded.CaptureLeft = Math.Clamp(loaded.CaptureLeft, -50, 50);
            loaded.CaptureRight = Math.Clamp(loaded.CaptureRight, -50, 50);
            loaded.CaptureTop = Math.Clamp(loaded.CaptureTop, -50, 50);
            loaded.CaptureBottom = Math.Clamp(loaded.CaptureBottom, -50, 50);

            return loaded;
        }
        catch (Exception exception)
        {
            startupSettingsError =
                $"Не удалось загрузить настройки: {exception.Message}";

            return new OverlaySettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            if (selectedWindow != null &&
                NativeMethods.IsWindow(selectedWindow.Handle))
            {
                string currentTitle =
                    NativeMethods.ReadWindowTitle(selectedWindow.Handle);

                if (!string.IsNullOrWhiteSpace(currentTitle))
                {
                    settings.WindowTitle = currentTitle;
                }
            }

            string? directory = Path.GetDirectoryName(settingsPath);

            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            );

            File.WriteAllText(settingsPath, json);
            Log("Настройки сохранены.");
        }
        catch (Exception exception)
        {
            Log($"Ошибка сохранения: {exception.Message}");
        }
    }

    private TableLayoutPanel CreateGroup(string title)
    {
        // Ширина и высота GroupBox задаются явно в ResizeGroups().
        // AutoSize у GroupBox вместе с FlowLayoutPanel мог схлопывать
        // вложенную таблицу до ширины заголовка.
        GroupBox group = new GroupBox
        {
            Text = title,
            Width = 720,
            Height = 100,
            AutoSize = false,
            Padding = new Padding(10, 22, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };

        TableLayoutPanel table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Width = 690,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty
        };

        table.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100)
        );

        group.Controls.Add(table);
        controlsPanel.Controls.Add(group);

        return table;
    }

    private static void AddRow(
        TableLayoutPanel table,
        Control control,
        int height
    )
    {
        int row = table.RowCount;
        table.RowCount++;

        table.RowStyles.Add(
            new RowStyle(SizeType.Absolute, height)
        );

        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, row);
    }

    private static Control CreateLabeledControl(
        string text,
        Control control
    )
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        row.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 250)
        );
        row.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100)
        );

        Label label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(control, 1, 0);

        return row;
    }

    private void AddNumber(
        TableLayoutPanel table,
        string key,
        string text,
        float minimum,
        float maximum,
        float initialValue,
        int decimals,
        Action<float> changed
    )
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        row.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 250)
        );
        row.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100)
        );
        row.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 110)
        );

        Label label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        int scale = (int)Math.Pow(10, decimals);

        TrackBar slider = new TrackBar
        {
            Dock = DockStyle.Fill,
            Minimum = (int)Math.Round(minimum * scale),
            Maximum = (int)Math.Round(maximum * scale),
            TickStyle = TickStyle.None,
            SmallChange = 1,
            LargeChange = Math.Max(1, scale / 10),
            AutoSize = false,
            Margin = new Padding(0)
        };

        NumericUpDown number = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            DecimalPlaces = decimals,
            Increment = 1m / scale,
            Margin = new Padding(3, 4, 3, 3)
        };

        float safeValue = float.IsFinite(initialValue)
            ? Math.Clamp(initialValue, minimum, maximum)
            : minimum;

        number.Value = Math.Round((decimal)safeValue, decimals);

        slider.Value = Math.Clamp(
            (int)Math.Round(number.Value * scale),
            slider.Minimum,
            slider.Maximum
        );

        bool syncing = false;

        number.ValueChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }

            syncing = true;

            try
            {
                slider.Value = Math.Clamp(
                    (int)Math.Round(number.Value * scale),
                    slider.Minimum,
                    slider.Maximum
                );

                changed((float)number.Value);
            }
            finally
            {
                syncing = false;
            }
        };

        slider.ValueChanged += (_, _) =>
        {
            if (syncing)
            {
                return;
            }

            syncing = true;

            try
            {
                number.Value = (decimal)slider.Value / scale;
                changed((float)number.Value);
            }
            finally
            {
                syncing = false;
            }
        };

        numberControls.Add(key, number);

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(slider, 1, 0);
        row.Controls.Add(number, 2, 0);

        AddRow(table, row, 38);

        changed((float)number.Value);
    }

    private static void AddNote(
        TableLayoutPanel table,
        string text
    )
    {
        Label label = new Label
        {
            Text = text,
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = false,
            Padding = new Padding(3, 5, 3, 0)
        };

        AddRow(table, label, 72);
    }

    private void ResizeGroups()
    {
        // Вызывается и при изменении размера окна, и после создания формы.
        int availableWidth =
            controlsPanel.ClientSize.Width -
            controlsPanel.Padding.Horizontal -
            (controlsPanel.VerticalScroll.Visible
                ? SystemInformation.VerticalScrollBarWidth
                : 0) -
            4;

        int width = Math.Max(300, availableWidth);

        foreach (Control control in controlsPanel.Controls)
        {
            if (control is not GroupBox group || group.Controls.Count == 0)
            {
                continue;
            }

            group.SuspendLayout();
            try
            {
                group.Width = width;

                if (group.Controls[0] is TableLayoutPanel table)
                {
                    int innerWidth = Math.Max(250, width - group.Padding.Horizontal - 8);
                    table.Width = innerWidth;
                    table.PerformLayout();

                    // Высота равна сумме высот строк таблицы плюс заголовок и отступы.
                    int rowsHeight = 0;
                    foreach (RowStyle rowStyle in table.RowStyles)
                    {
                        if (rowStyle.SizeType == SizeType.Absolute)
                        {
                            rowsHeight += (int)Math.Ceiling(rowStyle.Height);
                        }
                    }

                    group.Height = rowsHeight + group.Padding.Vertical + 28;
                }
            }
            finally
            {
                group.ResumeLayout(true);
            }
        }
    }

    private void SetNumber(string key, float value)
    {
        NumericUpDown control = numberControls[key];

        decimal safeValue = Math.Clamp(
            (decimal)value,
            control.Minimum,
            control.Maximum
        );

        control.Value = Math.Round(
            safeValue,
            control.DecimalPlaces
        );
    }

    private void RefreshWindowList()
    {
        IntPtr previousHandle =
            selectedWindow?.Handle ?? IntPtr.Zero;

        string desiredTitle = settings.WindowTitle;
        List<WindowItem> windows = NativeMethods.GetWindows(Handle);

        WindowItem? match = null;

        foreach (WindowItem window in windows)
        {
            if (previousHandle != IntPtr.Zero &&
                window.Handle == previousHandle)
            {
                match = window;
                break;
            }
        }

        if (match == null &&
            previousHandle == IntPtr.Zero &&
            desiredTitle.Length > 0)
        {
            foreach (WindowItem window in windows)
            {
                if (string.Equals(
                    window.Title,
                    desiredTitle,
                    StringComparison.Ordinal
                ))
                {
                    match = window;
                    break;
                }
            }
        }

        loadingWindowList = true;

        try
        {
            windowCombo.Items.Clear();

            foreach (WindowItem window in windows)
            {
                windowCombo.Items.Add(window);
            }

            if (match != null)
            {
                windowCombo.SelectedItem = match;
            }
            else
            {
                windowCombo.SelectedIndex = -1;
            }
        }
        finally
        {
            loadingWindowList = false;
        }

        ChangeSelectedWindow(match);

        if (match == null)
        {
            Log("Выбери окно в списке.");
        }
    }

    private void ChangeSelectedWindow(WindowItem? window)
    {
        IntPtr oldHandle =
            selectedWindow?.Handle ?? IntPtr.Zero;

        IntPtr newHandle =
            window?.Handle ?? IntPtr.Zero;

        selectedWindow = window;

        if (window != null)
        {
            settings.WindowTitle = window.Title;
        }

        if (oldHandle == newHandle)
        {
            return;
        }

        hasUploadedFrame = false;
        resettingTexture = true;
        lastCaptureError = "";
        lastLookTime = -1;

        if (window != null)
        {
            Log($"Выбрано окно: {window.Title}");
        }
        else
        {
            Log("Захват отключён.");
        }
    }

    private void InitializeSteamVr()
    {
        if (initialized)
        {
            Log("Приложение уже подключено к SteamVR.");
            return;
        }

        try
        {
            EVRInitError error = EVRInitError.None;

            OpenVR.Init(
                ref error,
                EVRApplicationType.VRApplication_Overlay
            );

            if (error != EVRInitError.None)
            {
                throw new InvalidOperationException(
                    $"Подключение к SteamVR: {error}"
                );
            }

            initialized = true;

            Check(
                OpenVR.Overlay.CreateOverlay(
                    "local.steamvr.overlay.prototype",
                    "SteamVR Window Overlay",
                    ref overlayHandle
                ),
                "CreateOverlay"
            );

            EnsureGraphicsDevice();

            resettingTexture = true;
            hasUploadedFrame = false;
            panelVisible = false;

            renderTimer.Start();

            Log("Подключение к SteamVR установлено.");
            statusLabel.Text =
                "SteamVR подключён.\r\nВыбери окно для захвата.";
        }
        catch (Exception exception)
        {
            renderTimer.Stop();
            ReleaseSteamVr();

            statusLabel.Text =
                "Не удалось подключиться.\r\n" +
                "Запусти SteamVR и нажми кнопку подключения.";

            Log(exception.Message);
        }
    }

    private void RenderTick()
    {
        if (!initialized || ticking || shuttingDown)
        {
            return;
        }

        ticking = true;

        try
        {
            if (resettingTexture)
            {
                SetVisible(false);
                ResetTexture();
                resettingTexture = false;
            }

            if (selectedWindow == null)
            {
                SetVisible(false);
                statusLabel.Text =
                    "SteamVR подключён.\r\nОкно не выбрано.";
                return;
            }

            OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding,
                0,
                poses
            );

            bool placementAvailable =
                UpdatePlacement(out HmdMatrix34_t absoluteTransform);

            Check(
                OpenVR.Overlay.SetOverlayWidthInMeters(
                    overlayHandle,
                    settings.Width
                ),
                "SetOverlayWidthInMeters"
            );

            Check(
                OpenVR.Overlay.SetOverlayAlpha(
                    overlayHandle,
                    settings.Opacity
                ),
                "SetOverlayAlpha"
            );

            bool captured = TryCaptureWindow(
                selectedWindow.Handle,
                out string captureError
            );

            if (captureError != lastCaptureError)
            {
                if (captureError.Length > 0)
                {
                    Log(captureError);
                }
                else if (lastCaptureError.Length > 0)
                {
                    Log("Захват изображения восстановлен.");
                }

                lastCaptureError = captureError;
            }

            if (captured && adjustedCaptureBitmap != null)
            {
                UploadBitmap(adjustedCaptureBitmap);
                hasUploadedFrame = true;
            }

            bool gazeAllowsVisibility =
                placementAvailable &&
                GazeAllowsVisibility(absoluteTransform);

            bool shouldBeVisible =
                placementAvailable &&
                hasUploadedFrame &&
                gazeAllowsVisibility;

            SetVisible(shouldBeVisible);

            string panelState = panelVisible
                ? "Панель видна"
                : "Панель скрыта";

            string captureState = captured
                ? "Захват работает"
                : "Нет нового кадра";

            statusLabel.Text =
                $"SteamVR подключён.\r\n" +
                $"{panelState}.\r\n" +
                $"{captureState}.";

            if (!placementAvailable)
            {
                statusLabel.Text += "\r\nНет трекинга устройства.";
            }

            if (lastVrError.Length > 0)
            {
                Log("Обновление оверлея восстановлено.");
                lastVrError = "";
            }
        }
        catch (OverlayException exception)
            when (exception.Error == EVROverlayError.RequestFailed)
        {
            LogVrError(exception.Message);

            statusLabel.Text =
                "SteamVR отклонил обновление.\r\n" +
                "Повторяем на следующем кадре.";
        }
        catch (Exception exception)
        {
            renderTimer.Stop();

            Log($"Обновление остановлено: {exception.Message}");

            ReleaseSteamVr();

            statusLabel.Text =
                "Обновление остановлено.\r\n" +
                "Проверь журнал и попробуй подключиться снова.";
        }
        finally
        {
            ticking = false;
        }
    }

    private bool UpdatePlacement(
        out HmdMatrix34_t absoluteTransform
    )
    {
        if (settings.Placement == PlacementMode.World)
        {
            absoluteTransform = MakeTransform(
                settings.WorldX,
                settings.WorldY,
                settings.WorldZ,
                settings.WorldPitch,
                settings.WorldYaw,
                settings.WorldRoll
            );

            Check(
                OpenVR.Overlay.SetOverlayTransformAbsolute(
                    overlayHandle,
                    ETrackingUniverseOrigin.TrackingUniverseStanding,
                    ref absoluteTransform
                ),
                "SetOverlayTransformAbsolute"
            );

            TrackedDevicePose_t head =
                poses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];

            return head.bDeviceIsConnected && head.bPoseIsValid;
        }

        ETrackedControllerRole role =
            settings.Placement == PlacementMode.LeftController
                ? ETrackedControllerRole.LeftHand
                : ETrackedControllerRole.RightHand;

        uint index =
            OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);

        absoluteTransform = default;

        if (index == OpenVR.k_unTrackedDeviceIndexInvalid ||
            index >= (uint)poses.Length)
        {
            lastLookTime = -1;
            return false;
        }

        TrackedDevicePose_t controller = poses[(int)index];

        if (!controller.bDeviceIsConnected ||
            !controller.bPoseIsValid)
        {
            lastLookTime = -1;
            return false;
        }

        HmdMatrix34_t relativeTransform = MakeTransform(
            settings.ControllerX,
            settings.ControllerY,
            settings.ControllerZ,
            settings.ControllerPitch,
            settings.ControllerYaw,
            settings.ControllerRoll
        );

        Check(
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(
                overlayHandle,
                index,
                ref relativeTransform
            ),
            "SetOverlayTransformTrackedDeviceRelative"
        );

        absoluteTransform = Multiply(
            controller.mDeviceToAbsoluteTracking,
            relativeTransform
        );

        TrackedDevicePose_t headPose =
            poses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];

        return headPose.bDeviceIsConnected && headPose.bPoseIsValid;
    }

    private bool GazeAllowsVisibility(
        HmdMatrix34_t panelTransform
    )
    {
        if (settings.Placement == PlacementMode.World ||
            !settings.ShowOnlyWhenLooking)
        {
            lastLookTime = -1;
            return true;
        }

        TrackedDevicePose_t headPose =
            poses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];

        if (!headPose.bDeviceIsConnected ||
            !headPose.bPoseIsValid)
        {
            lastLookTime = -1;
            return false;
        }

        HmdMatrix34_t head =
            headPose.mDeviceToAbsoluteTracking;

        float dx = panelTransform.m3 - head.m3;
        float dy = panelTransform.m7 - head.m7;
        float dz = panelTransform.m11 - head.m11;

        float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (distance < 0.02f)
        {
            lastLookTime = -1;
            return false;
        }

        dx /= distance;
        dy /= distance;
        dz /= distance;

        // Шлем смотрит вдоль локальной оси -Z.
        float viewDot =
            (-head.m2 * dx) +
            (-head.m6 * dy) +
            (-head.m10 * dz);

        // Лицевая сторона оверлея направлена вдоль локальной +Z.
        // Проверяем, обращена ли она к голове.
        float facingDot =
            panelTransform.m2 * -dx +
            panelTransform.m6 * -dy +
            panelTransform.m10 * -dz;

        float angle = settings.LookAngle;

        // Небольшой гистерезис уменьшает дрожание на границе угла.
        if (panelVisible)
        {
            angle = Math.Min(85.0f, angle + 5.0f);
        }

        float threshold = MathF.Cos(
            angle * MathF.PI / 180.0f
        );

        bool looking =
            viewDot >= threshold &&
            facingDot > 0.05f;

        long now = Environment.TickCount64;

        if (looking)
        {
            lastLookTime = now;
            return true;
        }

        return lastLookTime >= 0 &&
            now - lastLookTime <= settings.HideDelayMs;
    }

    private bool PlaceInFrontOfHead()
    {
        if (!initialized)
        {
            Log("Сначала подключись к SteamVR.");
            return false;
        }

        try
        {
            OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding,
                0,
                poses
            );

            TrackedDevicePose_t headPose =
                poses[(int)OpenVR.k_unTrackedDeviceIndex_Hmd];

            if (!headPose.bDeviceIsConnected ||
                !headPose.bPoseIsValid)
            {
                Log("Поза шлема недоступна.");
                return false;
            }

            HmdMatrix34_t head =
                headPose.mDeviceToAbsoluteTracking;

            const float distance = 1.2f;

            float x = head.m3 - head.m2 * distance;
            float y = head.m7 - head.m6 * distance;
            float z = head.m11 - head.m10 * distance;

            float pitch = MathF.Asin(
                Math.Clamp(-head.m6, -1.0f, 1.0f)
            );

            float yaw;
            float roll;

            if (MathF.Abs(MathF.Cos(pitch)) > 0.0001f)
            {
                yaw = MathF.Atan2(head.m2, head.m10);
                roll = MathF.Atan2(head.m4, head.m5);
            }
            else
            {
                yaw = MathF.Atan2(-head.m8, head.m0);
                roll = 0;
            }

            const float radiansToDegrees = 180.0f / MathF.PI;

            SetNumber("WorldX", x);
            SetNumber("WorldY", y);
            SetNumber("WorldZ", z);

            SetNumber("WorldPitch", pitch * radiansToDegrees);
            SetNumber("WorldYaw", yaw * radiansToDegrees);
            SetNumber("WorldRoll", roll * radiansToDegrees);

            Log("Панель размещена перед головой.");
            return true;
        }
        catch (Exception exception)
        {
            Log($"Не удалось разместить панель: {exception.Message}");
            return false;
        }
    }

    private bool TryCaptureWindow(
        IntPtr windowHandle,
        out string error
    )
    {
        error = "";

        if (!NativeMethods.IsWindow(windowHandle))
        {
            error =
                "Выбранное окно закрыто. Обнови список и выбери другое.";
            return false;
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            error =
                "Окно свёрнуто. Показывается последний успешный кадр.";
            return false;
        }

        if (!NativeMethods.GetWindowRect(
            windowHandle,
            out NativeMethods.RECT rectangle
        ))
        {
            error = "Не удалось получить размеры окна.";
            return false;
        }

        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;

        if (width <= 0 || height <= 0 ||
            width > 4096 || height > 4096)
        {
            error =
                "Некорректный размер окна или размер больше 4096 пикселей.";
            return false;
        }

        try
        {
            if (captureBitmap == null ||
                captureBitmap.Width != width ||
                captureBitmap.Height != height)
            {
                Bitmap newBitmap = new Bitmap(
                    width,
                    height,
                    PixelFormat.Format32bppArgb
                );

                captureBitmap?.Dispose();
                captureBitmap = newBitmap;
            }

            using Graphics graphics =
                Graphics.FromImage(captureBitmap);

            if (settings.Capture == CaptureMode.PrintWindow)
            {
                graphics.Clear(Color.Black);

                IntPtr deviceContext = graphics.GetHdc();

                bool success;

                try
                {
                    success = NativeMethods.PrintWindow(
                        windowHandle,
                        deviceContext,
                        2
                    );
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }

                if (!success)
                {
                    error =
                        "PrintWindow не смог получить кадр. " +
                        "Попробуй захват области экрана.";
                    return false;
                }
            }
            else
            {
                graphics.CopyFromScreen(
                    rectangle.Left,
                    rectangle.Top,
                    0,
                    0,
                    new Size(width, height),
                    CopyPixelOperation.SourceCopy
                );
            }

            adjustedCaptureBitmap?.Dispose();
            adjustedCaptureBitmap = ApplyCaptureMargins(captureBitmap);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Ошибка захвата: {exception.Message}";
            return false;
        }
    }

    private Bitmap ApplyCaptureMargins(Bitmap source)
    {
        int left = (int)Math.Round(source.Width * settings.CaptureLeft / 100.0);
        int right = (int)Math.Round(source.Width * settings.CaptureRight / 100.0);
        int top = (int)Math.Round(source.Height * settings.CaptureTop / 100.0);
        int bottom = (int)Math.Round(source.Height * settings.CaptureBottom / 100.0);

        // Оставляем как минимум один пиксель исходного изображения.
        left = Math.Clamp(left, -source.Width / 2, source.Width - 1);
        right = Math.Clamp(right, -source.Width / 2, source.Width - 1);
        top = Math.Clamp(top, -source.Height / 2, source.Height - 1);
        bottom = Math.Clamp(bottom, -source.Height / 2, source.Height - 1);

        int sourceX = Math.Max(0, left);
        int sourceY = Math.Max(0, top);
        int sourceRight = Math.Min(source.Width, source.Width - Math.Max(0, right));
        int sourceBottom = Math.Min(source.Height, source.Height - Math.Max(0, bottom));

        int sourceWidth = Math.Max(1, sourceRight - sourceX);
        int sourceHeight = Math.Max(1, sourceBottom - sourceY);

        int destinationX = Math.Max(0, -left);
        int destinationY = Math.Max(0, -top);
        int outputWidth = Math.Max(1, source.Width - left - right);
        int outputHeight = Math.Max(1, source.Height - top - bottom);

        Bitmap result = new Bitmap(
            outputWidth,
            outputHeight,
            PixelFormat.Format32bppArgb
        );

        using Graphics graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Black);
        graphics.DrawImage(
            source,
            new Rectangle(destinationX, destinationY, sourceWidth, sourceHeight),
            new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight),
            GraphicsUnit.Pixel
        );

        return result;
    }

    private void EnsureGraphicsDevice()
    {
        if (graphicsDevice != null)
        {
            return;
        }

        int adapterIndex = -1;
        OpenVR.System.GetDXGIOutputInfo(ref adapterIndex);

        if (adapterIndex < 0)
        {
            throw new InvalidOperationException(
                "SteamVR не сообщил индекс видеокарты."
            );
        }

        using SharpDX.DXGI.Factory1 factory =
            new SharpDX.DXGI.Factory1();

        using SharpDX.DXGI.Adapter1 adapter =
            factory.GetAdapter1(adapterIndex);

        graphicsDevice = new SharpDX.Direct3D11.Device(
            adapter,
            SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport
        );

        Log(
            "Видеокарта: " +
            adapter.Description1.Description.Trim()
        );
    }

    private void UploadBitmap(Bitmap bitmap)
    {
        EnsureGraphicsDevice();

        SharpDX.Direct3D11.Device device = graphicsDevice
            ?? throw new InvalidOperationException(
                "Устройство Direct3D не создано."
            );

        int rowBytes = checked(bitmap.Width * 4);
        int requiredLength = checked(rowBytes * bitmap.Height);

        if (pixelBuffer.Length != requiredLength)
        {
            pixelBuffer = new byte[requiredLength];
        }

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );

        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    pixelBuffer,
                    y * rowBytes,
                    rowBytes
                );
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        for (int i = 0; i < pixelBuffer.Length; i += 4)
        {
            byte blue = pixelBuffer[i];

            pixelBuffer[i] = pixelBuffer[i + 2];
            pixelBuffer[i + 2] = blue;
            pixelBuffer[i + 3] = 255;
        }

        bool recreate =
            overlayTexture == null ||
            textureWidth != bitmap.Width ||
            textureHeight != bitmap.Height;

        if (recreate)
        {
            SharpDX.Direct3D11.Texture2DDescription description =
                new SharpDX.Direct3D11.Texture2DDescription
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm,
                    SampleDescription =
                        new SharpDX.DXGI.SampleDescription(1, 0),
                    Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                    BindFlags =
                        SharpDX.Direct3D11.BindFlags.ShaderResource |
                        SharpDX.Direct3D11.BindFlags.RenderTarget,
                    CpuAccessFlags =
                        SharpDX.Direct3D11.CpuAccessFlags.None,
                    OptionFlags =
                        SharpDX.Direct3D11.ResourceOptionFlags.Shared
                };

            SharpDX.Direct3D11.Texture2D newTexture =
                new SharpDX.Direct3D11.Texture2D(device, description);

            try
            {
                if (overlayTexture != null)
                {
                    Check(
                        OpenVR.Overlay.ClearOverlayTexture(
                            overlayHandle
                        ),
                        "ClearOverlayTexture"
                    );
                }
            }
            catch
            {
                newTexture.Dispose();
                throw;
            }

            overlayTexture?.Dispose();

            overlayTexture = newTexture;
            textureWidth = bitmap.Width;
            textureHeight = bitmap.Height;
        }

        SharpDX.Direct3D11.Texture2D texture = overlayTexture
            ?? throw new InvalidOperationException(
                "Текстура не создана."
            );

        GCHandle pinnedPixels = GCHandle.Alloc(
            pixelBuffer,
            GCHandleType.Pinned
        );

        try
        {
            SharpDX.DataBox source = new SharpDX.DataBox(
                pinnedPixels.AddrOfPinnedObject(),
                rowBytes,
                0
            );

            device.ImmediateContext.UpdateSubresource(
                source,
                texture,
                0
            );

            device.ImmediateContext.Flush();
        }
        finally
        {
            pinnedPixels.Free();
        }

        Texture_t vrTexture = new Texture_t
        {
            handle = texture.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Gamma
        };

        Check(
            OpenVR.Overlay.SetOverlayTexture(
                overlayHandle,
                ref vrTexture
            ),
            "SetOverlayTexture"
        );
    }

    private void SetVisible(bool visible)
    {
        if (visible == panelVisible)
        {
            return;
        }

        if (visible)
        {
            Check(
                OpenVR.Overlay.ShowOverlay(overlayHandle),
                "ShowOverlay"
            );
        }
        else
        {
            Check(
                OpenVR.Overlay.HideOverlay(overlayHandle),
                "HideOverlay"
            );
        }

        panelVisible = visible;
    }

    private void ResetTexture()
    {
        if (overlayTexture != null)
        {
            Check(
                OpenVR.Overlay.ClearOverlayTexture(overlayHandle),
                "ClearOverlayTexture"
            );

            overlayTexture.Dispose();
            overlayTexture = null;
        }

        textureWidth = 0;
        textureHeight = 0;
        hasUploadedFrame = false;
    }

    private static HmdMatrix34_t MakeTransform(
        float x,
        float y,
        float z,
        float pitchDegrees,
        float yawDegrees,
        float rollDegrees
    )
    {
        const float degreesToRadians = MathF.PI / 180.0f;

        float pitch = pitchDegrees * degreesToRadians;
        float yaw = yawDegrees * degreesToRadians;
        float roll = rollDegrees * degreesToRadians;

        float sx = MathF.Sin(pitch);
        float cx = MathF.Cos(pitch);

        float sy = MathF.Sin(yaw);
        float cy = MathF.Cos(yaw);

        float sz = MathF.Sin(roll);
        float cz = MathF.Cos(roll);

        // Порядок вращений: Ry * Rx * Rz.
        return new HmdMatrix34_t
        {
            m0 = cy * cz + sy * sx * sz,
            m1 = -cy * sz + sy * sx * cz,
            m2 = sy * cx,
            m3 = x,

            m4 = cx * sz,
            m5 = cx * cz,
            m6 = -sx,
            m7 = y,

            m8 = -sy * cz + cy * sx * sz,
            m9 = sy * sz + cy * sx * cz,
            m10 = cy * cx,
            m11 = z
        };
    }

    private static HmdMatrix34_t Multiply(
        HmdMatrix34_t a,
        HmdMatrix34_t b
    )
    {
        return new HmdMatrix34_t
        {
            m0 = a.m0 * b.m0 + a.m1 * b.m4 + a.m2 * b.m8,
            m1 = a.m0 * b.m1 + a.m1 * b.m5 + a.m2 * b.m9,
            m2 = a.m0 * b.m2 + a.m1 * b.m6 + a.m2 * b.m10,
            m3 = a.m0 * b.m3 + a.m1 * b.m7 + a.m2 * b.m11 + a.m3,

            m4 = a.m4 * b.m0 + a.m5 * b.m4 + a.m6 * b.m8,
            m5 = a.m4 * b.m1 + a.m5 * b.m5 + a.m6 * b.m9,
            m6 = a.m4 * b.m2 + a.m5 * b.m6 + a.m6 * b.m10,
            m7 = a.m4 * b.m3 + a.m5 * b.m7 + a.m6 * b.m11 + a.m7,

            m8 = a.m8 * b.m0 + a.m9 * b.m4 + a.m10 * b.m8,
            m9 = a.m8 * b.m1 + a.m9 * b.m5 + a.m10 * b.m9,
            m10 = a.m8 * b.m2 + a.m9 * b.m6 + a.m10 * b.m10,
            m11 = a.m8 * b.m3 + a.m9 * b.m7 + a.m10 * b.m11 + a.m11
        };
    }

    private static void Check(
        EVROverlayError error,
        string operation
    )
    {
        if (error != EVROverlayError.None)
        {
            throw new OverlayException(error, operation);
        }
    }

    private void LogVrError(string message)
    {
        long now = Environment.TickCount64;

        if (message != lastVrError || now >= nextVrErrorLog)
        {
            Log(message);
            lastVrError = message;
            nextVrErrorLog = now + 3000;
        }
    }

    private void Log(string message)
    {
        if (logBox.TextLength > 30000)
        {
            logBox.Clear();
        }

        logBox.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {message}" +
            Environment.NewLine
        );
    }

    private void ReleaseSteamVr()
    {
        try
        {
            if (initialized)
            {
                if (overlayHandle != 0)
                {
                    OpenVR.Overlay.HideOverlay(overlayHandle);
                    OpenVR.Overlay.ClearOverlayTexture(overlayHandle);
                    OpenVR.Overlay.DestroyOverlay(overlayHandle);
                }

                OpenVR.Shutdown();
            }
        }
        catch (Exception exception)
        {
            Log($"Ошибка отключения: {exception.Message}");
        }
        finally
        {
            overlayHandle = 0;
            initialized = false;
            panelVisible = false;
            hasUploadedFrame = false;

            overlayTexture?.Dispose();
            overlayTexture = null;

            textureWidth = 0;
            textureHeight = 0;

            graphicsDevice?.Dispose();
            graphicsDevice = null;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (e.Cancel)
        {
            return;
        }

        shuttingDown = true;
        renderTimer.Stop();
        SaveSettings();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        renderTimer.Dispose();
        ReleaseSteamVr();

        captureBitmap?.Dispose();
        captureBitmap = null;
        adjustedCaptureBitmap?.Dispose();
        adjustedCaptureBitmap = null;

        base.OnFormClosed(e);
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(
        IntPtr windowHandle,
        IntPtr parameter
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr parameter
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maxCount
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr windowHandle,
        out RECT rectangle
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(
        IntPtr windowHandle,
        IntPtr deviceContext,
        uint flags
    );

    public static string ReadWindowTitle(IntPtr windowHandle)
    {
        int length = GetWindowTextLength(windowHandle);

        if (length <= 0)
        {
            return "";
        }

        StringBuilder title = new StringBuilder(length + 1);

        GetWindowText(
            windowHandle,
            title,
            title.Capacity
        );

        return title.ToString();
    }

    public static List<WindowItem> GetWindows(
        IntPtr excludedWindow
    )
    {
        List<WindowItem> windows = new List<WindowItem>();

        EnumWindows(
            (windowHandle, parameter) =>
            {
                if (windowHandle == excludedWindow ||
                    !IsWindowVisible(windowHandle))
                {
                    return true;
                }

                string title = ReadWindowTitle(windowHandle);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    windows.Add(new WindowItem(windowHandle, title));
                }

                return true;
            },
            IntPtr.Zero
        );

        windows.Sort(
            (first, second) => string.Compare(
                first.Title,
                second.Title,
                StringComparison.CurrentCultureIgnoreCase
            )
        );

        return windows;
    }
}