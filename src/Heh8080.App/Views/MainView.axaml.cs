using System;
using Avalonia.Controls;
using Avalonia.Input;
using Heh8080.UI.ViewModels;

namespace Heh8080.UI.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;
    /// <summary>
    /// Event fired when the FJM-3A logo is clicked.
    /// Platform-specific code should handle this to show configuration UI.
    /// </summary>
    public event Action? LogoClicked;

    /// <summary>
    /// Event fired when user clicks on non-interactive area (for window dragging).
    /// </summary>
    public event Action<PointerPressedEventArgs>? DragMoveRequested;

    /// <summary>
    /// Event fired when user clicks the exit button.
    /// </summary>
    public event Action? ExitRequested;
    public event Action? DisplaySizeChanged;

    public MainView()
    {
        InitializeComponent();

        // Wire terminal when DataContext is set
        DataContextChanged += (s, e) =>
        {
            if (_viewModel != null)
                _viewModel.ActiveMachineChanged -= ApplyActiveMachine;

            if (DataContext is MainViewModel vm)
            {
                _viewModel = vm;
                _viewModel.ActiveMachineChanged += ApplyActiveMachine;
                ApplyActiveMachine();
            }
        };

        // Forward logo click to platform code
        Terminal.LogoClicked += () => LogoClicked?.Invoke();

        // Forward drag move request to platform code
        Terminal.DragMoveRequested += (e) => DragMoveRequested?.Invoke(e);

        // Forward exit request to platform code
        Terminal.ExitClicked += () => ExitRequested?.Invoke();
    }

    private void ApplyActiveMachine()
    {
        if (_viewModel == null)
            return;

        Terminal.Terminal = _viewModel.IsCp500 ? null : _viewModel.Terminal;
        Terminal.Cp500Characters = _viewModel.IsCp500 ? _viewModel.Cp500Characters : null;
        Terminal.Cp500Video = _viewModel.IsCp500 ? _viewModel.Cp500Video : null;
        Terminal.Cp500Keyboard = _viewModel.IsCp500 ? _viewModel.Cp500Keyboard : null;
        DisplaySizeChanged?.Invoke();
    }

    /// <summary>
    /// Focus the terminal control for keyboard input.
    /// </summary>
    public void FocusTerminal()
    {
        Terminal.Focus();
    }

    /// <summary>
    /// Gets or sets the terminal display scale.
    /// </summary>
    public double TerminalScale
    {
        get => Terminal.Scale;
        set => Terminal.Scale = value;
    }

    /// <summary>
    /// Gets the terminal's current width.
    /// </summary>
    public double TerminalWidth => Terminal.Width;

    /// <summary>
    /// Gets the terminal's current height.
    /// </summary>
    public double TerminalHeight => Terminal.Height;

    /// <summary>
    /// Event fired when the terminal scale changes.
    /// </summary>
    public event Action<double>? TerminalScaleChanged
    {
        add => Terminal.ScaleChanged += value;
        remove => Terminal.ScaleChanged -= value;
    }
}
