using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DongGfx.Core.Fx;

/// <summary>
/// Family 9 — ML: a feature store on disk (features → label rows written by
/// the engine host as cycles run) and an online logistic classifier trained
/// by SGD. Deliberately small and inspectable: the walk-forward harness, not
/// the model, decides whether it trades.
/// </summary>
public static class FxFeatureStore
{
    /// <summary>Append one labeled row: label first, then features.</summary>
    public static void Append(string path, double label, IReadOnlyList<double> features)
    {
        var line = string.Join(",",
            new[] { label.ToString("R", CultureInfo.InvariantCulture) }
                .Concat(features.Select(f => f.ToString("R", CultureInfo.InvariantCulture))));
        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>Read all rows. Tolerates blank lines and bad rows (skipped).</summary>
    public static IReadOnlyList<(double Label, double[] Features)> Read(string path)
    {
        var rows = new List<(double, double[])>();
        if (!File.Exists(path))
        {
            return rows;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var label))
            {
                continue;
            }

            var feats = new double[parts.Length - 1];
            var ok = true;
            for (var i = 1; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out feats[i - 1]))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                rows.Add((label, feats));
            }
        }

        return rows;
    }
}

/// <summary>Online logistic regression (SGD, L2). Features are assumed
/// pre-scaled (z-scores) by the caller.</summary>
public sealed class OnlineLogistic
{
    private double[] _w;
    private double _b;
    private readonly double _lr;
    private readonly double _l2;

    public OnlineLogistic(int featureCount, double learningRate = 0.05, double l2 = 1e-4)
    {
        _w = new double[featureCount];
        _lr = learningRate;
        _l2 = l2;
    }

    public IReadOnlyList<double> Weights => _w;

    public double PredictProb(IReadOnlyList<double> x)
    {
        var z = _b;
        for (var i = 0; i < _w.Length && i < x.Count; i++)
        {
            z += _w[i] * x[i];
        }

        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>One SGD step. Returns the pre-update predicted probability.</summary>
    public double Observe(IReadOnlyList<double> x, double label)
    {
        var p = PredictProb(x);
        var err = label - p;
        for (var i = 0; i < _w.Length && i < x.Count; i++)
        {
            _w[i] += _lr * (err * x[i] - _l2 * _w[i]);
        }

        _b += _lr * err;
        return p;
    }

    /// <summary>Accuracy on a labeled set (majority-threshold 0.5).</summary>
    public double Evaluate(IEnumerable<(double Label, double[] Features)> rows)
    {
        var n = 0;
        var hit = 0;
        foreach (var (label, feats) in rows)
        {
            var pred = PredictProb(feats) >= 0.5 ? 1 : 0;
            hit += Math.Abs(pred - label) < 0.5 ? 1 : 0;
            n++;
        }

        return n > 0 ? (double)hit / n : 0;
    }
}

/// <summary>
/// Family 11 — deep learning: ONNX inference will be hosted in the MT5
/// sidecar (Python runtime, no native deps in the app). This is the contract
/// the sidecar model must satisfy; until a trained model is deployed, the
/// host simply never receives deep-learning signals.
/// </summary>
public interface IFxOnnxModel
{
    /// <summary>Input feature count the exported model expects.</summary>
    int FeatureCount { get; }

    /// <summary>Run inference; returns P(move up) for the next bar.</summary>
    double PredictUp(IReadOnlyList<double> features);
}
