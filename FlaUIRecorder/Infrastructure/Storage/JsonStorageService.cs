using FlaUIRecorder.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

public class JsonStorageService
{
    private string file = "steps.json";

    public void Save(List<ActionStep> steps)
    {
        var json = JsonSerializer.Serialize(steps, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

        File.WriteAllText(file, json);
    }

    public List<ActionStep> Load(string path)
    {
        if (!File.Exists(path))
            return new List<ActionStep>(); // ✅ safe

        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<List<ActionStep>>(json)
               ?? new List<ActionStep>(); // ✅ fallback
    }
}