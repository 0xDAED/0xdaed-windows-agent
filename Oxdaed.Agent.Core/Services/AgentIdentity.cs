using System.Text.Json;

namespace Oxdaed.Agent.Services;

public static class AgentIdentity
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "0xDAED");

    private static readonly string FilePath = Path.Combine(Dir, "agent.json");

    public static Guid GetOrCreatePcId()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<IdentityDto>(json);
                if (data != null && data.PcId != Guid.Empty)
                    return data.PcId;
            }
        }
        catch { /* ignore corrupted file */ }

        // create new
        var id = Guid.NewGuid();
        Save(id);
        return id;
    }

    private static void Save(Guid id)
    {
        Directory.CreateDirectory(Dir);

        var dto = new IdentityDto { PcId = id };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(FilePath, json);
    }

    private class IdentityDto
    {
        public Guid PcId { get; set; }
    }
}
