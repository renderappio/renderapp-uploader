using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Renderapp.Uploader.Publish;
using Renderapp.Uploader.ViewModels;
using Xunit;

namespace Renderapp.Uploader.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly Fixtures fixtures = new();
    private readonly FakePublisher publisher = new();

    public void Dispose() => fixtures.Dispose();

    private async Task<MainViewModel> OpenedAsync(string folder)
    {
        var model = new MainViewModel(publisher);
        await model.OpenAsync(folder);
        return model;
    }

    [AvaloniaFact]
    public async Task A_folder_is_summarised_and_named_after_itself()
    {
        var folder = fixtures.Folder("car_red.png", "car_blue.png", "bike_red.png");

        var model = await OpenedAsync(folder);

        Assert.Equal(Screen.Ready, model.Screen);
        Assert.Equal(Path.GetFileName(folder), model.ProjectName);
        Assert.StartsWith("3 images · ", model.SummaryText);
        Assert.Equal("4 options in 2 categories", model.DetailText);
        Assert.Equal(FolderLayout.FileNames, model.Layout);
        Assert.Equal(ImportKind.Stills, model.Kind);
        Assert.Equal(3, model.Previews.Count);
        Assert.Null(model.Warning);
        Assert.True(model.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Numbered_frames_are_taken_for_a_spin_and_folders_for_options()
    {
        var spin = await OpenedAsync(fixtures.Folder("car_red_001.png", "car_red_002.png", "car_red_003.png"));
        Assert.Equal(ImportKind.Spin, spin.Kind);
        Assert.Contains("3 frames", spin.DetailText);

        var tree = await OpenedAsync(fixtures.Folder(Path.Combine("paint", "red", "front.png"), Path.Combine("paint", "blue", "front.png")));
        Assert.Equal(FolderLayout.Folders, tree.Layout);
        Assert.Equal(ImportKind.Stills, tree.Kind);
    }

    [AvaloniaFact]
    public async Task A_folder_with_no_images_says_so_and_cannot_be_published()
    {
        var folder = fixtures.Folder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

        var model = await OpenedAsync(folder);

        Assert.NotNull(model.Warning);
        Assert.False(model.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Publishing_walks_the_steps_reports_progress_and_ends_published()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png", "car_blue.png"));
        model.ProjectName = "  Hero car  ";
        model.VersionNote = "New paint";

        var publishing = model.PublishCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Screen.Publishing, model.Screen);
        Assert.Equal([true, true, false], model.Steps.Select(step => step.IsDone));
        Assert.True(model.Steps[2].IsCurrent);
        Assert.Equal(0.25, model.Progress);
        Assert.Equal("1 of 4 files · 25 B of 100 B", model.ProgressText);
        Assert.Equal(("Hero car", "New paint"), publisher.Published.Single());

        publisher.Finish(new Destination("p1", 3, "w1"));
        await publishing;

        Assert.Equal(Screen.Published, model.Screen);
        Assert.Equal("Hero car, version 3", model.PublishedText);
        Assert.Equal(new Uri("https://renderapp.example/viewer/p1/3"), model.PublishedUri);
        Assert.All(model.Steps, step => Assert.True(step.IsDone));
    }

    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    [AvaloniaFact]
    public async Task Speed_and_time_left_come_from_the_last_few_seconds_of_progress()
    {
        var clock = new Clock();
        var model = new MainViewModel(publisher, clock);
        await model.OpenAsync(fixtures.Folder("car_red.png"));
        var publishing = model.PublishCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", model.SpeedText);

        clock.Now += 4_000;
        publisher.Progress!.Report(new UploadProgress(40_000_025, 640_000_025, 2, 4));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("10 MB/s · about 1 min left", model.SpeedText);
        publisher.Finish(new Destination("p", 1, "w"));
        await publishing;
    }

    [AvaloniaFact]
    public async Task Sign_out_shows_only_once_signed_in()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png"));
        Assert.False(model.IsSignedIn);

        var publishing = model.PublishCommand.ExecuteAsync(null);
        publisher.Finish(new Destination("p", 1, "w"));
        await publishing;
        Assert.True(model.IsSignedIn);

        model.SignOutCommand.Execute(null);
        Assert.False(model.IsSignedIn);
        Assert.True(publisher.SignedOut);
    }

    [AvaloniaFact]
    public async Task A_failed_publish_goes_back_with_the_reason_and_the_resume_note()
    {
        var folder = fixtures.Folder("car_red.png");
        var model = await OpenedAsync(folder);

        var publishing = model.PublishCommand.ExecuteAsync(null);
        publisher.Unfinished.Add(model.SourcePath);
        publisher.Fail(new PublishException("Storage refused an upload (403)."));
        await publishing;

        Assert.Equal(Screen.Ready, model.Screen);
        Assert.Equal("Storage refused an upload (403).", model.Error);
        Assert.True(model.Resumes);
        Assert.True(model.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Cancelling_stops_and_says_publishing_will_carry_on()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png"));

        var publishing = model.PublishCommand.ExecuteAsync(null);
        model.PublishCancelCommand.Execute(null);
        await publishing;

        Assert.Equal(Screen.Ready, model.Screen);
        Assert.Contains("carry on", model.Error);
        Assert.Equal("", model.StatusText);
    }

    [AvaloniaFact]
    public async Task An_encrypted_rio_is_opened_through_Renderapp()
    {
        var folder = fixtures.Folder("car_red.png");
        var rio = Path.Combine(folder, "car.rio");
        File.WriteAllText(rio, "ENCRYPTED:" + $$"""{"CacheDir":"{{folder.Replace('\\', '/')}}","Inputs":[],"Maps":[{"filename":"car_red.png","configuration":"","depth":0,"frame":0}]}""");

        var model = await OpenedAsync(rio);

        Assert.Equal(Screen.Ready, model.Screen);
        Assert.Equal("car", model.ProjectName);
        Assert.False(model.CanChooseLayout);
        Assert.StartsWith("1 image · ", model.SummaryText);
    }

    [AvaloniaFact]
    public async Task Starting_over_returns_to_the_drop_zone()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png"));

        model.StartOverCommand.Execute(null);

        Assert.Equal(Screen.Start, model.Screen);
        Assert.Empty(model.Previews);
    }

    // Changing the layout or kind rebuilds in the background, so a test waits for it to settle.
    private static async Task SettledAsync(MainViewModel model)
    {
        for (var tries = 0; model.IsBusy && tries < 500; tries++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(model.IsBusy);
    }

    [AvaloniaFact]
    public async Task Changing_the_kind_twice_lands_on_the_last_choice()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red_001.png", "car_red_002.png", "car_red_003.png"));
        Assert.Contains("frames", model.DetailText);

        model.Kind = ImportKind.StillsSequence;
        model.Kind = ImportKind.Stills;
        await SettledAsync(model);

        Assert.Equal(ImportKind.Stills, model.Kind);
        Assert.DoesNotContain("frames", model.DetailText);
    }

    [AvaloniaFact]
    public async Task Cancelling_a_rebuild_keeps_the_choice_the_summary_was_built_from()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red_001.png", "car_red_002.png", "car_red_003.png"));
        var detail = model.DetailText;

        model.Kind = ImportKind.Stills;
        model.CancelOpenCommand.Execute(null);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.False(model.IsBusy);
        Assert.Equal(ImportKind.Spin, model.Kind);
        Assert.Equal(detail, model.DetailText);
        Assert.True(model.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Cancelling_an_open_from_the_summary_keeps_the_project_shown()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png"));
        var (name, source) = (model.ProjectName, model.SourcePath);

        var opening = model.OpenAsync(fixtures.Folder("bike_red.png", "bike_blue.png"));
        model.CancelOpenCommand.Execute(null);
        await opening;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(name, model.ProjectName);
        Assert.Equal(source, model.SourcePath);
    }

    [AvaloniaFact]
    public async Task Publish_waits_while_the_folder_is_being_read()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red_001.png", "car_red_002.png"));

        model.Kind = ImportKind.Stills;

        Assert.True(model.IsBusy);
        Assert.False(model.PublishCommand.CanExecute(null));
        await SettledAsync(model);
        Assert.True(model.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Opening_something_else_mid_publish_is_ignored()
    {
        var model = await OpenedAsync(fixtures.Folder("car_red.png"));
        var name = model.ProjectName;

        var publishing = model.PublishCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        await model.OpenAsync(fixtures.Folder("bike_red.png", "bike_blue.png"));

        Assert.Equal(Screen.Publishing, model.Screen);
        Assert.Equal(name, model.ProjectName);
        Assert.False(model.CanSignOut);
        model.PublishCancelCommand.Execute(null);
        await publishing;
    }

    [AvaloniaFact]
    public async Task A_file_that_is_not_a_rio_is_turned_away()
    {
        var folder = fixtures.Folder("car_red.png");

        var model = await OpenedAsync(Path.Combine(folder, "car_red.png"));

        Assert.Equal(Screen.Start, model.Screen);
        Assert.Contains(".rio", model.Error);
    }

    [AvaloniaFact]
    public async Task Opening_an_encrypted_rio_can_be_cancelled_while_it_waits_for_sign_in()
    {
        var folder = fixtures.Folder("car_red.png");
        var rio = Path.Combine(folder, "car.rio");
        File.WriteAllText(rio, "ENCRYPTED:{}");
        publisher.HoldDecrypt = true;
        var model = new MainViewModel(publisher);

        var opening = model.OpenAsync(rio);
        for (var tries = 0; !model.BusyText.Contains("Sign in") && tries < 500; tries++) await Task.Delay(10);
        Assert.True(model.IsBusy);
        model.CancelOpenCommand.Execute(null);
        await opening;

        Assert.False(model.IsBusy);
        Assert.Equal(Screen.Start, model.Screen);
        Assert.Null(model.Error);
    }

    [AvaloniaFact]
    public void Steps_spell_out_their_state()
    {
        var step = new StepViewModel("Upload");
        Assert.Equal("Upload, not started", step.Description);
        step.IsCurrent = true;
        Assert.Equal("Upload, in progress", step.Description);
        step.IsDone = true;
        Assert.Equal("Upload, done", step.Description);
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(64_009_263, "64 MB")]
    [InlineData(2_340_000_000, "2.3 GB")]
    internal void Sizes_read_naturally(long bytes, string expected) => Assert.Equal(expected, MainViewModel.Bytes(bytes));
}
