using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using ZenFiles.Models;

namespace ZenFiles.Views;

/// <summary>Tegner mappe-, fil-, stasjons- eller miniatyrikon for et FileItem.</summary>
public sealed partial class ItemIcon : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(FileItem), typeof(ItemIcon), new PropertyMetadata(null, OnItemChanged));

    public FileItem? Item
    {
        get => (FileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public ItemIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Item != null) { Item.PropertyChanged -= OnItemProp; Item.PropertyChanged += OnItemProp; }
            Refresh();
        };
        Unloaded += (_, _) => { if (Item != null) Item.PropertyChanged -= OnItemProp; };
    }

    static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ItemIcon)d;
        if (e.OldValue is FileItem o) o.PropertyChanged -= self.OnItemProp;
        if (e.NewValue is FileItem n) { n.PropertyChanged -= self.OnItemProp; n.PropertyChanged += self.OnItemProp; }
        self.Refresh();
    }

    void OnItemProp(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItem.Thumbnail)) Refresh();
    }

    static Color Cat(string c) => c switch
    {
        "image" => Color.FromArgb(255, 255, 159, 10),
        "video" => Color.FromArgb(255, 191, 90, 242),
        "audio" => Color.FromArgb(255, 255, 55, 95),
        "code" => Color.FromArgb(255, 48, 176, 199),
        "archive" => Color.FromArgb(255, 160, 120, 70),
        _ => Color.FromArgb(255, 120, 126, 136)
    };

    void Refresh()
    {
        var it = Item;
        if (it == null)
        {
            FolderG.Visibility = FileG.Visibility = ThumbB.Visibility = DriveG.Visibility = Visibility.Collapsed;
            return;
        }

        bool thumb = it.Thumbnail != null;
        FolderG.Visibility = it.IsDirectory && !it.IsDrive ? Visibility.Visible : Visibility.Collapsed;
        DriveG.Visibility = it.IsDrive ? Visibility.Visible : Visibility.Collapsed;
        FileG.Visibility = !it.IsDirectory && !thumb ? Visibility.Visible : Visibility.Collapsed;
        ThumbB.Visibility = thumb ? Visibility.Visible : Visibility.Collapsed;

        if (thumb) Thumb.Source = it.Thumbnail;

        if (!it.IsDirectory)
        {
            var ext = Path.GetExtension(it.Name).TrimStart('.').ToUpperInvariant();
            ExtText.Text = ext.Length > 4 ? ext[..4] : ext;
            ExtText.Foreground = new SolidColorBrush(Cat(it.Category));
        }
    }
}
