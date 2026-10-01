using System.Text.Json;

namespace QT_MNQ_Orderflow_Algo.Persistence;

/// <summary>Atomic JSON persistence. Unreadable state fails closed: the caller must halt, never reset. Load never throws.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public StateStore(string path) => _path = Path.GetFullPath(path);

    public LoadResult Load()
    {
        if (Directory.Exists(_path)) return new(null, false, $"State path is a directory: {_path}");
        if (!File.Exists(_path)) return new(null, true, null);
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_path), Json)
                        ?? throw new JsonException("null document");
            Validate(state);
            return new(state, false, null);
        }
        catch (Exception ex)
        {
            return new(null, false, $"State unreadable ({ex.Message}); {PreserveCorruptFile()}");
        }
    }

    private static void Validate(PersistedState state)
    {
        if (state.SchemaVersion != PersistedState.CurrentSchemaVersion)
            throw new JsonException($"unsupported schema version {state.SchemaVersion} (expected {PersistedState.CurrentSchemaVersion})");
        if (state.Eval is null) throw new JsonException("missing Eval");
        if (state.Day is null) throw new JsonException("missing Day");
        if (state.Eval.DailyPnl is null) throw new JsonException("missing Eval.DailyPnl");
        if (state.Eval.MllFloor <= 0m) throw new JsonException("invalid Eval.MllFloor");
        if (state.Eval.EodHighBalance <= 0m) throw new JsonException("invalid Eval.EodHighBalance");
        if (state.Day.Date == default) throw new JsonException("missing Day.Date");
    }

    private string PreserveCorruptFile()
    {
        var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            File.Copy(_path, backup, overwrite: false);
            return $"preserved as {backup}";
        }
        catch (Exception ex)
        {
            return $"BACKUP FAILED ({ex.Message}); original file left in place";
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
