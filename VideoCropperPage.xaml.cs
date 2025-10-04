using DraggerResizer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
        private readonly CropperModel viewModel;
        private readonly CropProcessor cropProcessor;
        private RectangleGeometry mask;
        private bool progressChangedByCode;
        private readonly ObservableCollection<AspectRatio> ratios = [];
        private const double IconMaxSize = 40;
        private readonly double progressMax = 1_000_000;
        private string? outputFile;
        private string? navigateTo;
        private string ffmpegPath, videoPath;
        private CancellationTokenSource resizeTokenSource;
        private bool cropFrameInitialized;
        private bool zoomIntoFrame;
        private (string XText, string YText, string X2Text, string Y2Text) previousRect;

        public VideoCropperPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new CropperModel();
            cropProcessor = new CropProcessor();
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
            VideoPlayer.MediaPlayer.PlaybackSession.NaturalDurationChanged += PlaybackSessionOnNaturalDurationChanged;
            VideoPlayer.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            base.OnNavigatedTo(e);
        }

        private void UpdateUiWithCoordinates(Rect newRect)
        {
            X.Text = newRect.X.ToString("F0");
            Y.Text = newRect.Y.ToString("F0");
            XDelta.Text = newRect.Width.ToString("F0");
            YDelta.Text = newRect.Height.ToString("F0");
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

        private static AspectRatio GetAspectRatio(double aspectWidth, double aspectHeight)
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

        private void CoordinatesChanged(Rect newRect)
        {
            mask.Rect = newRect;
            UpdateUiWithCoordinates(newRect);
        }

        private void CoordinatesChanged()
        {
            CoordinatesChanged(new Rect(resizer.GetElementLeft(CropFrame), resizer.GetElementTop(CropFrame), CropFrame.Width, CropFrame.Height));
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

        private void SetVideoTime() => VideoTime.Text = $@"{VideoPlayer.MediaPlayer.PlaybackSession.Position:hh\:mm\:ss} / {VideoPlayer.MediaPlayer.PlaybackSession.NaturalDuration:hh\:mm\:ss}";

        private void VideoProgressSlider_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (progressChangedByCode) return;
            VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            SetVideoTime();
        }

        private void ToggleButton_OnChecked(object sender, RoutedEventArgs e)
        {
            var toggle = (ToggleButton)sender;
            resizer.SetNewHandlingParameters(CropFrame, GetAspectRatioParam(toggle.IsChecked ?? false));
            AspectToggleIcon.Glyph = toggle.IsChecked == true ? "\uF407" : "\uE799";
            AspectToggleText.Text = toggle.IsChecked == true ? "Locked Aspect Ratio" : "Unlocked Aspect Ratio";
        }

        private void SpecificRatio(object sender, RoutedEventArgs e)
        {
            var ratio = (AspectRatio)((Button)sender).DataContext;
            resizer.SetAspectRatio(CropFrame, ratio.Width / ratio.Height);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void CenterFrame(object sender, RoutedEventArgs e)
        {
            resizer.PositionElementAtCenter(CropFrame);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void VideoPlayer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Canvas.Width = CropFrame.Width = OverlayAndMask.Width = e.NewSize.Width;
            Canvas.Height = CropFrame.Height = OverlayAndMask.Height = e.NewSize.Height;
            var geometryGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
            geometryGroup.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            });
            geometryGroup.Children.Add(mask);
            OverlayAndMask.Data = geometryGroup;
            resizer.DeInitDraggerResizer(CropFrame);
            var orientations = Enum.GetValues<Orientation>().Append(Orientation.Horizontal | Orientation.Vertical)
                .ToDictionary(o => o, o => new Appearance { HandleThickness = 30 });
            CropFrame.UpdateLayout();
            resizer.InitDraggerResizer(CropFrame, orientations, callbacks: new HandlingCallbacks
            {
                BeforeDragging = point => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                AfterDragging = CoordinatesChanged,
                DragCompleted = FocusCropFrame,
                BeforeResizing = (point, _) => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                AfterResizing = (newRect, _) => CoordinatesChanged(newRect),
                ResizeCompleted = _ => FocusCropFrame()
            });
            CropFrame.UpdateLayout();
            ToggleButton_OnChecked(AspectRatioToggle, null);
            UpdateUiWithCoordinates(new Rect(0, 0, CropFrame.Width, CropFrame.Height));
            cropFrameInitialized = true;

            var originalAspectRatio = GetAspectRatio(e.NewSize.Width, e.NewSize.Height);
            originalAspectRatio.Title = "Original";
            ratios.Insert(0, originalAspectRatio);

            FitToView();
        }

        private void FocusCropFrame()
        {
            if (!zoomIntoFrame) return;
            const int margin = 200;
            var left = resizer.GetElementLeft(CropFrame);
            var top = resizer.GetElementTop(CropFrame);
            double panX, panY, zoom;
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < CropFrame.Width / CropFrame.Height)
            {
                zoom = CanvasContainer.ActualWidth / (CropFrame.Width + margin * 2);
                panX = -(left - margin) * zoom;
                panY = -(top * zoom - (CanvasContainer.ActualHeight - CropFrame.Height * zoom) / 2);
            }
            else
            {
                zoom = CanvasContainer.ActualHeight / (CropFrame.Height + margin * 2);
                panY = -(top - margin) * zoom;
                panX = -(left * zoom - (CanvasContainer.ActualWidth - CropFrame.Width * zoom) / 2);
            }
            AnimateTransform(panX, panY, zoom);
        }

        private void FitToView()
        {
            double panX, panY, zoom;
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < Canvas.Width / Canvas.Height)
            {
                zoom = CanvasContainer.ActualWidth / Canvas.Width; // Fit to width
                var scaledHeight = CanvasContainer.ActualWidth * Canvas.Height / Canvas.Width;
                panY = (CanvasContainer.ActualHeight - scaledHeight) / 2; // Center vertically
                panX = 0;
            }
            else
            {
                zoom = CanvasContainer.ActualHeight / Canvas.Height; // Fit to height
                var scaledWidth = CanvasContainer.ActualHeight * Canvas.Width / Canvas.Height;
                panX = (CanvasContainer.ActualWidth - scaledWidth) / 2; // Center horizontally
                panY = 0;
            }
            AnimateTransform(panX, panY, zoom);
        }

        private static HandlingParameters GetAspectRatioParam(bool lockedAspectRatio) => new() { KeepAspectRatio = lockedAspectRatio };

        private void AnimateTransform(double panX, double panY, double zoom)
        {
            var storyboard = new Storyboard(); //Animates PanTransform.X/Y and ZoomTransform.ScaleX/Y
            const double animDuration = 500;

            var animPanX = new DoubleAnimation
            {
                To = panX,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animPanX, PanTransform);
            Storyboard.SetTargetProperty(animPanX, "X");
            storyboard.Children.Add(animPanX);

            var animPanY = new DoubleAnimation
            {
                To = panY,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animPanY, PanTransform);
            Storyboard.SetTargetProperty(animPanY, "Y");
            storyboard.Children.Add(animPanY);

            var animZoomX = new DoubleAnimation
            {
                To = zoom,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animZoomX, ZoomTransform);
            Storyboard.SetTargetProperty(animZoomX, "ScaleX");
            storyboard.Children.Add(animZoomX);

            var animZoomY = new DoubleAnimation
            {
                To = zoom,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animZoomY, ZoomTransform);
            Storyboard.SetTargetProperty(animZoomY, "ScaleY");
            storyboard.Children.Add(animZoomY);

            storyboard.Begin();
        }

        private void X_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.XText == X.Text) return;
            resizer.PositionElementLeft(CropFrame, double.Parse(X.Text) / Canvas.Width * OverlayAndMask.Width);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void XDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.X2Text == XDelta.Text) return;
            resizer.ResizeElementWidth(CropFrame, double.Parse(XDelta.Text) / Canvas.Width * OverlayAndMask.Width,
                parameters: new HandlingParameters{ KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void Y_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.YText == Y.Text) return;
            resizer.PositionElementTop(CropFrame, double.Parse(Y.Text) / Canvas.Height * OverlayAndMask.Height);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void YDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.Y2Text == YDelta.Text) return;
            resizer.ResizeElementHeight(CropFrame, double.Parse(YDelta.Text) / Canvas.Height * OverlayAndMask.Height,
                parameters: new HandlingParameters { KeepAspectRatio = AspectRatioToggle.IsChecked == true });
            CoordinatesChanged();
            FocusCropFrame();
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
            string? tempOutputFile = null;
            outputFile = null;
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
                outputFile = tempOutputFile;
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

            void SetOutputFile(string file)
            {
                tempOutputFile = file;
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
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFile, new FrameNavigationOptions { IsNavigationStackEnabled = false });
        }

        private void CanvasContainer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            CanvasContainer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, CanvasContainer.ActualWidth, CanvasContainer.ActualHeight)
            };
            if (!cropFrameInitialized) return;
            if(zoomIntoFrame) FocusCropFrame();
            else FitToView();
        }

        private void ZoomFrameToggleButton_OnChecked(object sender, RoutedEventArgs e)
        {
            var checkbox = (CheckBox)sender;
            if (checkbox.IsChecked == true)
            {
                zoomIntoFrame = true;
                FocusCropFrame();
            }
            else
            {
                zoomIntoFrame = false;
                FitToView();
            }
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
