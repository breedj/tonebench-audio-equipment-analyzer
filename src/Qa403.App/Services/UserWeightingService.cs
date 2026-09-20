using System.Text.Json;
using Qa403.App.Json;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Imports, clears and loads the two user weighting tables: the config documents
/// (<see cref="ConfigRepository.UserWeightingKey"/>) are the durable copy and
/// <see cref="UserWeightingSetting"/> the live one, and every write here updates both. The browser
/// cannot read a local file, so an import is a <b>path</b> the server reads and parses -- picked
/// through the same in-app file listing the project dialogs use.
/// </summary>
public sealed class UserWeightingService(ConfigRepository repo, UserWeightingSetting setting, ILogger<UserWeightingService> logger)
{
    /// <summary>
    /// Reads and parses the file at <paramref name="path"/> into <paramref name="slot"/> (1 or 2).
    /// </summary>
    /// <exception cref="FileNotFoundException">No file at the path.</exception>
    /// <exception cref="FormatException">The file is not a "frequency, dB" table -- the message names the line.</exception>
    public async Task<UserWeightingTable> ImportAsync(int slot, string path, CancellationToken ct)
    {
        string key = ConfigRepository.UserWeightingKey(slot);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("No such file.", path);

        string text = await File.ReadAllTextAsync(path, ct);
        var points = WeightingTableParser.Parse(text);
        var table = new UserWeightingTable(
            Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), points, DateTimeOffset.UtcNow);

        await repo.SetAsync(key, JsonSerializer.Serialize(table, AppJsonContext.Default.UserWeightingTable), ct);
        setting.Set(slot, table);
        logger.LogInformation("User weighting {Slot} loaded from {Path}: {Name}, {Count} points", slot, path, table.Name, points.Count);
        return table;
    }

    public async Task ClearAsync(int slot, CancellationToken ct)
    {
        string key = ConfigRepository.UserWeightingKey(slot);
        await repo.DeleteAsync(key, ct);
        setting.Set(slot, null);
    }

    /// <summary>
    /// Loads both slots at startup. A missing or unreadable document leaves its slot empty and is
    /// logged, never thrown: a corrupt weighting file must not keep the app from starting.
    /// </summary>
    public async Task LoadAllAsync(CancellationToken ct)
    {
        for (int slot = 1; slot <= 2; slot++)
        {
            string key = ConfigRepository.UserWeightingKey(slot);
            try
            {
                string? stored = await repo.GetAsync(key, ct);
                if (stored is null)
                    continue;
                var table = JsonSerializer.Deserialize(stored, AppJsonContext.Default.UserWeightingTable);
                if (table is { IsValid: true })
                    setting.Set(slot, table);
                else
                    logger.LogWarning("User weighting {Slot} document is not a usable table; slot left empty", slot);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                logger.LogWarning(ex, "User weighting {Slot} document could not be loaded; slot left empty", slot);
            }
        }
    }
}
