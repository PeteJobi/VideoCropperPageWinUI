using System;
using System.IO;
using System.Threading.Tasks;
using WinUIShared.Controls;
using WinUIShared.Helpers;

namespace VideoCropperPage
{
    public class CropProcessor(string ffmpegPath): Processor(ffmpegPath)
    {
        public async Task Crop(string fileName, string ffmpegPath, string x, string y, string width, string height)
        {
            progressPrimary.Report(0);
            centerTextPrimary.Report("0.0 %");
            rightTextPrimary.Report("Cropping...");
            var cropParams = gpuInfo?.Vendor switch
            {
                GpuVendor.Nvidia => $"hwdownload,format=nv12,crop={width}:{height}:{x}:{y},hwupload_cuda",
                GpuVendor.Amd => $"hwdownload,format=nv12,crop={width}:{height}:{x}:{y},format=nv12",
                GpuVendor.Intel => $"vpp_qsv=w={width}:h={height}:crop_x={x}:crop_y={y}",
                _ => $"crop={width}:{height}:{x}:{y}"
            };
            await StartFfmpegTranscodingProcessDefaultQuality([fileName], GetOutputName(fileName), $"-vf \"{cropParams}\"", (progress, _, _, _) =>
            {
                IncrementProgress(progress);
            });
            if (HasBeenKilled()) return;
            AllDone();
        }

        private string GetOutputName(string path)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            outputFile = Path.Combine(parentFolder, $"{inputName}_CROPPED{extension}");
            File.Delete(outputFile);
            return outputFile;
        }

        private void IncrementProgress(double progress)
        {
            progressPrimary.Report(progress);
            centerTextPrimary.Report($"{Math.Round(progress, 2)} %");
        }

        private void AllDone()
        {
            progressPrimary.Report(ProgressMax);
            centerTextPrimary.Report("100 %");
        }
    }
}
