using System.Text;

namespace RmlClient.Core.Configuration;

/// <summary>
/// 与官方 IniParser 兼容的极简 INI 实现。
///
/// 官方 HMLCore 用 ini-parser 读写 config.ini，格式要点：
///   - 段名用 [name] 包裹
///   - 键值分隔符为 '='，键与值两侧的空格会被去掉
///   - ';' 或 '#' 开头为注释
///   - 键名大小写不敏感（ini-parser 默认 lower-case 化）
///
/// 为了与官方配置文件字节级互通，本实现刻意保持同样的宽松行为，
/// 但额外保证：写回时保留未知的段与键（官方会整体重写，丢失未知键）。
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Section> _sections = new(StringComparer.OrdinalIgnoreCase);

    public sealed class Section
    {
        internal Section(string name) => Name = name;

        public string Name { get; }

        internal List<string> Order { get; } = new();

        internal Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Entries => Values;
    }

    public IReadOnlyCollection<Section> Sections => _order.Select(n => _sections[n]).ToList();

    public Section GetOrAddSection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_sections.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var section = new Section(name);
        _sections[name] = section;
        _order.Add(name);
        return section;
    }

    public bool TryGetSection(string name, out Section section) =>
        _sections.TryGetValue(name, out section!);

    public string? Get(string sectionName, string key, string? fallback = null)
    {
        if (_sections.TryGetValue(sectionName, out var section) &&
            section.Values.TryGetValue(key, out var value))
        {
            return value;
        }

        return fallback;
    }

    public int GetInt(string sectionName, string key, int fallback = 0)
    {
        var raw = Get(sectionName, key);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public bool GetBool(string sectionName, string key, bool fallback = false)
    {
        var raw = Get(sectionName, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // 官方写入的是 Python/ini-parser 风格的 True/False，读取时两种都要认。
        var normalized = raw.Trim();
        if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            normalized == "1")
        {
            return true;
        }

        if (normalized.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            normalized == "0")
        {
            return false;
        }

        return fallback;
    }

    public void Set(string sectionName, string key, string? value)
    {
        var section = GetOrAddSection(sectionName);
        if (!section.Values.ContainsKey(key))
        {
            section.Order.Add(key);
        }

        section.Values[key] = value ?? string.Empty;
    }

    public void Set(string sectionName, string key, bool value) =>
        Set(sectionName, key, value ? "True" : "False");

    public void Set(string sectionName, string key, int value) =>
        Set(sectionName, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool Remove(string sectionName, string key)
    {
        if (!_sections.TryGetValue(sectionName, out var section))
        {
            return false;
        }

        section.Order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return section.Values.Remove(key);
    }

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Section? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (name.Length > 0)
                {
                    current = ini.GetOrAddSection(name);
                }

                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            // 没有段头的键值对，归入一个名为空字符串的段，与 ini-parser 行为一致。
            current ??= ini.GetOrAddSection(string.Empty);

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!current.Values.ContainsKey(key))
            {
                current.Order.Add(key);
            }

            current.Values[key] = value;
        }

        return ini;
    }

    public static IniFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new IniFile();
        }

        // 官方配置文件由 .NET Framework 的 StreamWriter 写出，可能带 BOM。
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Parse(text);
    }

    public string Serialize()
    {
        var builder = new StringBuilder();

        foreach (var sectionName in _order)
        {
            var section = _sections[sectionName];
            if (sectionName.Length > 0)
            {
                builder.Append('[').Append(sectionName).Append(']').Append("\r\n");
            }

            foreach (var key in section.Order)
            {
                if (!section.Values.TryGetValue(key, out var value))
                {
                    continue;
                }

                builder.Append(key).Append(" = ").Append(value).Append("\r\n");
            }

            if (sectionName.Length > 0)
            {
                builder.Append("\r\n");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 原子写入：先写临时文件再替换，避免断电/崩溃留下半个配置文件。
    /// </summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
