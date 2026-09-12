using System.Windows;
using System.Windows.Controls;
using AtFutureMe.ViewModels;

namespace AtFutureMe.Windows;

// auto model
public sealed class BoardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }
    public DataTemplate? SectionTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is OverlayItemVm ? ItemTemplate : SectionTemplate;
}
