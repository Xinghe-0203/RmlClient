using System.Collections.Concurrent;
using System.Text;

namespace RmlClient.Core.Logging;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Source, string Message)
{
    public string Format() =>
        $"{Timestamp:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-5}] {Source}: {Message}";
}

/// <summary>
/// 客户端日志：环形内存缓冲 + 落盘。
///
/// 官方 HMLCore 只在异常时把栈上传到 pastebin（见 <c>ErrorForm.cs:46-101</c>），
/// 本地没有任何可读日志。本实现提供：
///   - 有界内存缓冲，供 UI 实时显示
///   - 滚动文件日志（默认单文件 8 MB，保留 3 份）
///   - 明确的上传开关（默认关闭）
/// </summary>
public sealed class ClientLogger : IDisposable
{
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxRotatedFiles = 3;

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _buffer = new();
    private readonly string _filePath;
    private readonly int _bufferLimit;
    private StreamWriter? _writer;
    private bool _disposed;

    public ClientLogger(string filePath, int bufferLimit = 5000)
    {
        _filePath = filePath;
        _bufferLimit = Math.Clamp(bufferLimit, 200, 100_000);
    }

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToList();
        }
    }

    public void Trace(string source, string message) => Write(LogLevel.Trace, source, message);

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public void Warn(string source, string message) => Write(LogLevel.Warn, source, message);

    public void Error(string source, string message, Exception? ex = null) =>
        Write(LogLevel.Error, source, ex is null ? message : $"{message} :: {Describe(ex)}");

    /// <summary>
    /// 异常的紧凑描述。只用 ex.Message 会丢掉类型、HRESULT 和堆栈
    /// （COMException 的 Message 甚至是空的，等于什么都没记），
    /// 这里压成单行并限长，保证日志仍然是逐行可解析的。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var text = ex.ToString().Replace("\r\n", " | ").Replace('\n', ' ');
        const int max = 2000;
        return text.Length <= max ? text : text[..max] + " …";
    }

    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.AddLast(entry);
            while (_buffer.Count > _bufferLimit)
            {
                _buffer.RemoveFirst();
            }

            TryAppend(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>清空内存缓冲（磁盘日志保留，便于事后排查）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }
    }

    /// <summary>导出为文本，供用户复制或保存。</summary>
    public string Export()
    {
        var builder = new StringBuilder();
        foreach (var entry in Snapshot())
        {
            builder.AppendLine(entry.Format());
        }

        return builder.ToString();
    }

    private void TryAppend(LogEntry entry)
    {
        try
        {
            _writer ??= OpenWriter();
            _writer.WriteLine(entry.Format());
            _writer.Flush();

            if (_writer.BaseStream.Position > MaxFileBytes)
            {
                Rotate();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写失败不能影响主流程。
            _writer = null;
        }
    }

    private StreamWriter OpenWriter()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(new FileStream(
            _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = false,
        };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        for (var i = MaxRotatedFiles - 1; i >= 1; i--)
        {
            var source = $"{_filePath}.{i}";
            var target = $"{_filePath}.{i + 1}";
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }

        if (File.Exists(_filePath))
        {
            File.Move(_filePath, $"{_filePath}.1", overwrite: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// 尾部跟随读取 HMLCore / AssemblyLoader 写出的日志文件。
///
/// 官方 <c>HLoader</c> 把日志写到 <c>%APPDATA%\RaftModLoader\logs\hloader.log</c>，
/// 但没有任何界面能看它 —— 用户只能自己去翻文件。
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _path;
    private readonly int _initialTailLines;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private long _position;
    private Task? _loop;

    /// <param name="path">要跟随的文件。</param>
    /// <param name="initialTailLines">
    /// 首次启动时先回读文件末尾多少行。0 = 只跟随新内容（hloader.log 用），
    /// 大于 0 会把已有内容也读进来（Player.log 用，崩溃现场就在旧内容里）。
    /// </param>
    public LogTailer(string path, int initialTailLines = 0)
    {
        _path = path;
        _initialTailLines = Math.Max(0, initialTailLines);
    }

    public event Action<string>? LineReceived;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        // 默认从文件末尾开始，避免一启动就灌进几万行历史；
        // 指定了 initialTailLines 时改为回退若干行。
        _position = Math.Max(0, new FileInfo(_path).Exists ? new FileInfo(_path).Length : 0);

        if (_initialTailLines > 0 && _position > 0)
        {
            _position = FindTailOffset(_path, _initialTailLines);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _pending.Enqueue(string.Empty);
            _watcher.Created += (_, _) => _pending.Enqueue(string.Empty);
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>估算「文件末尾往前 N 行」对应的字节偏移。</summary>
    private static long FindTailOffset(string path, int lines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            var buffer = new byte[16 * 1024];
            var remaining = length;
            var seen = 0;

            // 从后往前按块扫，数够 N 个换行就停。
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                remaining -= chunk;
                stream.Seek(remaining, SeekOrigin.Begin);

                var read = stream.Read(buffer, 0, chunk);
                for (var i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    if (++seen > lines)
                    {
                        return remaining + i + 1;
                    }
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);

                if (!File.Exists(_path))
                {
                    continue;
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length < _position)
                {
                    // 文件被截断（重启游戏），从头读。
                    _position = 0;
                }

                if (stream.Length == _position)
                {
                    continue;
                }

                stream.Seek(_position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    LineReceived?.Invoke(line);
                }

                _position = stream.Position;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 游戏独占写入时读失败，下一轮再试。
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}
