using System.Collections.Concurrent;
using System.Text;
using System.Windows.Controls;
using System.Windows;

namespace MavlinkInspector;

public static class Extensions
{
    private static readonly ConcurrentDictionary<string, WeakReference<TreeViewItem>> _itemCache =
        new ConcurrentDictionary<string, WeakReference<TreeViewItem>>();

    public static TreeViewItem FindOrCreateChild(this ItemsControl parent, string header, object tag, object? data = null)
    {
        var cacheKey = $"{parent.GetHashCode()}_{tag}";

        if (_itemCache.TryGetValue(cacheKey, out var weakRef) &&
            weakRef.TryGetTarget(out var cachedItem))
        {
            if (cachedItem.Header.ToString() != header)
            {
                cachedItem.Header = CreateTreeItemHeader(header);
            }
            if (data != null)
            {
                cachedItem.Tag = data;
            }
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
                IsExpanded = true
            };
            parent.Items.Add(item);
        }

        _itemCache[cacheKey] = new WeakReference<TreeViewItem>(item);
        return item;
    }

    private static StackPanel CreateTreeItemHeader(string text)
    {
        var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

        var treeSymbol = new TextBlock
        {
            Text = "🌳 ",  // Tree symbol
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var textBlock = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };

        stackPanel.Children.Add(treeSymbol);
        stackPanel.Children.Add(textBlock);

        return stackPanel;
    }

    public static void UpdateMessageDetails(this TextBox textBox, MAVLink.MAVLinkMessage message)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Message Type: {message.msgtypename}");
        sb.AppendLine($"System ID: {message.sysid}");
        sb.AppendLine($"Component ID: {message.compid}");
        sb.AppendLine($"Message ID: {message.msgid}");
        sb.AppendLine($"Length: {message.Length} bytes");
        sb.AppendLine();

        var messageInfo = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(message.msgid);
        if (messageInfo.type != null)
        {
            foreach (var field in messageInfo.type.GetFields())
            {
                var value = field.GetValue(message.data);
                sb.AppendLine($"{field.Name}: {FormatFieldValue(value)}");
            }
        }

        textBox.Text = sb.ToString();
    }

    private static string FormatFieldValue(object value)
    {
        if (value == null) return "null";
        if (value is Array arr)
            return string.Join(", ", arr.Cast<object>());
        return value.ToString();
    }
}

public static class TreeViewExtensions
{
    public static TreeViewItem? FindChild(this ItemsControl parent, object tag)
    {
        return parent.Items.OfType<TreeViewItem>()
                          .FirstOrDefault(item => item.Tag?.Equals(tag) == true);
    }

    public static System.Windows.Media.Color GetRandomColor(this Random random)
    {
        return System.Windows.Media.Color.FromRgb(
            (byte)random.Next(256),
            (byte)random.Next(256),
            (byte)random.Next(256)
        );
    }
}
