using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GestureVault.Services
{
    public class GestureDetectedEventArgs : EventArgs
    {
        public string Direction { get; }
        public GestureDetectedEventArgs(string direction) => Direction = direction;
    }

    public class GestureService : IDisposable
    {
        public event EventHandler<Mat>? FrameReady;
        public event EventHandler<GestureDetectedEventArgs>? GestureObserved;
        public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

        public bool IsRunning { get; private set; }

        private VideoCapture? _capture;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private readonly DispatcherQueue _dispatcher;
        private readonly object _captureGate = new();
        private readonly MediaPipeGestureRecognizer _mediaPipeRecognizer = new();
        private readonly GestureMlClassifier _mlClassifier = new();

        // Static sign tracking: emit only after the same pose is stable.
        private readonly Queue<string> _recentSigns = new(10);
        private const int SignWindowSize = 8;
        private const int StableSignFramesRequired = 5;
        private const int GestureCooldownMs = 900;
        private const int ObservationCooldownMs = 250;
        private DateTime _lastGestureAt = DateTime.MinValue;
        private DateTime _lastObservationAt = DateTime.MinValue;
        private string? _lastObservedSign;

        public GestureService(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void Start()
        {
            if (IsRunning) return;

            _capture = new VideoCapture(0);
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
                // Best-effort shutdown.
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
            _recentSigns.Clear();
            _lastObservedSign = null;
        }

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

                var previewClone = frame.Clone();
                _dispatcher.TryEnqueue(() =>
                {
                    FrameReady?.Invoke(this, previewClone);
                    previewClone.Dispose();
                });

                string? detectedSign = DetectHandSign(frame);
                TrackObservedSign(detectedSign);
                TrackStableSign(detectedSign);
                Thread.Sleep(33);
            }
        }

        private string? DetectHandSign(Mat frame)
        {
            if (_mediaPipeRecognizer.IsAvailable)
                return _mediaPipeRecognizer.Predict(frame);

            if (_mlClassifier.IsAvailable)
                return _mlClassifier.Predict(frame);

            using var mask = BuildSkinMask(frame);

            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0) return null;

            foreach (Point[] contour in contours.OrderByDescending(contour => Cv2.ContourArea(contour)))
            {
                string? sign = ClassifyContour(frame, contour);
                if (!string.IsNullOrWhiteSpace(sign))
                    return sign;
            }

            return null;
        }

        private static string? ClassifyContour(Mat frame, Point[] contour)
        {
            Point[] largest = contour;
            double area = Cv2.ContourArea(largest);
            double frameArea = frame.Width * frame.Height;
            if (area < Math.Max(3500, frameArea * 0.01)) return null;

            var moments = Cv2.Moments(largest);
            if (moments.M00 == 0) return null;

            int cx = (int)(moments.M10 / moments.M00);
            int cy = (int)(moments.M01 / moments.M00);
            var rect = Cv2.BoundingRect(largest);

            bool centered = cx > frame.Width * 0.18 && cx < frame.Width * 0.82 &&
                            cy > frame.Height * 0.20 && cy < frame.Height * 0.88;
            bool handSized = area > frameArea * 0.01;
            bool plausibleHand = centered && handSized &&
                                 rect.Width >= 55 && rect.Height >= 55 &&
                                 rect.Width < frame.Width * 0.72 &&
                                 rect.Height < frame.Height * 0.86;
            if (!plausibleHand) return null;

            Point[] hull = Cv2.ConvexHull(largest);
            double hullArea = Cv2.ContourArea(hull);
            if (hullArea <= 0) return null;

            double solidity = area / hullArea;
            double extent = area / Math.Max(1, rect.Width * rect.Height);
            double aspect = rect.Width / (double)rect.Height;
            double perimeter = Cv2.ArcLength(largest, true);
            Point[] approx = Cv2.ApproxPolyDP(largest, perimeter * 0.018, true);
            int convexityDefectCount = CountConvexityDefects(largest);

            if (LooksLikeFace(frame, rect, area, solidity, extent, aspect, convexityDefectCount))
                return null;

            if (convexityDefectCount >= 3 || (approx.Length >= 12 && extent < 0.50))
                return "OPEN_HAND";

            if (aspect < 0.66 && rect.Height > rect.Width * 1.35 && convexityDefectCount <= 2)
                return "POINT";

            if (solidity >= 0.64 && extent >= 0.34 && convexityDefectCount <= 2 &&
                aspect > 0.42 && aspect < 1.55)
                return "FIST";

            return null;
        }

        private static bool LooksLikeFace(
            Mat frame,
            Rect rect,
            double area,
            double solidity,
            double extent,
            double aspect,
            int convexityDefectCount)
        {
            double frameArea = frame.Width * frame.Height;
            double centerX = rect.X + rect.Width / 2.0;
            double centerY = rect.Y + rect.Height / 2.0;
            bool inFaceZone =
                centerX > frame.Width * 0.24 && centerX < frame.Width * 0.76 &&
                centerY > frame.Height * 0.12 && centerY < frame.Height * 0.58;
            bool ovalSkinBlob =
                aspect > 0.58 && aspect < 1.12 &&
                solidity > 0.82 &&
                extent > 0.50 &&
                convexityDefectCount <= 2;
            bool faceSized =
                area > frameArea * 0.035 ||
                rect.Height > frame.Height * 0.24;

            return inFaceZone && ovalSkinBlob && faceSized;
        }

        private static bool LooksTooOvalForFist(double aspect, double extent, int convexityDefectCount)
        {
            return convexityDefectCount == 0 &&
                   aspect > 0.62 && aspect < 1.12 &&
                   extent > 0.62;
        }

        private static int CountConvexityDefects(Point[] contour)
        {
            if (contour.Length < 4)
                return 0;

            int[] hullIndices = Cv2.ConvexHullIndices(contour);
            if (hullIndices.Length < 4)
                return 0;

            try
            {
                Vec4i[] defects = Cv2.ConvexityDefects(contour, hullIndices);
                return defects.Count(defect => defect.Item3 / 256.0 > 12);
            }
            catch
            {
                return 0;
            }
        }

        private static Mat BuildSkinMask(Mat frame)
        {
            var hsv = new Mat();
            var ycrcb = new Mat();
            var maskA = new Mat();
            var maskB = new Mat();
            var maskC = new Mat();
            var mask = new Mat();

            Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.CvtColor(frame, ycrcb, ColorConversionCodes.BGR2YCrCb);
            Cv2.InRange(hsv, new Scalar(0, 16, 45), new Scalar(28, 255, 255), maskA);
            Cv2.InRange(hsv, new Scalar(160, 16, 45), new Scalar(179, 255, 255), maskB);
            Cv2.BitwiseOr(maskA, maskB, mask);
            Cv2.InRange(ycrcb, new Scalar(0, 133, 77), new Scalar(255, 173, 127), maskC);
            Cv2.BitwiseAnd(mask, maskC, mask);

            maskA.Dispose();
            maskB.Dispose();
            maskC.Dispose();
            hsv.Dispose();
            ycrcb.Dispose();

            Cv2.GaussianBlur(mask, mask, new Size(7, 7), 0);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

            return mask;
        }

        private void TrackStableSign(string? sign)
        {
            if (string.IsNullOrWhiteSpace(sign))
            {
                _recentSigns.Clear();
                return;
            }

            if (_recentSigns.Count >= SignWindowSize)
                _recentSigns.Dequeue();
            _recentSigns.Enqueue(sign);

            if ((DateTime.UtcNow - _lastGestureAt).TotalMilliseconds < GestureCooldownMs)
                return;

            int matches = _recentSigns.Count(s => s == sign);
            if (_recentSigns.Count < SignWindowSize || matches < StableSignFramesRequired)
                return;

            _recentSigns.Clear();
            _lastGestureAt = DateTime.UtcNow;

            _dispatcher.TryEnqueue(() =>
            {
                GestureDetected?.Invoke(this, new GestureDetectedEventArgs(sign));
            });
        }

        private void TrackObservedSign(string? sign)
        {
            if (string.IsNullOrWhiteSpace(sign))
            {
                _lastObservedSign = null;
                return;
            }

            bool changed = sign != _lastObservedSign;
            bool cooledDown = (DateTime.UtcNow - _lastObservationAt).TotalMilliseconds >= ObservationCooldownMs;
            if (!changed && !cooledDown)
                return;

            _lastObservedSign = sign;
            _lastObservationAt = DateTime.UtcNow;

            _dispatcher.TryEnqueue(() =>
            {
                GestureObserved?.Invoke(this, new GestureDetectedEventArgs(sign));
            });
        }

        public void Dispose()
        {
            Stop();
            _mediaPipeRecognizer.Dispose();
            _mlClassifier.Dispose();
        }
    }
}
