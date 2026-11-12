using System.Text;

namespace TaskSandbox.Obsidian;

internal sealed class MarkdownFrontmatter
{
    private const string Delimiter = "---";

    private MarkdownFrontmatter(
        Dictionary<string, string> values,
        Dictionary<string, List<string>> lists,
        string body)
    {
        Values = values;
        Lists = lists;
        Body = body;
    }

    public Dictionary<string, string> Values { get; }
    public Dictionary<string, List<string>> Lists { get; }
    public string Body { get; set; }

    public string GetValue(string key, string fallback = "") =>
        Values.TryGetValue(key, out string? value) ? value : fallback;

    public IReadOnlyList<string> GetList(string key) =>
        Lists.TryGetValue(key, out List<string>? values) ? values : Array.Empty<string>();

    public void SetValue(string key, string value)
    {
        Values[key] = value;
        Lists.Remove(key);
    }

    public void SetList(string key, IEnumerable<string> values)
    {
        Lists[key] = values.ToList();
        Values.Remove(key);
    }

    public void Remove(string key)
    {
        Values.Remove(key);
        Lists.Remove(key);
    }

    public static MarkdownFrontmatter Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Delimiter, StringComparison.Ordinal))
            return new MarkdownFrontmatter(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), markdown);

        int closingIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), Delimiter, StringComparison.Ordinal))
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
            throw new InvalidDataException("Markdown Frontmatter начинается с '---', но не имеет закрывающего '---'.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? activeListKey = null;

        for (int i = 1; i < closingIndex; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (activeListKey is null)
                    throw new InvalidDataException($"Некорректный элемент YAML-списка в строке {i + 1}.");

                lists[activeListKey].Add(Unquote(trimmed[2..].Trim()));
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException($"Некорректная строка Frontmatter: '{line}'.");

            string key = line[..separator].Trim();
            if (!IsValidKey(key))
                throw new InvalidDataException($"Недопустимый ключ Frontmatter: '{key}'.");

            if (values.ContainsKey(key) || lists.ContainsKey(key))
                throw new InvalidDataException($"Повторяющийся ключ Frontmatter: '{key}'.");

            string rawValue = line[(separator + 1)..].Trim();
            if (rawValue.Length == 0)
            {
                lists.Add(key, []);
                activeListKey = key;
            }
            else if (rawValue.Length >= 2 && rawValue[0] == '[' && rawValue[^1] == ']')
            {
                lists.Add(key, ParseInlineList(rawValue[1..^1]));
                activeListKey = null;
            }
            else
            {
                values.Add(key, Unquote(rawValue));
                activeListKey = null;
            }
        }

        string body = string.Join(Environment.NewLine, lines.Skip(closingIndex + 1));
        if (body.StartsWith(Environment.NewLine, StringComparison.Ordinal))
            body = body[Environment.NewLine.Length..];

        return new MarkdownFrontmatter(values, lists, body);
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder(Body.Length + 256);
        builder.AppendLine(Delimiter);

        foreach ((string key, string value) in Values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(key).Append(": ").Append(Quote(value)).AppendLine();

        foreach ((string key, List<string> items) in Lists.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).AppendLine(":");
            foreach (string item in items)
                builder.Append("  - ").Append(Quote(item)).AppendLine();
        }

        builder.AppendLine(Delimiter);
        if (Body.Length > 0)
        {
            builder.AppendLine();
            builder.Append(Body.TrimStart('\r', '\n'));
        }

        return builder.ToString();
    }

    private static List<string> ParseInlineList(string value)
    {
        var result = new List<string>();
        var item = new StringBuilder();
        char quote = '\0';
        bool escaped = false;

        foreach (char character in value)
        {
            if (escaped)
            {
                item.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\' && quote == '"')
            {
                escaped = true;
                item.Append(character);
                continue;
            }

            if ((character == '"' || character == '\'') && (quote == '\0' || quote == character))
            {
                quote = quote == '\0' ? character : '\0';
                item.Append(character);
                continue;
            }

            if (character == ',' && quote == '\0')
            {
                string parsed = Unquote(item.ToString().Trim());
                if (parsed.Length > 0)
                    result.Add(parsed);
                item.Clear();
                continue;
            }

            item.Append(character);
        }

        string last = Unquote(item.ToString().Trim());
        if (last.Length > 0)
            result.Add(last);

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);

        return value;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0)
            return false;

        foreach (char character in key)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }
}
