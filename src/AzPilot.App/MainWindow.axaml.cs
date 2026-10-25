using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using AzPilot.App.ViewModels;
using AzPilot.Core;

namespace AzPilot.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HashSet<Key> _heldKeys = [];
    private bool _allowClose;
    private bool _lastNightVision;
    private bool _setupOpen;
    private const double CollapsedWidth = 300;
    private const double ExpandedWidth = 900;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        ManualRateCeilingBox.SelectionChanged += (_, _) =>
        {
            if (ManualRateCeilingBox.SelectedItem is double rate) _viewModel.MaximumManualRate = rate;
        };
        GoToRateBox.SelectionChanged += (_, _) =>
        {
            if (GoToRateBox.SelectedItem is double rate) _viewModel.GoToRate = rate;
        };
        foreach (var button in this.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is string))
        {
            button.AddHandler(Button.PointerPressedEvent, Direction_Pressed, RoutingStrategies.Bubble, handledEventsToo: true);
            button.AddHandler(Button.PointerReleasedEvent, Direction_Released, RoutingStrategies.Bubble, handledEventsToo: true);
            button.AddHandler(Button.PointerCaptureLostEvent, Direction_CaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        ConnectionTypeBox.SelectedIndex = _viewModel.IsSimulator ? 0 : 1;
        ConnectionTypeBox.SelectionChanged += (_, _) =>
        {
            if (ConnectionTypeBox.SelectedIndex >= 0) _viewModel.IsSimulator = ConnectionTypeBox.SelectedIndex == 0;
        };
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ApplyPalette(_viewModel.NightVision);
        Loaded += async (_, _) => await _viewModel.StartAsync();
        Closing += MainWindow_Closing;
        LostFocus += (_, _) => { if (_heldKeys.Count > 0) _ = StopHeldKeys(); };
    }

    private void Setup_Click(object? sender, RoutedEventArgs e)
    {
        if (_setupOpen) CloseSetup();
        else OpenSetup("Settings");
    }

    private void CloseSetup_Click(object? sender, RoutedEventArgs e) => CloseSetup();

    private void OpenSetup(string page)
    {
        _viewModel.Page = page;
        if (_setupOpen) return;
        _setupOpen = true;
        SetupPanel.IsVisible = true;
        MinWidth = ExpandedWidth;
        Width = ExpandedWidth;
    }

    private void CloseSetup()
    {
        _setupOpen = false;
        SetupPanel.IsVisible = false;
        MinWidth = CollapsedWidth;
        Width = CollapsedWidth;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_lastNightVision != _viewModel.NightVision) ApplyPalette(_viewModel.NightVision);
    }

    private void ApplyPalette(bool night)
    {
        _lastNightVision = night;
        RequestedThemeVariant = ThemeVariant.Dark;
        SetBrush("WindowBg", night ? "#151811" : "#07111F");
        SetBrush("SidebarBg", night ? "#24291B" : "#0D2038");
        SetBrush("CardBg", night ? "#050505" : "#0B182A");
        SetBrush("CardBorder", night ? "#85897D" : "#47769F");
        SetBrush("FieldBg", night ? "#180000" : "#081525");
        SetBrush("TextPrimary", night ? "#E7E5D9" : "#F4F8FF");
        SetBrush("TextMuted", night ? "#B5B7AA" : "#B8CCE2");
        SetBrush("Accent", night ? "#FF3028" : "#79C7FF");
        SetBrush("ControllerBg", night ? "#0C0F09" : "#081421");
        SetBrush("PanelBg", night ? "#10130D" : "#091726");
        SetBrush("PanelBorder", night ? "#737866" : "#4B7194");
        SetBrush("HeaderBorder", night ? "#8B9077" : "#3F6588");
        SetBrush("ScreenBg", night ? "#000000" : "#020813");
        SetBrush("DisplayBg", night ? "#170000" : "#071B31");
        SetBrush("DisplayFg", night ? "#FF3028" : "#EAF6FF");
        SetBrush("ButtonBg", night ? "#D8D5C9" : "#16324F");
        SetBrush("ButtonFg", night ? "#11130F" : "#FFFFFF");
        SetBrush("ButtonBorder", night ? "#F2F0E8" : "#6E9FC5");
        SetBrush("ButtonHover", night ? "#F3F0E5" : "#245378");
        SetBrush("MutedBorder", night ? "#55594F" : "#5B7894");
        SetBrush("TabBg", night ? "#0A0C08" : "#0D2038");
        SetBrush("ErrorBg", night ? "#681016" : "#8B1018");
        SetBrush("ErrorFg", night ? "#FFFFFF" : "#FFFFFF");
        SetBrush("LogoBg", night ? "#101C3D" : "#12385C");
        SetBrush("LogoFg", night ? "#E8EFF9" : "#FFFFFF");
        Resources["SystemAccentColor"] = Color.Parse(night ? "#B91C1C" : "#79C7FF");
        Resources["SystemAccentColorDark1"] = Color.Parse(night ? "#8F1717" : "#3D9BDC");
        Resources["SystemAccentColorDark2"] = Color.Parse(night ? "#641010" : "#1E6394");
        Resources["SystemAccentColorLight1"] = Color.Parse(night ? "#DC3C35" : "#A9DDFF");
        Resources["SystemAccentColorLight2"] = Color.Parse(night ? "#EF6A64" : "#D2EDFF");
    }

    private void SetBrush(string key, string color) => Resources[key] = new SolidColorBrush(Color.Parse(color));

    private async void Direction_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        e.Handled = true;
        var parts = tag.Split(',');
        if (Enum.TryParse<MountAxis>(parts[0], out var axis)) await _viewModel.BeginMove(axis, int.Parse(parts[1]));
    }

    private async void Direction_Released(object? sender, PointerReleasedEventArgs e) => await StopDirection(sender);
    private async void Direction_CaptureLost(object? sender, PointerCaptureLostEventArgs e) => await StopDirection(sender);
    private async Task StopDirection(object? sender)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MountAxis>(tag.Split(',')[0], out var axis))
            await _viewModel.EndMove(axis);
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEditing(e.Source)) return;
        if (e.Key is Key.Space or Key.Escape)
        {
            e.Handled = true; await _viewModel.EmergencyStop(); return;
        }
        if (_heldKeys.Contains(e.Key)) { e.Handled = true; return; }
        var motion = KeyMotion(e.Key);
        if (motion == null) return;
        e.Handled = true; _heldKeys.Add(e.Key);
        await _viewModel.BeginMove(motion.Value.axis, motion.Value.direction);
    }

    private async void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (!_heldKeys.Remove(e.Key)) return;
        var motion = KeyMotion(e.Key);
        if (motion != null) { e.Handled = true; await _viewModel.EndMove(motion.Value.axis); }
    }

    private async Task StopHeldKeys()
    {
        _heldKeys.Clear(); await _viewModel.EmergencyStop();
    }

    private static (MountAxis axis, int direction)? KeyMotion(Key key) => key switch
    {
        Key.Up => (MountAxis.Altitude, 1), Key.Down => (MountAxis.Altitude, -1),
        Key.Left => (MountAxis.Azimuth, -1), Key.Right => (MountAxis.Azimuth, 1), _ => null
    };
    private static bool IsEditing(object? source) => source is TextBox or NumericUpDown or ComboBox;

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _allowClose = true;
        Focus();
        await _viewModel.DisposeAsync();
        Close();
    }
}
