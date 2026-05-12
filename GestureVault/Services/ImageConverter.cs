using Microsoft.UI.Xaml.Media.Imaging;
using OpenCvSharp;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace GestureVault.Services
{
    public static class ImageConverter
    {
        /// <summary>
        /// Converts an OpenCV Mat (BGR format) to a SoftwareBitmapSource for WinUI Image display.
        /// </summary>
        public static async Task<SoftwareBitmapSource> MatToSoftwareBitmapSource(Mat mat)
        {
            if (mat == null || mat.Empty())
                throw new ArgumentException("Frame is empty");

            // Convert BGR to BGRA (add alpha channel)
            using var bgra = new Mat();
            Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);

            // Create SoftwareBitmap
            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                bgra.Width,
                bgra.Height,
                BitmapAlphaMode.Premultiplied);

            // Copy pixel data - convert nint to byte[] first
            int dataSize = bgra.Width * bgra.Height * 4; // BGRA = 4 bytes per pixel
            byte[] pixelData = new byte[dataSize];

            // Marshal the unmanaged data pointer to managed byte array
            Marshal.Copy(bgra.Data, pixelData, 0, dataSize);

            // Copy to SoftwareBitmap buffer
            softwareBitmap.CopyFromBuffer(pixelData.AsBuffer());

            // Create SoftwareBitmapSource
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);

            return source;
        }
    }
}