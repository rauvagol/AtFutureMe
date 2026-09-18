using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace AtFutureMe.ViewModels;

public sealed class OverlayItemVm : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string ItemKind { get; init; } = "";
    public string CategoryId { get; init; } = "";
    public DateTime SortAt { get; init; } = DateTime.MaxValue;
    public int PinSlot { get; init; }
    // auto model: placeholder for ApplyPinnedOrder when HoldPinWhenHidden item is not on the overlay
    public bool IsPinHoldGhost { get; init; }

    int _contentIndent;
    // auto model
    public int ContentIndent
    {
        get => _contentIndent;
        set
        {
            if (_contentIndent == value)
                return;
            _contentIndent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowMargin));
        }
    }

    public Thickness RowMargin => new(_contentIndent, 1, 0, 1);

    string _title = "";
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    string? _url;
    public string? Url
    {
        get => _url;
        set
        {
            if (_url == value)
                return;
            _url = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLink));
            OnPropertyChanged(nameof(HitTag));
            OnPropertyChanged(nameof(Decorations));
        }
    }

    public bool HasLink => !string.IsNullOrWhiteSpace(_url);
    public string? HitTag => HasLink ? "hit" : null;

    string _meta = "";
    public string Meta
    {
        get => _meta;
        set
        {
            if (_meta == value)
                return;
            _meta = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMeta));
            OnPropertyChanged(nameof(ShowCountdown));
            OnPropertyChanged(nameof(ShowBody));
        }
    }

    public bool HasMeta => !string.IsNullOrEmpty(_meta);
    public bool ShowCountdown => ItemKind != "note" && HasMeta;
    public bool ShowBody => ItemKind == "note" && HasMeta;

    bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnPropertyChanged(); }
    }

    bool _showCheck = true;
    public bool ShowCheck
    {
        get => _showCheck;
        set { _showCheck = value; OnPropertyChanged(); }
    }

    bool _checkEnabled = true;
    public bool CheckEnabled
    {
        get => _checkEnabled;
        set { _checkEnabled = value; OnPropertyChanged(); }
    }

    bool _showCheckIn;
    public bool ShowCheckIn
    {
        get => _showCheckIn;
        set { _showCheckIn = value; OnPropertyChanged(); }
    }

    bool _showSetWhen;
    public bool ShowSetWhen
    {
        get => _showSetWhen;
        set { _showSetWhen = value; OnPropertyChanged(); }
    }

    bool _showTrash;
    public bool ShowTrash
    {
        get => _showTrash;
        set { _showTrash = value; OnPropertyChanged(); }
    }

    bool _showDelete;
    public bool ShowDelete
    {
        get => _showDelete;
        set { _showDelete = value; OnPropertyChanged(); }
    }

    public bool IsNote => ItemKind == "note";

    FontWeight _titleWeight = FontWeights.Normal;
    public FontWeight TitleWeight
    {
        get => _titleWeight;
        set { _titleWeight = value; OnPropertyChanged(); }
    }

    bool _showBackdrop;
    public bool ShowBackdrop
    {
        get => _showBackdrop;
        set
        {
            _showBackdrop = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackdropVisibility));
        }
    }

    public Visibility BackdropVisibility =>
        ShowBackdrop ? Visibility.Visible : Visibility.Collapsed;

    Brush _backdropBrush = Brushes.Transparent;
    public Brush BackdropBrush
    {
        get => _backdropBrush;
        set { _backdropBrush = value; OnPropertyChanged(); }
    }

    Brush _foreground = Brushes.White;
    public Brush Foreground
    {
        get => _foreground;
        set { _foreground = value; OnPropertyChanged(); }
    }

    bool _strikethrough;
    public bool Strikethrough
    {
        get => _strikethrough;
        set { _strikethrough = value; OnPropertyChanged(); OnPropertyChanged(nameof(Decorations)); }
    }

    public TextDecorationCollection? Decorations
    {
        get
        {
            if (Strikethrough && HasLink)
            {
                var both = new TextDecorationCollection
                {
                    TextDecorations.Strikethrough[0],
                    TextDecorations.Underline[0]
                };
                both.Freeze();
                return both;
            }

            if (Strikethrough)
                return TextDecorations.Strikethrough;
            if (HasLink)
                return TextDecorations.Underline;
            return null;
        }
    }

    double _attentionGlow;
    public double AttentionGlow
    {
        get => _attentionGlow;
        set
        {
            if (Math.Abs(_attentionGlow - value) < 0.01)
                return;
            _attentionGlow = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CategorySectionVm : INotifyPropertyChanged
{
    public string Id { get; set; } = "";

    // auto model
    public int PinSlot { get; set; }

    string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged();
        }
    }

    bool _showButtons;
    public bool ShowButtons
    {
        get => _showButtons;
        set { _showButtons = value; OnPropertyChanged(); }
    }

    public System.Collections.ObjectModel.ObservableCollection<OverlayItemVm> Items { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<CategorySectionVm> Children { get; } = [];
    // auto model — mixed tasks + nested folders when Manual sort is on
    public System.Collections.ObjectModel.ObservableCollection<object> Entries { get; } = [];

    int _depth;
    public int Depth
    {
        get => _depth;
        set
        {
            _depth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlockMargin));
        }
    }

    int _indent = 14;
    public int Indent
    {
        get => _indent;
        set
        {
            // auto model: always refresh margin so slider changes apply even when depth is unchanged
            _indent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlockMargin));
        }
    }

    public Thickness BlockMargin => new(Math.Max(0, Indent), Depth == 0 ? 10 : 4, 4, 0);

    bool _hasChildren;
    public bool HasChildren
    {
        get => _hasChildren;
        set
        {
            _hasChildren = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandVisibility));
        }
    }

    bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BodyVisibility));
            OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public Visibility ExpandVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BodyVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    bool _dropHover;
    public bool DropHover
    {
        get => _dropHover;
        set
        {
            _dropHover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DropHighlight));
        }
    }

    public Visibility DropHighlight => DropHover ? Visibility.Visible : Visibility.Collapsed;

    double _attentionGlow;
    public double AttentionGlow
    {
        get => _attentionGlow;
        set
        {
            if (Math.Abs(_attentionGlow - value) < 0.01)
                return;
            _attentionGlow = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
