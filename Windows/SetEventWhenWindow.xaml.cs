using System.Windows;
using System.Windows.Controls;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class SetEventWhenWindow : Window
{
    string _id = "";
    bool _loading;

    public SetEventWhenWindow(string id)
    {
        InitializeComponent();
        Bind(id);
    }

    public void Bind(string id)
    {
        _id = id;
        var ev = AppHost.Instance.Data.Events.FirstOrDefault(e => e.Id == id);
        if (ev == null)
        {
            Close();
            return;
        }

        _loading = true;
        TitleLabel.Text = string.IsNullOrWhiteSpace(ev.Title) ? "Scheduled" : ev.Title;
        WhenDate.SelectedDate = DateTime.Today;
        WhenTime.Text = "09:00";
        _loading = false;
    }

    void TimeChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClock(false);
    }

    void TimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClock(true);
    }

    bool ApplyClock(bool rewrite)
    {
        if (!ClockText.TryFormat(WhenTime.Text, true, out var clock))
            return false;
        if (rewrite && WhenTime.Text != clock)
        {
            _loading = true;
            WhenTime.Text = clock;
            WhenTime.CaretIndex = clock.Length;
            _loading = false;
        }
        return true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();

    void OnSave(object sender, RoutedEventArgs e)
    {
        ApplyClock(true);
        var date = WhenDate.SelectedDate ?? DateTime.Today;
        var time = ClockText.ParseOr(WhenTime.Text, TimeSpan.FromHours(9));
        if (!AppHost.Instance.SetEventWhen(_id, date.Date + time))
            return;
        Close();
    }
}
