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
            Text = "-> ",  // Daha basit tree sembolü
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 5, 0),
            Width = 15 // Sabit genişlik
        };

        var textBlock = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };

        stackPanel.Children.Add(treeSymbol);
        stackPanel.Children.Add(textBlock);

        return stackPanel;
    }

    public static void UpdateMessageDetails(this TextBox textBox, MAVLink.MAVLinkMessage message)
    {
        var sb = new StringBuilder();
        // Header bilgileri
        sb.AppendLine($"{"Message Type:",-20} {message.msgtypename}");
        sb.AppendLine($"{"System ID:",-20} {message.sysid}");
        sb.AppendLine($"{"Component ID:",-20} {message.compid}");
        sb.AppendLine($"{"Message ID:",-20} {message.msgid}");
        sb.AppendLine($"{"Length:",-20} {message.Length} bytes");
        sb.AppendLine();

        var messageInfo = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(message.msgid);
        if (messageInfo.type != null)
        {
            // En uzun field ismini ve değeri bul
            var fields = messageInfo.type.GetFields();
            int maxNameLength = fields.Max(f => f.Name.Length);
            int maxValueLength = 15; // Sabit değer genişliği

            foreach (var field in fields)
            {
                var value = field.GetValue(message.data)?.ToString() ?? "null";
                var typeName = field.FieldType.Name;

                // Format: FieldName     Value          Type
                //         |<-name->|<---value--->|<---type--->|
                sb.AppendLine($"{field.Name.PadRight(maxNameLength)}    {value.PadLeft(maxValueLength)}    {typeName,-12}");
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
