using System.Windows;
using System.Windows.Input;
using AtFutureMe.ViewModels;

namespace AtFutureMe.Windows;

public partial class CategoryOverlay : OverlayWindowBase
{
    public string CategoryId { get; }

    public CategoryOverlay(string categoryId)
    {
        CategoryId = categoryId;
        InitializeComponent();
        BindSection();
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        AppHost.Instance.UiChanged += ApplyChrome;
        Loaded += (_, _) => ApplyChrome();
        Closed += (_, _) => AppHost.Instance.UiChanged -= ApplyChrome;
    }

    public void BindSection()
    {
        var section = AppHost.Instance.SectionFor(CategoryId);
        // auto model
        BoardList.ItemTemplateSelector ??= RemindersOverlay.CreateBoardSelector();
        BoardList.ItemsSource = section?.Entries;
        TitleText.Text = section?.Name ?? "Category";
        Title = TitleText.Text;
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        ApplyDragChrome(ChromeBar, ChromeActions, TitleText, ChromeRow, BodyScroll);
        BoardList.FontSize = host.Data.Settings.FontSize;
        BoardList.FontWeight = host.OverlayChromeWeight();
        TitleText.FontWeight = host.OverlayChromeWeight();
        UndoButton.IsEnabled = host.CanUndoDelete;
        UndoButton.ToolTip = host.UndoDeleteTip;
        ApplyPanelChrome(PanelBorder, DitherLayer);
        BindSection();
        ResizeMode = host.Data.Settings.ClickThrough ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    void OnWindowDrag(object sender, MouseButtonEventArgs e) => DragIfUnlocked(e);

    void OnDock(object sender, RoutedEventArgs e) => AppHost.Instance.SetCategoryPoppedOut(CategoryId, false);

    void OnAdd(object sender, RoutedEventArgs e) => AppHost.Instance.OpenAddEntry(CategoryId);

    void OnAddFolder(object sender, RoutedEventArgs e) => AppHost.Instance.OpenAddFolder(CategoryId);

    void OnOpenConfig(object sender, RoutedEventArgs e) => AppHost.Instance.OpenConfig();

    void OnUndoDelete(object sender, RoutedEventArgs e) => AppHost.Instance.UndoLastDelete();

    void OnTitleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(OverlayDrag)) is OverlayDrag
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnTitleDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OverlayDrag)) is OverlayDrag drag)
            AppHost.Instance.DropOnCategory(CategoryId, drag, "inside");
        e.Handled = true;
    }
}
