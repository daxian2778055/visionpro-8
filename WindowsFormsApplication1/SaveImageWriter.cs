using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:定制存图写文件。
    ///   BMP 仍由 CogImageFileBMP 写（与现场既有 cuntu_fangfa 行为一致，像素格式不丢）；
    ///   JPG 走 GDI+ 编码（Cognex 的 CogImageFileBMP 只能写 BMP），质量 50~100。
    /// </summary>
    internal static class SaveImageWriter
    {
        public static void SaveJpeg(Bitmap bitmap, string filePath, int quality)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException("filePath");
            if (quality < SaveImageSettings.MinJpegQuality) quality = SaveImageSettings.MinJpegQuality;
            if (quality > SaveImageSettings.MaxJpegQuality) quality = SaveImageSettings.MaxJpegQuality;

            ImageCodecInfo codec = GetJpegCodec();
            if (codec == null)
            {
                // ch:理论上不会发生；退化为默认 JPEG 质量而不是抛异常丢图
                bitmap.Save(filePath, ImageFormat.Jpeg);
                return;
            }
            using (EncoderParameters ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bitmap.Save(filePath, codec, ps);
            }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == ImageFormat.Jpeg.Guid)
                    return codecs[i];
            }
            return null;
        }
    }
}
