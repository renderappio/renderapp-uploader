using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Data;
using System.Xml.Linq;
using System.Reflection;

namespace Renderapp.Uploader;

internal class LoadFromFolder
{
    static List<string> uniqueRules = new List<string>();
    static List<JObject> categories = new List<JObject>();
    static Dictionary<string, string> nameRulePairs = new Dictionary<string, string>();
    static Dictionary<string, JObject> ruleOptionPairs = new Dictionary<string, JObject>();
    static JObject rioJson;
    static Random random = new Random(0);
    static List<string> frames = new List<string>();
    static JObject animationOption;

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

            rioJson["Inputs"].Value<JArray>().Add(newCategory);
            categories.Add(newCategory);

            return newCategory;
        }

        return categories[index];
    }
    static void CleanupJson()
    {
        List<string> rulesToKeep = new List<string>();
        JArray inputs = rioJson["Inputs"].Value<JArray>();
        JArray maps = rioJson["Maps"].Value<JArray>();

        List<JObject> optionsToKeep = new List<JObject>();
        List<JObject> categoriesToRemove = new List<JObject>();
        int categoryCount = 0;
        foreach (JObject category in inputs)
        {
            JArray categoryOptions = category["Options"].Value<JArray>();
            if(categoryOptions.Count > 0)
            {
                categoryCount++;
                category["Name"] = $"Category {categoryCount}";

                foreach (JObject option in categoryOptions)
                {
                    optionsToKeep.Add(option);
                    rulesToKeep.Add(option["FrontEndRule"].Value<string>());
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
            foreach(string condition in option["Conditions"].Value<JArray>())
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
            foreach(string rule in map["configuration"].Value<string>().Split("+"))
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
    static string[] RecursiveFileSearch(string directoryPath)
    {
        // Get all files in the current directory
        string[] files = Directory.GetFiles(directoryPath);

        // Create an array to store file paths
        string[] filePaths = new string[files.Length];

        // Process each file
        for (int i = 0; i < files.Length; i++)
        {
            // Replace backslashes with forward slashes
            filePaths[i] = files[i].Replace('\\', '/');
        }

        // Recursively search in subdirectories
        string[] subDirectories = Directory.GetDirectories(directoryPath);
        foreach (string subDirectory in subDirectories)
        {
            string[] subDirectoryFilePaths = RecursiveFileSearch(subDirectory);

            // Concatenate the current file paths with the subdirectory file paths
            filePaths = filePaths.Concat(subDirectoryFilePaths).ToArray();
        }

        return filePaths;
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
                    animationOption["Frames"].Value<JArray>().Add(frame);
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
                    option["Conditions"].Value<JArray>().Add(completeRule);
                    category["Options"].Value<JArray>().Add(option);

                    ruleOptionPairs.Add(rule, option);
                }
                else
                {
                    bool exists = false;
                    foreach (string condition in ruleOptionPairs[rule]["Conditions"].Value<JArray>())
                    {
                        if (condition.Equals(completeRule))
                        {
                            exists = true;
                        }
                    }


                    // Debug.WriteLine($"{previousRule} {list}");
                    if (!exists)
                        ruleOptionPairs[rule]["Conditions"].Value<JArray>().Add(completeRule);
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
        rioJson["Maps"].Value<JArray>().Add(newMap);

    }
    internal static JObject LoadFilenames(string folder, int type)
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
            rioJson["Inputs"].Value<JArray>().Add(newCategory);
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
                newCategory["Options"].Value<JArray>().Add(animationOption);
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
                newCategory["Options"].Value<JArray>().Add(animationOption);
                ruleOptionPairs.Add("+SLS", animationOption);
            }
        }

        IEnumerable files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Where(s => s.EndsWith(".jpg") || s.EndsWith(".jpeg") || s.EndsWith(".png"));
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
    internal static JObject LoadDirectory(string folder, int type)
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
            rioJson["Inputs"].Value<JArray>().Add(newCategory);
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
                newCategory["Options"].Value<JArray>().Add(animationOption);
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
                newCategory["Options"].Value<JArray>().Add(animationOption);
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
    internal static JObject ChangeMapFilenameExtToWebp(JObject rioJson)
    {
        foreach (JObject mapItem in rioJson["Maps"])
        {
            mapItem["filename"] = Path.ChangeExtension(mapItem["filename"].Value<string>(), ".webp");
        }
        return rioJson;
    }
}
