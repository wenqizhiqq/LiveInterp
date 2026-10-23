using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LiveInterp.Core;

public sealed record KnowledgeBaseMeta(
    int Pairs,
    int Terms,
    string PairsLicense,
    string TermsLicense,
    bool Available);

public sealed record KnowledgeMatch(
    string Translation,
    double Score,
    string MatchedSource,
    string MatchedText);

/// <summary>
/// 离线翻译记忆 + 术语库，支持中→英与英→中两个方向的整句检索。
/// Tatoeba 句对负责高置信度命中，CC-CEDICT 只作为中文短术语兜底。
/// </summary>
public sealed class KnowledgeBase
{
    private const double FuzzyThreshold = 0.86;

    private readonly string[] _zh;
    private readonly string[] _en;
    private readonly string[] _zhNorm;
    private readonly string[] _enNorm;
    private readonly Dictionary<string, int> _exactZh;
    private readonly Dictionary<string, int> _exactEn;
    private readonly Dictionary<string, string> _terms;

    public int PairCount => _zh.Length;
    public int TermCount => _terms.Count;

    private KnowledgeBase(
        string[] zh, string[] en, string[] zhNorm, string[] enNorm,
        Dictionary<string, int> exactZh, Dictionary<string, int> exactEn,
        Dictionary<string, string> terms)
    {
        _zh = zh;
        _en = en;
        _zhNorm = zhNorm;
        _enNorm = enNorm;
        _exactZh = exactZh;
        _exactEn = exactEn;
        _terms = terms;
    }

    public static KnowledgeBaseMeta ReadMeta(string dbPath)
    {
        if (!File.Exists(dbPath))
            return new KnowledgeBaseMeta(0, 0, string.Empty, string.Empty, false);

        try
        {
            using var connection = Open(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM meta";
            using var reader = command.ExecuteReader();
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) meta[reader.GetString(0)] = reader.GetString(1);

            int.TryParse(meta.GetValueOrDefault("pairs"), out int pairs);
            int.TryParse(meta.GetValueOrDefault("terms"), out int terms);
            return new KnowledgeBaseMeta(
                pairs, terms,
                meta.GetValueOrDefault("pairs_license", string.Empty),
                meta.GetValueOrDefault("terms_license", string.Empty),
                true);
        }
        catch
        {
            return new KnowledgeBaseMeta(0, 0, string.Empty, string.Empty, false);
        }
    }

    public static KnowledgeBase? TryLoad(string dbPath, out string? error)
    {
        error = null;
        if (!File.Exists(dbPath))
        {
            error = "知识库文件不存在。";
            return null;
        }

        try
        {
            var zh = new List<string>(60000);
            var en = new List<string>(60000);
            var zhNorm = new List<string>(60000);
            var enNorm = new List<string>(60000);
            var exactZh = new Dictionary<string, int>(60000, StringComparer.Ordinal);
            var exactEn = new Dictionary<string, int>(60000, StringComparer.Ordinal);
            var terms = new Dictionary<string, string>(100000, StringComparer.Ordinal);

            using var connection = Open(dbPath);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT zh, en, zh_norm, en_norm FROM pairs ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string sourceZh = reader.GetString(2);
                    string sourceEn = reader.GetString(3);
                    int index = zh.Count;
                    zh.Add(reader.GetString(0));
                    en.Add(reader.GetString(1));
                    zhNorm.Add(sourceZh);
                    enNorm.Add(sourceEn);
                    exactZh[sourceZh] = index;
                    exactEn.TryAdd(sourceEn, index);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT zh_norm, en FROM terms ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string source = reader.GetString(0);
                    if (!terms.ContainsKey(source)) terms[source] = reader.GetString(1);
                }
            }

            return new KnowledgeBase(
                zh.ToArray(), en.ToArray(), zhNorm.ToArray(), enNorm.ToArray(),
                exactZh, exactEn, terms);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>中→英检索。</summary>
    public KnowledgeMatch? Find(string text)
    {
        string norm = TextNormalizer.NormalizeCjk(text);
        if (norm.Length == 0) return null;

        if (_exactZh.TryGetValue(norm, out int exactIndex))
            return new KnowledgeMatch(_en[exactIndex], 1.0, _zh[exactIndex], "知识库");

        if (norm.Length <= 12 && _terms.TryGetValue(norm, out string? term))
            return new KnowledgeMatch(term, 1.0, text.Trim(), "术语库");

        int bestIndex = FindFuzzy(norm, _zhNorm, out double bestScore);
        if (bestIndex < 0) return null;
        return new KnowledgeMatch(_en[bestIndex], bestScore, _zh[bestIndex], "知识库");
    }

    /// <summary>英→中检索。</summary>
    public KnowledgeMatch? FindEnglish(string text)
    {
        string norm = TextNormalizer.NormalizeLatin(text);
        if (norm.Length == 0) return null;

        if (_exactEn.TryGetValue(norm, out int exactIndex))
            return new KnowledgeMatch(_zh[exactIndex], 1.0, _en[exactIndex], "知识库");

        int bestIndex = FindFuzzy(norm, _enNorm, out double bestScore);
        if (bestIndex < 0) return null;
        return new KnowledgeMatch(_zh[bestIndex], bestScore, _en[bestIndex], "知识库");
    }

    private static int FindFuzzy(string norm, string[] corpus, out double bestScore)
    {
        bestScore = 0;
        if (norm.Length < 4) return -1;

        var inputBigrams = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < norm.Length - 1; i++)
            inputBigrams.Add(norm.Substring(i, 2));
        if (inputBigrams.Count == 0) return -1;

        int bestIndex = -1;
        var candidateBigrams = new HashSet<string>(StringComparer.Ordinal);
        int lengthTolerance = Math.Max(4, (int)Math.Ceiling(norm.Length * 0.25));

        for (int index = 0; index < corpus.Length; index++)
        {
            string candidate = corpus[index];
            if (Math.Abs(candidate.Length - norm.Length) > lengthTolerance) continue;

            candidateBigrams.Clear();
            for (int i = 0; i < candidate.Length - 1; i++)
                candidateBigrams.Add(candidate.Substring(i, 2));
            if (candidateBigrams.Count == 0) continue;

            int intersection = 0;
            foreach (string bigram in candidateBigrams)
                if (inputBigrams.Contains(bigram)) intersection++;

            int union = inputBigrams.Count + candidateBigrams.Count - intersection;
            double jaccard = (double)intersection / union;
            if (jaccard < FuzzyThreshold) continue;

            double lengthRatio = (double)Math.Min(candidate.Length, norm.Length)
                                 / Math.Max(candidate.Length, norm.Length);
            double score = jaccard * 0.85 + lengthRatio * 0.15;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }
}
