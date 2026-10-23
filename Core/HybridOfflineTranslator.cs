using System.IO;

namespace LiveInterp.Core;

/// <summary>
/// 离线混合翻译：知识库高置信度命中优先，未命中走对应方向的 OPUS-MT，
/// 中→英最后可退回内置会议词表。两个方向共用同一份 Tatoeba/术语库。
/// </summary>
public sealed class HybridOfflineTranslator : ITranslator, IDisposable
{
    private readonly TranslateDirection _direction;
    private readonly Lazy<KnowledgeBase?> _knowledge;
    private readonly Lazy<NmtTranslator?> _nmt;
    private readonly PhrasebookTranslator _fallback = new();
    private readonly Action<string> _notice;
    private int _nmtUnavailableNotified;

    public HybridOfflineTranslator(
        string kbPath,
        string zhEnModelDir,
        string enZhModelDir,
        TranslateDirection direction,
        Action<string> notice)
    {
        _direction = direction;
        _notice = notice;

        _knowledge = new Lazy<KnowledgeBase?>(() =>
        {
            var kb = KnowledgeBase.TryLoad(kbPath, out string? error);
            if (kb == null && !string.IsNullOrWhiteSpace(error))
                _notice($"离线知识库加载失败：{error}");
            return kb;
        });

        string modelDir = direction == TranslateDirection.ZhToEn ? zhEnModelDir : enZhModelDir;
        _nmt = new Lazy<NmtTranslator?>(() =>
        {
            try
            {
                if (!NmtTranslator.IsAvailable(modelDir))
                {
                    _notice("离线 NMT 模型缺失，当前只使用知识库和内置词表。");
                    return null;
                }
                return new NmtTranslator(modelDir);
            }
            catch (Exception ex)
            {
                _notice($"离线 NMT 加载失败：{ex.Message}");
                return null;
            }
        });
    }

    public async Task<TranslationResult?> TranslateAsync(string text, CancellationToken token)
    {
        KnowledgeBase? knowledge = null;
        try { knowledge = _knowledge.Value; }
        catch (Exception ex) { _notice($"离线知识库异常：{ex.Message}"); }

        var match = _direction == TranslateDirection.ZhToEn
            ? knowledge?.Find(text)
            : knowledge?.FindEnglish(text);
        if (match != null)
        {
            return new TranslationResult(
                match.Translation,
                match.MatchedSource,
                match.Score,
                match.MatchedText);
        }

        if (_direction == TranslateDirection.ZhToEn
            && _fallback.TryGetExact(text, out string curated))
        {
            return new TranslationResult(curated, "词表", 1.0);
        }

        if (_direction == TranslateDirection.EnToZh
            && _fallback.TryGetExactEnglish(text, out string curatedZh))
        {
            return new TranslationResult(curatedZh, "词表", 1.0);
        }

        NmtTranslator? nmt = null;
        try { nmt = _nmt.Value; }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _nmtUnavailableNotified, 1) == 0)
                _notice($"离线 NMT 不可用：{ex.Message}");
        }

        if (nmt != null)
        {
            try
            {
                string? translated = await nmt.TranslateAsync(text, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated))
                    return new TranslationResult(translated, "离线 NMT");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _nmtUnavailableNotified, 1) == 0)
                    _notice($"离线 NMT 推理失败：{ex.Message}");
            }
        }

        return _direction == TranslateDirection.ZhToEn
            ? await _fallback.TranslateAsync(text, token).ConfigureAwait(false)
            : null;
    }

    public void Warmup()
    {
        try
        {
            _ = _knowledge.Value;
            _ = _nmt.Value;
        }
        catch
        {
            // 预热失败会在首次翻译时重试并提示
        }
    }

    public void Dispose()
    {
        if (_nmt.IsValueCreated) _nmt.Value?.Dispose();
        _fallback.Dispose();
    }
}
