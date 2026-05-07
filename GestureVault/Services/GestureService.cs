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
        private readonly DispatcherQueue _dispatcher;

        // Motion tracking — stores last 20 hand centroids
        private readonly Queue<Point> _centroids = new(20);
        private const int QueueCapacity = 20;
        private const int MinDeltaPixels = 60; // minimum movement to count as a swipe

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

            Task.Run(() => CaptureLoop(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            _cts?.Cancel();
            _capture?.Release();
            _capture?.Dispose();
            _centroids.Clear();
        }

        // ── Main capture loop (runs on background thread) ─────────────────────
        private void CaptureLoop(CancellationToken token)
        {
            using var frame = new Mat();

            while (!token.IsCancellationRequested)
            {
                if (_capture == null || !_capture.Read(frame) || frame.Empty())
                    continue;

                // 1. Send raw frame to UI for preview
                var previewClone = frame.Clone();
                _dispatcher.TryEnqueue(() =>
                {
                    FrameReady?.Invoke(this, previewClone);
                    previewClone.Dispose();
                });

                // 2. Try to find a hand centroid in this frame
                var centroid = FindHandCentroid(frame);

                if (centroid.HasValue)
                {
                    EnqueueCentroid(centroid.Value);
                    AnalyzeMotion();
                }
                else
                {
                    // No hand visible — reset tracking
                    _centroids.Clear();
                }

                Thread.Sleep(33); // ~30 FPS
            }
        }

        // ── Hand detection via skin-tone HSV masking ──────────────────────────
        private Point? FindHandCentroid(Mat frame)
        {
            using var hsv = new Mat();
            using var mask = new Mat();

            // Convert to HSV colour space
            Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);

            // Skin tone range in HSV
            var lower = new Scalar(0, 20, 70);
            var upper = new Scalar(20, 255, 255);
            Cv2.InRange(hsv, lower, upper, mask);

            // Clean up noise
            Cv2.GaussianBlur(mask, mask, new Size(5, 5), 0);

            // Find contours
            Cv2.FindContours(mask, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0) return null;

            // Find the largest contour (most likely the hand)
            double maxArea = 0;
            Point[]? largest = null;
            foreach (var c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area > maxArea)
                {
                    maxArea = area;
                    largest = c;
                }
            }

            // Ignore very small blobs (noise)
            if (largest == null || maxArea < 3000) return null;

            // Calculate centroid using image moments
            var m = Cv2.Moments(largest);
            if (m.M00 == 0) return null;

            int cx = (int)(m.M10 / m.M00);
            int cy = (int)(m.M01 / m.M00);
            return new Point(cx, cy);
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

            // Must exceed minimum pixel threshold to count as intentional
            if (absDeltaX < MinDeltaPixels && absDeltaY < MinDeltaPixels)
                return;

            string direction;

            if (absDeltaY > absDeltaX)
                direction = deltaY < 0 ? "SWIPE_UP" : "SWIPE_DOWN";
            else
                direction = deltaX < 0 ? "SWIPE_LEFT" : "SWIPE_RIGHT";

            // Reset queue so it doesn't fire repeatedly for the same swipe
            _centroids.Clear();

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