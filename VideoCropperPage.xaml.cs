using DraggerResizer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using static System.Collections.Specialized.BitVector32;
using Orientation = DraggerResizer.Orientation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VideoCropper
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class VideoCropperPage : Page
    {
        private DraggerResizer.DraggerResizer resizer;
        private CropperModel viewModel;
        private RectangleGeometry mask;
        private bool progressChangedByCode;
        private ObservableCollection<AspectRatio> ratios;
        private const double IconMaxSize = 40;
        private string ffmpegPath, videoPath;
        private double videoWidth, videoHeight;
        private CancellationTokenSource resizeTokenSource;
        private bool startedResizing;
        private (string XText, string YText, string X2Text, string Y2Text) previousRect;

        public CropperPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new CropperModel();
            ratios = new ObservableCollection<AspectRatio>();
            PopulateAspectRatios();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var props = (CropperProps)e.Parameter;
            ffmpegPath = props.FfmpegPath;
            videoPath = props.VideoPath;
            //videoPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/Video.mp4");
            //ffmpegPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/ffmpeg.exe");
            VideoName.Text = Path.GetFileName(videoPath);
            VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(videoPath));
            VideoPlayer.MediaPlayer.PlaybackSession.NaturalVideoSizeChanged += PlaybackSessionOnNaturalVideoSizeChanged;
            VideoPlayer.MediaPlayer.PlaybackSession.NaturalDurationChanged += PlaybackSessionOnNaturalDurationChanged;
            VideoPlayer.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            base.OnNavigatedTo(e);
        }

        private void CreateCropper(bool lockedAspectRatio)
        {
            resizer.DeInitDraggerResizer(CropFrame);
            var orientations = Enum.GetValues<Orientation>().Append(Orientation.Horizontal | Orientation.Vertical)
                .ToDictionary(o => o, o => new Appearance { HandleThickness = 30 });
            CropFrame.UpdateLayout();
            resizer.InitDraggerResizer(CropFrame, orientations, parameters: new HandlingParameters { KeepAspectRatio = lockedAspectRatio },
                dragged: CoordinatesChanged, resized: CoordinatesChanged, ended: UpdateUiWithCoordinates);
            CropFrame.UpdateLayout();
            UpdateUiWithCoordinates();
        }

        private void UpdateUiWithCoordinates()
        {
            var fakeLeft = resizer.GetElementLeft(CropFrame);
            var fakeTop = resizer.GetElementTop(CropFrame);
            var fakeX2 = CropFrame.Width;
            var fakeY2 = CropFrame.Height;
            var fakeWidth = OverlayAndMask.Width;
            var fakeHeight = OverlayAndMask.Height;
            X.Text = (fakeLeft / fakeWidth * videoWidth).ToString("F0");
            Y.Text = (fakeTop / fakeHeight * videoHeight).ToString("F0");
            X2.Text = (fakeX2 / fakeWidth * videoWidth).ToString("F0");
            Y2.Text = (fakeY2 / fakeHeight * videoHeight).ToString("F0");
            previousRect.XText = X.Text;
            previousRect.YText = Y.Text;
            previousRect.X2Text = X2.Text;
            previousRect.Y2Text = Y2.Text;
        }

        private void PopulateAspectRatios()
        {
            ratios.Add(new AspectRatio { Title = "Square", Width = IconMaxSize, Height = IconMaxSize });
            ratios.Add(GetAspectRatio(16, 9));
            ratios.Add(GetAspectRatio(9, 16));
            ratios.Add(GetAspectRatio(5, 4));
            ratios.Add(GetAspectRatio(4, 5));
            ratios.Add(GetAspectRatio(4, 3));
            ratios.Add(GetAspectRatio(3, 4));
            ratios.Add(GetAspectRatio(3, 2));
            ratios.Add(GetAspectRatio(2, 3));
            ratios.Add(GetAspectRatio(2, 1));
            ratios.Add(GetAspectRatio(1, 2));
        }

        AspectRatio GetAspectRatio(double aspectWidth, double aspectHeight)
        {
            var withinSize = 40;
            double width, height;
            if (aspectWidth > aspectHeight)
            {
                width = withinSize;
                height = withinSize * aspectHeight / aspectWidth;
            }
            else
            {
                height = withinSize;
                width = withinSize * aspectWidth / aspectHeight;
            }

            return new AspectRatio { Title = $"{aspectWidth}:{aspectHeight}", Width = width, Height = height };
        }

        private void PlaybackSessionOnNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                videoWidth = sender.NaturalVideoWidth;
                videoHeight = sender.NaturalVideoHeight;
                CreateCropperSizeChange();

                var originalAspectRatio = GetAspectRatio(videoWidth, videoHeight);
                originalAspectRatio.Title = "Original";
                ratios.Insert(0, originalAspectRatio);
            });
        }

        private void CreateCropperSizeChange()
        {
            var videoSize = VideoPlayer.ActualSize;
            var aspectRatio = videoWidth / videoHeight;
            double width, height;
            if (aspectRatio > videoSize.X / videoSize.Y)
            {
                width = videoSize.X;
                height = videoSize.X / aspectRatio;
            }
            else
            {
                width = videoSize.Y * aspectRatio;
                height = videoSize.Y;
            }
            Canvas.Width = CropFrame.Width = OverlayAndMask.Width = width;
            Canvas.Height = CropFrame.Height = OverlayAndMask.Height = height;
            var geometryGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask = new RectangleGeometry
            {
                Rect = new Rect(0, 0, width, height)
            };
            geometryGroup.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(0, 0, width, height)
            });
            geometryGroup.Children.Add(mask);
            OverlayAndMask.Data = geometryGroup;
            ToggleButton_OnChecked(AspectRatioToggle, null);
        }

        private void PlaybackSessionOnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                VideoProgressSlider.Maximum = sender.NaturalDuration.TotalSeconds;
                VideoProgressSlider.Value = 0;
                SetVideoTime();
            });
        }

        private async void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Playing) await AnimateSeeker(sender);
            else DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => viewModel.IsPlaying = false);
        }

        private void CoordinatesChanged()
        {
            mask.Rect = new Rect(resizer.GetElementLeft(CropFrame), resizer.GetElementTop(CropFrame), CropFrame.Width, CropFrame.Height);
        }

        private void PlayPause(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                VideoPlayer.MediaPlayer.Pause();
                viewModel.IsPlaying = false;
            }
            else
            {
                VideoPlayer.MediaPlayer.Play();
                viewModel.IsPlaying = true;
            }
        }

        private async Task AnimateSeeker(MediaPlaybackSession session)
        {
            const int frameTime24Fps = 1000 / 24;
            while (session.PlaybackState == MediaPlaybackState.Playing)
            {
                if (DispatcherQueue == null) return;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    progressChangedByCode = true;
                    VideoProgressSlider.Value = session.Position.TotalSeconds;
                    progressChangedByCode = false;
                    SetVideoTime();
                });
                await Task.Delay(frameTime24Fps);
            }
        }

        private void SetVideoTime() => VideoTime.Text = $"{VideoPlayer.MediaPlayer.PlaybackSession.Position:hh\\:mm\\:ss} / {VideoPlayer.MediaPlayer.PlaybackSession.NaturalDuration:hh\\:mm\\:ss}";

        private void VideoProgressSlider_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (progressChangedByCode) return;
            VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            SetVideoTime();
        }

        private void ToggleButton_OnChecked(object sender, RoutedEventArgs e)
        {
            var toggle = (ToggleButton)sender;
            CreateCropper(toggle.IsChecked == true);
            AspectToggleIcon.Glyph = toggle.IsChecked == true ? "\uF407" : "\uE799";
            AspectToggleText.Text = toggle.IsChecked == true ? "Locked Aspect Ratio" : "Unlocked Aspect Ratio";
        }

        private void SpecificRatio(object sender, RoutedEventArgs e)
        {
            var ratio = (AspectRatio)((Button)sender).DataContext;
            resizer.SetAspectRatio(CropFrame, ratio.Width / ratio.Height);
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        private void CenterFrame(object sender, RoutedEventArgs e)
        {
            resizer.PositionElementAtCenter(CropFrame);
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        private async void VideoPlayer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if(videoWidth == 0 || videoHeight == 0)
            {
                resizeTokenSource = new CancellationTokenSource();
                return;
            }

            if (!startedResizing)
            {
                CropFrame.Visibility = OverlayAndMask.Visibility = Visibility.Collapsed;
                resizer.PositionElement(CropFrame, 0, 0);
                startedResizing = true;
            }
            await resizeTokenSource.CancelAsync();
            resizeTokenSource = new CancellationTokenSource();
            try
            {
                await Task.Delay(500, resizeTokenSource.Token);
                CropFrame.Visibility = OverlayAndMask.Visibility = Visibility.Visible;
                startedResizing = false;
                CreateCropperSizeChange();
            }
            catch (AggregateException) { }
            catch (TaskCanceledException) { }
        }

        private void X_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.XText == X.Text) return;
            resizer.PositionElementLeft(CropFrame, double.Parse(X.Text) / videoWidth * OverlayAndMask.Width);
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        private void X2_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.X2Text == X2.Text) return;
            resizer.ResizeElementWidth(CropFrame, double.Parse(X2.Text) / videoWidth * OverlayAndMask.Width,
                parameters: new HandlingParameters{ KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        private void Y_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.YText == Y.Text) return;
            resizer.PositionElementTop(CropFrame, double.Parse(Y.Text) / videoHeight * OverlayAndMask.Height);
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        private void Y2_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.Y2Text == Y2.Text) return;
            resizer.ResizeElementHeight(CropFrame, double.Parse(Y2.Text) / videoHeight * OverlayAndMask.Height,
                parameters: new HandlingParameters { KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
            UpdateUiWithCoordinates();
        }

        string GetOutputName(string path)
        {
            string inputName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            string parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            return Path.Combine(parentFolder, $"{inputName}_CROPPED{extension}");
        }

        private void InfoBarClosed(InfoBar sender, object args)
        {
            viewModel.State = CropperModel.OperationState.BeforeOperation;
        }

        private async void Crop(object sender, RoutedEventArgs e)
        {
            var outputFile = GetOutputName(videoPath);
            File.Delete(outputFile);
            CropProgressText.Text = "0.0";
            CropProgressValue.Value = 0;
            viewModel.State = CropperModel.OperationState.DuringOperation;
            try
            {
                await StartProcess(ffmpegPath,
                    $"-i \"{videoPath}\" -vf \"crop={X2.Text}:{Y2.Text}:{X.Text}:{Y.Text}\" \"{outputFile}\"", null,
                    (o, args) =>
                    {
                        if (string.IsNullOrWhiteSpace(args.Data) /*|| hasBeenKilled*/) return;
                        Debug.WriteLine(args.Data);
                        if (args.Data.StartsWith("frame"))
                        {
                            //if (CheckNoSpaceDuringOperation(args.Data)) return;
                            MatchCollection matchCollection = Regex.Matches(args.Data,
                                @"^frame=\s*\d+\s.+?time=(\d{2}:\d{2}:\d{2}\.\d{2}).+");
                            if (matchCollection.Count == 0) return;
                            IncrementProgress(TimeSpan.Parse(matchCollection[0].Groups[1].Value));
                        }
                    });
                Info.Severity = InfoBarSeverity.Success;
                Info.Message = "Crop completed successfully!";
            }
            catch (Exception ex)
            {
                Info.Severity = InfoBarSeverity.Error;
                Info.Message = $"Crop failed: {ex.Message}";
            }
            finally
            {
                viewModel.State = CropperModel.OperationState.AfterOperation;
            }
        }

        private void IncrementProgress(TimeSpan currentTime)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                var fraction = currentTime / VideoPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                CropProgressValue.Value = fraction * CropProgressValue.Maximum;
                CropProgressText.Text = Math.Round(fraction * 100, 1).ToString("F1");
            });
        }

        private static async Task StartProcess(string processFileName, string arguments, DataReceivedEventHandler? outputEventHandler, DataReceivedEventHandler? errorEventHandler)
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
            await ffmpeg.WaitForExitAsync();
            ffmpeg.Dispose();
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            VideoPlayer.MediaPlayer.Pause();
            Frame.GoBack();
        }
    }

    class AspectRatio
    {
        public string Title { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class CropperProps{
        public string FfmpegPath { get; set; }
        public string VideoPath { get; set; }
    }
}
