using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MMUCS.Core.Media;
using MMUCS.Desktop.ViewModels;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>
/// The media player's view model driven as its window drives it: selections, ticks, hooks and seeks.
/// Needs the game (MMUCS_GAME); run with ALSOFT_DRIVERS=null to keep it silent.
/// </summary>
[Collection(GameProjectCollection.Name)]
public class MediaPlayerViewModelTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    [Fact]
    public void Music_plays_follows_hooks_and_cutscenes_show_frames()
    {
        if (Game == null) return;
        UiThread.Run(async ui =>
        {
            var vm = new MediaPlayerViewModel(Game);
            try
            {
                await ui.Until(() => vm.Items.Count > 0, "the library to load");
                Assert.Equal(129, vm.Items.Count); // Music is the first category shown

                // Music: plays, raises events, and takes a hooked jump when its hook is set.
                vm.SelectedItem = vm.Items.Single(i => i.Id == "music/1200");
                await ui.Until(() => vm.IsAudio && vm.IsPlaying, "the track to start");
                await ui.Ticks(vm, 300);
                Assert.Contains(vm.Events, e => e.Contains("region"));
                Assert.Contains(vm.Events, e => e.Contains("jump skipped (needs hook 1)"));

                vm.StopCommand.Execute(null);
                Assert.False(vm.IsPlaying);
                vm.Hook = 1;
                vm.SetHookCommand.Execute(null);
                vm.PlayPauseCommand.Execute(null);
                await ui.Ticks(vm, 300);
                Assert.Contains(vm.Events, e => e.Contains("jump →") && e.Contains("(hook 1)"));
                Assert.Equal("Track hook: 0", vm.HookText); // used up by the jump

                // Pausing stops the sound where it is.
                vm.PlayPauseCommand.Execute(null);
                await ui.Ticks(vm, 150);
                long paused = vm.SoundFrame;
                await ui.Ticks(vm, 200);
                Assert.Equal(paused, vm.SoundFrame);

                // Cutscene: frames follow the clock, and seeking shows the frame at that time.
                vm.SelectedCategory = vm.Categories.Single(c => c.Kind == MediaKind.Cutscene);
                Assert.Equal(15, vm.Items.Count);
                vm.SelectedItem = vm.Items.Single(i => i.Id == "video/sb010");
                await ui.Until(() => vm.IsVideo && vm.Frame != null, "the first frame");
                Assert.Contains("sound 22,050 Hz stereo", vm.VideoInfo);
                await ui.Ticks(vm, 600);
                Assert.Matches(@"^frame ([5-9]|1\d) /", vm.FrameText);

                vm.PlayPauseCommand.Execute(null); // pause
                vm.Position = 3.0;
                await ui.Until(() => vm.FrameText.StartsWith("frame 37 /"), "frame 37 (3 s at 12 fps)");
            }
            finally { vm.Dispose(); }
        });
    }

    [Fact]
    public void Speech_filters_by_scene_speaker_and_words()
    {
        if (Game == null) return;
        var root = MMUCS.Core.Scumm.ScummProject.Load(MMUCS.Core.Scumm.ScummProject.FindRelatedFiles(Game));
        UiThread.Run(async ui =>
        {
            var vm = new MediaPlayerViewModel(Game, root);
            try
            {
                await ui.Until(() => vm.Items.Count > 0, "the library to load");
                vm.SelectedCategory = vm.Categories.Single(c => c.Kind == MediaKind.Voice);
                Assert.True(vm.IsSpeech);
                Assert.Equal(8076, vm.Items.Count);
                Assert.StartsWith("All scenes", vm.Scenes[0].Label);

                vm.SelectedScene = vm.Scenes.Single(f => f.Code == "BC");
                Assert.All(vm.Items, i => Assert.Equal("BC", i.Scene));
                Assert.Contains(vm.Speakers, f => f.Code == "CB");

                vm.SelectedSpeaker = vm.Speakers.Single(f => f.Code == "CB");
                Assert.All(vm.Items, i => Assert.Equal(("BC", "CB"), (i.Scene, i.Speaker)));

                vm.Filter = "pollo diablo";
                Assert.Contains(vm.Items, i => i.Id == "voice/bccb301");

                // Back to all scenes: the speaker choice stays when that speaker is still listed.
                vm.Filter = "";
                vm.SelectedScene = vm.Scenes[0];
                Assert.Equal("CB", vm.SelectedSpeaker.Code);
                Assert.All(vm.Items, i => Assert.Equal("CB", i.Speaker));
            }
            finally { vm.Dispose(); }
        });
    }

    /// <summary>A single-threaded synchronisation context, so the view model's UI continuations run as in the app.</summary>
    private sealed class UiThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object)> _queue = [];

        public override void Post(SendOrPostCallback d, object state) => _queue.Add((d, state));

        public static void Run(Func<UiThread, Task> test)
        {
            var ui = new UiThread();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(ui);
                var task = test(ui);
                task.ContinueWith(_ => ui._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (d, s) in ui._queue.GetConsumingEnumerable()) d(s);
                failure = task.Exception?.GetBaseException();
            });
            thread.Start();
            thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Called through the UI-thread handle the test is given.")]
        public async Task Ticks(MediaPlayerViewModel vm, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { vm.Tick(); await Task.Delay(16); }
            vm.Tick();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Called through the UI-thread handle the test is given.")]
        public async Task Until(Func<bool> condition, string what, int timeoutMs = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > end) throw new TimeoutException($"waited {timeoutMs} ms for {what}");
                await Task.Delay(16);
            }
        }
    }
}
