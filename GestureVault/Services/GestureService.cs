using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace GestureVault.Services
{
    public class GestureDetectedEventArgs : EventArgs
    {
        public string Direction { get; }
        public GestureDetectedEventArgs(string direction) => Direction = direction;
    }

    public class GestureService : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<Mat>? FrameReady;
        public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsRunning { get; private set; }

        private VideoCapture? _capture;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private readonly DispatcherQueue _dispatcher;
        private readonly object _captureGate = new();

        // Motion tracking — stores last 20 hand centroids
        private readonly Queue<Point> _centroids = new(20);
        private const int QueueCapacity = 18;
        private const int MinDeltaPixels = 130;
        private const double DominanceRatio = 1.35;
        private const int GestureCooldownMs = 1200;
        private const int PalmReadyFramesRequired = 8;
        private bool _armedForGesture = false;
        private int _palmReadyFrames = 0;
        private DateTime _lastGestureAt = DateTime.MinValue;

        public GestureService(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // ── Start / Stop ──────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;

            _capture = new VideoCapture(0); // 0 = default webcam
            if (!_capture.IsOpened())
                throw new InvalidOperationException("Could not open webcam. Check that it is connected and not in use.");

            _capture.Set(VideoCaptureProperties.Fps, 30);

            _cts = new CancellationTokenSource();
            IsRunning = true;

            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            _cts?.Cancel();

            try
            {
                if (_captureTask != null && !_captureTask.IsCompleted)
                    _captureTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // The capture loop is best-effort during shutdown.
            }

            lock (_captureGate)
            {
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
            _centroids.Clear();
            _armedForGesture = false;
            _palmReadyFrames = 0;
        }

        // ── Main capture loop (runs on background thread) ─────────────────────
        private void CaptureLoop(CancellationToken token)
        {
            using var frame = new Mat();

            while (!token.IsCancellationRequested)
            {
                bool hasFrame;
                lock (_captureGate)
                {
                    hasFrame = _capture != null && _capture.Read(frame) && !frame.Empty();
                }

                if (!hasFrame)
                    continue;

                // 1. Send raw frame to UI for preview
                var previewClone = frame.Clone();
                _dispatcher.TryEnqueue(() =>
                {
                    FrameReady?.Invoke(this, previewClone);
                    previewClone.Dispose();
                });

                var observation = FindHandObservation(frame);

                if (observation.HasValue)
                {
                    if (!_armedForGesture)
                    {
                        if (observation.Value.IsPalmReady)
                        {
                            _palmReadyFrames++;
                            if (_palmReadyFrames >= PalmReadyFramesRequired)
                            {
                                _armedForGesture = true;
                                _centroids.Clear();
                            }
                        }
                        else
                        {
                            _palmReadyFrames = 0;
                        }
                    }
                    else
                    {
                        EnqueueCentroid(observation.Value.Centroid);
                        AnalyzeMotion();
                    }
                }
                else
                {
                    _centroids.Clear();
                    _armedForGesture = false;
                    _palmReadyFrames = 0;
                }

                Thread.Sleep(33); // ~30 FPS
            }
        }

        private readonly struct HandObservation
        {
            public HandObservation(Point centroid, bool isPalmReady)
            {
                Centroid = centroid;
                IsPalmReady = isPalmReady;
            }

            public Point Centroid { get; }
            public bool IsPalmReady { get; }
        }

        private HandObservation? FindHandObservation(Mat frame)
        {
            using var hsv = new Mat();
            using var mask = new Mat();

            Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);

            var lower = new Scalar(0, 20, 70);
            var upper = new Scalar(20, 255, 255);
            Cv2.InRange(hsv, lower, upper, mask);

            Cv2.GaussianBlur(mask, mask, new Size(7, 7), 0);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

            Cv2.FindContours(mask, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0) return null;

            double maxArea = 0;
            Point[]? largest = null;
            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area > maxArea)
                {
                    maxArea = area;
                    largest = contour;
                }
            }

            if (largest == null || maxArea < 7000) return null;

            var moments = Cv2.Moments(largest);
            if (moments.M00 == 0) return null;

            int cx = (int)(moments.M10 / moments.M00);
            int cy = (int)(moments.M01 / moments.M00);
            var rect = Cv2.BoundingRect(largest);

            bool centered = cx > frame.Width * 0.32 && cx < frame.Width * 0.68 &&
                            cy > frame.Height * 0.25 && cy < frame.Height * 0.75;
            bool palmSized = maxArea > frame.Width * frame.Height * 0.025;
            bool palmLike = rect.Width >= 70 && rect.Height >= 70 &&
                            rect.Width < frame.Width * 0.75 && rect.Height < frame.Height * 0.85;

            return new HandObservation(new Point(cx, cy), centered && palmSized && palmLike);
        }
        // ── Queue management ──────────────────────────────────────────────────
        private void EnqueueCentroid(Point p)
        {
            if (_centroids.Count >= QueueCapacity)
                _centroids.Dequeue();
            _centroids.Enqueue(p);
        }

        // ── Motion direction analysis ─────────────────────────────────────────
        private void AnalyzeMotion()
        {
            if (_centroids.Count < QueueCapacity) return;

            var points = _centroids.ToArray();
            var first = points[0];
            var last = points[^1];

            int deltaX = last.X - first.X;
            int deltaY = last.Y - first.Y;

            int absDeltaX = Math.Abs(deltaX);
            int absDeltaY = Math.Abs(deltaY);

            if ((DateTime.UtcNow - _lastGestureAt).TotalMilliseconds < GestureCooldownMs)
                return;

            if (absDeltaX < MinDeltaPixels && absDeltaY < MinDeltaPixels)
                return;

            if (absDeltaX > absDeltaY && absDeltaX < absDeltaY * DominanceRatio)
                return;
            if (absDeltaY > absDeltaX && absDeltaY < absDeltaX * DominanceRatio)
                return;

            string direction;

            if (absDeltaY > absDeltaX)
                direction = deltaY < 0 ? "SWIPE_UP" : "SWIPE_DOWN";
            else
                direction = deltaX < 0 ? "SWIPE_LEFT" : "SWIPE_RIGHT";

            // Reset queue so it doesn't fire repeatedly for the same swipe
            _centroids.Clear();
            _armedForGesture = false;
            _palmReadyFrames = 0;
            _lastGestureAt = DateTime.UtcNow;

            _dispatcher.TryEnqueue(() =>
            {
                GestureDetected?.Invoke(this, new GestureDetectedEventArgs(direction));
            });
        }

        // ── Cleanup ───────────────────────────────────────────────────────────
        public void Dispose()
        {
            Stop();
        }
    }
}
