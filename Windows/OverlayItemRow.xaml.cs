using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtFutureMe.ViewModels;

namespace AtFutureMe.Windows;

public partial class OverlayItemRow : UserControl
{
    Point _dragStart;
    bool _dragArmed;
    bool _didDrag;

    public OverlayItemRow()
    {
        InitializeComponent();
    }

    void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsDragChrome(source))
            return;
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
        _didDrag = false;
    }

    void OnDragUp(object sender, MouseButtonEventArgs e) => _dragArmed = false;

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8)
            return;
        _dragArmed = false;
        _didDrag = true;
        if (DataContext is not OverlayItemVm vm)
            return;
        AppHost.Instance.BeginOverlayDrag();
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(typeof(OverlayDrag), new OverlayDrag(vm.ItemKind, vm.Id)), DragDropEffects.Move);
        }
        finally
        {
            AppHost.Instance.EndOverlayDrag();
        }
    }

    static bool IsDragChrome(DependencyObject source)
    {
        var current = source;
        while (current != null && current is not OverlayItemRow)
        {
            if (current is CheckBox or Button)
                return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    void OnTitleUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag || e.ClickCount != 1 || DataContext is not OverlayItemVm vm)
            return;
        if (!vm.HasLink)
            return;
        AppHost.Instance.OpenTaskLink(vm.Url, vm.FocusExePath, vm.FocusWindowTitle);
        if (vm.ItemKind == "event")
            AppHost.Instance.MarkCheckedNow(vm.ItemKind, vm.Id);
        e.Handled = true;
    }


    void OnCheck(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayItemVm vm || sender is not CheckBox box)
            return;
        if (!vm.CheckEnabled)
        {
            box.IsChecked = vm.IsChecked;
            return;
        }
        if (vm.ItemKind == "todo")
            AppHost.Instance.ToggleTodo(vm.Id, box.IsChecked == true);
        else if (vm.ItemKind == "once")
            AppHost.Instance.ToggleOnce(vm.Id, box.IsChecked == true);
        else if (vm.ItemKind == "event")
            AppHost.Instance.ToggleEvent(vm.Id, box.IsChecked == true);
        box.IsChecked = vm.IsChecked;
    }

    void OnSetWhen(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayItemVm vm)
            AppHost.Instance.PromptSetEventWhen(vm.Id, Window.GetWindow(this));
    }

    void OnDelay12(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayItemVm vm)
            AppHost.Instance.DelayTodoHours(vm.Id, 12);
    }

    void OnCheckedNow(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayItemVm vm)
            AppHost.Instance.MarkCheckedNow(vm.ItemKind, vm.Id);
    }

    void OnHide(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayItemVm vm)
            AppHost.Instance.HideNote(vm.Id);
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayItemVm vm)
            AppHost.Instance.DeleteItem(vm.Id, vm.ItemKind);
    }
}
