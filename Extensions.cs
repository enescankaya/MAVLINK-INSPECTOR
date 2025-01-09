using System.Collections.Concurrent;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;

namespace MavlinkInspector;

/// <summary>
/// TreeViewItem genişletme metodları.
/// </summary>
public static class Extensions
{
    // Cache yönetimi için sabitler
    private const int CACHE_CLEANUP_INTERVAL = 60000; // 1 dakika
    private const int MAX_CACHE_SIZE = 1000;

    private static readonly ConcurrentDictionary<string, WeakReference<TreeViewItem>> _itemCache = new();
    private static readonly Timer _cleanupTimer;
    private static readonly object _cleanupLock = new();

    static Extensions()
    {
        // Cache cleanup timer'ı başlat
        _cleanupTimer = new Timer(CleanupCache, null, CACHE_CLEANUP_INTERVAL, CACHE_CLEANUP_INTERVAL);
    }

    private static void CleanupCache(object? state)
    {
        if (!Monitor.TryEnter(_cleanupLock)) return;

        try
        {
            var keysToRemove = _itemCache
                .Where(kvp => !kvp.Value.TryGetTarget(out _))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _itemCache.TryRemove(key, out _);
            }

            // Cache boyutu kontrolü
            if (_itemCache.Count > MAX_CACHE_SIZE)
            {
                var excessKeys = _itemCache.Keys.Take(_itemCache.Count - MAX_CACHE_SIZE);
                foreach (var key in excessKeys)
                {
                    _itemCache.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }

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
        var cacheKey = $"{header}_{tag}";

        if (_itemCache.TryGetValue(cacheKey, out var weakRef) &&
            weakRef.TryGetTarget(out var cachedItem) &&
            parent.Items.Contains(cachedItem))
        {
            return cachedItem;
        }

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

            // Sıralama için optimizasyon
            if (parent.Items.Count > 1)
            {
                BatchSortTreeItems(parent);
            }

            _itemCache[cacheKey] = new WeakReference<TreeViewItem>(item);
        }
        else if (item.Header.ToString() != header)
        {
            item.Header = CreateTreeItemHeader(header);
        }

        return item;
    }

    // Batch sorting için yeni metod
    private static void BatchSortTreeItems(ItemsControl parent)
    {
        var items = parent.Items.Cast<TreeViewItem>().ToList();
        if (items.Count <= 1) return;

        var needsSort = false;
        for (int i = 1; i < items.Count; i++)
        {
            if (CompareTreeItems(items[i - 1], items[i]) > 0)
            {
                needsSort = true;
                break;
            }
        }

        if (needsSort)
        {
            items.Sort(CompareTreeItems);
            parent.Items.Clear();
            foreach (var item in items)
            {
                parent.Items.Add(item);
            }
        }
    }

    private static int CompareTreeItems(TreeViewItem x, TreeViewItem y)
    {
        string xText = GetSortableText(x);
        string yText = GetSortableText(y);
        return string.Compare(xText, yText, StringComparison.OrdinalIgnoreCase);
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