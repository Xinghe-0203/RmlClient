namespace RmlClient.Core.Translation;

/// <summary>翻译失败的分类，用于决定「换一家重试」还是「直接放弃」。</summary>
public enum TranslationFailure
{
    /// <summary>网络不通、超时、DNS 失败。</summary>
    Network,

    /// <summary>接口返回了非预期的状态或结构（例如被限流、参数被拒）。</summary>
    Rejected,

    /// <summary>文本太长且无法安全切分。</summary>
    TooLong,
}

/// <summary>单个翻译提供方。</summary>
public interface ITranslationProvider
{
    /// <summary>显示名，用于日志与「由谁翻译」的提示。</summary>
    string Name { get; }

    /// <summary>单次请求能接受的最大字符数，切分时用。</summary>
    int MaxChunkLength { get; }

    /// <summary>
    /// 翻译一段文本。<paramref name="from"/> 传 <c>"auto"</c> 表示自动检测。
    /// 失败时抛 <see cref="TranslationException"/>。
    /// </summary>
    Task<string> TranslateAsync(
        string text,
        string from,
        TranslationLanguage target,
        CancellationToken ct = default);
}

/// <summary>翻译失败。带分类，方便上层决定是否换提供方。</summary>
public sealed class TranslationException : Exception
{
    public TranslationException(TranslationFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    public TranslationFailure Failure { get; }
}
