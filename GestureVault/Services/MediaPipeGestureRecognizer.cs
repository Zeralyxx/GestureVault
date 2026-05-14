using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace GestureVault.Services
{
    internal sealed class MediaPipeGestureRecognizer : IDisposable
    {
        private const string ModelRelativePath = "Assets/Models/gesture_recognizer.task";
        private const string ScriptRelativePath = "Tools/mediapipe_gesture_recognizer.py";
        private const float MinimumConfidence = 0.50f;
        private readonly object _gate = new();
        private Process? _process;
        private bool _disabled;

        public bool IsAvailable => !_disabled && ResolveModelPath() != null && ResolveScriptPath() != null;

        public string? Predict(Mat frame)
        {
            if (!IsAvailable)
                return null;

            lock (_gate)
            {
                try
                {
                    EnsureStarted();
                    if (_process?.HasExited != false ||
                        _process.StandardInput.BaseStream == null ||
                        _process.StandardOutput.BaseStream == null)
                    {
                        return null;
                    }

                    if (!Cv2.ImEncode(".jpg", frame, out byte[] imageBytes))
                        return null;

                    _process.StandardInput.WriteLine(Convert.ToBase64String(imageBytes));
                    string? line = ReadLineWithTimeout(_process, TimeSpan.FromMilliseconds(700));
                    if (string.IsNullOrWhiteSpace(line))
                        return null;

                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 2)
                        return null;

                    string label = parts[0];
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float confidence))
                        return null;

                    if (confidence < MinimumConfidence || label == "NO_HAND")
                        return null;

                    return label;
                }
                catch
                {
                    StopProcess();
                    _disabled = true;
                    return null;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
                StopProcess();
        }

        private void EnsureStarted()
        {
            if (_process?.HasExited == false)
                return;

            string? scriptPath = ResolveScriptPath();
            string? modelPath = ResolveModelPath();
            if (scriptPath == null || modelPath == null)
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" \"{modelPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(startInfo);
        }

        private static string? ReadLineWithTimeout(Process process, TimeSpan timeout)
        {
            Task<string?> readTask = process.StandardOutput.ReadLineAsync();
            return readTask.Wait(timeout) ? readTask.Result : null;
        }

        private void StopProcess()
        {
            try
            {
                if (_process?.HasExited == false)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        private static string? ResolveModelPath() => ResolvePath(ModelRelativePath);

        private static string? ResolveScriptPath() => ResolvePath(ScriptRelativePath);

        private static string? ResolvePath(string relativePath)
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), relativePath)
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
