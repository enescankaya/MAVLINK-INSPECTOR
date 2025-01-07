using System.Collections.Concurrent;
using System.Text;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;

namespace MavlinkInspector;

public static class Extensions
{
    private static readonly ConcurrentDictionary<string, WeakReference<TreeViewItem>> _itemCache =
        new ConcurrentDictionary<string, WeakReference<TreeViewItem>>();

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
                DataContext = data,  // DataContext'i message olarak ayarla
                IsExpanded = true
            };
            parent.Items.Add(item);
            SortTreeItems(parent); // Yeni item eklendiğinde sırala
        }
        else if (item.Header.ToString() != header)
        {
            item.Header = CreateTreeItemHeader(header);
        }

        return item;
    }

    private static void SortTreeItems(ItemsControl parent)
    {
        var items = parent.Items.Cast<TreeViewItem>().ToList();

        // Mesaj node'larını sırala, Vehicle ve Component node'larını olduğu gibi bırak
        var sortedItems = items.Where(i => i.Header is StackPanel)
                             .OrderBy(GetSortableText)
                             .Concat(items.Where(i => !(i.Header is StackPanel)));

        parent.Items.Clear();
        foreach (var item in sortedItems)
        {
            parent.Items.Add(item);
        }
    }

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

    public static void UpdateMessageDetails(this TextBox textBox, MAVLink.MAVLinkMessage message)
    {
        var sb = new StringBuilder();

        // Header bilgileri
        sb.AppendFormat("Message Type:      {0}\n", message.msgtypename);
        sb.AppendFormat("System ID:         {0}\n", message.sysid);
        sb.AppendFormat("Component ID:      {0}\n", message.compid);
        sb.AppendFormat("Message ID:        {0}\n", message.msgid);
        sb.AppendFormat("Length:            {0} bytes\n", message.Length);
        sb.AppendLine();

        try
        {
            // Message fields
            var messageInfo = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(message.msgid);
            if (messageInfo.type != null)
            {
                sb.AppendLine("Fields:");
                sb.AppendLine("Name                    Value             Type");
                sb.AppendLine("----------------------------------------------------");

                foreach (var field in messageInfo.type.GetFields())
                {
                    string fieldName = field.Name;
                    object fieldValue = field.GetValue(message.data) ?? "null";
                    string typeName = GetFriendlyTypeName(field.FieldType);

                    // Array handling
                    if (field.FieldType.IsArray)
                    {
                        fieldValue = FormatArrayValue(fieldValue as Array);
                    }
                    // Special types handling
                    else if (field.Name.Contains("time", StringComparison.OrdinalIgnoreCase) &&
                            fieldValue is ulong timestamp)
                    {
                        fieldValue = ConvertTimestamp(timestamp);
                    }

                    // Format the line with proper padding
                    sb.AppendFormat("{0,-20} {1,-20} {2}\n",
                        fieldName,
                        fieldValue?.ToString() ?? "null",
                        typeName);
                }
            }
            else
            {
                sb.AppendLine("No message field information available.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\nError parsing message fields: {ex.Message}");
        }

        textBox.Text = sb.ToString();
    }

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

    private static string FormatArrayValue(Array array)
    {
        if (array == null) return "null";

        // For byte arrays, show as hex if small, otherwise show length
        if (array is byte[] bytes)
        {
            if (bytes.Length <= 8)
                return BitConverter.ToString(bytes);
            return $"[{bytes.Length} bytes]";
        }

        // For other arrays, show first few elements
        var items = array.Cast<object>()
                        .Take(5)
                        .Select(x => x?.ToString() ?? "null");

        string result = string.Join(", ", items);
        if (array.Length > 5)
            result += $"... +{array.Length - 5} more";

        return $"[{result}]";
    }

    private static string ConvertTimestamp(ulong timestamp)
    {
        try
        {
            if (timestamp == 0) return "0";

            // Assume microseconds since Unix epoch
            var dateTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(timestamp / 1000))
                                      .LocalDateTime;
            return dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }
        catch
        {
            return timestamp.ToString();
        }
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