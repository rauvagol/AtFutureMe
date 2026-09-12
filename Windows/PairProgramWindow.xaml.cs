using System.Windows;
using System.Windows.Input;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class PairProgramWindow : Window
{
    readonly Category? _category;
    readonly bool _forLayout;

    public RunningExe? Chosen { get; private set; }

    public PairProgramWindow(Category category) : this(category, false)
    {
    }

    public PairProgramWindow(bool forLayout) : this(null, forLayout)
    {
    }

    PairProgramWindow(Category? category, bool forLayout)
    {
        _category = category;
        _forLayout = forLayout;
        InitializeComponent();
        if (forLayout)
        {
            Title = "Flag a running program";
            HintText.Text = "Pick a running program. While it is running, AtFutureMe uses that program’s layout (position, pop, hide, lock, collapse). Closing it restores the saved desktop. Flag the program first, then arrange the overlays.";
        }

        LoadExes();
    }

    void LoadExes()
    {
        var host = AppHost.Instance;
        var keep = _category?.PairedExePath;
        var items = ProcessWatch.ListPairTargets(host.Data.Settings.IgnoredPairTargets, keep);
        ExeList.ItemsSource = items;
        var match = items.FirstOrDefault(e => ProcessWatch.PathsRelated(keep, e.FilePath));
        if (match != null)
            ExeList.SelectedItem = match;
        else if (items.Count > 0)
            ExeList.SelectedIndex = 0;
    }

    void OnHide(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RunningExe exe })
            return;
        e.Handled = true;
        AppHost.Instance.HidePairTarget(exe.Name, exe.FilePath);
        LoadExes();
    }

    void OnRefresh(object sender, RoutedEventArgs e) => LoadExes();

    void OnCancel(object sender, RoutedEventArgs e) => Close();

    void OnPair(object sender, RoutedEventArgs e) => PairSelected();

    void OnPair(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
            PairSelected();
    }

    void PairSelected()
    {
        if (ExeList.SelectedItem is not RunningExe exe)
            return;
        Chosen = exe;
        if (_category != null)
        {
            _category.PairedExePath = exe.FilePath;
            _category.PairedExeName = exe.Name;
            _category.PairMinimizeMain = false;
            AppHost.Instance.RememberPairingBaseline(_category.Id);
        }

        DialogResult = true;
        Close();
    }
}
