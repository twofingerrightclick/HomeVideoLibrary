using System.Reflection;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class LibrarySelectionTests
{
    [Fact]
    public async Task IndexRefreshKeepsCheckedIdsAndFocusWhileFilteredResultsDropOnlyAbsentIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvl-selection-" + Guid.NewGuid());
        try
        {
            var catalog = new LibraryCatalog(new LibraryPaths(root));
            await catalog.InitializeAsync();
            var videos = new[] { "first", "second" }.Select(name => new LibraryVideo(Guid.NewGuid(), $"Videos/{name}.mkv", name,
                TimeSpan.FromSeconds(10), DateTimeOffset.Now, DateTimeOffset.Now)).ToArray();
            foreach (var video in videos)
            {
                await File.WriteAllTextAsync(catalog.GetVideoPath(video.RelativePath), "synthetic metadata test");
                await catalog.AddAsync(video);
            }
            var index = new LibrarySearchIndex(catalog, new Metadata());
            var vm = new MainWindowViewModel { TargetAlbum = new VideoAlbum(Guid.NewGuid(), "Album") };
            typeof(MainWindowViewModel).GetField("_searchIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, index);
            await vm.RefreshVideosAsync();
            foreach (var tape in vm.Tapes) tape.IsSelected = true;
            vm.SelectedVideo = vm.Videos.Single(video => video.Id == videos[1].Id);
            var changed = new HashSet<string?>();
            vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            await vm.RefreshIndexAsync(); // Includes the automatic results refresh after indexing.
            Assert.Equal(videos.Select(video => video.Id).Order(), vm.SelectedVideos.Select(video => video.Id).Order());
            Assert.All(vm.Tapes, tape => Assert.True(tape.IsSelected));
            Assert.Equal(videos[1].Id, vm.SelectedVideo?.Id);
            Assert.Equal("2 selected", vm.SelectionLabel);
            Assert.True(vm.CanAddSelected); Assert.True(vm.CanEditClips);
            Assert.Contains(nameof(vm.SelectionLabel), changed);
            Assert.Contains(nameof(vm.CanAddSelected), changed);
            Assert.Contains(nameof(vm.CanEditClips), changed);

            vm.SearchText = "first";
            await vm.RefreshVideosAsync();
            Assert.Equal(videos[0].Id, Assert.Single(vm.SelectedVideos).Id);
            Assert.True(Assert.Single(vm.Tapes).IsSelected);
            Assert.Equal(videos[0].Id, vm.SelectedVideo?.Id);
            Assert.Equal("1 selected", vm.SelectionLabel);
            vm.SearchText = "missing";
            await vm.RefreshVideosAsync();
            Assert.Empty(vm.SelectedVideos); Assert.Null(vm.SelectedVideo);
            Assert.False(vm.CanAddSelected); Assert.False(vm.CanEditClips);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class Metadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoMetadata.Empty with { Description = video.DisplayName });
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
