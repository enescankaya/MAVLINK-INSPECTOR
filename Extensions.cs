using System.Collections.Concurrent;
using System.Text;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;

namespace MavlinkInspector;

/// <summary>
/// TreeViewItem genişletme metodları.
/// </summary>
public static class Extensions
{
    private static readonly ConcurrentDictionary<string, WeakReference<TreeViewItem>> _itemCache =
        new ConcurrentDictionary<string, WeakReference<TreeViewItem>>();

    /// <summary>
    /// Belirtilen başlık ve etiketle bir TreeViewItem bulur veya oluşturur.
    /// </summary>
    /// <param name="parent">Üst öğe.</param>
    /// <param name="header">Başlık.</param>
    /// <param name="tag">Etiket.</param>
    /// <param name="data">Veri.</param>
    /// <returns>TreeViewItem.</returns>
    public static TreeViewItem FindOrCreateChild(this ItemsControl parent, string header, object tag, object? data = null)
    {
        var item = parent.Items.OfType<TreeViewItem>()
                             .FirstOrDefault(item => item.Tag?.Equals(tag) == true);

        if (item == null)
        {
            item = new TreeViewItem
            {
                Header = CreateTreeItemHeader(header),
                Tag = tag,
                DataContext = data,
                IsExpanded = true
            };
            parent.Items.Add(item);
            SortTreeItems(parent);
        }
        else if (item.Header.ToString() != header)
        {
            item.Header = CreateTreeItemHeader(header);
        }

        return item;
    }

    /// <summary>
    /// TreeViewItem öğelerini sıralar.
    /// </summary>
    /// <param name="parent">Üst öğe.</param>
    private static void SortTreeItems(ItemsControl parent)
    {
        var items = parent.Items.Cast<TreeViewItem>().ToList();

        var sortedItems = items.Where(i => i.Header is StackPanel)
                             .OrderBy(GetSortableText)
                             .Concat(items.Where(i => !(i.Header is StackPanel)));

        parent.Items.Clear();
        foreach (var item in sortedItems)
        {
            parent.Items.Add(item);
        }
    }

    /// <summary>
    /// TreeViewItem öğesinin sıralanabilir metnini döndürür.
    /// </summary>
    /// <param name="item">TreeViewItem öğesi.</param>
    /// <returns>Sıralanabilir metin.</returns>
    private static string GetSortableText(TreeViewItem item)
    {
        if (item.Header is StackPanel sp && sp.Children.Count > 1 &&
            sp.Children[1] is TextBlock tb)
        {
            string text = tb.Text;
            int bracketIndex = text.IndexOf('(');
            return bracketIndex > 0
                ? text.Substring(0, bracketIndex).Trim().ToUpperInvariant()
                : text.ToUpperInvariant();
        }
        return item.Header?.ToString()?.ToUpperInvariant() ?? string.Empty;
    }

    /// <summary>
    /// TreeViewItem başlığı oluşturur.
    /// </summary>
    /// <param name="text">Başlık metni.</param>
    /// <returns>StackPanel.</returns>
    private static StackPanel CreateTreeItemHeader(string text)
    {
        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2)
        };

        var treeSymbol = new TextBlock
        {
            Text = "▸",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
        };

        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White)
        };

        stackPanel.Children.Add(treeSymbol);
        stackPanel.Children.Add(textBlock);

        return stackPanel;
    }

    /// <summary>
    /// Türün dostça adını döndürür.
    /// </summary>
    /// <param name="type">Tür.</param>
    /// <returns>Dostça ad.</returns>
    private static string GetFriendlyTypeName(Type type)
    {
        if (type.IsArray)
            return $"{GetFriendlyTypeName(type.GetElementType())}[]";

        return type.Name switch
        {
            "Single" => "float",
            "Int32" => "int",
            "UInt32" => "uint",
            "Int16" => "short",
            "UInt16" => "ushort",
            "Byte" => "byte",
            "SByte" => "sbyte",
            "Int64" => "long",
            "UInt64" => "ulong",
            _ => type.Name
        };
    }
}