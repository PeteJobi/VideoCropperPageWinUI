using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoCropperPage
{
    public class CropProcessor
    {
        private Process? currentProcess;
        private bool hasBeenKilled;
        private const string FileNameLongError =
            "The source file name is too long. Shorten it to get the total number of characters in the destination directory lower than 256.\n\nDestination directory: ";

        public async Task Crop(string fileName, string ffmpegPath, string x, string y, string width, string height, double progressMax, IProgress<ValueProgress> progress, Action<string> setOutputFile, Action<string> error)
        {
            var duration = TimeSpan.MinValue;
            progress.Report(new ValueProgress(0, "0.0 %"));
            var outputFile = GetOutputName(fileName, setOutputFile);
            await StartProcess(ffmpegPath, $"-i \"{fileName}\" -vf \"crop={width}:{height}:{x}:{y}\" -c:v libx265 -c:a copy -crf 18 -preset slow \"{outputFile}\"", null, (o, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) return;
                Debug.WriteLine(args.Data);
                if (CheckFileNameLongError(args.Data, error)) return;
                if (duration == TimeSpan.MinValue)
                {
                    var matchCollection = Regex.Matches(args.Data, @"\s*Duration:\s(\d{2}:\d{2}:\d{2}\.\d{2}).+");
                    if (matchCollection.Count == 0) return;
                    duration = TimeSpan.Parse(matchCollection[0].Groups[1].Value);
                }
                if (args.Data.StartsWith("frame"))
                {
                    if (CheckNoSpaceDuringOperation(args.Data, error)) return;
                    var matchCollection = Regex.Matches(args.Data, @"^frame=\s*\d+\s.+?time=(\d{2}:\d{2}:\d{2}\.\d{2}).+");
                    if (matchCollection.Count == 0) return;
                    IncrementProgress(TimeSpan.Parse(matchCollection[0].Groups[1].Value), duration, progressMax, progress);
                }
            });
            if (HasBeenKilled()) return;
            AllDone(progressMax, progress);
        }

        private static string GetOutputName(string path, Action<string> setFile)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            var outputFile = Path.Combine(parentFolder, $"{inputName}_CROPPED{extension}");
            setFile(outputFile);
            File.Delete(outputFile);
            return outputFile;
        }

        private bool CheckNoSpaceDuringOperation(string line, Action<string> error)
        {
            if (!line.EndsWith("No space left on device") && !line.EndsWith("I/O error")) return false;
            SuspendProcess(currentProcess);
            error($"Process failed.\nError message: {line}");
            return true;
        }

        private static bool CheckFileNameLongError(string line, Action<string> error)
        {
            const string noSuchDirectory = ": No such file or directory";
            if (!line.EndsWith(noSuchDirectory)) return false;
            error(FileNameLongError + line[..^noSuchDirectory.Length]);
            return true;
        }

        private void IncrementProgress(TimeSpan currentTime, TimeSpan totalDuration, double max, IProgress<ValueProgress> progress)
        {
            var fraction = currentTime / totalDuration;
            progress.Report(new ValueProgress(fraction * max, $"{Math.Round(fraction * 100, 2)} %"));
        }

        void AllDone(double max, IProgress<ValueProgress> valueProgress)
        {
            currentProcess = null;
            valueProgress.Report(new ValueProgress
            {
                ActionProgress = max,
                ActionProgressText = "100 %"
            });
        }

        public void ViewFiles(string file)
        {
            var info = new ProcessStartInfo();
            info.FileName = "explorer";
            info.Arguments = $"/e, /select, \"{file}\"";
            Process.Start(info);
        }

        bool HasBeenKilled()
        {
            if (!hasBeenKilled) return false;
            hasBeenKilled = false;
            return true;
        }

        private static void SuspendProcess(Process process)
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                SuspendThread(pOpenThread);

                CloseHandle(pOpenThread);
            }
        }

        public async Task Cancel(string? outputFolder)
        {
            if (currentProcess == null) return;
            currentProcess.Kill();
            await currentProcess.WaitForExitAsync();
            hasBeenKilled = true;
            currentProcess = null;
            if (File.Exists(outputFolder)) File.Delete(outputFolder);
        }

        public void Pause()
        {
            if (currentProcess == null) return;
            SuspendProcess(currentProcess);
        }

        public void Resume()
        {
            if (currentProcess == null) return;
            if (currentProcess.ProcessName == string.Empty)
                return;

            foreach (ProcessThread pT in currentProcess.Threads)
            {
                var pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                int suspendCount;
                do
                {
                    suspendCount = ResumeThread(pOpenThread);
                } while (suspendCount > 0);

                CloseHandle(pOpenThread);
            }
        }

        private async Task StartProcess(string processFileName, string arguments, DataReceivedEventHandler? outputEventHandler, DataReceivedEventHandler? errorEventHandler)
        {
            Process ffmpeg = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = processFileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
                EnableRaisingEvents = true
            };
            ffmpeg.OutputDataReceived += outputEventHandler;
            ffmpeg.ErrorDataReceived += errorEventHandler;
            ffmpeg.Start();
            ffmpeg.BeginErrorReadLine();
            ffmpeg.BeginOutputReadLine();
            currentProcess = ffmpeg;
            await ffmpeg.WaitForExitAsync();
            ffmpeg.Dispose();
        }

        [Flags]
        public enum ThreadAccess : int
        {
            SUSPEND_RESUME = (0x0002)
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll")]
        static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")]
        static extern int ResumeThread(IntPtr hThread);
        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
    }

    public struct ValueProgress(double actionProgress, string actionProgressText)
    {
        public double ActionProgress { get; set; } = actionProgress;
        public string ActionProgressText { get; set; } = actionProgressText;
    }
}
