using Microsoft.ML;
using Microsoft.ML.Data;
using OpenCvSharp;
using System;
using System.IO;
using System.Linq;

namespace GestureVault.Services
{
    internal sealed class GestureMlClassifier : IDisposable
    {
        private const string ModelRelativePath = "Assets/Models/GestureModel.zip";
        private const float MinimumConfidence = 0.70f;

        private readonly MLContext _mlContext = new(seed: 1);
        private readonly PredictionEngine<GestureModelInput, GestureModelOutput>? _predictionEngine;

        public bool IsAvailable => _predictionEngine != null;

        public GestureMlClassifier()
        {
            string? modelPath = ResolveModelPath();
            if (modelPath == null)
                return;

            try
            {
                ITransformer model = _mlContext.Model.Load(modelPath, out _);
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<GestureModelInput, GestureModelOutput>(model);
            }
            catch
            {
                _predictionEngine = null;
            }
        }

        public string? Predict(Mat frame)
        {
            if (_predictionEngine == null)
                return null;

            if (!Cv2.ImEncode(".jpg", frame, out byte[] imageBytes))
                return null;

            var output = _predictionEngine.Predict(new GestureModelInput
            {
                ImageSource = imageBytes
            });

            float confidence = output.Score?.Length > 0 ? output.Score.Max() : 0;
            if (confidence < MinimumConfidence)
                return null;

            return NormalizeLabel(output.PredictedLabel);
        }

        public void Dispose()
        {
            _predictionEngine?.Dispose();
        }

        private static string? ResolveModelPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, ModelRelativePath),
                Path.Combine(Directory.GetCurrentDirectory(), ModelRelativePath)
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string? NormalizeLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            string normalized = label
                .Trim()
                .Replace("-", "_")
                .Replace(" ", "_")
                .ToUpperInvariant();

            return normalized switch
            {
                "OPENHAND" => "OPEN_HAND",
                "OPEN_HAND" => "OPEN_HAND",
                "FIST" => "FIST",
                "POINT" => "POINT",
                "NOHAND" => null,
                "NO_HAND" => null,
                _ => null
            };
        }

        private sealed class GestureModelInput
        {
            [ColumnName("ImageSource")]
            public byte[] ImageSource { get; set; } = Array.Empty<byte>();
        }

        private sealed class GestureModelOutput
        {
            [ColumnName("PredictedLabel")]
            public string? PredictedLabel { get; set; }

            [ColumnName("Score")]
            public float[]? Score { get; set; }
        }
    }
}
