using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Heh8080.Devices;
using Heh8080.Terminal;
using Heh8080.UI.Diagnostics;
using SkiaSharp;

namespace Heh8080.UI.Controls;

/// <summary>
/// Retro CRT terminal control with green phosphor effect.
/// Renders an Adm3aTerminal buffer with shader-based CRT visual effects.
/// </summary>
public class RetroTerminalControl : Control
{
    private Adm3aTerminal? _terminal;
    private Cp500VideoDevice? _cp500Video;
    private Cp500KeyboardDevice? _cp500Keyboard;
    private int _cp500PulseGeneration;
    private (int Row, int Column, bool Shift)? _cp500ActivePulse;
    private int _cp500PulseScanCount;
    private readonly Queue<(int Row, int Column, bool Shift)> _cp500PulseQueue = new();
    private readonly HashSet<Key> _cp500HeldSpecialKeys = new();
    private double _charWidth;
    private double _charHeight;
    private bool _hasMeasured;

    // Base dimensions at 100% scale - FJM-3A style
    private const double BaseFontSize = 28.0;
    private const double BaseBezelSize = 48;
    private const double BaseInnerBezelSize = 12;  // Dark frame around screen
    private const double BaseScreenCornerRadius = 40;
    private const double BaseHousingCornerRadius = 16;  // Rounded corners on outer housing
    private const double BaseInnerBezelCornerRadius = 44;  // Rounded corners on inner dark bezel
    private const double BaseScreenPaddingHorizontal = 120;  // Keep content away from curved edges
    private const double BaseScreenPaddingVertical = 80;     // Vertical padding from curved edges
    private const double BaseLogoWidth = 80;
    private const double BaseLogoHeight = 24;
    private const double BaseLogoMargin = 8;

    // Scaled dimensions (computed from Scale property)
    private double _bezelSize;
    private double _innerBezelSize;
    private double _screenCornerRadius;
    private float _housingCornerRadius;
    private float _innerBezelCornerRadius;
    private double _screenPaddingHorizontal;
    private double _screenPaddingVertical;
    private double _logoWidth;
    private double _logoHeight;
    private double _logoMargin;

    // Scale button areas
    private Rect[] _scaleButtonRects = new Rect[4];
    private static readonly double[] AvailableScales = { 1.0, 0.8, 0.6, 0.4 };
    private static readonly string[] ScaleLabels = { "100%", "80%", "60%", "40%" };
    private Rect _logoRect;

    /// <summary>
    /// Event fired when the FJM-3A logo is clicked.
    /// </summary>
    public event Action? LogoClicked;

    /// <summary>
    /// Event fired when user clicks on non-interactive area (for window dragging).
    /// </summary>
    public event Action<PointerPressedEventArgs>? DragMoveRequested;

    /// <summary>
    /// Event fired when the exit button is clicked.
    /// </summary>
    public event Action? ExitClicked;

    // Exit button area
    private Rect _exitButtonRect;
    private bool _exitPending;

    // Green phosphor colors (P1 phosphor)
    private static readonly SKColor BackgroundColor = new(0x0A, 0x14, 0x0A);
    private static readonly SKColor ForegroundColor = new(0x33, 0xFF, 0x33);
    private static readonly SKColor GlowColor = new(0x40, 0x33, 0xFF, 0x33);

    // Avalonia brushes for bezel - light gray like ADM-3A housing
    private static readonly Color BezelColorLight = Color.FromRgb(0xB8, 0xB8, 0xB0);  // Light gray
    private static readonly Color BezelColorDark = Color.FromRgb(0x90, 0x90, 0x88);   // Shadow
    private static readonly Color BezelColorHighlight = Color.FromRgb(0xD0, 0xD0, 0xC8); // Highlight
    private static readonly Color ScreenBezelColor = Color.FromRgb(0x50, 0x50, 0x48);  // Inner bezel base
    private static readonly Color ScreenBezelShadow = Color.FromRgb(0x30, 0x30, 0x28);  // Inner bezel shadow
    private static readonly Color ScreenBezelHighlight = Color.FromRgb(0x68, 0x68, 0x60);  // Inner bezel highlight
    private static readonly TimeSpan Cp500KeyPulseDuration = TimeSpan.FromMilliseconds(50);

    private SKTypeface? _typeface;

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<RetroTerminalControl, double>(nameof(Scale), 1.0);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<RetroTerminalControl, double>(nameof(FontSize), BaseFontSize);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<RetroTerminalControl, FontFamily>(nameof(FontFamily),
            new FontFamily(GetDefaultMonospaceFont()));

    /// <summary>
    /// Gets or sets the display scale (1.0 = 100%, 0.5 = 50%, etc.)
    /// </summary>
    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>
    /// Event fired when the scale changes.
    /// </summary>
    public event Action<double>? ScaleChanged;

    private static string GetDefaultMonospaceFont()
    {
        if (OperatingSystem.IsBrowser())
            return "monospace";
        if (OperatingSystem.IsWindows())
            return "Consolas";
        if (OperatingSystem.IsMacOS())
            return "Menlo";
        return "DejaVu Sans Mono";
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public Adm3aTerminal? Terminal
    {
        get => _terminal;
        set
        {
            if (_terminal != null)
                _terminal.ContentChanged -= OnContentChanged;

            _terminal = value;

            if (_terminal != null)
                _terminal.ContentChanged += OnContentChanged;

            InvalidateVisual();
        }
    }

    public Cp500VideoDevice? Cp500Video
    {
        get => _cp500Video;
        set
        {
            if (_cp500Video != null)
                _cp500Video.CharacterChanged -= OnCp500CharacterChanged;

            _cp500Video = value;
            if (_cp500Video != null)
                _cp500Video.CharacterChanged += OnCp500CharacterChanged;

            MeasureCharacterSize();
            InvalidateVisual();
        }
    }

    public Cp500KeyboardDevice? Cp500Keyboard
    {
        get => _cp500Keyboard;
        set
        {
            if (_cp500Keyboard != null)
                _cp500Keyboard.Matrix.ReadObserved -= OnCp500MatrixRead;

            _cp500Keyboard = value;
            if (_cp500Keyboard != null)
                _cp500Keyboard.Matrix.ReadObserved += OnCp500MatrixRead;
        }
    }

    public RetroTerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        UpdateScaledDimensions();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ScaleProperty)
        {
            // Update font size based on scale
            SetValue(FontSizeProperty, BaseFontSize * Scale);
            UpdateScaledDimensions();
            _typeface = null;
            MeasureCharacterSize();
            InvalidateVisual();
            ScaleChanged?.Invoke(Scale);
        }
        else if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty)
        {
            _typeface = null;
            MeasureCharacterSize();
            InvalidateVisual();
        }
    }

    private void UpdateScaledDimensions()
    {
        var scale = Scale;
        _bezelSize = BaseBezelSize * scale;
        _innerBezelSize = BaseInnerBezelSize * scale;
        _screenCornerRadius = BaseScreenCornerRadius * scale;
        _housingCornerRadius = (float)(BaseHousingCornerRadius * scale);
        _innerBezelCornerRadius = (float)(BaseInnerBezelCornerRadius * scale);
        _screenPaddingHorizontal = BaseScreenPaddingHorizontal * scale;
        _screenPaddingVertical = BaseScreenPaddingVertical * scale;
        _logoWidth = BaseLogoWidth * scale;
        _logoHeight = BaseLogoHeight * scale;
        _logoMargin = BaseLogoMargin * scale;
    }

    private void OnContentChanged()
    {
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    private void OnCp500CharacterChanged(int column, int row, byte character) =>
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeasureCharacterSize();
    }

    private void MeasureCharacterSize()
    {
        // Use SkiaSharp to measure
        var fontName = GetDefaultMonospaceFont();
        _typeface = SKTypeface.FromFamilyName(fontName, SKFontStyle.Normal);

        using var font = new SKFont(_typeface, (float)FontSize);
        using var paint = new SKPaint(font);

        _charWidth = paint.MeasureText("M");
        var metrics = font.Metrics;
        _charHeight = metrics.Descent - metrics.Ascent;
        _hasMeasured = true;

        // Set preferred size based on 80x24 grid with 4:3 screen aspect ratio
        int columns = _cp500Video == null ? TerminalBuffer.Width : Cp500VideoDevice.Columns;
        int rows = _cp500Video == null ? TerminalBuffer.Height : Cp500VideoDevice.Rows;
        var terminalWidth = _charWidth * columns;
        var terminalHeight = _charHeight * rows;

        // Screen width is terminal + horizontal padding (using scaled values)
        var screenWidth = terminalWidth + (_screenPaddingHorizontal * 2);
        // Screen height is calculated for 4:3 aspect ratio
        var screenHeight = screenWidth * 3.0 / 4.0;

        Width = screenWidth + (_bezelSize * 2);
        Height = screenHeight + (_bezelSize * 2);
    }

    public override void Render(DrawingContext context)
    {
        if (!_hasMeasured)
            MeasureCharacterSize();

        var bounds = new Rect(Bounds.Size);
        int columns = _cp500Video == null ? TerminalBuffer.Width : Cp500VideoDevice.Columns;
        int rows = _cp500Video == null ? TerminalBuffer.Height : Cp500VideoDevice.Rows;
        var terminalWidth = _charWidth * columns;
        var terminalHeight = _charHeight * rows;
        // Screen area with 4:3 aspect ratio (using scaled padding)
        var screenWidth = terminalWidth + (_screenPaddingHorizontal * 2);
        var screenHeight = screenWidth * 3.0 / 4.0;
        var screenRect = new Rect(_bezelSize, _bezelSize, screenWidth, screenHeight);

        // Draw FJM-3A style housing with shading (rounded corners)
        // Base color
        var baseBrush = new SolidColorBrush(BezelColorLight);
        context.FillRectangle(baseBrush, bounds, _housingCornerRadius);

        // Top edge highlight (outer surface catches light)
        var topHighlight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0.08, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(BezelColorHighlight, 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        context.FillRectangle(topHighlight, bounds, _housingCornerRadius);

        // Bottom shadow (outer surface)
        var bottomShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.92, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(BezelColorDark, 1)
            }
        };
        context.FillRectangle(bottomShadow, bounds, _housingCornerRadius);

        // Left edge highlight (outer surface)
        var leftHighlight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.05, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(30, 255, 255, 255), 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        context.FillRectangle(leftHighlight, bounds, _housingCornerRadius);

        // Right shadow (outer surface)
        var rightShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.95, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Color.FromArgb(40, 0, 0, 0), 1)
            }
        };
        context.FillRectangle(rightShadow, bounds, _housingCornerRadius);

        // Draw inner bezel (frame around screen) with depth shading
        var innerBezelRect = new Rect(
            _bezelSize - _innerBezelSize,
            _bezelSize - _innerBezelSize,
            screenWidth + (_innerBezelSize * 2),
            screenHeight + (_innerBezelSize * 2));

        // Base color
        var innerBezelBrush = new SolidColorBrush(ScreenBezelColor);
        context.FillRectangle(innerBezelBrush, innerBezelRect, _innerBezelCornerRadius);

        // Top shadow (inset, so top is darker)
        var innerTopShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ScreenBezelShadow, 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        context.FillRectangle(innerTopShadow, innerBezelRect, _innerBezelCornerRadius);

        // Left shadow (inset)
        var innerLeftShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.15, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(80, 0, 0, 0), 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        context.FillRectangle(innerLeftShadow, innerBezelRect, _innerBezelCornerRadius);

        // Bottom highlight (inset, so bottom catches light)
        var innerBottomHighlight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.85, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(ScreenBezelHighlight, 1)
            }
        };
        context.FillRectangle(innerBottomHighlight, innerBezelRect, _innerBezelCornerRadius);

        // Right highlight (inset)
        var innerRightHighlight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.9, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Color.FromArgb(40, 255, 255, 255), 1)
            }
        };
        context.FillRectangle(innerRightHighlight, innerBezelRect, _innerBezelCornerRadius);

        // Use custom draw operation for CRT effect (includes edge shadow in shader)
        var operation = new TerminalDrawOperation(
            screenRect, _terminal, _cp500Video, _typeface, (float)FontSize,
            (float)_charWidth, (float)_charHeight, _screenCornerRadius);

        context.Custom(operation);

        // Draw FJM-3A logo button in top-left bezel area
        var logoCornerRadius = (float)(4 * Scale);
        _logoRect = new Rect(_logoMargin, _logoMargin, _logoWidth, _logoHeight);

        // Logo background - dark inset
        var logoBackground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x60, 0x60, 0x58), 0),
                new GradientStop(Color.FromRgb(0x80, 0x80, 0x78), 1)
            }
        };
        context.FillRectangle(logoBackground, _logoRect, logoCornerRadius);

        // Logo border
        var logoBorder = new Pen(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x48)), 1);
        context.DrawRectangle(null, logoBorder, _logoRect, logoCornerRadius, logoCornerRadius);

        // Logo text
        var logoFontSize = 12 * Scale;
        var logoText = new FormattedText(
            _cp500Video == null ? "FJM-3A" : "CP-500",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold),
            logoFontSize,
            new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33)));

        var textX = _logoRect.X + (_logoRect.Width - logoText.Width) / 2;
        var textY = _logoRect.Y + (_logoRect.Height - logoText.Height) / 2;
        context.DrawText(logoText, new Point(textX, textY));

        // Draw scale buttons in top-right bezel area
        RenderScaleButtons(context, bounds);
    }

    private void RenderScaleButtons(DrawingContext context, Rect bounds)
    {
        // Button dimensions (scaled)
        var buttonWidth = 36 * Scale;
        var buttonHeight = 20 * Scale;
        var buttonSpacing = 4 * Scale;
        var buttonMargin = _logoMargin;
        var buttonCornerRadius = (float)(3 * Scale);
        var buttonFontSize = 9 * Scale;

        // Position buttons from right edge
        var startX = bounds.Width - buttonMargin - (buttonWidth * 4) - (buttonSpacing * 3);
        var buttonY = buttonMargin;

        for (int i = 0; i < 4; i++)
        {
            var buttonX = startX + i * (buttonWidth + buttonSpacing);
            var buttonRect = new Rect(buttonX, buttonY, buttonWidth, buttonHeight);
            _scaleButtonRects[i] = buttonRect;

            var isSelected = Math.Abs(Scale - AvailableScales[i]) < 0.01;

            // Button background
            var bgColor = isSelected
                ? Color.FromRgb(0x33, 0x88, 0x33)  // Highlighted green for selected
                : Color.FromRgb(0x60, 0x60, 0x58);
            var bgBrush = new SolidColorBrush(bgColor);
            context.FillRectangle(bgBrush, buttonRect, buttonCornerRadius);

            // Button border
            var borderColor = isSelected
                ? Color.FromRgb(0x33, 0xFF, 0x33)
                : Color.FromRgb(0x50, 0x50, 0x48);
            var borderPen = new Pen(new SolidColorBrush(borderColor), 1);
            context.DrawRectangle(null, borderPen, buttonRect, buttonCornerRadius, buttonCornerRadius);

            // Button text
            var textColor = isSelected
                ? Color.FromRgb(0xFF, 0xFF, 0xFF)
                : Color.FromRgb(0xB0, 0xB0, 0xA8);
            var buttonText = new FormattedText(
                ScaleLabels[i],
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle.Normal, FontWeight.Normal),
                buttonFontSize,
                new SolidColorBrush(textColor));

            var txtX = buttonRect.X + (buttonRect.Width - buttonText.Width) / 2;
            var txtY = buttonRect.Y + (buttonRect.Height - buttonText.Height) / 2;
            context.DrawText(buttonText, new Point(txtX, txtY));
        }

        // Draw exit button in bottom-right corner
        RenderExitButton(context, bounds);
    }

    private void RenderExitButton(DrawingContext context, Rect bounds)
    {
        // Rectangular button, 2x width
        var buttonHeight = 20 * Scale;
        var buttonWidth = 40 * Scale;
        var buttonMargin = _logoMargin;
        var buttonCornerRadius = (float)(3 * Scale);

        // Position in bottom-right corner
        var buttonX = bounds.Width - buttonMargin - buttonWidth;
        var buttonY = bounds.Height - buttonMargin - buttonHeight;
        _exitButtonRect = new Rect(buttonX, buttonY, buttonWidth, buttonHeight);

        // Green normally, dark red when exit pending
        var bgColor = _exitPending
            ? Color.FromRgb(0x88, 0x22, 0x22)  // Dark red
            : Color.FromRgb(0x33, 0xAA, 0x33); // Green
        var bgBrush = new SolidColorBrush(bgColor);
        context.FillRectangle(bgBrush, _exitButtonRect, buttonCornerRadius);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_cp500Keyboard != null && !string.IsNullOrEmpty(e.Text))
        {
            foreach (char character in e.Text)
            {
                if (TryMapCp500Character(character, out int row, out int column, out bool shift))
                {
                    Cp500DiagnosticTrace.Write(
                        $"text-input char=U+{(int)character:X4} row={row} column={column} shift={shift}");
                    PulseCp500Key(row, column, shift);
                    e.Handled = true;
                }
            }
            return;
        }

        if (_terminal == null || string.IsNullOrEmpty(e.Text))
            return;

        // ADM-3A only supports printable ASCII (0x20-0x7E)
        foreach (char c in e.Text)
        {
            if (c >= 0x20 && c <= 0x7E)
            {
                _terminal.QueueInput((byte)c);
                e.Handled = true;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_cp500Keyboard != null)
        {
            if (TryMapCp500SpecialKey(e.Key, out int row, out int column))
            {
                if (_cp500HeldSpecialKeys.Add(e.Key))
                {
                    Cp500DiagnosticTrace.Write($"special-key-down key={e.Key} row={row} column={column}");
                    PulseCp500Key(row, column, shift: false);
                }
                e.Handled = true;
            }
            return;
        }

        if (_terminal == null)
            return;

        byte? keyByte = e.Key switch
        {
            Key.Return or Key.Enter => 0x0D,
            Key.Back => 0x08,
            Key.Delete => 0x7F,
            Key.Escape => 0x1B,
            Key.Tab => 0x09,
            Key.Up => 0x0B,
            Key.Down => 0x0A,
            Key.Left => 0x08,
            Key.Right => 0x0C,
            _ => null
        };

        if (keyByte == null && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key >= Key.A && e.Key <= Key.Z)
                keyByte = (byte)(1 + (e.Key - Key.A));
        }

        if (keyByte.HasValue)
        {
            _terminal.QueueInput(keyByte.Value);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_cp500Keyboard != null &&
            TryMapCp500SpecialKey(e.Key, out int row, out int column))
        {
            Cp500DiagnosticTrace.Write($"special-key-up key={e.Key} row={row} column={column}");
            _cp500HeldSpecialKeys.Remove(e.Key);
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        Cp500DiagnosticTrace.Write("terminal-focus-lost");
        _cp500PulseGeneration++;
        _cp500ActivePulse = null;
        _cp500PulseScanCount = 0;
        _cp500PulseQueue.Clear();
        _cp500HeldSpecialKeys.Clear();
        _cp500Keyboard?.ReleaseAll();
        base.OnLostFocus(e);
    }

    private void PulseCp500Key(int row, int column, bool shift)
    {
        _cp500PulseQueue.Enqueue((row, column, shift));
        if (_cp500ActivePulse == null)
            StartNextCp500Pulse();
    }

    private void StartNextCp500Pulse()
    {
        if (_cp500Keyboard == null || _cp500PulseQueue.Count == 0)
        {
            _cp500ActivePulse = null;
            return;
        }

        Cp500KeyboardDevice keyboard = _cp500Keyboard;
        int generation = _cp500PulseGeneration;
        var pulse = _cp500PulseQueue.Dequeue();
        _cp500ActivePulse = pulse;
        _cp500PulseScanCount = 0;
        Cp500DiagnosticTrace.Write(
            $"pulse-start row={pulse.Row} column={pulse.Column} shift={pulse.Shift} queued={_cp500PulseQueue.Count}");
        if (pulse.Shift)
            keyboard.SetKeyState(7, 0, true);
        keyboard.SetKeyState(pulse.Row, pulse.Column, true);
        DispatcherTimer.RunOnce(() =>
        {
            EndCp500Pulse(generation, pulse);
        }, Cp500KeyPulseDuration);
    }

    private void OnCp500MatrixRead(byte rowMask, byte value)
    {
        var pulse = _cp500ActivePulse;
        if (pulse == null)
            return;

        byte selectedRow = (byte)(1 << pulse.Value.Row);
        byte pressedColumn = (byte)(1 << pulse.Value.Column);
        if ((rowMask & selectedRow) == 0 || (value & pressedColumn) == 0)
            return;

        Interlocked.Increment(ref _cp500PulseScanCount);
    }

    private void EndCp500Pulse(int generation, (int Row, int Column, bool Shift) pulse)
    {
        if (generation != _cp500PulseGeneration || _cp500ActivePulse == null || _cp500Keyboard == null)
            return;

        _cp500Keyboard.SetKeyState(pulse.Row, pulse.Column, false);
        if (pulse.Shift)
            _cp500Keyboard.SetKeyState(7, 0, false);
        Cp500DiagnosticTrace.Write(
            $"pulse-end row={pulse.Row} column={pulse.Column} shift={pulse.Shift} scans={_cp500PulseScanCount}");
        _cp500ActivePulse = null;
        StartNextCp500Pulse();
    }

    private static bool TryMapCp500SpecialKey(Key key, out int row, out int column)
    {
        (row, column) = key switch
        {
            Key.Return or Key.Enter => (6, 0),
            Key.Back => (6, 1),
            Key.Escape => (6, 2),
            Key.Up => (6, 3),
            Key.Down => (6, 4),
            Key.Left => (6, 5),
            Key.Right => (6, 6),
            _ => (-1, -1)
        };
        return row >= 0;
    }

    private static bool TryMapCp500Character(char character, out int row, out int column, out bool shift)
    {
        shift = false;
        char c = char.ToUpperInvariant(character);

        if (c >= 'A' && c <= 'G') { row = 0; column = c - 'A' + 1; return true; }
        if (c >= 'H' && c <= 'O') { row = 1; column = c - 'H'; return true; }
        if (c >= 'P' && c <= 'W') { row = 2; column = c - 'P'; return true; }
        if (c >= 'X' && c <= 'Z') { row = 3; column = c - 'X'; return true; }
        if (c >= '0' && c <= '7') { row = 4; column = c - '0'; return true; }
        if (c is '8' or '9') { row = 5; column = c - '8'; return true; }

        (row, column, shift) = c switch
        {
            '@' => (0, 0, false),
            '[' => (3, 3, false), '\\' => (3, 4, false), ']' => (3, 5, false),
            '^' => (3, 6, false), '_' => (3, 7, false),
            ':' => (5, 2, false), ';' => (5, 3, false), ',' => (5, 4, false),
            '-' => (5, 5, false), '.' => (5, 6, false), '/' => (5, 7, false),
            ' ' => (6, 7, false),
            '!' => (4, 1, true), '"' => (4, 2, true), '#' => (4, 3, true),
            '$' => (4, 4, true), '%' => (4, 5, true), '&' => (4, 6, true),
            '\'' => (4, 7, true), '(' => (5, 0, true), ')' => (5, 1, true),
            '*' => (5, 2, true), '+' => (5, 3, true), '<' => (5, 4, true),
            '=' => (5, 5, true), '>' => (5, 6, true), '?' => (5, 7, true),
            _ => (-1, -1, false)
        };
        return row >= 0;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);

        // Check exit button first
        if (_exitButtonRect.Contains(point))
        {
            // Show dark red color before exiting
            _exitPending = true;
            InvalidateVisual();

            // Process messages so color change is visible, then exit
            Dispatcher.UIThread.Post(() => ExitClicked?.Invoke(), DispatcherPriority.Background);
            e.Handled = true;
            return;
        }

        // Check scale buttons
        for (int i = 0; i < _scaleButtonRects.Length; i++)
        {
            if (_scaleButtonRects[i].Contains(point))
            {
                Scale = AvailableScales[i];
                e.Handled = true;
                return;
            }
        }

        if (_logoRect.Contains(point))
        {
            LogoClicked?.Invoke();
            e.Handled = true;
        }
        else
        {
            // Focus the control for keyboard input
            Focus();

            // Request window drag move for non-button clicks
            DragMoveRequested?.Invoke(e);
        }
    }
}

/// <summary>
/// Custom draw operation that renders terminal with CRT shader effect.
/// </summary>
internal class TerminalDrawOperation : ICustomDrawOperation
{
    private static SKRuntimeEffect? _crtEffect;
    private static bool _shaderFailed;

    private readonly Rect _bounds;
    private readonly Adm3aTerminal? _terminal;
    private readonly Cp500VideoDevice? _cp500Video;
    private readonly SKTypeface? _typeface;
    private readonly float _fontSize;
    private readonly float _charWidth;
    private readonly float _charHeight;
    private readonly float _cornerRadius;

    // CRT shader with barrel distortion, scanlines, bloom, vignette, edge shadow
    private const string CrtShaderSource = @"
uniform shader content;
uniform float2 resolution;
uniform float2 offset;

half4 main(float2 fragCoord) {
    float2 uv = (fragCoord - offset) / resolution;

    // Strong barrel distortion
    float2 centered = uv - 0.5;
    float r2 = dot(centered, centered);
    float distortion = 1.0 + 0.3 * r2 + 0.2 * r2 * r2;
    float2 distortedUv = centered * distortion + 0.5;

    // Check if outside the curved CRT boundary (in distorted space)
    // Use superellipse for curved edges: pow(x^n + y^n, 1/n)
    float2 fromCenter = abs(distortedUv - 0.5) * 2.0;
    float n = 7.0;  // Controls curvature: 2=ellipse, higher=more rectangular
    float curvedDist = pow(pow(fromCenter.x, n) + pow(fromCenter.y, n), 1.0/n);

    // Outside curved boundary - return transparent
    if (curvedDist > 1.0) {
        return half4(0.0, 0.0, 0.0, 0.0);
    }

    // Smooth edge for anti-aliasing
    float edgeAA = 1.0 - clamp((curvedDist - 0.98) / 0.02, 0.0, 1.0);

    // Sample main color
    float2 texel = 1.0 / resolution;
    half4 color = sample(content, distortedUv * resolution);

    // Simple 8-tap bloom
    half4 bloom = sample(content, (distortedUv + float2(-2.0, 0.0) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(2.0, 0.0) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(0.0, -2.0) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(0.0, 2.0) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(-1.5, -1.5) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(1.5, -1.5) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(-1.5, 1.5) * texel) * resolution);
    bloom += sample(content, (distortedUv + float2(1.5, 1.5) * texel) * resolution);
    bloom *= 0.125;
    color.rgb += bloom.rgb * 0.5;

    // Prominent scanlines
    float scanlinePos = fract(fragCoord.y / 3.0);
    float scanline = scanlinePos >= 0.5 ? 1.0 : 0.0;
    color.rgb *= 0.7 + 0.3 * scanline;

    // Phosphor tint
    color.g *= 1.1;

    // Vignette
    float vig = 1.0 - dot(centered * 1.4, centered * 1.4);
    vig = clamp(vig, 0.0, 1.0);
    color.rgb *= 0.5 + 0.5 * vig;

    // Edge shadow
    float t = clamp((curvedDist - 0.7) / 0.3, 0.0, 1.0);
    float edgeShadow = 1.0 - t * t * (3.0 - 2.0 * t);
    color.rgb *= 0.5 + 0.5 * edgeShadow;

    // Apply edge anti-aliasing
    color.a = edgeAA;

    return color;
}
";

    public TerminalDrawOperation(Rect bounds, Adm3aTerminal? terminal, Cp500VideoDevice? cp500Video,
        SKTypeface? typeface, float fontSize, float charWidth, float charHeight, double cornerRadius)
    {
        _bounds = bounds;
        _terminal = terminal;
        _cp500Video = cp500Video;
        _typeface = typeface;
        _fontSize = fontSize;
        _charWidth = charWidth;
        _charHeight = charHeight;
        _cornerRadius = (float)cornerRadius;
    }

    public Rect Bounds => _bounds;
    public bool HitTest(Point p) => _bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        int width = (int)_bounds.Width;
        int height = (int)_bounds.Height;

        // Create offscreen surface for terminal content
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        if (surface == null)
        {
            RenderTerminalDirect(canvas);
            return;
        }

        var offscreen = surface.Canvas;

        // Render terminal to offscreen
        RenderTerminalContent(offscreen, width, height);

        // Get image from offscreen
        using var image = surface.Snapshot();

        // Try to apply CRT shader
        if (_crtEffect == null && !_shaderFailed)
        {
            _crtEffect = SKRuntimeEffect.Create(CrtShaderSource, out var error);
            if (_crtEffect == null)
            {
                Console.WriteLine($"CRT Shader compilation failed: {error}");
                _shaderFailed = true;
            }
            else
            {
                Console.WriteLine("CRT Shader compiled successfully");
            }
        }

        if (_crtEffect != null)
        {
            using var imageShader = image.ToShader();
            var uniforms = new SKRuntimeEffectUniforms(_crtEffect)
            {
                ["resolution"] = new[] { (float)width, (float)height },
                ["offset"] = new[] { (float)_bounds.X, (float)_bounds.Y }
            };
            var children = new SKRuntimeEffectChildren(_crtEffect)
            {
                ["content"] = imageShader
            };

            using var shader = _crtEffect.ToShader(false, uniforms, children);
            using var paint = new SKPaint { Shader = shader };

            canvas.DrawRect((float)_bounds.X, (float)_bounds.Y, width, height, paint);
        }
        else
        {
            canvas.DrawImage(image, (float)_bounds.X, (float)_bounds.Y);
        }
    }

    private void RenderTerminalContent(SKCanvas canvas, int width, int height)
    {
        // Clear with background
        canvas.Clear(new SKColor(0x0A, 0x14, 0x0A));

        if (_typeface == null) return;
        if (_cp500Video != null)
        {
            RenderCp500Content(canvas, width, height);
            return;
        }
        if (_terminal == null) return;

        var buffer = _terminal.Buffer;
        using var font = new SKFont(_typeface, _fontSize);
        var metrics = font.Metrics;
        float baseline = -metrics.Ascent;

        // Center terminal content in screen area
        float contentWidth = _charWidth * TerminalBuffer.Width;
        float contentHeight = _charHeight * TerminalBuffer.Height;
        float offsetX = (width - contentWidth) / 2;
        float offsetY = (height - contentHeight) / 2;

        // Glow paint
        using var glowPaint = new SKPaint(font)
        {
            Color = new SKColor(0x33, 0xFF, 0x33, 0x60),
            IsAntialias = true
        };

        // Main text paint
        using var textPaint = new SKPaint(font)
        {
            Color = new SKColor(0x33, 0xFF, 0x33),
            IsAntialias = true
        };

        // Cursor paint
        using var cursorPaint = new SKPaint
        {
            Color = new SKColor(0x33, 0xFF, 0x33),
            Style = SKPaintStyle.Fill
        };

        // Render glow layer (offset copies)
        float[] glowOffsets = { -1.5f, 1.5f };
        foreach (float gox in glowOffsets)
        {
            foreach (float goy in glowOffsets)
            {
                for (int y = 0; y < TerminalBuffer.Height; y++)
                {
                    for (int x = 0; x < TerminalBuffer.Width; x++)
                    {
                        ref readonly var cell = ref buffer[x, y];
                        if (cell.IsEmpty) continue;

                        float px = offsetX + x * _charWidth + gox;
                        float py = offsetY + y * _charHeight + baseline + goy;
                        canvas.DrawText(cell.Character.ToString(), px, py, glowPaint);
                    }

                }
            }
        }

        // Render main text
        for (int y = 0; y < TerminalBuffer.Height; y++)
        {
            for (int x = 0; x < TerminalBuffer.Width; x++)
            {
                ref readonly var cell = ref buffer[x, y];
                if (cell.IsEmpty) continue;

                float px = offsetX + x * _charWidth;
                float py = offsetY + y * _charHeight + baseline;
                canvas.DrawText(cell.Character.ToString(), px, py, textPaint);
            }
        }

        // Render cursor
        if (buffer.CursorVisible)
        {
            int cx = buffer.CursorX;
            int cy = buffer.CursorY;
            if (cx >= 0 && cx < TerminalBuffer.Width && cy >= 0 && cy < TerminalBuffer.Height)
            {
                var cursorRect = new SKRect(
                    offsetX + cx * _charWidth,
                    offsetY + cy * _charHeight,
                    offsetX + (cx + 1) * _charWidth,
                    offsetY + (cy + 1) * _charHeight);
                canvas.DrawRect(cursorRect, cursorPaint);

                // Draw character inverse
                ref readonly var cursorCell = ref buffer[cx, cy];
                if (!cursorCell.IsEmpty)
                {
                    using var inversePaint = new SKPaint(font)
                    {
                        Color = new SKColor(0x0A, 0x14, 0x0A),
                        IsAntialias = true
                    };
                    canvas.DrawText(cursorCell.Character.ToString(),
                        offsetX + cx * _charWidth, offsetY + cy * _charHeight + baseline, inversePaint);
                }
            }
        }
    }

    private void RenderCp500Content(SKCanvas canvas, int width, int height)
    {
        using var font = new SKFont(_typeface, _fontSize);
        var metrics = font.Metrics;
        float baseline = -metrics.Ascent;
        float contentWidth = _charWidth * Cp500VideoDevice.Columns;
        float contentHeight = _charHeight * Cp500VideoDevice.Rows;
        float offsetX = (width - contentWidth) / 2;
        float offsetY = (height - contentHeight) / 2;

        using var glowPaint = new SKPaint(font)
        {
            Color = new SKColor(0x33, 0xFF, 0x33, 0x60),
            IsAntialias = true
        };
        using var textPaint = new SKPaint(font)
        {
            Color = new SKColor(0x33, 0xFF, 0x33),
            IsAntialias = true
        };
        using var graphicsPaint = new SKPaint
        {
            Color = new SKColor(0x33, 0xFF, 0x33),
            Style = SKPaintStyle.Fill
        };

        for (int y = 0; y < Cp500VideoDevice.Rows; y++)
        {
            for (int x = 0; x < Cp500VideoDevice.Columns; x++)
            {
                byte value = _cp500Video!.ReadCharacter(x, y);
                float px = offsetX + x * _charWidth;
                float py = offsetY + y * _charHeight;
                if ((value & 0x80) != 0)
                {
                    float blockWidth = _charWidth / 2;
                    float blockHeight = _charHeight / 3;
                    for (int bit = 0; bit < 6; bit++)
                    {
                        if ((value & (1 << bit)) == 0) continue;
                        int bx = bit & 1;
                        int by = bit >> 1;
                        canvas.DrawRect(
                            px + bx * blockWidth, py + by * blockHeight,
                            blockWidth + 0.5f, blockHeight + 0.5f, graphicsPaint);
                    }
                    continue;
                }

                if (value == 0)
                    continue;

                char character = value < 0x20 ? (char)(value + 0x40) : (char)value;
                if (character == ' ') continue;
                string text = character.ToString();
                canvas.DrawText(text, px - 1, py + baseline - 1, glowPaint);
                canvas.DrawText(text, px + 1, py + baseline + 1, glowPaint);
                canvas.DrawText(text, px, py + baseline, textPaint);
            }
        }
    }

    private void RenderTerminalDirect(SKCanvas canvas)
    {
        // Fallback: render directly without shader
        canvas.Save();
        canvas.Translate((float)_bounds.X, (float)_bounds.Y);
        RenderTerminalContent(canvas, (int)_bounds.Width, (int)_bounds.Height);
        canvas.Restore();
    }
}
