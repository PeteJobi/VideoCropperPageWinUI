using System;
using System.IO;
using System.Threading.Tasks;
using WinUIShared.Controls;
using WinUIShared.Helpers;

namespace VideoCropperPage
{
    public class CropProcessor(string ffmpegPath): Processor(ffmpegPath, new FileLogger.FileLogger("ReelBox/Crop"))
    {
        public async Task Crop(string fileName, string ffmpegPath, string x, string y, string width, string height)
        {
            progressPrimary.Report(0);
            centerTextPrimary.Report("0.0 %");
            rightTextPrimary.Report("Cropping...");
            var cropFilter = $"crop={width}:{height}:{x}:{y}";
            switch (gpuInfo?.Vendor)
            {
                case GpuVendor.Intel:
                    cropFilter = $"vpp_qsv=cw={width}:ch={height}:cx={x}:cy={y}";
                    break;
                case GpuVendor.Nvidia or GpuVendor.Amd:
                    var gpuPixelFormat = await GetGpuPixelFormat(fileName);
                    var (hwDownArgs, hwUpArgs) = GpuInfo.FilterParams(gpuInfo, gpuPixelFormat);
                    cropFilter = $"{hwDownArgs}{cropFilter}{hwUpArgs}";
                    break;
            }

            await Ffmpeg().Input(fileName).Output(GetOutputName(fileName))
                .VideoFilter(cropFilter).ProgressHandler((progress, _, _, _) => IncrementProgress(progress))
                .StartTranscodingProcess();
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
