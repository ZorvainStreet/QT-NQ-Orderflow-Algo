using System.Text.Json;

namespace QT_MNQ_Orderflow_Algo.Persistence;

/// <summary>Atomic JSON persistence. Unreadable state fails closed: the caller must halt, never reset.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public StateStore(string path) => _path = path;

    public LoadResult Load()
    {
        if (!File.Exists(_path)) return new(null, true, null);
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_path), Json)
                        ?? throw new JsonException("null document");
            return new(state, false, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, backup, overwrite: false);
            return new(null, false, $"State unreadable ({ex.Message}); preserved as {backup}");
        }
    }

    public void Save(PersistedState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
