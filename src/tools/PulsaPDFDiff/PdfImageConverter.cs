using System.Runtime.Versioning;
using Docnet.Core;
using Docnet.Core.Models;

namespace PulsaPDFDiff;

[SupportedOSPlatform("windows")]
public static class PdfImageConverter
{
    public static List<string> ConvertToBase64Images(Stream pdfStream, int scalingFactor = 3)
    {
        var images = new List<string>();
        using var ms = new MemoryStream();
        pdfStream.CopyTo(ms);
        var pdfBytes = ms.ToArray();

        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scalingFactor));
        for (var i = 0; i < docReader.GetPageCount(); i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var pngBytes = ConvertBgraToPng(rawBytes, width, height);
            images.Add(Convert.ToBase64String(pngBytes));
        }

        return images;
    }

    private static byte[] ConvertBgraToPng(byte[] bgraData, int width, int height)
    {
        using var bmp = new System.Drawing.Bitmap(width, height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, bmpData.Scan0, bgraData.Length);
        bmp.UnlockBits(bmpData);

        using var output = new MemoryStream();
        bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }
}
