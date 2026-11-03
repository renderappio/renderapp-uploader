using Newtonsoft.Json.Linq;

namespace Renderapp.Uploader;

internal class LoadFromFolder
{
    static List<string> uniqueRules = new List<string>();
    static List<JObject> categories = new List<JObject>();
    static Dictionary<string, string> nameRulePairs = new Dictionary<string, string>();
    static Dictionary<string, JObject> ruleOptionPairs = new Dictionary<string, JObject>();
    static JObject rioJson = new();
    static Random random = new Random(0);
    static List<string> frames = new List<string>();
    static JObject animationOption = new();

    static JArray ArrayAt(JToken node, string key) =>
        node[key] as JArray ?? throw new InvalidDataException($"The rio being built has no {key} list.");

    static string StringAt(JToken node, string key) =>
        node[key]?.Value<string>() ?? throw new InvalidDataException($"The rio being built has no {key}.");

    static string GenerateUniqueRule()
    {
        string[] chars = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        for (int attempt=0; attempt < 100; attempt++)
        {
            string rule = "+";
            for (int i=0; i<5; i++)
            {
                int randomIndex = random.Next(0, chars.Length);
                rule += chars[randomIndex];
            }

            if (!uniqueRules.Contains(rule))
            {
                uniqueRules.Add(rule);
                return rule;
            }
        }

        return string.Empty;
    }
    static string GetRule(string name)
    {
        string rule = string.Empty;
        if (nameRulePairs.ContainsKey(name))
        {
            rule = nameRulePairs[name];
        }
        else
        {
            rule = GenerateUniqueRule();
            nameRulePairs[name] = rule;
        }
        return rule;
    }
    static JObject GetCategory(int index)
    {
        if (index >= categories.Count)
        {
            JObject newCategory = new JObject();
            newCategory["Type"] = "Radio";
            newCategory["Name"] = $"Category {index+1}";
            newCategory["Group"] = $"+CAT{index}";
            newCategory["Options"] = new JArray(); ;

            ArrayAt(rioJson, "Inputs").Add(newCategory);
            categories.Add(newCategory);

            return newCategory;
        }

        return categories[index];
    }
    static void CleanupJson()
    {
        List<string> rulesToKeep = new List<string>();
        JArray inputs = ArrayAt(rioJson, "Inputs");
        JArray maps = ArrayAt(rioJson, "Maps");

        List<JObject> optionsToKeep = new List<JObject>();
        List<JObject> categoriesToRemove = new List<JObject>();
        int categoryCount = 0;
        foreach (JObject category in inputs)
        {
            JArray categoryOptions = ArrayAt(category, "Options");
            if(categoryOptions.Count > 0)
            {
                categoryCount++;
                category["Name"] = $"Category {categoryCount}";

                foreach (JObject option in categoryOptions)
                {
                    optionsToKeep.Add(option);
                    rulesToKeep.Add(StringAt(option, "FrontEndRule"));
                }
            }
            else
            {
                categoriesToRemove.Add(category);
            }
        }
        // Delete categories
        foreach (JObject category in categoriesToRemove)
        {
            inputs.Remove(category);
        }
        // Cleanup conditions
        foreach(JObject option in optionsToKeep)
        {
            JArray newConditions = new JArray();
            foreach(string condition in ArrayAt(option, "Conditions").Values<string>().OfType<string>())
            {
                string newCondition = "";
                foreach(string rule in condition.Split("+"))
                {
                    if (rulesToKeep.Contains($"+{rule}"))
                    {
                        newCondition += $"+{rule}";
                    }
                }
                if(newCondition.Length > 0)
                {
                    newConditions.Add(newCondition);
                }
            }
            option["Conditions"] = newConditions;
        }
        // Clean up configurations
        foreach(JObject map in maps)
        {
            string newRules = "";
            foreach(string rule in StringAt(map, "configuration").Split("+"))
            {
                if (rulesToKeep.Contains($"+{rule}"))
                {
                    newRules += $"+{rule}";
                }
            }

            if(newRules.Length > 0)
            {
                map["configuration"] = newRules;
            }
        }
    }
    static void AddFile(string filename, string[] nameParts, int type)
    {
        int frame = 0;
        string completeRule = "";
        for (int i=0; i < nameParts.Length; i++)
        {

            if (type > 0 && i == nameParts.Length - 1)
            {
                frame = frames.IndexOf(nameParts[i]);
                if (frame == -1)
                {
                    frames.Add(nameParts[i]);
                    frame = frames.Count;
                    ArrayAt(animationOption, "Frames").Add(frame);
                    if(type == 1)
                    {
                        completeRule = "+ANM" + completeRule;
                    }
                    else if (type == 2)
                    {
                        completeRule = "+SLS" + completeRule;
                    }
                }
                else frame += 1;
            }
            else
            {
                string name = nameParts[i];
                string rule = GetRule(name);


                if (rule == string.Empty) continue;


                JObject category = GetCategory(i);
                if (!ruleOptionPairs.ContainsKey(rule))
                {
                    JObject option = new JObject();
                    option["Name"] = name;
                    option["Type"] = "NotAView";
                    option["FrontEndRule"] = rule;
                    option["BackEndRule"] = rule;
                    option["Defaults"] = new JArray();
                    option["Conditions"] = new JArray();
                    ArrayAt(option, "Conditions").Add(completeRule);
                    ArrayAt(category, "Options").Add(option);

                    ruleOptionPairs.Add(rule, option);
                }
                else
                {
                    bool exists = false;
                    foreach (string condition in ArrayAt(ruleOptionPairs[rule], "Conditions").Values<string>().OfType<string>())
                    {
                        if (condition.Equals(completeRule))
                        {
                            exists = true;
                        }
                    }


                    // Debug.WriteLine($"{previousRule} {list}");
                    if (!exists)
                        ArrayAt(ruleOptionPairs[rule], "Conditions").Add(completeRule);
                }

                completeRule += rule;
            }
        }

        // Add Map
        JObject newMap = new JObject();
        newMap["configuration"] = completeRule;
        newMap["depth"] = 0;
        newMap["frame"] = frame;
        newMap["filename"] = filename;
        ArrayAt(rioJson, "Maps").Add(newMap);

    }
    // The working state above is shared, so one build runs at a time, from whichever thread.
    static readonly Lock Building = new();

    internal static JObject LoadFilenames(string folder, int type)
    {
        lock (Building) return BuildFromFilenames(folder, type);
    }

    internal static JObject LoadDirectory(string folder, int type)
    {
        lock (Building) return BuildFromDirectory(folder, type);
    }

    static JObject BuildFromFilenames(string folder, int type)
    {
        random = new Random(0);
        nameRulePairs.Clear();
        ruleOptionPairs.Clear();
        uniqueRules.Clear();
        categories.Clear();

        rioJson = new JObject();
        rioJson["Loader"] = "RenderMap";
        rioJson["Exporter"] = "Renderappio Visualiser";
        rioJson["ExporterType"] = "Filenames to rio";
        rioJson["CacheDir"] = folder.Replace("\\", "/");
        rioJson["Inputs"] = new JArray();
        rioJson["Maps"] = new JArray();

        if (type > 0)
        {
            frames.Clear();

            JObject newCategory = new JObject();
            newCategory["Type"] = "Radio";
            newCategory["Name"] = $"View";
            newCategory["Group"] = $"+VEW";
            newCategory["Options"] = new JArray(); ;
            ArrayAt(rioJson, "Inputs").Add(newCategory);
            // categories.Add(newCategory);

            if (type == 1)
            {
                animationOption = new JObject();
                animationOption["Name"] = "Spin";
                animationOption["Type"] = "Animation";
                animationOption["FrontEndRule"] = "+ANM";
                animationOption["BackEndRule"] = "+ANM";
                animationOption["Defaults"] = new JArray();
                animationOption["Frames"] = new JArray();
                animationOption["Conditions"] = new JArray();
                ArrayAt(newCategory, "Options").Add(animationOption);
                ruleOptionPairs.Add("+ANM", animationOption);
            }
            else
            {
                animationOption = new JObject();
                animationOption["Name"] = "Stills";
                animationOption["Type"] = "Stills";
                animationOption["FrontEndRule"] = "+SLS";
                animationOption["BackEndRule"] = "+SLS";
                animationOption["Defaults"] = new JArray();
                animationOption["Frames"] = new JArray();
                animationOption["Conditions"] = new JArray();
                ArrayAt(newCategory, "Options").Add(animationOption);
                ruleOptionPairs.Add("+SLS", animationOption);
            }
        }

        IEnumerable<string> files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Where(s => s.EndsWith(".jpg") || s.EndsWith(".jpeg") || s.EndsWith(".png"));
        foreach (string file in files)
        {
            string relativePath = file.Replace(folder, "").Substring(1);
            string[] nameParts = Path.GetFileNameWithoutExtension(file).Split("_");
            AddFile(relativePath, nameParts, type);
        }

        CleanupJson();
        // File.WriteAllText($"{folder}.rio", rioJson.ToString());

        return rioJson;
    }
    static JObject BuildFromDirectory(string folder, int type)
    {
        random = new Random(0);
        nameRulePairs.Clear();
        ruleOptionPairs.Clear();
        uniqueRules.Clear();
        categories.Clear();

        rioJson = new JObject();
        rioJson["Loader"] = "Rendermap";
        rioJson["ExporterFrom"] = "Renderappio Visualiser";
        rioJson["ExporterType"] = "Directory to rio";
        rioJson["CacheDir"] = folder.Replace("\\", "/");
        rioJson["Inputs"] = new JArray();
        rioJson["Maps"] = new JArray();

        if(type > 0)
        {
            frames.Clear();

            JObject newCategory = new JObject();
            newCategory["Type"] = "Radio";
            newCategory["Name"] = $"View";
            newCategory["Group"] = $"+VEW";
            newCategory["Options"] = new JArray(); ;
            ArrayAt(rioJson, "Inputs").Add(newCategory);
            // categories.Add(newCategory);

            if(type==1)
            {
                animationOption = new JObject();
                animationOption["Name"] = "Spin";
                animationOption["Type"] = "Animation";
                animationOption["FrontEndRule"] = "+ANM";
                animationOption["BackEndRule"] = "+ANM";
                animationOption["Defaults"] = new JArray();
                animationOption["Frames"] = new JArray();
                animationOption["Conditions"] = new JArray();
                ArrayAt(newCategory, "Options").Add(animationOption);
                ruleOptionPairs.Add("+ANM", animationOption);
            }
            else
            {
                animationOption = new JObject();
                animationOption["Name"] = "Stills";
                animationOption["Type"] = "Stills";
                animationOption["FrontEndRule"] = "+SLS";
                animationOption["BackEndRule"] = "+SLS";
                animationOption["Defaults"] = new JArray();
                animationOption["Frames"] = new JArray();
                animationOption["Conditions"] = new JArray();
                ArrayAt(newCategory, "Options").Add(animationOption);
                ruleOptionPairs.Add("+SLS", animationOption);
            }
        }

        IEnumerable<string> files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Where(s => s.EndsWith(".jpg") || s.EndsWith(".jpeg") || s.EndsWith(".png"));
        var sortedFilesByNameLowercase = files.OrderBy(filePath => Path.GetFileName(filePath).ToLower());
        foreach (string file in sortedFilesByNameLowercase) {
            string relativePath = file.Replace("\\", "/").Replace(folder.Replace("\\", "/"), "").Substring(1);
            string extension = Path.GetExtension(relativePath);

            string[] nameParts = relativePath.Replace(extension, string.Empty).Split("/");
            AddFile(relativePath, nameParts, type);
        }

        CleanupJson();
        // File.WriteAllText($"{folder}__.rio", rioJson.ToString());

        return rioJson;
    }
}
