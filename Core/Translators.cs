using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LiveInterp.Core;

public sealed class OpenAiTranslator : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _source;
    private readonly string _target;

    public OpenAiTranslator(string apiBase, string apiKey, string model, string source, string target)
    {
        _endpoint = ApiEndpoints.Build(apiBase, "/chat/completions");
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
        _source = source;
        _target = target;
    }

    public async Task<TranslationResult?> TranslateAsync(string text, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["temperature"] = 0.2,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = $"You are a professional simultaneous interpreter. Translate the speaker's {_source} utterance into {_target}. Output only the translation: no quotes, no explanations, no notes. Keep it natural, concise and faithful to the original."
                },
                new { role = "user", content = text }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"翻译接口 HTTP {(int)response.StatusCode}：{WhisperRecognizer.Shorten(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("翻译接口返回为空。");

        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content)
            ? null
            : new TranslationResult(content.Trim().Trim('"'), "在线");
    }

    public void Dispose() { }
}

/// <summary>
/// 离线演示用会议词表：整句命中 / 包含匹配 / 二元组相似度 / 分句拼装。
/// 只用于在没有 API Key 时演示「识别→翻译→朗读→打断」，不是通用机器翻译。
/// </summary>
public sealed class PhrasebookTranslator : ITranslator
{
    private sealed record Entry(string Norm, string En);

    private static readonly Lazy<Entry[]> EntriesHolder = new(BuildEntries);
    private static Entry[] Entries => EntriesHolder.Value;

    public static int EntryCount => Entries.Length;

    public Task<TranslationResult?> TranslateAsync(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? result = Translate(text);
        return Task.FromResult(result == null
            ? null
            : new TranslationResult(result, "词表", 0.68));
    }

    /// <summary>命中内置会议短语时优先于 NMT，保证固定译法不被模型改写。</summary>
    public bool TryGetExact(string text, out string translation)
    {
        translation = string.Empty;
        string norm = Normalize(text);
        if (norm.Length == 0) return false;
        foreach (var entry in Entries)
        {
            if (entry.Norm != norm) continue;
            translation = entry.En;
            return true;
        }
        return false;
    }

    /// <summary>英→中方向的固定短语反查，避免模型改写常用句。</summary>
    public bool TryGetExactEnglish(string text, out string translation)
    {
        translation = string.Empty;
        string norm = Normalize(text);
        if (norm.Length == 0) return false;
        if (!Reverse.TryGetValue(norm, out string? zh)) return false;
        translation = zh;
        return true;
    }

    private static readonly Lazy<Dictionary<string, string>> ReverseHolder = new(BuildReverse);
    private static Dictionary<string, string> Reverse => ReverseHolder.Value;

    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Pairs)
        {
            string key = Normalize(pair.En);
            if (key.Length == 0) continue;
            map.TryAdd(key, pair.Zh);
        }
        return map;
    }

    public void Dispose() { }

    private static string? Translate(string input)
    {
        string norm = Normalize(input);
        if (norm.Length == 0) return null;

        foreach (var entry in Entries)
            if (entry.Norm == norm) return entry.En;

        // 整句包含某个更长的词条，且该词条覆盖原句大部分内容
        Entry? contained = null;
        foreach (var entry in Entries)
        {
            if (entry.Norm.Length < 4 || entry.Norm.Length < norm.Length * 0.6) continue;
            if (!norm.Contains(entry.Norm, StringComparison.Ordinal)) continue;
            if (contained == null || entry.Norm.Length > contained.Norm.Length) contained = entry;
        }
        if (contained != null) return contained.En;

        string? segmented = GreedySegment(norm);
        if (segmented != null) return segmented;

        var best = BestMatch(norm);
        if (best.Score >= 0.78) return best.Entry.En;

        var clauses = System.Text.RegularExpressions.Regex
            .Split(input, @"[，,。.；;！!？?\s]+")
            .Where(c => c.Length > 0)
            .ToArray();
        if (clauses.Length > 1)
        {
            var pieces = new List<string>();
            int total = 0;
            int matched = 0;
            foreach (var clause in clauses)
            {
                string clauseNorm = Normalize(clause);
                if (clauseNorm.Length == 0) continue;
                total += clauseNorm.Length;
                var match = BestMatch(clauseNorm);
                if (match.Score >= 0.66)
                {
                    pieces.Add(match.Entry.En);
                    matched += clauseNorm.Length;
                }
            }
            if (pieces.Count > 0 && total > 0 && (double)matched / total >= 0.6)
                return string.Join(" ", pieces);
        }

        return null;
    }

    /// <summary>SAPI 通常不给标点，按最长词条贪心切分整段话，能覆盖连读的常见句。</summary>
    private static string? GreedySegment(string norm)
    {
        var pieces = new List<string>();
        int index = 0;
        int matched = 0;
        while (index < norm.Length)
        {
            Entry? best = null;
            foreach (var entry in Entries)
            {
                if (entry.Norm.Length < 2 || entry.Norm.Length > norm.Length - index) continue;
                if (string.CompareOrdinal(norm, index, entry.Norm, 0, entry.Norm.Length) != 0) continue;
                if (best == null || entry.Norm.Length > best.Norm.Length) best = entry;
            }

            if (best != null)
            {
                pieces.Add(best.En);
                index += best.Norm.Length;
                matched += best.Norm.Length;
            }
            else
            {
                index++;
            }
        }

        if (matched == 0 || (double)matched / norm.Length < 0.8) return null;
        return string.Join(" ", pieces);
    }

    private static (Entry Entry, double Score) BestMatch(string norm)
    {
        Entry best = Entries[0];
        double bestScore = -1;
        foreach (var entry in Entries)
        {
            double score = Jaccard(Bigrams(norm), Bigrams(entry.Norm));
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }
        return (best, bestScore);
    }

    private static HashSet<string> Bigrams(string text)
    {
        var set = new HashSet<string>();
        if (text.Length == 1) set.Add(text);
        for (int i = 0; i < text.Length - 1; i++) set.Add(text.Substring(i, 2));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int intersection = a.Count(x => b.Contains(x));
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static Entry[] BuildEntries()
        => Pairs.Select(p => new Entry(Normalize(p.Zh), p.En)).ToArray();

    private static readonly (string Zh, string En)[] Pairs =
    {
        ("大家好，欢迎参加今天的会议", "Hello everyone, welcome to today's meeting."),
        ("大家好", "Hello everyone."),
        ("你好", "Hello."),
        ("你好吗", "How are you?"),
        ("能听到我吗", "Can you hear me?"),
        ("欢迎参加今天的会议", "Welcome to today's meeting."),
        ("欢迎大家", "Welcome everyone."),
        ("很高兴见到大家", "It's a pleasure to meet you all."),
        ("会议现在开始", "The meeting will now begin."),
        ("我们开始吧", "Let's get started."),
        ("请开始", "Please begin."),
        ("我们继续", "Let's continue."),
        ("今天讨论三个议题", "Today we will discuss three topics."),
        ("今天的议程", "Today's agenda."),
        ("第一个议题", "The first topic."),
        ("第二个议题", "The second topic."),
        ("第三个议题", "The third topic."),
        ("我先介绍一下背景", "Let me first introduce the background."),
        ("请大家看一下屏幕", "Please take a look at the screen."),
        ("现在开始测试", "Let's start the test now."),
        ("这是一个测试", "This is a test."),
        ("我正在测试同声传译", "I am testing simultaneous interpretation."),
        ("我正在测试这个软件", "I am testing this software."),
        ("我在测试麦克风", "I am testing the microphone."),
        ("这个软件可以自动打断", "This software can interrupt automatically."),
        ("你一说话它就会停下来", "It stops as soon as you start speaking."),
        ("你说话我就停", "I stop as soon as you speak."),
        ("测试一下打断", "Let's test the interruption."),
        ("这个功能很好", "This feature works well."),
        ("请打断我", "Please interrupt me."),
        ("打断一下", "Let me interrupt for a moment."),
        ("请停一下", "Please stop for a moment."),
        ("请翻译这句话", "Please translate this sentence."),
        ("我听不懂", "I don't understand."),
        ("谢谢你的讲解", "Thank you for the explanation."),
        ("时间不多了", "We are running out of time."),
        ("我们休息十分钟", "Let's take a ten-minute break."),
        ("会议到此结束", "That's all for today's meeting."),
        ("谢谢大家", "Thank you all."),
        ("谢谢你的时间", "Thank you for your time."),
        ("谢谢", "Thanks."),
        ("再见", "Goodbye."),
        ("好的", "Okay."),
        ("没问题", "No problem."),
        ("可以", "Sure."),
        ("不行", "That won't work."),
        ("我明白了", "I understand."),
        ("请说得慢一点", "Please speak a little slower."),
        ("请再说一遍", "Please say that again."),
        ("能再解释一下吗", "Could you explain that again?"),
        ("我没听清", "I didn't catch that."),
        ("我有一个问题", "I have a question."),
        ("我来回答这个问题", "I'll answer that question."),
        ("感谢你的提问", "Thank you for your question."),
        ("这个问题很重要", "This question is very important."),
        ("我同意你的看法", "I agree with you."),
        ("我不同意", "I disagree."),
        ("我的观点是", "My point is this."),
        ("换句话说", "In other words."),
        ("总而言之", "In summary."),
        ("总的来说", "Overall."),
        ("举个例子", "For example."),
        ("数据显示", "The data shows that."),
        ("目前进展顺利", "Progress is going well so far."),
        ("我们遇到了一些问题", "We have run into some problems."),
        ("我们的目标是", "Our goal is to."),
        ("这个方案可行", "This plan is feasible."),
        ("这个方案需要讨论", "This plan needs discussion."),
        ("预算需要重新评估", "The budget needs to be re-evaluated."),
        ("风险在可控范围内", "The risk is under control."),
        ("客户反馈很好", "Customer feedback is positive."),
        ("质量比数量更重要", "Quality is more important than quantity."),
        ("我们需要更多时间", "We need more time."),
        ("我们需要优化流程", "We need to optimize the process."),
        ("这周完成测试", "Finish testing this week."),
        ("下个月上线", "Launch next month."),
        ("需要大家配合", "We need everyone's cooperation."),
        ("请把资料发给我", "Please send me the materials."),
        ("我会跟进这件事", "I will follow up on this."),
        ("我们可以安排一次会议", "We can schedule a meeting."),
        ("明天上午十点开始", "It starts at ten tomorrow morning."),
        ("地点在第三会议室", "The location is meeting room three."),
        ("请大家提前五分钟到场", "Please arrive five minutes early."),
        ("我们下周继续讨论", "We will continue the discussion next week."),
        ("同比增长百分之二十", "A twenty percent year-on-year increase."),
        ("成本下降了", "Costs have gone down."),
        ("请确认一下", "Please confirm."),
        ("有问题随时联系我", "Feel free to contact me anytime."),
        ("请注意听", "Please listen carefully."),
        ("请注意", "Please note."),
        ("我说一句", "Let me say a few words."),
        ("请继续", "Please continue."),
        ("请稍等", "Please wait a moment."),
        ("我马上回来", "I'll be right back."),
        ("今天天气很好", "The weather is nice today."),
        ("你吃饭了吗", "Have you eaten?"),
        ("首先", "First of all."),
        ("其次", "Secondly."),
        ("最后", "Finally."),
        ("因为", "Because."),
        ("所以", "Therefore."),
        ("但是", "However."),
        ("另外", "In addition."),
        ("这意味着", "This means that."),
        ("我建议", "I suggest that."),
        ("我们必须", "We must."),
        ("我们可以", "We can."),
        ("我们需要", "We need to."),
        ("这个项目", "This project."),
        ("这个问题", "This problem."),
        ("这个想法很好", "That's a good idea."),
        ("我们支持你", "We support you."),
        ("会议记录", "Meeting minutes."),
        ("下一步计划", "Next steps."),
        ("进度", "Progress."),
        ("预算", "Budget."),
        ("客户", "Customer."),
        ("风险", "Risk.")
    };
}
