using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtFutureMe.ViewModels;

namespace AtFutureMe.Windows;

public partial class CategorySectionBlock : UserControl
{
    Point _dragStart;
    bool _dragArmed;

    public CategorySectionBlock()
    {
        InitializeComponent();
    }

    void OnToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategorySectionVm vm)
            AppHost.Instance.ToggleCategoryCollapsed(vm.Id);
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategorySectionVm vm)
            AppHost.Instance.OpenAddEntry(vm.Id);
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategorySectionVm vm)
            AppHost.Instance.OpenAddFolder(vm.Id);
    }

    void OnPop(object sender, RoutedEventArgs e)
    {
        if (DataContext is CategorySectionVm vm)
            AppHost.Instance.SetCategoryPoppedOut(vm.Id, true);
    }

    void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsButton(source))
            return;
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
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
        if (DataContext is not CategorySectionVm vm)
            return;
        AppHost.Instance.BeginOverlayDrag();
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(typeof(OverlayDrag), new OverlayDrag("category", vm.Id)), DragDropEffects.Move);
        }
        finally
        {
            AppHost.Instance.EndOverlayDrag();
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!TryPayload(e, out var drag) || DataContext is not CategorySectionVm vm)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (drag.Kind == "category" && drag.Id == vm.Id)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        AppHost.Instance.SetCategoryDropHover(vm.Id);
        e.Handled = true;
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        if (DataContext is CategorySectionVm vm)
            AppHost.Instance.SetCategoryDropHover(null);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (!TryPayload(e, out var drag) || DataContext is not CategorySectionVm vm)
            return;
        var place = "inside";
        if (drag.Kind == "category" && sender is IInputElement input)
        {
            var y = e.GetPosition(input).Y;
            var height = sender is FrameworkElement fe ? Math.Max(1, fe.ActualHeight) : 24;
            if (y < height * 0.28)
                place = "before";
            else if (y > height * 0.72)
                place = "after";
        }

        AppHost.Instance.DropOnCategory(vm.Id, drag, place);
        e.Handled = true;
    }

    static bool TryPayload(DragEventArgs e, out OverlayDrag drag)
    {
        if (e.Data.GetData(typeof(OverlayDrag)) is OverlayDrag payload)
        {
            drag = payload;
            return true;
        }

        drag = null!;
        return false;
    }

    static bool IsButton(DependencyObject source)
    {
        var current = source;
        while (current != null && current is not CategorySectionBlock)
        {
            if (current is Button)
                return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
