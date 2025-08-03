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
using VideoCropperPage;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using static VideoCropper.CropperModel;
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
        private CropProcessor cropProcessor;
        private RectangleGeometry mask;
        private bool progressChangedByCode;
        private ObservableCollection<AspectRatio> ratios;
        private const double IconMaxSize = 40;
        private readonly double progressMax = 1_000_000;
        private string outputFile;
        private readonly List<string> outputFiles = [];
        private string? navigateTo;
        private string ffmpegPath, videoPath;
        private double videoWidth, videoHeight;
        private CancellationTokenSource resizeTokenSource;
        private bool startedResizing;
        private (string XText, string YText, string X2Text, string Y2Text) previousRect;
        private HandlingCallbacks callbacks;

        public VideoCropperPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new CropperModel();
            cropProcessor = new CropProcessor();
            ratios = new ObservableCollection<AspectRatio>();
            callbacks = new HandlingCallbacks
            {
                Dragging = CoordinatesChanged,
                Resizing = _ => { CoordinatesChanged(); }
            };
            PopulateAspectRatios();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var props = (CropperProps)e.Parameter;
            ffmpegPath = props.FfmpegPath;
            videoPath = props.VideoPath;
            navigateTo = props.TypeToNavigateTo;
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
            resizer.InitDraggerResizer(CropFrame, orientations, parameters: new HandlingParameters { KeepAspectRatio = lockedAspectRatio }, callbacks);
            CropFrame.UpdateLayout();
            UpdateUiWithCoordinates(0, 0);
        }

        private void UpdateUiWithCoordinates(double fakeLeft, double fakeTop)
        {
            var fakeX2 = CropFrame.Width;
            var fakeY2 = CropFrame.Height;
            var fakeWidth = OverlayAndMask.Width;
            var fakeHeight = OverlayAndMask.Height;
            X.Text = (fakeLeft / fakeWidth * videoWidth).ToString("F0");
            Y.Text = (fakeTop / fakeHeight * videoHeight).ToString("F0");
            XDelta.Text = (fakeX2 / fakeWidth * videoWidth).ToString("F0");
            YDelta.Text = (fakeY2 / fakeHeight * videoHeight).ToString("F0");
            previousRect.XText = X.Text;
            previousRect.YText = Y.Text;
            previousRect.X2Text = XDelta.Text;
            previousRect.Y2Text = YDelta.Text;
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
            double width, height;
            if (aspectWidth > aspectHeight)
            {
                width = IconMaxSize;
                height = IconMaxSize * aspectHeight / aspectWidth;
            }
            else
            {
                height = IconMaxSize;
                width = IconMaxSize * aspectWidth / aspectHeight;
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
                width = Math.Ceiling(videoSize.X);
                height = Math.Ceiling(videoSize.X / aspectRatio);
            }
            else
            {
                width = Math.Ceiling(videoSize.Y * aspectRatio);
                height = Math.Ceiling(videoSize.Y);
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
            var left = resizer.GetElementLeft(CropFrame);
            var top = resizer.GetElementTop(CropFrame);
            mask.Rect = new Rect(left, top, CropFrame.Width, CropFrame.Height);
            UpdateUiWithCoordinates(left, top);
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
        }

        private void CenterFrame(object sender, RoutedEventArgs e)
        {
            resizer.PositionElementAtCenter(CropFrame);
            CoordinatesChanged();
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
        }

        private void XDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.X2Text == XDelta.Text) return;
            resizer.ResizeElementWidth(CropFrame, double.Parse(XDelta.Text) / videoWidth * OverlayAndMask.Width,
                parameters: new HandlingParameters{ KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
        }

        private void Y_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.YText == Y.Text) return;
            resizer.PositionElementTop(CropFrame, double.Parse(Y.Text) / videoHeight * OverlayAndMask.Height);
            CoordinatesChanged();
        }

        private void YDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.Y2Text == YDelta.Text) return;
            resizer.ResizeElementHeight(CropFrame, double.Parse(YDelta.Text) / videoHeight * OverlayAndMask.Height,
                parameters: new HandlingParameters { KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
        }

        private void InfoBarClosed(InfoBar sender, object args)
        {
            viewModel.State = CropperModel.OperationState.BeforeOperation;
        }

        private async void Crop(object sender, RoutedEventArgs e)
        {
            CropProgressText.Text = "0.0";
            CropProgressValue.Value = 0;
            viewModel.State = OperationState.DuringOperation;
            var valueProgress = new Progress<ValueProgress>(progress =>
            {
                CropProgressValue.Value = progress.ActionProgress;
                CropProgressText.Text = progress.ActionProgressText;
            });
            var failed = false;
            string? errorMessage = null;
            try
            {
                await cropProcessor.Crop(videoPath, ffmpegPath, X.Text, Y.Text, XDelta.Text, YDelta.Text, progressMax,
                    valueProgress, SetOutputFile, ErrorActionFromFfmpeg);

                if (viewModel.State == OperationState.BeforeOperation) return; //Canceled
                if (failed)
                {
                    viewModel.State = OperationState.BeforeOperation;
                    await ErrorAction(errorMessage!);
                    await cropProcessor.Cancel(outputFile);
                    return;
                }

                viewModel.State = OperationState.AfterOperation;
                outputFiles.Add(outputFile);
            }
            catch (Exception ex)
            {
                await ErrorAction(ex.Message);
                viewModel.State = OperationState.BeforeOperation;
            }

            void ErrorActionFromFfmpeg(string message)
            {
                failed = true;
                errorMessage = message;
            }

            void SetOutputFile(string folder)
            {
                outputFile = folder;
            }

            async Task ErrorAction(string message)
            {
                ErrorDialog.Title = "Crop operation failed";
                ErrorDialog.Content = message;
                await ErrorDialog.ShowAsync();
            }
        }

        private void PauseOrViewSplit_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                cropProcessor.ViewFiles(outputFile);
                return;
            }

            if (viewModel.ProcessPaused)
            {
                cropProcessor.Resume();
                viewModel.ProcessPaused = false;
            }
            else
            {
                cropProcessor.Pause();
                viewModel.ProcessPaused = true;
            }
        }

        private void CancelOrCloseSplit_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                viewModel.State = OperationState.BeforeOperation;
                return;
            }

            FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
        }

        private async void CancelProcess(object sender, RoutedEventArgs e)
        {
            await cropProcessor.Cancel(outputFile);
            viewModel.State = OperationState.BeforeOperation;
            viewModel.ProcessPaused = false;
            CancelFlyout.Hide();
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            VideoPlayer.MediaPlayer.Pause();
            _ = cropProcessor.Cancel(outputFile);
            if (navigateTo == null) Frame.GoBack();
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFiles, new FrameNavigationOptions { IsNavigationStackEnabled = false });
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
        public string? TypeToNavigateTo { get; set; }
    }
}
