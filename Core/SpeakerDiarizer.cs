namespace LiveInterp.Core;

/// <summary>在线说话人聚类：用 Vosk x-vector 的余弦相似度把每句归到最近的簇。</summary>
public sealed class SpeakerDiarizer
{
    private const double Threshold = 0.55;
    private const int MaxSpeakers = 8;

    private readonly object _gate = new();
    private readonly List<Cluster> _clusters = new();

    public int SpeakerCount
    {
        get { lock (_gate) return _clusters.Count; }
    }

    public string Assign(float[] embedding)
    {
        if (embedding.Length == 0) return "未知";

        lock (_gate)
        {
            int bestIndex = -1;
            double bestSimilarity = -1;
            for (int index = 0; index < _clusters.Count; index++)
            {
                double similarity = Cosine(embedding, _clusters[index].Centroid);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = index;
                }
            }

            if (bestIndex >= 0 && bestSimilarity >= Threshold)
            {
                Update(bestIndex, embedding);
                return Label(bestIndex);
            }

            if (_clusters.Count < MaxSpeakers)
            {
                _clusters.Add(new Cluster(embedding, 1));
                return Label(_clusters.Count - 1);
            }

            Update(bestIndex, embedding);
            return Label(bestIndex);
        }
    }

    public void Reset()
    {
        lock (_gate) _clusters.Clear();
    }

    private void Update(int index, float[] embedding)
    {
        var cluster = _clusters[index];
        int count = cluster.Count + 1;
        for (int i = 0; i < cluster.Centroid.Length && i < embedding.Length; i++)
            cluster.Centroid[i] = (cluster.Centroid[i] * cluster.Count + embedding[i]) / count;
        cluster.Count = count;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denominator = Math.Sqrt(normA * normB);
        return denominator <= 1e-9 ? 0 : dot / denominator;
    }

    private static string Label(int index) => $"说话人 {index + 1}";

    private sealed class Cluster
    {
        public Cluster(float[] centroid, int count)
        {
            Centroid = (float[])centroid.Clone();
            Count = count;
        }

        public float[] Centroid { get; }
        public int Count { get; set; }
    }
}
