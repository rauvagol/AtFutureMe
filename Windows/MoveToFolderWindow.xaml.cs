using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class MoveToFolderWindow : Window
{
    sealed class FolderPickRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Icon { get; init; } = "";
        public bool IsUp { get; init; }
    }

    readonly Func<string, bool> _canMove;
    string _currentId;

    public string ChosenId { get; private set; } = "";

    public MoveToFolderWindow(string startFolderId, Func<string, bool> canMove)
    {
        _canMove = canMove;
        _currentId = AppHost.Instance.ResolveCategoryId(startFolderId);
        InitializeComponent();
        Bind();
        FolderList.Focus();
    }

    void Bind(string? selectId = null)
    {
        var data = AppHost.Instance.Data;
        CategoryTree.RefreshLabels(data.Categories);
        var current = data.Categories.FirstOrDefault(c => c.Id == _currentId);
        PathBox.Text = current == null ? "Top level" : current.PathLabel;
        var rows = new List<FolderPickRow>();
        if (_currentId.Length > 0)
        {
            rows.Add(new FolderPickRow
            {
                Id = "",
                Name = "..",
                Icon = "\uE74A",
                IsUp = true
            });
        }

        foreach (var category in CategoryTree.ChildrenOf(data.Categories, string.IsNullOrEmpty(_currentId) ? null : _currentId))
        {
            rows.Add(new FolderPickRow
            {
                Id = category.Id,
                Name = category.Name,
                Icon = "\uE8B7"
            });
        }

        FolderList.ItemsSource = rows;
        if (selectId != null && rows.FirstOrDefault(r => r.Id == selectId) is FolderPickRow match)
            FolderList.SelectedItem = match;
        else
            FolderList.SelectedIndex = rows.Count > 0 && rows[0].IsUp ? 0 : -1;
        UpdateMoveButton();
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMoveButton();

    string TargetId() =>
        FolderList.SelectedItem is FolderPickRow row && !row.IsUp ? row.Id : _currentId;

    void UpdateMoveButton() => MoveButton.IsEnabled = _canMove(TargetId());

    void OnWindowPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && NewFolderBar.Visibility == Visibility.Visible
            && NewFolderName.IsKeyboardFocusWithin)
        {
            HideNewFolderBar();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (NewFolderBar.Visibility == Visibility.Visible && NewFolderName.IsKeyboardFocusWithin)
        {
            if (CreateAndMoveButton.IsEnabled)
                OnCreateAndMove(sender, e);
            else
                OnCreateFolder(sender, e);
            e.Handled = true;
            return;
        }

        if (FolderList.IsKeyboardFocusWithin)
        {
            NavigateSelected();
            e.Handled = true;
        }
    }

    void OnActivate(object sender, MouseButtonEventArgs e) => NavigateSelected();

    void NavigateSelected()
    {
        if (FolderList.SelectedItem is not FolderPickRow row)
            return;
        if (row.IsUp)
        {
            var current = AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == _currentId);
            _currentId = AppHost.Instance.ResolveCategoryId(current?.ParentId);
        }
        else
            _currentId = row.Id;
        HideNewFolderBar();
        Bind();
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        NewFolderBar.Visibility = Visibility.Visible;
        NewFolderName.Text = UniqueFolderName();
        CreateAndMoveButton.IsEnabled = _canMove(_currentId);
        NewFolderBar.UpdateLayout();
        NewFolderName.Focus();
        NewFolderName.SelectAll();
        if (!NewFolderName.IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(() =>
            {
                NewFolderName.Focus();
                NewFolderName.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    Category CreateFolder()
    {
        var name = NewFolderName.Text.Trim();
        if (name.Length == 0)
            name = UniqueFolderName();
        var parentId = string.IsNullOrEmpty(_currentId) ? null : _currentId;
        return AppHost.Instance.AddSubcategory(parentId, name);
    }

    void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var created = CreateFolder();
        HideNewFolderBar();
        Bind(created.Id);
    }

    void OnCreateAndMove(object sender, RoutedEventArgs e)
    {
        var created = CreateFolder();
        if (!_canMove(created.Id))
        {
            HideNewFolderBar();
            Bind(created.Id);
            return;
        }

        ChosenId = created.Id;
        DialogResult = true;
    }

    void HideNewFolderBar()
    {
        NewFolderBar.Visibility = Visibility.Collapsed;
        FolderList.Focus();
    }

    string UniqueFolderName()
    {
        var parentId = string.IsNullOrEmpty(_currentId) ? null : _currentId;
        var names = CategoryTree.ChildrenOf(AppHost.Instance.Data.Categories, parentId)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = "New folder";
        var n = 2;
        while (names.Contains(name))
            name = $"New folder ({n++})";
        return name;
    }

    void OnMove(object sender, RoutedEventArgs e)
    {
        var target = TargetId();
        if (!_canMove(target))
            return;
        ChosenId = target;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
