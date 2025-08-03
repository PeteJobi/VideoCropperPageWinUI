# Video Cropper Page WinUI 3
This is a WinUI 3 page that provides an interface for cropping videos.

<img width="1791" height="1023" alt="Screenshot 2025-07-28 103546" src="https://github.com/user-attachments/assets/d0651502-26fc-454a-9e74-c6aa7c8ad141" />

# How to use
This library depends on [DraggerResizerWinUI](https://github.com/PeteJobi/DraggerResizerWinUI) and [VideoSplitterBaseWinUI](https://github.com/PeteJobi/VideoSplitterBaseWinUI). Include all three libraries into your WinUI solution and reference them in your WinUI project. Then navigate to the VideoCropperPage when the user requests for it, passing a CropperProps object as parameter.
The CropperProps object should contain the path to ffmpeg, the path to the video file, and optionally, the full name of the Page type to navigate back to when the user is done. If this last parameter is provided, you can get a list of the files that was generated on the Video Cropper page. If not, the user will be navigated back to whichever page called the Cropper page and there'll be no parameters. 
```
private void GoToCropper(){
  var ffmpegPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/ffmpeg.exe");
  var videoPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/video.mp4");
  Frame.Navigate(typeof(VideoCropper.VideoCropperPage), new CropperProps { FfmpegPath = ffmpegPath, VideoPath = videoPath, TypeToNavigateTo = typeof(ThisPage).FullName});
}

protected override void OnNavigatedTo(NavigationEventArgs e)
{
    //croppedFiles is sent only if TypeToNavigateTo was specified in CropperProps.
    if (e.Parameter is List<string> croppedFiles)
    {
        Console.WriteLine($"{croppedFiles.Count} files were generated");
    }
}
```

You may check out [VideoSplitter](https://github.com/PeteJobi/VideoSplitter) to see how a full application that uses this page.
