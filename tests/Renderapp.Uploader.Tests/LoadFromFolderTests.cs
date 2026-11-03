using Newtonsoft.Json.Linq;
using Xunit;

namespace Renderapp.Uploader.Tests;

// What a rio built from a folder holds. The order files come back in is the file system's, so
// these check structure rather than exact JSON.
public sealed class LoadFromFolderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("renderapp-uploader-tests-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Folder(params string[] files)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
        return folder;
    }

    private static JArray Inputs(JObject rio) => (JArray)rio["Inputs"]!;
    private static JArray Maps(JObject rio) => (JArray)rio["Maps"]!;
    private static IEnumerable<JObject> Options(JObject rio) => Inputs(rio).SelectMany(input => ((JArray)input["Options"]!).Cast<JObject>());

    private static string RuleOf(JObject rio, string name) =>
        Options(rio).Single(option => (string?)option["Name"] == name)["FrontEndRule"]!.Value<string>()!;

    private static JObject MapOf(JObject rio, string filename) =>
        Maps(rio).Cast<JObject>().Single(map => (string?)map["filename"] == filename);

    [Fact]
    public void Stills_from_file_names_get_an_option_per_name_part()
    {
        var folder = Folder("car_red.png", "car_blue.png", "bike_red.jpg", "notes.txt");

        var rio = LoadFromFolder.LoadFilenames(folder, 0);

        Assert.Equal(folder.Replace('\\', '/'), rio["CacheDir"]!.Value<string>());
        Assert.Equal(["bike_red.jpg", "car_blue.png", "car_red.png"], Maps(rio).Select(map => (string)map["filename"]!).Order());
        Assert.Equal(["Category 1", "Category 2"], Inputs(rio).Select(input => (string)input["Name"]!));
        Assert.Equal(["bike", "blue", "car", "red"], Options(rio).Select(option => (string)option["Name"]!).Order());
        Assert.All(Options(rio), option => Assert.Equal("NotAView", (string?)option["Type"]));
        Assert.Equal(RuleOf(rio, "car") + RuleOf(rio, "blue"), MapOf(rio, "car_blue.png")["configuration"]!.Value<string>());
        Assert.Equal(RuleOf(rio, "bike") + RuleOf(rio, "red"), MapOf(rio, "bike_red.jpg")["configuration"]!.Value<string>());
        Assert.All(Maps(rio), map => Assert.Equal(0, map["frame"]!.Value<int>()));

        // An option lists the rule chains that lead to it.
        Assert.Equal(new[] { RuleOf(rio, "bike"), RuleOf(rio, "car") }.Order(), ConditionsOf(rio, "red").Order());
        Assert.Equal([RuleOf(rio, "car")], ConditionsOf(rio, "blue"));
    }

    private static IEnumerable<string> ConditionsOf(JObject rio, string name) =>
        ((JArray)Options(rio).Single(option => (string?)option["Name"] == name)["Conditions"]!).Select(condition => (string)condition!);

    [Theory]
    [InlineData(1, "Spin", "Animation", "+ANM")]
    [InlineData(2, "Stills", "Stills", "+SLS")]
    public void A_sequence_turns_the_last_name_part_into_frames(int type, string viewName, string viewType, string viewRule)
    {
        var folder = Folder("car_red_001.png", "car_red_002.png", "car_blue_001.png", "car_blue_002.png");

        var rio = LoadFromFolder.LoadFilenames(folder, type);

        var view = (JObject)Inputs(rio)[0];
        Assert.Equal("+VEW", (string?)view["Group"]);
        var option = (JObject)((JArray)view["Options"]!).Single();
        Assert.Equal(viewName, (string?)option["Name"]);
        Assert.Equal(viewType, (string?)option["Type"]);
        Assert.Equal([1, 2], ((JArray)option["Frames"]!).Select(frame => (int)frame));

        var first = MapOf(rio, "car_red_001.png");
        var second = MapOf(rio, "car_red_002.png");
        Assert.NotEqual(first["frame"]!.Value<int>(), second["frame"]!.Value<int>());
        Assert.Equal(first["frame"]!.Value<int>(), MapOf(rio, "car_blue_001.png")["frame"]!.Value<int>());
        Assert.DoesNotContain(Options(rio), candidate => (string?)candidate["Name"] is "001" or "002");

        // Only the file that first brings in each frame number carries the view's rule; which
        // file that is depends on the listing order.
        string Configuration(string file) => MapOf(rio, file)["configuration"]!.Value<string>()!;
        Assert.Equal(RuleOf(rio, "car") + RuleOf(rio, "red"), Configuration("car_red_001.png").Replace(viewRule, ""));
        Assert.Equal(RuleOf(rio, "car") + RuleOf(rio, "blue"), Configuration("car_blue_002.png").Replace(viewRule, ""));
        Assert.Equal(2, Maps(rio).Count(map => map["configuration"]!.Value<string>()!.StartsWith(viewRule)));
    }

    [Fact]
    public void Folders_become_name_parts_and_files_keep_their_relative_paths()
    {
        var folder = Folder(Path.Combine("paint", "red", "front.png"), Path.Combine("paint", "blue", "front.png"), Path.Combine("paint", "red", "side.png"));

        var rio = LoadFromFolder.LoadDirectory(folder, 0);

        Assert.Equal(["paint/blue/front.png", "paint/red/front.png", "paint/red/side.png"], Maps(rio).Select(map => (string)map["filename"]!).Order());
        Assert.Equal(["Category 1", "Category 2", "Category 3"], Inputs(rio).Select(input => (string)input["Name"]!));
        Assert.Equal(RuleOf(rio, "paint") + RuleOf(rio, "red") + RuleOf(rio, "side"), MapOf(rio, "paint/red/side.png")["configuration"]!.Value<string>());
        Assert.Equal([RuleOf(rio, "paint") + RuleOf(rio, "red")], ConditionsOf(rio, "side"));
    }

    [Fact]
    public void A_name_part_shared_by_every_file_still_gets_its_option()
    {
        var folder = Folder("car.png");

        var rio = LoadFromFolder.LoadFilenames(folder, 0);

        Assert.Equal(RuleOf(rio, "car"), MapOf(rio, "car.png")["configuration"]!.Value<string>());
        Assert.Empty(((JArray)Options(rio).Single()["Conditions"]!));
    }
}
