using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using WindowController.App.Services;
using Hardcodet.Wpf.TaskbarNotification;
using static WindowController.App.Services.WindowControlService;

namespace WindowController.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly WindowControlService _service;
    private readonly ObservableCollection<WindowRule> _rules = new();
    private TaskbarIcon? _trayIcon;
    private bool _isExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "windowrules.json");
        ConfigPathText.Text = configPath;
        _service = new WindowControlService(configPath);
        _service.Start();
        var cfg = _service.GetConfig();
        ApplyAllOnStartupInput.IsChecked = cfg.ApplyAllOnStartup;
        StartInTrayInput.IsChecked = cfg.StartInTray;
        if (cfg.ApplyAllOnStartup)
            _service.ApplyAllRulesOnce();
        else
            _service.ApplyStartupRulesOnce();
        LoadRulesToGrid();
        RulesGrid.ItemsSource = _rules;
        UpdateButtonsEnabled();

        SetupTray();
        if (cfg.StartInTray)
        {
            Hide();
        }
        
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
    }
    
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExitRequested)
            return;

        e.Cancel = true;
        Hide();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _service.Dispose();
        DisposeTrayIcon();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_isExitRequested)
            return;

        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        _service.Reload();
        LoadRulesToGrid();
        StatusText.Text = "Rules reloaded";
    }

    private void ApplyNowButton_Click(object sender, RoutedEventArgs e)
    {
        // Simulate newly opened for 5s window control, then reload rules
        _service.SimulateAppJustOpenedForAll();
        _service.Reload();
        StatusText.Text = "Applying for 5s...";
    }

    private async void PickWindowButton_Click(object sender, RoutedEventArgs e)
    {
        var thisProcId = Environment.ProcessId;
        var oldState = WindowState;
        try
        {
            WindowState = WindowState.Minimized;
            StatusText.Text = "Click a window to capture...";
            var picked = await WindowPicker.PickOnNextClickAsync(TimeSpan.FromSeconds(8), thisProcId);
            if (picked != null)
            {
                ProcessNameInput.Text = picked.ProcessName;
                TitleContainsInput.Text = picked.Title;
                XInput.Text = picked.X.ToString();
                YInput.Text = picked.Y.ToString();
                WidthInput.Text = picked.Width.ToString();
                HeightInput.Text = picked.Height.ToString();
                StatusText.Text = "Window captured";
            }
            else
            {
                StatusText.Text = "Timed out";
            }
        }
        finally
        {
            WindowState = oldState;
            Activate();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var config = _service.GetConfig();
        config.Rules = _rules.ToList();
        config.ApplyAllOnStartup = ApplyAllOnStartupInput.IsChecked == true;
        config.StartInTray = StartInTrayInput.IsChecked == true;
        _service.Save(config);
        StatusText.Text = "Rules saved";
        UpdateButtonsEnabled();
    }

    private void AddOrUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(XInput.Text, out var x)) x = 0;
        if (!int.TryParse(YInput.Text, out var y)) y = 0;
        if (!int.TryParse(WidthInput.Text, out var w)) w = 800;
        if (!int.TryParse(HeightInput.Text, out var h)) h = 600;

        var process = (ProcessNameInput.Text ?? string.Empty).Trim();
        var title = (TitleContainsInput.Text ?? string.Empty).Trim();
        var excludeTitle = (ExcludeTitleContainsInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(process))
        {
            StatusText.Text = "Process name required";
            return;
        }

        var onTopValue = OnTopInput.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag ? 
            (tag == "True" ? true : tag == "False" ? false : (bool?)null) : null;

        var existing = _rules.FirstOrDefault(r => string.Equals(r.ProcessName, process, StringComparison.InvariantCultureIgnoreCase) && string.Equals(r.MatchTitleContains ?? string.Empty, title, StringComparison.InvariantCultureIgnoreCase));
        if (existing != null)
        {
            existing.X = x;
            existing.Y = y;
            existing.Width = w;
            existing.Height = h;
            existing.Enabled = true;
            existing.MatchTitleContains = string.IsNullOrEmpty(title) ? null : title;
            existing.ExcludeTitleContains = string.IsNullOrEmpty(excludeTitle) ? null : excludeTitle;
            existing.OnTop = onTopValue;
            RulesGrid.Items.Refresh();
            StatusText.Text = "Rule updated";
        }
        else
        {
            _rules.Add(new WindowRule
            {
                ProcessName = process,
                MatchTitleContains = string.IsNullOrEmpty(title) ? null : title,
                ExcludeTitleContains = string.IsNullOrEmpty(excludeTitle) ? null : excludeTitle,
                X = x,
                Y = y,
                Width = w,
                Height = h,
                Enabled = true,
                OnTop = onTopValue
            });
            StatusText.Text = "Rule added";
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is WindowRule rule)
        {
            ProcessNameInput.Text = rule.ProcessName;
            TitleContainsInput.Text = rule.MatchTitleContains ?? string.Empty;
            ExcludeTitleContainsInput.Text = rule.ExcludeTitleContains ?? string.Empty;
            XInput.Text = rule.X.ToString();
            YInput.Text = rule.Y.ToString();
            WidthInput.Text = rule.Width.ToString();
            HeightInput.Text = rule.Height.ToString();
            
            // Set OnTop combo
            OnTopInput.SelectedIndex = rule.OnTop switch
            {
                true => 1,
                false => 2,
                null => 0
            };
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is WindowRule rule)
        {
            _rules.Remove(rule);
            UpdateButtonsEnabled();
        }
    }

    private void LoadRulesToGrid()
    {
        _rules.Clear();
        foreach (var r in _service.GetConfig().Rules)
        {
            _rules.Add(new WindowRule
            {
                ProcessName = r.ProcessName,
                MatchTitleContains = r.MatchTitleContains,
                ExcludeTitleContains = r.ExcludeTitleContains,
                X = r.X,
                Y = r.Y,
                Width = r.Width,
                Height = r.Height,
                Enabled = r.Enabled,
                ApplyOnStartup = r.ApplyOnStartup,
                OnTop = r.OnTop
            });
        }
        UpdateButtonsEnabled();
    }

    private void UpdateButtonsEnabled()
    {
        var hasRules = _rules.Any();
        ApplyNowButton.IsEnabled = hasRules;
        SaveButton.IsEnabled = true;
        AddOrUpdateButton.IsEnabled = true;
    }

    private void PresetHalfWidth_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        var workW = wa.Width;
        WidthInput.Text = Math.Max(100, (int)(workW / 2)).ToString();
    }

    private void PresetHalfHeight_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        var workH = wa.Height;
        HeightInput.Text = Math.Max(100, (int)(workH / 2)).ToString();
    }

    private void PresetFullWidth_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        WidthInput.Text = Math.Max(100, (int)wa.Width).ToString();
    }

    private void PresetFullHeight_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        HeightInput.Text = Math.Max(100, (int)wa.Height).ToString();
    }

    private void PresetTopLeft_Click(object sender, RoutedEventArgs e)
    {
        XInput.Text = "0";
        YInput.Text = "0";
    }

    private void PresetTopRight_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        if (int.TryParse(WidthInput.Text, out var w))
        {
            XInput.Text = Math.Max(0, (int)(wa.Left + wa.Width) - w).ToString();
        }
        YInput.Text = "0";
    }

    private void PresetBottomLeft_Click(object sender, RoutedEventArgs e)
    {
        var screenH = SystemParameters.PrimaryScreenHeight;
        XInput.Text = "0";
        if (int.TryParse(HeightInput.Text, out var h))
        {
            YInput.Text = Math.Max(0, (int)screenH - h).ToString();
        }
    }

    private void PresetBottomRight_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        if (int.TryParse(WidthInput.Text, out var w))
        {
            XInput.Text = Math.Max(0, (int)(wa.Left + wa.Width) - w).ToString();
        }
        if (int.TryParse(HeightInput.Text, out var h))
        {
            YInput.Text = Math.Max(0, (int)(wa.Top + wa.Height) - h).ToString();
        }
    }

    private void PresetSnapLeft_Click(object sender, RoutedEventArgs e)
    {
        XInput.Text = "0";
    }

    private void PresetSnapRight_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        if (int.TryParse(WidthInput.Text, out var w))
        {
            XInput.Text = Math.Max(0, (int)(wa.Left + wa.Width) - w).ToString();
        }
    }

    private void PresetSnapTop_Click(object sender, RoutedEventArgs e)
    {
        YInput.Text = "0";
    }

    private void PresetSnapBottom_Click(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        if (int.TryParse(HeightInput.Text, out var h))
        {
            YInput.Text = Math.Max(0, (int)(wa.Top + wa.Height) - h).ToString();
        }
    }

    private void SetupTray()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/WindowController.App;component/icon.ico")),
            ToolTipText = "Window Controller",
            Visibility = System.Windows.Visibility.Visible
        };
        
        // Create context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();
        
        var showItem = new System.Windows.Controls.MenuItem { Header = "Show" };
        showItem.Click += (_, __) => ShowFromTray();
        contextMenu.Items.Add(showItem);
        
        var applyItem = new System.Windows.Controls.MenuItem { Header = "Apply Now (5s)" };
        applyItem.Click += (_, __) => { ApplyNowButton_Click(this, new RoutedEventArgs()); };
        contextMenu.Items.Add(applyItem);
        
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, __) => ExitApplication();
        contextMenu.Items.Add(exitItem);
        
        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayLeftMouseUp += (_, __) => ShowFromTray();
    }

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;

        _trayIcon.Visibility = Visibility.Collapsed;
        _trayIcon.Dispose();
        _trayIcon = null;
    }
}
